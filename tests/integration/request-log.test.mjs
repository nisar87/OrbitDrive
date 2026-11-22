// R46: every response carries a correlation id, the access log names route templates (never raw paths with download links or ids), and a caller cannot put text into the log.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair } from './harness.mjs';

let gateway, agent, browser, pcId, rootId;
before(async () => {
  const state = tempDir('reqlog-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Log PC');
  pcId = agent.identity.pcId;
  browser = await new Browser(gateway, 'reqlog@example.test').signIn();
  await pair(agent, browser);
  rootId = (await browser.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

test('every response has a request id, and a plain caller id is echoed back', async () => {
  const generated = await fetch(gateway.url + '/healthz');
  assert.match(generated.headers.get('x-request-id') ?? '', /^[A-Za-z0-9_-]{8,64}$/);
  const echoed = await fetch(gateway.url + '/healthz', { headers: { 'x-request-id': 'support-case-12345' } });
  assert.equal(echoed.headers.get('x-request-id'), 'support-case-12345');
});

test('an id that is not plainly an id is replaced, so nothing can be smuggled into the log', async () => {
  for (const bad of ['short', 'has spaces in it ok', 'x'.repeat(200), 'semi;colon;value1', 'emoji-💥-value-here']) {
    let response;
    try { response = await fetch(gateway.url + '/healthz', { headers: { 'x-request-id': bad } }); } catch { continue; }   // some values are refused by the client itself
    assert.notEqual(response.headers.get('x-request-id'), bad);
    assert.match(response.headers.get('x-request-id') ?? '', /^[A-Za-z0-9_-]{8,64}$/);
  }
});

test('the log has route templates and ids, and no download link, device secret or token', async () => {
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: null, pageSize: 200 });
  const target = page.body.entries.find((e) => e.name === 'data.bin');
  const created = await browser.post('/v1/download-sessions', { pcId, entryId: target.entryId });
  assert.equal(created.status, 201);
  const probe = 'probe-id-for-the-log-9f3a';
  const served = await fetch(gateway.url + created.body.urlPath, { headers: { range: 'bytes=0-9', 'x-request-id': probe } });
  await served.arrayBuffer();
  assert.equal(served.headers.get('x-request-id'), probe);
  await new Promise((resolve) => setTimeout(resolve, 300));
  const log = gateway.log;
  assert.ok(log.includes(`[${probe}]`), 'the correlation id is in the log');
  assert.match(log, /GET \/\S+ -> 206 in \d+ ms/);
  const linkParts = created.body.urlPath.split('/').filter((part) => part.length >= 16);
  for (const secret of [...linkParts, browser.device?.secret, browser.token].filter(Boolean)) assert.ok(!log.includes(secret), 'a secret reached the log');
});
