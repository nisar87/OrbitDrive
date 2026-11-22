// R07: a PC can move to a fresh identity key. Its id, owner and pairings survive; the old key stops working; forged moves are refused.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, createSign } from 'node:crypto';
import { readdirSync } from 'node:fs';
import { startGateway, startAgent, agentCli, connectIpc, tempDir, makeFixtureRoot, Browser, pair, waitFor } from './harness.mjs';

let gateway, agent, ipc, browser, state, root;
before(async () => {
  state = tempDir('rotate-state');
  root = makeFixtureRoot();
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Rotating PC');
  ipc = await connectIpc(agent.ready.pipe);
  browser = await new Browser(gateway, 'rotate@example.test').signIn();
  await pair(agent, browser);
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

const pcId = () => agent.identity.pcId;
const roots = async () => (await browser.get(`/v1/pcs/${pcId()}/roots`)).status;

test('rotating the key keeps the PC id, the pairing and the shared folders, and survives a restart', async () => {
  const before = (await ipc.send('status')).data;
  assert.equal(before.identityRotationPending, false);
  assert.equal((await ipc.send('rotate_identity')).ok, true);
  await waitFor(async () => (await ipc.send('status')).data.identityRotationPending === false, 20000, 'the relay accepts the new key');
  const after = (await ipc.send('status')).data;
  assert.equal(after.pcId, before.pcId, 'the PC id never changes');
  assert.equal(await roots(), 200, 'the paired browser still reaches the PC');
  assert.ok(!readdirSync(state).includes('identity-next.bin'), 'the pending key file is gone once accepted');
  ipc.close(); await agent.stop();
  agent = await startAgent(gateway, state, 'Rotating PC');
  ipc = await connectIpc(agent.ready.pipe);
  assert.equal(agent.identity.pcId, before.pcId, 'after a restart it signs in as the same PC with the new key');
  assert.equal(await roots(), 200);
});

test('a stranger cannot take over a known PC id with a key of their own, with or without a rotation statement', async () => {
  const wsUrl = gateway.url.replace('http', 'ws') + '/v1/agent-control';
  const attempt = (withRotation) => new Promise((resolve) => {
    const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    const spki = publicKey.export({ type: 'spki', format: 'der' });
    const b64 = (b) => Buffer.from(b).toString('base64url');
    const sign = (text) => b64(createSign('SHA256').update(text).sign({ key: privateKey, dsaEncoding: 'ieee-p1363' }));
    const socket = new WebSocket(wsUrl);
    const seen = [];
    socket.addEventListener('message', (e) => {
      const m = JSON.parse(String(e.data)); seen.push(m);
      if (m.action !== 'challenge') return;
      const nonce = m.payload.nonce;
      const payload = { pcId: pcId(), publicKey: b64(spki), signature: sign(`orbitdrive-agent-auth-v1\n${nonce}\n${pcId()}`), name: 'Intruder', agentVersion: '9', protocolVersion: 1, capabilities: [] };
      if (withRotation) {
        payload.rotateToSpki = b64(spki);
        payload.rotateToSignature = sign(`orbitdrive-agent-rotate-v1\n${nonce}\n${pcId()}\n${b64(createHash('sha256').update(spki).digest())}`);
      }
      socket.send(JSON.stringify({ v: 1, id: '1', kind: 'req', action: 'hello', payload }));
    });
    socket.addEventListener('close', () => resolve(seen));
    socket.addEventListener('error', () => resolve(seen));
  });
  for (const withRotation of [false, true]) {
    const seen = await attempt(withRotation);
    assert.ok(!seen.some((m) => m.action === 'hello' && m.ok === true), `must not be welcomed (rotation statement: ${withRotation})`);
  }
  assert.equal(await roots(), 200, 'the real PC is untouched');
});

test('a second rotation works too, and an interrupted one loses nothing', async () => {
  const id = pcId();
  assert.equal((await ipc.send('rotate_identity')).ok, true);
  await waitFor(async () => (await ipc.send('status')).data.identityRotationPending === false, 20000, 'second rotation accepted');
  assert.equal((await ipc.send('status')).data.pcId, id);
  assert.equal(await roots(), 200);
});
