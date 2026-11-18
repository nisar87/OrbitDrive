// D11 (found by the soak): download sessions stay valid for an hour so a transfer can be resumed, and they used to count against the
// per-browser limit for that whole hour even after the file had been delivered. A browser that simply saved a lot of files was then refused.
// Finished sessions are now retired to make room; waiting, running and resumable ones are never taken, and the limit still holds against them.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair } from './harness.mjs';

const LIMIT = 4;
let gateway, agent, owner, pcId, entries;
before(async () => {
  const root = makeFixtureRoot(), state = tempDir('sessions-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway({ OrbitDrive__MaxDownloadSessionsPerDevice: String(LIMIT) });
  agent = await startAgent(gateway, state, 'Sessions PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'sessions@example.test').signIn();
  await pair(agent, owner, 'Phone');
  const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
  entries = (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 })).body.entries.filter((e) => e.name === 'data.bin' || e.name === 'hello.txt');
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

const open = async (name) => owner.post('/v1/download-sessions', { pcId, entryId: entries.find((e) => e.name === name).entryId });
const fetchAll = async (session) => { const response = await fetch(gateway.url + session.body.urlPath); assert.equal(response.status, 200); return (await response.arrayBuffer()).byteLength; };

test('finished downloads do not use up the limit: far more than the limit can be saved one after another', async () => {
  for (let i = 0; i < LIMIT * 3; i++) {
    const session = await open('data.bin');
    assert.equal(session.status, 201, `download ${i + 1} was refused: ${JSON.stringify(session.body)}`);
    assert.equal(await fetchAll(session), 300_000);
  }
});

test('sessions that are only waiting are never taken away, and the limit still refuses a browser that piles up unfetched ones', async () => {
  const waiting = [];
  for (let i = 0; i < LIMIT; i++) { const s = await open('hello.txt'); assert.equal(s.status, 201, JSON.stringify(s.body)); waiting.push(s); }
  const refused = await open('hello.txt');
  assert.equal(refused.status, 429, 'with every slot held by an unfetched session the limit must hold');
  for (const s of waiting) assert.equal(await fetchAll(s), 'Hello from OrbitDrive\n'.length, 'a waiting session must still work: nothing was taken from it');
});

test('a retired session no longer works, and one that is still in use does', async () => {
  const sessions = [];
  for (let i = 0; i < LIMIT; i++) { const s = await open('hello.txt'); assert.equal(s.status, 201); sessions.push(s); }
  await fetchAll(sessions[0]);                                  // finished: the oldest, and the only one that may be retired
  const next = await open('hello.txt');                         // needs a slot: retires the finished one
  assert.equal(next.status, 201, JSON.stringify(next.body));
  assert.equal((await fetch(gateway.url + sessions[0].body.urlPath)).status, 404, 'the retired session is gone');
  for (const s of sessions.slice(1)) assert.equal(await fetchAll(s), 'Hello from OrbitDrive\n'.length, 'unfetched sessions are untouched');
});
