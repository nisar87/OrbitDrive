// D2/W2: device management — listing, revoking a browser from the PC or from another browser, disconnecting a PC.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, writeLargeFile } from './harness.mjs';

let gateway, agent, other, ipc, otherIpc, phone, laptop, stranger, pcId, root;

async function firstRoot(browser, id = pcId) { return (await browser.get(`/v1/pcs/${id}/roots`)); }

before(async () => {
  root = makeFixtureRoot();
  await writeLargeFile(join(root, 'big.bin'), 48);
  const state = tempDir('dev-state'), otherState = tempDir('dev-state2');
  agentCli(state, 'roots', 'add', 'Share', root);
  agentCli(otherState, 'roots', 'add', 'Other', makeFixtureRoot());
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Home PC');
  other = await startAgent(gateway, otherState, 'Other PC');
  ipc = await connectIpc(agent.ready.pipe);
  otherIpc = await connectIpc(other.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  laptop = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, laptop, 'Laptop');
  await pair(other, stranger, 'Stranger phone');
});
after(async () => { ipc?.close(); otherIpc?.close(); await agent?.stop(); await other?.stop(); await gateway?.stop(); });

test('each side lists only its own devices', async () => {
  const mine = await phone.get('/v1/browser-devices');
  assert.equal(mine.status, 200);
  assert.deepEqual(mine.body.devices.map((d) => [d.name, d.current]).sort(), [['Laptop', false], ['Phone', true]]);
  assert.deepEqual(mine.body.devices[0].pcIds, [pcId]);
  assert.deepEqual((await stranger.get('/v1/browser-devices')).body.devices.map((d) => d.name), ['Stranger phone']);
  assert.deepEqual((await ipc.send('devices')).data.map((d) => d.name).sort(), ['Laptop', 'Phone']);
  assert.deepEqual((await otherIpc.send('devices')).data.map((d) => d.name), ['Stranger phone']);
  assert.equal((await fetch(`${gateway.url}/v1/browser-devices`)).status, 401);
  assert.ok(!JSON.stringify(mine.body).includes(phone.device.secret), 'device secrets are never returned');
});

test('one PC cannot revoke another PC’s device, and accounts cannot revoke each other’s', async () => {
  const reply = await otherIpc.send('revoke_device', { deviceId: phone.device.id });
  assert.deepEqual([reply.ok, reply.error], [false, 'That device is not paired with this PC.']);
  assert.equal((await ipc.send('revoke_device', { deviceId: 'no-such-device' })).ok, false);
  assert.equal((await stranger.call('DELETE', `/v1/browser-devices/${phone.device.id}`)).status, 404);
  assert.equal((await stranger.call('DELETE', `/v1/pcs/${pcId}`)).status, 404);
  assert.equal((await firstRoot(phone)).status, 200, 'the phone is unaffected by those attempts');
});

test('revoking a device at the PC cuts its running transfer and its access, and nobody else’s', async () => {
  const page = await laptop.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: (await firstRoot(laptop)).body.roots[0].rootId, pageSize: 50 });
  const big = page.body.entries.find((e) => e.name === 'big.bin');
  const session = (await laptop.post('/v1/download-sessions', { pcId, entryId: big.entryId })).body;
  const response = await fetch(gateway.url + session.urlPath);
  const reader = response.body.getReader();
  let received = (await reader.read()).value.length, cut = false;
  const laptopId = (await ipc.send('devices')).data.find((d) => d.name === 'Laptop').deviceId;
  assert.equal((await ipc.send('revoke_device', { deviceId: laptopId })).ok, true);
  try { for (;;) { const { done, value } = await reader.read(); if (done) break; received += value.length; } } catch { cut = true; }
  assert.ok(cut && received < 48 << 20, `transfer must be cut (received ${received})`);
  assert.equal((await fetch(gateway.url + session.urlPath)).status, 404);
  assert.equal((await firstRoot(laptop)).status, 404);
  assert.deepEqual((await laptop.get('/v1/pcs')).body.pcs, [], 'the browser stays signed in but sees no PC');
  assert.deepEqual((await ipc.send('devices')).data.map((d) => d.name), ['Phone']);
  assert.equal((await firstRoot(phone)).status, 200);
  assert.ok(ipc.events.some((e) => e.event === 'device_revoked'));
});

test('one browser can revoke another of the same account; the revoked one is fully locked out', async () => {
  await pair(agent, laptop, 'Laptop again');
  assert.equal((await firstRoot(laptop)).status, 200);
  assert.equal((await phone.call('DELETE', `/v1/browser-devices/${laptop.device.id}`)).status, 200);
  assert.equal((await laptop.get('/v1/pcs')).status, 403);
  // The laptop's first registration lost its PC grant earlier (revoked at the PC) and remains listed with no PCs.
  const remaining = (await phone.get('/v1/browser-devices')).body.devices.map((d) => [d.name, d.pcIds.length]).sort();
  assert.deepEqual(remaining, [['Laptop', 0], ['Phone', 1]]);
});

test('disconnecting the PC ends every grant and frees it for a new owner', async () => {
  assert.equal((await phone.call('DELETE', `/v1/pcs/${pcId}`)).status, 200);
  assert.equal((await firstRoot(phone)).status, 404);
  assert.deepEqual((await phone.get('/v1/pcs')).body.pcs, []);
  assert.deepEqual((await ipc.send('devices')).data, []);
  await pair(agent, stranger, 'New owner'); // would be refused with "different account" if ownership had not been released
  assert.equal((await firstRoot(stranger)).status, 200);
  assert.equal((await firstRoot(phone)).status, 404, 'the previous owner cannot come back without the PC approving again');
});
