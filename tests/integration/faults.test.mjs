// W3/D3 fault injection: leases and heartbeats on the control channel, hard crashes on both sides, and resuming a
// transfer after the relay dies. Real gateway and agent processes; faults are injected at the TCP and process level.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { createHash, generateKeyPairSync, sign, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { startGateway, startAgent, startProxy, kill, freeze, thaw, agentCli, tempDir, makeFixtureRoot, writeLargeFile, Browser, pair, sleep, freePort } from './harness.mjs';

const HEARTBEAT = 1, LEASE = 3;
const dataDir = tempDir('faults-data');
const secret = randomBytes(32).toString('hex');
const port = await freePort();   // one address for the whole file: the gateway is restarted on it
const boot = () => startGateway({
  OrbitDrive__DataDirectory: dataDir, OrbitDrive__DevIdentity__Secret: secret,
  OrbitDrive__AgentHeartbeatSeconds: String(HEARTBEAT), OrbitDrive__AgentLeaseSeconds: String(LEASE),
}, port);
let gateway, proxy, agent, phone, pcId, state, root, bigHash;
const BIG_MIB = 48;
after(async () => { await agent?.stop(); proxy?.stop(); await gateway?.stop(); });

const online = async () => (await phone.get('/v1/pcs')).body.pcs[0].online;
async function until(check, ms, what) {
  const start = Date.now();
  while (Date.now() - start < ms) { if (await check()) return Date.now() - start; await sleep(100); }
  assert.fail(`${what} did not happen within ${ms} ms`);
}

test('setup: agent connects through the fault proxy and is paired', { timeout: 120_000 }, async () => {
  root = makeFixtureRoot();
  bigHash = await writeLargeFile(join(root, 'big.bin'), BIG_MIB);
  state = tempDir('faults-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await boot();
  proxy = await startProxy(gateway);
  agent = await startAgent(proxy, state, 'Fault PC');
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, phone, 'Phone');
});

test('an idle but healthy connection is kept alive by heartbeats (no false offline)', { timeout: 60_000 }, async () => {
  agent.drain();
  await sleep(LEASE * 2500);                                   // 2.5 leases with no application traffic at all
  assert.equal(await online(), true);
  assert.equal(agent.events.filter((e) => !e.consumed && (e.event === 'disconnected' || e.event === 'connected')).length, 0, 'no reconnect happened');
  assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200);
});

test('silent network loss: both sides notice within the lease, requests fail fast, and it heals by itself', { timeout: 90_000 }, async () => {
  agent.drain();
  const before = Number(agent.identity.generation);
  proxy.blackhole();                                           // nothing is closed; packets just stop arriving
  const gatewayNoticed = await until(async () => !(await online()), (LEASE + 4) * 1000, 'gateway marking the PC offline');
  assert.ok(gatewayNoticed >= (LEASE - HEARTBEAT - 1) * 1000, `not dropped early (${gatewayNoticed} ms)`);
  const started = Date.now();
  const refused = await phone.get(`/v1/pcs/${pcId}/roots`);
  assert.deepEqual([refused.status, refused.body.error.code], [503, 'pc_offline']);
  assert.ok(Date.now() - started < 2000, 'an offline PC is reported at once, not after a request timeout');
  const lost = await agent.next('disconnected', (LEASE + 6) * 1000);
  assert.equal(lost.reason, 'service_silent', 'the agent detects the dead path itself');
  proxy.heal();
  const again = await agent.next('connected', 60_000);
  assert.ok(Number(again.generation) > before, 'a new connection generation');
  agent.identity = again;
  assert.equal(await online(), true);
  assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200, 'no re-pairing needed');
});

