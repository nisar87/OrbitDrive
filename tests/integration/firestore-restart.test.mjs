// A real gateway process on the real Firestore emulator: graceful shutdown must be CLEAN (exit 0, no exception in its log), and what was stored
// before it must be there after it starts again. Runs in the gateway workflow under `firebase emulators:exec`; a missing emulator FAILS there.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair, freePort, sleep } from './harness.mjs';

const emulator = process.env.FIRESTORE_EMULATOR_HOST;
if (!emulator && process.env.ORBIT_REQUIRE_FIRESTORE === '1') throw new Error('ORBIT_REQUIRE_FIRESTORE=1 but FIRESTORE_EMULATOR_HOST is not set: the Firestore emulator is not running.');
const run = emulator ? test : test.skip;   // skipped only on a machine without the emulator, never in CI

const project = 'demo-orbit-' + randomBytes(5).toString('hex');
const secret = randomBytes(32).toString('hex');
const port = await freePort();
const boot = () => startGateway({
  OrbitDrive__Firestore__ProjectId: project, FIRESTORE_EMULATOR_HOST: emulator, OrbitDrive__DevIdentity__Secret: secret,
}, port);
let gateway, agent, phone;
after(async () => { await agent?.stop(); await gateway?.stop(); });

/** SIGTERM, then require a CLEAN exit: status 0, not killed by a signal, and nothing exceptional in its log. Surviving via a restart does not count. */
async function shutDownCleanly(g) {
  const exited = once(g.child, 'exit');
  g.child.kill('SIGTERM');
  const [code, signal] = await Promise.race([exited, sleep(30_000).then(() => { throw new Error('gateway did not exit within 30 s of SIGTERM'); })]);
  assert.equal(signal, null, `gateway was killed by ${signal}\n${g.log}`);
  assert.equal(code, 0, `gateway exit code ${code}\n${g.log}`);
  assert.doesNotMatch(g.log, /Unhandled exception|ObjectDisposedException|System\.\w*Exception|fail:/i, `the shutdown log must be clean:\n${g.log}`);
}

run('a gateway shuts down cleanly on SIGTERM and a restart finds every grant, including a revocation made just before it', { timeout: 180_000 }, async () => {
  const root = makeFixtureRoot();
  const state = tempDir('fs-restart-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await boot();
  agent = await startAgent(gateway, state, 'Firestore PC');
  const pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  const tablet = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, tablet, 'Tablet');
  assert.equal((await phone.call('DELETE', `/v1/browser-devices/${tablet.device.id}`)).status, 200);   // revoked immediately before shutdown, while the write may still be queued

  for (let round = 1; round <= 3; round++) {
    await shutDownCleanly(gateway);
    agent.drain();
    gateway = await boot();
    const again = await agent.next('connected', 60_000);
    assert.deepEqual([again.pcId, again.ownerBound], [pcId, true], `round ${round}: same PC, still owned`);
    assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).status, 200, `round ${round}: approved browser needs no re-pairing`);
    assert.equal((await tablet.get('/v1/pcs')).status, 403, `round ${round}: the revoked browser stays revoked`);
  }
});

run('a gateway stopped in the middle of a burst of changes still shuts down cleanly and keeps the last state', { timeout: 180_000 }, async () => {
  // Many preference writes (each one a durable change), then SIGTERM while they may still be flushing to Firestore.
  const writes = Array.from({ length: 30 }, (_, i) => phone.call('PUT', '/v1/account/preferences', { historyRetentionDays: i % 2 ? 7 : 30, notifyInvitations: true, diagnosticsConsent: false }).catch(() => null));
  await Promise.race([Promise.all(writes), sleep(50)]);
  await shutDownCleanly(gateway);
  gateway = await boot();
  phone.gateway = gateway;
  const account = await phone.get('/v1/account');
  assert.equal(account.status, 200);
  assert.ok([7, 30].includes(account.body.preferences.historyRetentionDays), 'a valid last state was stored');
});
