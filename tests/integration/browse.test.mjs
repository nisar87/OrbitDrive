// D4: owner-only folder options and disk binding, through the real agent, gateway and local channel.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, repoRoot } from './harness.mjs';

let gateway, agent, ipc, phone, pcId, state, root, rootId;
const configPath = () => join(state, 'config.json');
const listing = async () => (await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, pageSize: 200 }));

before(async () => {
  root = makeFixtureRoot();
  writeFileSync(join(root, '.private-notes'), 'hidden');
  if (process.platform === 'win32') execFileSync('attrib', ['+h', join(root, '.private-notes')]);
  state = tempDir('browse-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Browse PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, phone, 'Phone');
  rootId = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

test('hidden items are private until the PC owner opts in for that folder; the browser cannot change it', async () => {
  assert.ok(!(await listing()).body.entries.some((e) => e.name === '.private-notes'), 'not listed by default');
  assert.deepEqual((await ipc.send('status')).data.roots.map((r) => r.showHidden), [false]);

  // There is no remote instruction that changes what a PC shares: every folder-changing command is local only.
  const schema = JSON.parse(readFileSync(join(repoRoot, 'packages', 'protocol', 'schema', 'protocol.schema.json'), 'utf8'));
  const remoteActions = JSON.stringify(schema).match(/"(add_root|remove_root|set_root_hidden|set_gateway)"/g);
  assert.equal(remoteActions, null);
  for (const path of [`/v1/pcs/${pcId}/roots`, `/v1/pcs/${pcId}/roots/${rootId}`]) {
    for (const method of ['POST', 'PUT', 'PATCH']) {
      const attempt = await phone.call(method, path, { showHidden: true, name: 'x', path: '/' });
      assert.ok([404, 405].includes(attempt.status), `${method} ${path} -> ${attempt.status}`);
    }
  }

  assert.equal((await ipc.send('set_root_hidden', { rootId, showHidden: true })).ok, true);
  await agent.next('roots_changed');
  const shown = (await listing()).body.entries.find((e) => e.name === '.private-notes');
  assert.equal(shown?.hidden, true, 'listed and marked hidden after the owner opts in');
  assert.ok((await listing()).body.entries.filter((e) => e.name !== '.private-notes').every((e) => e.hidden === false));
  assert.equal(JSON.parse(readFileSync(configPath(), 'utf8')).Roots[0].ShowHidden, true, 'the choice is saved');
  const bad = await ipc.send('set_root_hidden', { rootId: 'nope', showHidden: true });
  assert.deepEqual([bad.ok, bad.error], [false, 'That folder is not shared.']);

  assert.equal((await ipc.send('set_root_hidden', { rootId, showHidden: false })).ok, true);
  assert.ok(!(await listing()).body.entries.some((e) => e.name === '.private-notes'), 'private again');
});

test('a shared folder is tied to the disk it was approved on', { timeout: 120_000 }, async () => {
  const saved = JSON.parse(readFileSync(configPath(), 'utf8'));
  const original = saved.Roots[0].VolumeId;
  assert.match(original ?? '', /^(vol-[0-9A-F]{8}|dev-\d+:\d+)$/, 'the disk identity was recorded when the folder was shared');
  const file = (await listing()).body.entries.find((e) => e.name === 'hello.txt');
  const prepared = await phone.post('/v1/download-sessions', { pcId, entryId: file.entryId });
  assert.equal(prepared.status, 201);

  // Simulate "another disk is now at this path": the stored identity no longer matches the real one.
  ipc.close(); await agent.stop();
  saved.Roots[0].VolumeId = original.startsWith('vol-') ? 'vol-0BADD15C' : 'dev-999:999';
  writeFileSync(configPath(), JSON.stringify(saved));
  agent = await startAgent(gateway, state, 'Browse PC');
  ipc = await connectIpc(agent.ready.pipe);
  assert.deepEqual((await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots.map((r) => [r.rootId, r.available]), [[rootId, false]]);
  assert.deepEqual((await ipc.send('status')).data.roots.map((r) => r.available), [false]);
  const refused = await listing();
  assert.equal(refused.status, 503);
  assert.match(refused.body.error.message, /different disk/);
  assert.equal((await phone.post('/v1/download-sessions', { pcId, entryId: file.entryId })).status, 503, 'no download from the other disk');
  assert.equal(JSON.parse(readFileSync(configPath(), 'utf8')).Roots[0].VolumeId, saved.Roots[0].VolumeId, 'a mismatch is never silently re-bound');

  // A folder shared by an older version (no identity stored) is bound once, to the disk it is on now.
  ipc.close(); await agent.stop();
  delete saved.Roots[0].VolumeId;
  writeFileSync(configPath(), JSON.stringify(saved));
  agent = await startAgent(gateway, state, 'Browse PC');
  ipc = await connectIpc(agent.ready.pipe);
  assert.equal(JSON.parse(readFileSync(configPath(), 'utf8')).Roots[0].VolumeId, original);
  assert.equal((await listing()).status, 200);
});