// Scripted agent speaking the real handshake, to control exactly what it sends afterwards.
async function scriptedAgent(capabilities) {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const spki = publicKey.export({ type: 'spki', format: 'der' });
  const id = createHash('sha256').update(spki).digest('base64url');
  const ws = new WebSocket(gateway.url.replace('http', 'ws') + '/v1/agent-control');
  const messages = [];
  let closedAt = 0;
  ws.addEventListener('close', () => { closedAt = Date.now(); });
  ws.addEventListener('error', () => {});
  const next = () => new Promise((resolve, reject) => {
    ws.addEventListener('message', (e) => resolve(JSON.parse(e.data)), { once: true });
    ws.addEventListener('close', () => reject(new Error('closed')), { once: true });
  });
  const challenge = await next();
  const signature = sign('sha256', Buffer.from(`orbitdrive-agent-auth-v1\n${challenge.payload.nonce}\n${id}`), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64url');
  ws.send(JSON.stringify({ v: 1, id: 'hello', kind: 'req', action: 'hello', payload: { protocolVersion: 1, pcId: id, publicKeySpki: spki.toString('base64url'), signature, name: 'Scripted', agentVersion: '0.0.1', capabilities } }));
  const welcome = await next();
  ws.addEventListener('message', (e) => messages.push(JSON.parse(e.data)));
  return { ws, welcome, messages, get closedAt() { return closedAt; } };
}

test('a connected agent that stops sending heartbeats is dropped when its lease runs out; answered heartbeats keep it', { timeout: 60_000 }, async () => {
  const silent = await scriptedAgent(['browse', 'heartbeat']);
  assert.equal(silent.welcome.error ?? null, null);
  assert.deepEqual([silent.welcome.payload.heartbeatSeconds, silent.welcome.payload.leaseSeconds], [HEARTBEAT, LEASE], 'the service tells the agent its lease terms');
  const start = Date.now();
  await until(() => silent.closedAt > 0, (LEASE + 4) * 1000, 'dropping the silent agent');
  assert.ok(silent.closedAt - start >= (LEASE - 1) * 1000, 'not before the lease ran out');

  const beating = await scriptedAgent(['browse', 'heartbeat']);
  for (let i = 0; i < LEASE * 2; i++) { await sleep(HEARTBEAT * 1000); beating.ws.send(JSON.stringify({ v: 1, id: `hb-${i}`, kind: 'req', action: 'heartbeat', payload: { ok: true } })); }
  assert.equal(beating.closedAt, 0, 'heartbeats renew the lease');
  await until(() => beating.messages.filter((m) => m.action === 'heartbeat').length === LEASE * 2, 3000, 'the last heartbeat answer');
  const acks = beating.messages.filter((m) => m.kind === 'res' && m.action === 'heartbeat');
  assert.equal(acks.length, LEASE * 2);
  assert.ok(acks.every((m) => m.payload.ok === true && !m.error));
  beating.ws.close();
});

test('an older agent that does not announce heartbeats is not held to a lease', { timeout: 60_000 }, async () => {
  const legacy = await scriptedAgent(['browse', 'range-read']);
  assert.equal(legacy.welcome.payload.heartbeatSeconds ?? null, null);
  await sleep((LEASE + 2) * 1000);
  assert.equal(legacy.closedAt, 0);
  legacy.ws.close();
});

test('agent crash: offline is reported at once, and the restarted agent serves the same grant', { timeout: 120_000 }, async () => {
  await kill(agent);
  await until(async () => !(await online()), 5000, 'gateway noticing the crashed agent');
  const refused = await phone.get(`/v1/pcs/${pcId}/roots`);
  assert.deepEqual([refused.status, refused.body.error.code], [503, 'pc_offline']);
  agent = await startAgent(proxy, state, 'Fault PC');
  assert.equal(agent.identity.pcId, pcId, 'same PC identity after the crash');
  assert.equal(agent.identity.ownerBound, true);
  assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200);
});

