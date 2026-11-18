// W12: maintenance mode refuses only NEW work and never cuts or corrupts what is already running; readiness tells the truth; ending it restores service
// without a restart. The switch is a file in the gateway's data directory (no restart, so nothing in flight is dropped).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, writeLargeFile, Browser, pair, sleep, sha256 } from './harness.mjs';
import { readFileSync } from 'node:fs';

let gateway, agent, owner, pcId, roots, dataDir, flag, bigPath, entries;
before(async () => {
  const root = makeFixtureRoot(), state = tempDir('maint-state');
  dataDir = tempDir('maint-data'); flag = join(dataDir, 'MAINTENANCE');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway({ OrbitDrive__DataDirectory: dataDir });
  agent = await startAgent(gateway, state, 'Maintenance PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'maint@example.test').signIn();
  await pair(agent, owner, 'Phone');
  roots = (await owner.get(`/v1/pcs/${pcId}/roots`)).body.roots;
  entries = (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots[0].rootId, pageSize: 50 })).body.entries;
  bigPath = join(root, 'maintenance-big.bin');
  await writeLargeFile(bigPath, 24);
  entries = (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots[0].rootId, pageSize: 50 })).body.entries;
});
after(async () => { rmSync(flag, { force: true }); await agent?.stop(); await gateway?.stop(); });

const entry = (name) => entries.find((e) => e.name === name);
const ready = async () => fetch(gateway.url + '/readyz');
const settle = () => sleep(1200);   // the gateway looks at the flag at most once a second

test('normal: ready, and new work is accepted', async () => {
  const r = await ready(); assert.equal(r.status, 200); assert.equal(await r.text(), 'ready');
  assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: entry('hello.txt').entryId })).status, 201);
});

test('maintenance refuses new downloads with 503 + Retry-After, keeps the process "healthy", reports "not ready", and still lets people browse and cancel', async () => {
  writeFileSync(flag, ''); await settle();
  const r = await ready(); assert.equal(r.status, 503); assert.equal(await r.text(), 'maintenance');
  assert.equal((await fetch(gateway.url + '/healthz')).status, 200, 'the process is alive');
  const refused = await owner.post('/v1/download-sessions', { pcId, entryId: entry('hello.txt').entryId });
  assert.equal(refused.status, 503); assert.equal(refused.body.error.code, 'unavailable');
  assert.match(refused.body.error.message, /maintained/);
  assert.equal((await owner.get(`/v1/pcs/${pcId}/roots`)).status, 200, 'browsing still works');
  assert.equal((await owner.get('/v1/download-sessions')).status, 200, 'the list of running transfers is visible');
  assert.equal((await owner.post(`/v1/pcs/${pcId}/searches`, { rootId: roots[0].rootId, query: 'hello' })).status, 503, 'new searches are new work');
  rmSync(flag); await settle();
});

test('a transfer that began before maintenance finishes intact while maintenance is on, and ending maintenance restores service without a restart', async () => {
  const session = await owner.post('/v1/download-sessions', { pcId, entryId: entry('maintenance-big.bin').entryId });
  assert.equal(session.status, 201, JSON.stringify(session.body));
  const response = await fetch(gateway.url + session.body.urlPath);
  assert.equal(response.status, 200);
  const reader = response.body.getReader();
  const chunks = []; let got = 0;
  const first = await reader.read(); chunks.push(first.value); got += first.value.length;
  writeFileSync(flag, ''); await settle();                                         // maintenance begins mid-transfer
  assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: entry('hello.txt').entryId })).status, 503);
  for (;;) { const { done, value } = await reader.read(); if (done) break; chunks.push(value); got += value.length; }
  const body = Buffer.concat(chunks);
  assert.equal(got, 24 * 1024 * 1024, 'the whole file arrived');
  assert.equal(sha256(body), sha256(readFileSync(bigPath)), 'and it is byte-identical');
  rmSync(flag); await settle();
  assert.equal((await ready()).status, 200);
  assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: entry('hello.txt').entryId })).status, 201, 'service is back');
});

test('security: maintenance does not weaken authorization (a stranger still gets nothing, and the refusal leaks nothing about files)', async () => {
  writeFileSync(flag, ''); await settle();
  const stranger = await new Browser(gateway, 'stranger-maint@example.test').signIn();
  const r = await stranger.post('/v1/download-sessions', { pcId, entryId: 'whatever' });
  assert.ok([401, 403, 404, 503].includes(r.status));
  assert.doesNotMatch(JSON.stringify(r.body), /hello\.txt|data\.bin|Share/);
  rmSync(flag); await settle();
});
