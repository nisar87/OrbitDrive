// R21: a download link is limited on its own (starts per minute, transfers at once), without affecting other links or the browser's other work.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair } from './harness.mjs';

const STARTS = 6;
let gateway, agent, browser, pcId, rootId;

before(async () => {
  const state = tempDir('caplimit-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  gateway = await startGateway({ OrbitDrive__DownloadStartsPerCapabilityPerMinute: String(STARTS) });
  agent = await startAgent(gateway, state, 'Limits PC');
  pcId = agent.identity.pcId;
  browser = await new Browser(gateway, 'caplimit@example.test').signIn();
  await pair(agent, browser);
  rootId = (await browser.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

async function link(name) {
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: null, pageSize: 200 });
  const target = page.body.entries.find((e) => e.name === name);
  const created = await browser.post('/v1/download-sessions', { pcId, entryId: target.entryId });
  assert.equal(created.status, 201, JSON.stringify(created.body));
  return created.body;
}
const get = (session, headers = {}) => fetch(gateway.url + session.urlPath, { headers });

test('one link stops after its starts per minute, with Retry-After, and another link is unaffected', async () => {
  const noisy = await link('data.bin');
  const quiet = await link('data.bin');
  const statuses = [];
  for (let i = 0; i < STARTS + 3; i++) { const r = await get(noisy, { range: 'bytes=0-9' }); statuses.push(r.status); await r.arrayBuffer(); if (r.status === 429) assert.ok(Number(r.headers.get('retry-after')) >= 1); }
  assert.deepEqual(statuses.slice(0, STARTS), Array(STARTS).fill(206));
  assert.deepEqual(statuses.slice(STARTS), [429, 429, 429]);
  const other = await get(quiet, { range: 'bytes=0-9' });
  assert.equal(other.status, 206, 'a different link keeps working');
  await other.arrayBuffer();
});

test('HEAD does not use up the budget and the refusal does not reveal the link', async () => {
  const fresh = await link('data.bin');
  for (let i = 0; i < STARTS * 2; i++) assert.equal((await fetch(gateway.url + fresh.urlPath, { method: 'HEAD' })).status, 200);
  const first = await get(fresh, { range: 'bytes=0-9' });
  assert.equal(first.status, 206);
  await first.arrayBuffer();
  const noisy = await link('data.bin');
  let refused, text = '';
  for (let i = 0; i < STARTS + 1; i++) { refused = await get(noisy, { range: 'bytes=0-9' }); text = await refused.text(); }
  assert.equal(refused.status, 429);
  assert.ok(!text.includes(noisy.capability));
});