test('SIMULATED sleep: a frozen agent goes offline within the lease; when it wakes it reconnects with the same grant and nothing is re-paired', { timeout: 120_000 }, async () => {
  // Freezing the process stops timers, heartbeats and reads at once, as sleep does. The machine clock and network stay up, so this is NOT
  // evidence of real sleep/resume (that needs a real PC); it proves how the relay and the agent behave around a total stop and restart.
  agent.drain();
  const before = Number(agent.identity.generation);
  freeze(agent);
  try {
    await until(async () => !(await online()), (LEASE + 6) * 1000, 'the relay marking the frozen PC offline');
    const refused = await phone.get(`/v1/pcs/${pcId}/roots`);
    assert.deepEqual([refused.status, refused.body.error.code], [503, 'pc_offline']);
    await sleep(LEASE * 1000);                                 // stay frozen well past the lease, as a laptop would
  } finally { thaw(agent); }
  await until(online, 60_000, 'the woken agent coming back online');
  const again = await agent.next('connected', 30_000);
  assert.ok(Number(again.generation) > before, 'a new connection generation after waking');
  agent.identity = again;
  assert.equal(again.pcId, pcId, 'same PC identity');
  assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200, 'the grant still works: no re-pairing');
  const roots = await phone.get(`/v1/pcs/${pcId}/roots`);
  const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
  assert.equal(page.status, 200, 'browsing works straight after waking');
});

async function prepareBig() {
  const roots = await phone.get(`/v1/pcs/${pcId}/roots`);
  const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
  const created = await phone.post('/v1/download-sessions', { pcId, entryId: page.body.entries.find((e) => e.name === 'big.bin').entryId });
  assert.equal(created.status, 201, JSON.stringify(created.body));
  return created.body;
}

test('relay crash in the middle of a download: the partial file is never passed off as complete, and a ranged resume is byte-exact', { timeout: 300_000 }, async () => {
  const total = BIG_MIB * (1 << 20);
  const first = await prepareBig();
  const hash = createHash('sha256');
  let received = 0, failed = false, killed = false;
  const response = await fetch(gateway.url + first.urlPath);
  assert.equal(response.status, 200);
  assert.equal(Number(response.headers.get('content-length')), total);
  const reader = response.body.getReader();
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      hash.update(value); received += value.length;
      if (received >= 6 * (1 << 20) && !killed) { killed = true; await kill(gateway); }      // power-cut the relay mid-stream
    }
  } catch { failed = true; }
  assert.ok(failed, 'the truncated response is an error for the client, not a clean end of file');
  assert.ok(received >= 6 * (1 << 20) && received < total, `partial (${received} of ${total})`);
  const interrupted = await agent.next('stream_finished', 30_000);
  assert.notEqual(interrupted.outcome, 'sent_to_relay', 'the PC does not report an interrupted transfer as delivered');

  agent.drain();
  gateway = await boot();                                                    // same address, same durable store
  const again = await agent.next('connected', 60_000);
  assert.equal(again.ownerBound, true);
  assert.equal((await fetch(gateway.url + first.urlPath)).status, 404, 'the old download link died with the relay');
  const second = await prepareBig();
  assert.equal(second.revision, first.revision, 'same content revision, so resuming is safe');
  const resumed = await fetch(gateway.url + second.urlPath, { headers: { range: `bytes=${received}-`, 'if-range': `"${first.revision}"` } });
  assert.equal(resumed.status, 206);
  assert.equal(resumed.headers.get('content-range'), `bytes ${received}-${total - 1}/${total}`);
  for await (const chunk of resumed.body) { hash.update(chunk); received += chunk.length; }
  assert.equal(received, total);
  assert.equal(hash.digest('hex'), bigHash, 'first part + resumed part equals the source file exactly');
});

test('graceful relay shutdown releases agents immediately', { timeout: 120_000 }, async () => {
  agent.drain();
  const started = Date.now();
  await gateway.stop();
  await agent.next('disconnected', 5000);
  assert.ok(Date.now() - started < 5000);
  gateway = await boot();
  await agent.next('connected', 60_000);
});
