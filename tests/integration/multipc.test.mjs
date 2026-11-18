// W8/D8: one browser paired with several PCs keeps ONE device that works with all of them; PCs stay isolated from each other.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, Browser, pair } from './harness.mjs';

let gateway, agentA, agentB, ipcB, browser, pcA, pcB, stateA, stateB;
before(async () => {
  const dirA = tempDir('mp-a'), dirB = tempDir('mp-b'); stateA = tempDir('mp-state-a'); stateB = tempDir('mp-state-b');
  writeFileSync(join(dirA, 'same.txt'), 'A'); writeFileSync(join(dirB, 'same.txt'), 'B');
  agentCli(stateA, 'roots', 'add', 'Docs', dirA); agentCli(stateB, 'roots', 'add', 'Docs', dirB);
  gateway = await startGateway();
  agentA = await startAgent(gateway, stateA, 'Tower'); agentB = await startAgent(gateway, stateB, 'Laptop');
  ipcB = await connectIpc(agentB.ready.pipe);
  pcA = agentA.identity.pcId; pcB = agentB.identity.pcId;
  browser = await new Browser(gateway, 'owner@example.test').signIn();
});
after(async () => { ipcB?.close(); await agentA?.stop(); await agentB?.stop(); await gateway?.stop(); });

const firstRoot = async (pc) => (await browser.get(`/v1/pcs/${pc}/roots`));

test('pairing a second PC adds it to the same browser device; both PCs work with one credential', async () => {
  await pair(agentA, browser, 'My phone');
  const first = { ...browser.device };
  await pair(agentB, browser, 'My phone');
  assert.deepEqual(browser.device, first, 'the device credential did not change');
  const pcs = (await browser.get('/v1/pcs')).body.pcs;
  assert.deepEqual(pcs.map((p) => p.name).sort(), ['Laptop', 'Tower']);
  const a = await firstRoot(pcA), b = await firstRoot(pcB);
  assert.equal(a.status, 200); assert.equal(b.status, 200);
  assert.notEqual(a.body.roots[0].rootId, b.body.roots[0].rootId, 'two PCs sharing a folder with the same name have different folder ids');
  const listA = await browser.post(`/v1/pcs/${pcA}/directory-queries`, { rootId: a.body.roots[0].rootId, entryId: null, pageSize: 20 });
  const listB = await browser.post(`/v1/pcs/${pcB}/directory-queries`, { rootId: b.body.roots[0].rootId, entryId: null, pageSize: 20 });
  assert.equal(listA.body.entries[0].name, listB.body.entries[0].name);
  assert.notEqual(listA.body.entries[0].entryId, listB.body.entries[0].entryId);
  // Each PC's identifiers are only good on that PC.
  assert.equal((await browser.post(`/v1/pcs/${pcB}/directory-queries`, { rootId: b.body.roots[0].rootId, entryId: listA.body.entries[0].entryId, pageSize: 5 })).status, 400);
  assert.notEqual((await browser.post('/v1/download-sessions', { pcId: pcB, entryId: listA.body.entries[0].entryId })).status, 201);
});

test('a PC owner removing this browser from one PC leaves the other PC working', async () => {
  const devices = (await ipcB.send('devices')).data;
  assert.equal(devices.length, 1);
  assert.ok((await ipcB.send('revoke_device', { deviceId: devices[0].deviceId })).ok);
  assert.notEqual((await firstRoot(pcB)).status, 200);
  assert.equal((await firstRoot(pcA)).status, 200);
  assert.deepEqual((await browser.get('/v1/pcs')).body.pcs.map((p) => p.name), ['Tower']);
});

test('denying another PC\'s pairing leaves the browser and its other PC untouched', async () => {
  agentB.send('pair');
  const pairing = await agentB.next('pairing');
  const fragment = new URLSearchParams(new URL(pairing.url).hash.slice(1));
  const claim = await browser.post(`/v1/pairing-requests/${fragment.get('id')}/claim`, { secret: fragment.get('s'), browserName: 'My phone' });
  assert.equal(claim.status, 200);
  await agentB.next('pairing_claimed');
  agentB.send('deny'); await agentB.next('pairing_result');
  assert.equal((await firstRoot(pcA)).status, 200, 'the device survived a denied pairing');
  assert.notEqual((await firstRoot(pcB)).status, 200);
});
