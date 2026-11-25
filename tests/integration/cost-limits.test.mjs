// R42: one account cannot make the PC prepare archives faster than its own limit however many browsers it uses, and the operator sees a cost alert
// when the day's relayed bytes pass the configured line.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair } from './harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
let gateway, agent, browser, pcId, rootId;
before(async () => {
  const state = tempDir('cost-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  gateway = await startGateway({ OrbitDrive__ArchiveStartsPerOwnerPerMinute: '3', OrbitDrive__RelayBytesDailyAlert: '1', OrbitDrive__OperatorUids__0: uidOf('ops@example.test') });
  agent = await startAgent(gateway, state, 'Cost PC');
  pcId = agent.identity.pcId;
  browser = await new Browser(gateway, 'cost@example.test').signIn();
  await pair(agent, browser);
  rootId = (await browser.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

test('archive starts are limited per account', async () => {
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: null, pageSize: 200 });
  const entry = page.body.entries.find((e) => e.name === 'data.bin');
  const statuses = [];
  for (let i = 0; i < 5; i++) statuses.push((await browser.post(`/v1/pcs/${pcId}/archives`, { entryIds: [entry.entryId], strict: false })).status);
  assert.deepEqual(statuses.slice(0, 3), [202, 202, 202]);
  assert.deepEqual(statuses.slice(3), [429, 429]);
});

test('the operator overview raises a cost alert once the day passes the configured line', async () => {
  const ops = await new Browser(gateway, 'ops@example.test').signIn();   // the operator is identified by account id and needs no paired browser
  const before = (await ops.get('/v1/admin/overview')).body;
  assert.equal(before.relayBytesDailyAlert, '1');
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: null, pageSize: 200 });
  const entry = page.body.entries.find((e) => e.name === 'data.bin');
  const session = await browser.post('/v1/download-sessions', { pcId, entryId: entry.entryId });
  await (await fetch(gateway.url + session.body.urlPath)).arrayBuffer();
  // The gateway counts a download's bytes when the request handler finishes, which can be a moment after the client has read the last byte.
  // Wait (up to 10 s) for that count to show instead of reading it once; the assertions below are unchanged.
  let after = (await ops.get('/v1/admin/overview')).body;
  for (let i = 0; i < 100 && BigInt(after.relayBytesToday) < 1n; i++) { await new Promise((r) => setTimeout(r, 100)); after = (await ops.get('/v1/admin/overview')).body; }
  assert.ok(BigInt(after.relayBytesToday) >= 1n);
  assert.equal(after.costAlert, true);
});
