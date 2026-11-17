// W3: durable registrations. A gateway restart must keep PCs, ownership and approved browsers, must NOT revive
// revoked grants, pairing requests or download capabilities, and an unreadable store must grant nothing.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair, sleep } from './harness.mjs';

const dataDir = tempDir('gateway-data');
const secret = randomBytes(32).toString('hex');
const port = 5400 + Math.floor(Math.random() * 300);
const boot = () => startGateway({ OrbitDrive__DataDirectory: dataDir, OrbitDrive__DevIdentity__Secret: secret }, port);
let gateway, agent;
after(async () => { await agent?.stop(); await gateway?.stop(); });

test('registrations survive a gateway restart; revoked and short-lived things do not come back', { timeout: 120_000 }, async () => {
  const root = makeFixtureRoot();
  const state = tempDir('restart-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await boot();
  agent = await startAgent(gateway, state, 'Durable PC');
  const pcId = agent.identity.pcId;
  const phone = await new Browser(gateway, 'owner@example.test').signIn();
  const tablet = await new Browser(gateway, 'owner@example.test').signIn();
  const stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, tablet, 'Tablet');
  assert.equal((await phone.call('DELETE', `/v1/browser-devices/${tablet.device.id}`)).status, 200);   // revoke before the restart
  const roots = await phone.get(`/v1/pcs/${pcId}/roots`);
  const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
  const file = page.body.entries.find((e) => e.name === 'hello.txt');
  const download = (await phone.post('/v1/download-sessions', { pcId, entryId: file.entryId })).body;
  agent.send('pair');
  const [pendingId, pendingSecret] = (await agent.next('pairing')).code.split('-');

  const stored = readFileSync(join(dataDir, 'registrations.json'), 'utf8');
  for (const leak of [phone.device.secret, tablet.device.secret, phone.token, download.capability, pendingSecret])
    assert.ok(!stored.includes(leak), 'no usable secret is written to disk (only hashes)');
  if (process.platform !== 'win32') assert.equal(statSync(join(dataDir, 'registrations.json')).mode & 0o077, 0, 'store is readable by the service account only');

  await gateway.stop();                       // --- restart ---
  agent.drain();
  gateway = await boot();
  const again = await agent.next('connected', 60_000);   // the agent reconnects by itself, with backoff
  assert.deepEqual([again.pcId, again.ownerBound], [pcId, true], 'same PC, still owned');

  const pcs = await phone.get('/v1/pcs');
  assert.deepEqual(pcs.body.pcs.map((p) => [p.pcId, p.name, p.online]), [[pcId, 'Durable PC', true]]);
  assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200, 'approved browser works without re-pairing');
  assert.equal((await tablet.get('/v1/pcs')).status, 403, 'a revoked browser stays revoked after the restart');
  assert.equal((await stranger.get(`/v1/pcs/${pcId}/roots`)).status, 403);
  assert.equal((await fetch(gateway.url + download.urlPath)).status, 404, 'download capabilities do not survive a restart');
  assert.equal((await phone.post(`/v1/pairing-requests/${pendingId}/claim`, { secret: pendingSecret, browserName: 'x' })).status, 404, 'pending pairing requests do not survive a restart');
  // Ownership is still enforced: another account cannot take the PC over with a fresh pairing request.
  agent.send('pair');
  const [id, code] = (await agent.next('pairing')).code.split('-');
  assert.equal((await stranger.post(`/v1/pairing-requests/${id}/claim`, { secret: code, browserName: 'Evil' })).status, 403);
  // A fresh download works again after re-preparing.
  const fresh = (await phone.post('/v1/download-sessions', { pcId, entryId: file.entryId })).body;
  assert.equal(await (await fetch(gateway.url + fresh.urlPath)).text(), 'Hello from OrbitDrive\n');

  // Disconnecting the PC is durable too.
  assert.equal((await phone.call('DELETE', `/v1/pcs/${pcId}`)).status, 200);
  await gateway.stop(); agent.drain(); gateway = await boot();
  assert.equal((await agent.next('connected', 60_000)).ownerBound, false);
  assert.deepEqual((await phone.get('/v1/pcs')).body.pcs, []);
});

test('an unreadable store grants nothing and is kept aside for investigation', { timeout: 60_000 }, async () => {
  await gateway.stop();
  writeFileSync(join(dataDir, 'registrations.json'), '{"Version":1,"Pcs":[{"PcId":"x","Spki":"not base64!!"');
  agent.drain();
  gateway = await boot();
  await agent.next('connected', 60_000);
  const anyone = await new Browser(gateway, 'owner@example.test').signIn();
  assert.equal((await anyone.get('/v1/pcs')).status, 403);
  assert.ok(readdirSync(dataDir).some((name) => name.startsWith('registrations.json.corrupt-')));
  await sleep(100);
});
