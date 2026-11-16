// D1: the owner console's local channel to the agent, single-instance and lifecycle behaviour (real processes).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, agentCliRaw, connectIpc, tempDir, makeFixtureRoot, Browser, sleep } from './harness.mjs';

let gateway, agent, ipc, stateDir, root;

before(async () => {
  root = makeFixtureRoot();
  stateDir = tempDir('ipc-state');
  gateway = await startGateway();
  agent = await startAgent(null, stateDir, 'IPC PC'); // no relay configured yet
  ipc = await connectIpc(agent.ready.pipe);
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

const status = async () => (await ipc.send('status')).data;

test('a fresh agent shares nothing and is not connected anywhere', async () => {
  assert.equal(agent.ready.state, 'not_configured');
  const s = await status();
  assert.deepEqual([s.state, s.paused, s.roots.length, s.gateway, s.activeStreams], ['not_configured', false, 0, null, 0]);
  assert.equal(s.pcName, 'IPC PC');
  const pair = await ipc.send('pair');
  assert.deepEqual([pair.ok, pair.error], [false, 'Set the relay address first.']);
});

test('the command set is closed: no execution, no unknown or malformed requests', async () => {
  for (const cmd of ['exec', 'shell', 'run', 'open_read', '', 'STATUS', '__proto__']) {
    const reply = await ipc.send(cmd, { command: 'calc.exe' });
    assert.deepEqual([reply.ok, reply.error], [false, 'Unknown command.'], cmd);
  }
  assert.equal((await ipc.raw('this is not json\n')).error, 'Malformed request.');
  assert.equal((await ipc.raw('[1,2,3]\n')).error, 'Malformed request.');
  assert.equal((await ipc.raw('{"cmd":42}\n')).error, 'Malformed request.');
  assert.equal((await status()).state, 'not_configured', 'the channel survives bad input');
});

test('an oversized message ends that connection without affecting the agent', async () => {
  const noisy = await connectIpc(agent.ready.pipe);
  noisy.socket.write('x'.repeat(70_000));
  for (let i = 0; i < 50 && !noisy.closed; i++) await sleep(100);
  assert.equal(noisy.closed, true);
  assert.equal((await status()).pcName, 'IPC PC');
});

test('folders are shared only by explicit local action, with validation', async () => {
  const missing = await ipc.send('add_root', { name: 'Ghost', path: join(root, 'does-not-exist') });
  assert.equal(missing.ok, false);
  assert.match(missing.error, /Folder not found/);
  assert.equal((await ipc.send('add_root', { name: 'x'.repeat(65), path: root })).ok, false);
  assert.equal((await ipc.send('add_root', { path: root })).ok, false);
  assert.equal((await ipc.send('remove_root', { rootId: 'nope' })).error, 'That folder is not shared.');
  const added = await ipc.send('add_root', { name: 'Shared Docs', path: root });
  assert.equal(added.ok, true);
  const s = await status();
  assert.deepEqual(s.roots.map((r) => [r.name, r.readOnly, r.available]), [['Shared Docs', true, true]]);
  assert.equal(ipc.events.some((e) => e.event === 'roots_changed'), true, 'consoles are notified');
});

test('a second agent for the same user state refuses to start; ctl reaches the running one', async () => {
  const second = agentCliRaw(stateDir, 'run');
  assert.equal(second.status, 3);
  assert.match(second.stderr, /already running/);
  const viaCtl = agentCliRaw(stateDir, 'ctl', 'status');
  assert.equal(viaCtl.status, 0);
  assert.equal(JSON.parse(viaCtl.stdout).pcName, 'IPC PC');
  const elsewhere = agentCliRaw(tempDir('ipc-empty'), 'ctl', 'status');
  assert.equal(elsewhere.status, 1);
  assert.match(elsewhere.stderr, /not running/);
});

test('relay address must be HTTPS (loopback development relay excepted)', async () => {
  for (const bad of ['http://relay.example.com', 'ftp://relay.example.com', 'https://relay.example.com/path', 'https://user:pw@relay.example.com', 'not a url']) {
    assert.equal((await ipc.send('set_gateway', { gateway: bad })).ok, false, bad);
  }
  assert.equal((await status()).state, 'not_configured');
});

test('pair, approve, pause and quit through the local channel', async () => {
  const second = await connectIpc(agent.ready.pipe); // e.g. tray + window both attached
  assert.equal((await ipc.send('set_gateway', { gateway: gateway.url, webOrigin: 'http://localhost:5173' })).ok, true);
  for (let i = 0; i < 100 && (await status()).state !== 'online'; i++) await sleep(100);
  assert.equal((await status()).state, 'online');

  const pairing = (await ipc.send('pair')).data;
  assert.match(pairing.url, /^http:\/\/localhost:5173\/pair#id=[A-Z2-9]{6}&s=[A-Z2-9]{10}$/);
  assert.equal((await status()).activePairing.code, pairing.code);
  assert.equal((await ipc.send('approve')).error, 'Nothing is waiting for approval.');

  const browser = await new Browser(gateway, 'owner@example.test').signIn();
  const [id, secret] = pairing.code.split('-');
  const claim = await browser.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'Pixel' });
  assert.equal(claim.status, 200);
  for (let i = 0; i < 50 && !(await status()).pendingApproval; i++) await sleep(100);
  const pending = (await status()).pendingApproval;
  assert.deepEqual([pending.accountLabel, pending.browserName, pending.confirmationCode], ['owner@example.test', 'Pixel', claim.body.confirmationCode]);
  assert.ok(second.events.some((e) => e.event === 'pairing_claimed'), 'every attached console sees the approval prompt');

  assert.equal((await ipc.send('approve')).data.state, 'approved');
  const after = await status();
  assert.deepEqual([after.pendingApproval, after.activePairing], [null, null]);
  browser.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
  const pcId = after.pcId;
  const roots = await browser.get(`/v1/pcs/${pcId}/roots`);
  assert.deepEqual(roots.body.roots.map((r) => r.name), ['Shared Docs'], 'the folder shared through the console is what the phone sees');

  await ipc.send('pause');
  assert.equal((await status()).state, 'paused');
  assert.equal((await browser.get(`/v1/pcs/${pcId}/roots`)).status, 503);
  await ipc.send('resume');
  assert.equal((await browser.get(`/v1/pcs/${pcId}/roots`)).status, 200);

  // Removing the folder in the console takes effect immediately, with no restart.
  await ipc.send('remove_root', { rootId: after.roots[0].rootId });
  assert.equal((await browser.get(`/v1/pcs/${pcId}/roots`)).body.roots.length, 0);

  assert.equal((await ipc.send('quit')).ok, true);
  const [code] = await once(agent.child, 'exit');
  assert.equal(code, 0, 'Quit ends the worker cleanly');
  assert.equal((await browser.get('/v1/pcs')).body.pcs[0].online, false, 'Quit ends remote access');
  second.close();
});

test('after a restart the saved relay address and identity are reused; the lock is free again', async () => {
  agent = await startAgent(null, stateDir, 'IPC PC');
  ipc = await connectIpc(agent.ready.pipe);
  for (let i = 0; i < 100 && (await status()).state !== 'online'; i++) await sleep(100);
  const s = await status();
  assert.deepEqual([s.state, s.gateway], ['online', gateway.url]);
  assert.equal(existsSync(join(stateDir, 'identity.bin')), true);
});
