// A PC that is asked for far more than it can do at once must stay healthy and answer every caller: either with the data or with a plain
// "busy, try again" (429), never with a hang, a crash or a server error; and it must serve normally again as soon as the burst is over.
// (The agent takes 16 requests at once and the relay holds 64 per PC; this drives a burst well beyond both against the real processes.)
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, sleep } from './harness.mjs';

let gateway, agent, ipc, phone, pcId, rootId;

before(async () => {
  const root = makeFixtureRoot();
  const state = tempDir('busy-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Busy PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, phone, 'Phone');
  rootId = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

const listing = () => phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, pageSize: 200 });
async function burst(count) {
  const started = Date.now();
  const results = await Promise.all(Array.from({ length: count }, () => listing().then((r) => r, (e) => ({ status: 0, error: String(e) }))));
  return { results, ms: Date.now() - started };
}

test('a burst far beyond what the PC can do at once is answered completely: data or a plain "busy", nothing else', async () => {
  let refused = 0, served = 0, total = 0;
  for (let round = 0; round < 5 && refused === 0; round++) {
    const { results, ms } = await burst(800);
    assert.ok(ms < 60_000, `the burst finished in ${ms} ms, not hung`);
    for (const r of results) {
      total++;
      assert.ok(r.status === 200 || r.status === 429, `every answer is data or a refusal, got ${r.status} ${r.error ?? JSON.stringify(r.body)}`);
      if (r.status === 200) { served++; assert.equal(r.body.entries.length, 7, 'served answers are complete and correct'); }
      else { refused++; assert.equal(r.body.error.code, 'limit_exceeded'); assert.match(r.body.error.message, /busy/i); }
    }
  }
  assert.ok(served > 0, 'some requests were served during the burst');
  assert.ok(refused > 0, `the limit was reached (${total} requests sent): without refusals this test would prove nothing`);
});

test('after the burst the PC is healthy and serves normally, again and again', async () => {
  await sleep(500);
  for (let i = 0; i < 3; i++) {
    const r = await listing();
    assert.equal(r.status, 200, `request ${i} after the burst`);
    assert.equal(r.body.entries.length, 7);
  }
  const status = (await ipc.send('status')).data;
  assert.equal(status.state, 'online');
  assert.equal(status.activeStreams, 0, 'nothing is left running');
  // a second burst behaves the same: the slots were all given back
  const { results } = await burst(300);
  assert.ok(results.every((r) => r.status === 200 || r.status === 429));
  assert.ok(results.some((r) => r.status === 200), 'the slots were released after the first burst');
  assert.equal((await listing()).status, 200);
});
