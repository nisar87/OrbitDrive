// D2: the owner renames this PC; the relay and paired devices show the new name, it survives a restart, and bad names are refused.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, agentCliRaw, connectIpc, tempDir, makeFixtureRoot, Browser, pair, waitFor } from './harness.mjs';

let gateway, agent, ipc, browser, state;
before(async () => {
  state = tempDir('rename-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Old Name');
  ipc = await connectIpc(agent.ready.pipe);
  browser = await new Browser(gateway, 'rename@example.test').signIn();
  await pair(agent, browser);
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

const shownName = async () => (await browser.get('/v1/pcs')).body.pcs.find((p) => p.pcId === agent.identity.pcId)?.name;

test('renaming the PC shows the new name to paired devices and in the status', async () => {
  assert.equal(await shownName(), 'Old Name');
  assert.equal((await ipc.send('set_pc_name', { name: '  Kitchen PC  ' })).ok, true);
  await waitFor(async () => (await shownName()) === 'Kitchen PC', 15000, 'the relay shows the new name');
  assert.equal((await ipc.send('status')).data.pcName, 'Kitchen PC');
});

test('an empty name goes back to the computer name', async () => {
  await ipc.send('set_pc_name', { name: '' });
  await waitFor(async () => (await shownName()) === 'Old Name', 15000, 'the original name returns');
});

test('names with control characters or an absurd length are refused and change nothing', async () => {
  const control = await ipc.send('set_pc_name', { name: 'bad\u0007name' });
  assert.equal(control.ok, false); assert.match(control.error, /control/);
  const long = await ipc.send('set_pc_name', { name: 'x'.repeat(61) });
  assert.equal(long.ok, false); assert.match(long.error, /at most 60/);
  assert.equal((await ipc.send('status')).data.pcName, 'Old Name');
});

test('a unicode name survives and is stored as plain text', async () => {
  await ipc.send('set_pc_name', { name: 'دفتر 💻 <b>PC</b>' });
  await waitFor(async () => (await shownName()) === 'دفتر 💻 <b>PC</b>', 15000, 'unicode shown exactly as typed');
});
