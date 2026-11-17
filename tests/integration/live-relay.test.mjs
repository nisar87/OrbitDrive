// Real agent ↔ LIVE development relay over the internet (set ORBIT_LIVE_RELAY_URL). Runs from GitHub runners, i.e. a
// different network from the relay, with outbound connections only. Proves connectivity, the agent handshake and
// pairing-code issuance through TLS. It does NOT cover claim/browse/transfer on the live relay: those need a verified
// Firebase test account, which has not been provisioned (see docs/IMPLEMENTATION_STATE.md, blockers).
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, sleep } from './harness.mjs';

const url = process.env.ORBIT_LIVE_RELAY_URL;
const skip = url ? false : 'ORBIT_LIVE_RELAY_URL is not set';
let agent, ipc;
after(async () => { ipc?.close(); await agent?.stop(); });

test('live relay answers over verified HTTPS and states its transport honestly', { skip }, async () => {
  const meta = await (await fetch(`${url}/v1/meta`)).json();
  assert.match(meta.version, /^\d+\.\d+\.\d+$/);
  assert.deepEqual([meta.environment, meta.relayTerminatesTls, meta.endToEndEncrypted], ['Production', true, false]);
  assert.equal((await fetch(`${url}/healthz`)).status, 200);
});

test('live relay refuses unauthenticated, development and forged identities', { skip }, async () => {
  assert.equal((await fetch(`${url}/v1/pcs`)).status, 401);
  assert.equal((await fetch(`${url}/v1/pcs`, { headers: { authorization: 'Bearer dev.e30.AAAA' } })).status, 401);
  const unsigned = `${Buffer.from('{"alg":"none"}').toString('base64url')}.${Buffer.from(JSON.stringify({ sub: 'x', aud: 'orbitdrive-dev', iss: 'https://securetoken.google.com/orbitdrive-dev', exp: 9999999999, iat: 1, auth_time: 1, email_verified: true, email: 'a@b.c' })).toString('base64url')}.`;
  assert.equal((await fetch(`${url}/v1/pcs`, { headers: { authorization: `Bearer ${unsigned}` } })).status, 401, 'emulator-style tokens must not work in production');
  assert.equal((await fetch(`${url}/v1/dev/sign-in`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"email":"a@b.c"}' })).status, 404);
  assert.equal((await fetch(`${url}/v1/downloads/${'A'.repeat(43)}`)).status, 404);
  assert.equal((await fetch(`${url}/v1/agent-streams/x`, { method: 'POST', headers: { authorization: 'Bearer x' }, body: 'x' })).status, 403);
});

test('a real agent on another network connects outbound, authenticates and gets a one-time pairing code', { skip, timeout: 120_000 }, async () => {
  const state = tempDir('live-state');
  agentCli(state, 'roots', 'add', 'Live Share', makeFixtureRoot());
  agent = await startAgent({ url }, state, `ci-${process.platform}`, 'https://orbitdrive-dev.web.app');
  assert.match(agent.identity.pcId, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(agent.identity.ownerBound, false, 'a fresh PC identity has no owner');
  ipc = await connectIpc(agent.ready.pipe);
  for (let i = 0; i < 50 && (await ipc.send('status')).data.state !== 'online'; i++) await sleep(200);
  assert.equal((await ipc.send('status')).data.state, 'online');
  const pairing = (await ipc.send('pair')).data;
  assert.match(pairing.url, /^https:\/\/orbitdrive-dev\.web\.app\/pair#id=[A-Z2-9]{6}&s=[A-Z2-9]{10}$/);
  const [id] = pairing.code.split('-');
  // Without a verified account nobody can claim it, and wrong secrets are refused before identity matters.
  assert.equal((await fetch(`${url}/v1/pairing-requests/${id}/claim`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ secret: 'WRONGWRONG', browserName: 'x' }) })).status, 401);
  assert.equal((await ipc.send('quit')).ok, true);
});
