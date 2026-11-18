// D11: the owner-visible activity history and the support bundle, against the real gateway and a real agent doing real work.
// Everything that passes through the agent uses deliberately private names; none of them may appear anywhere in the history or a bundle.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, Browser, pair, sha256, waitFor, readZip } from './harness.mjs';

const PRIVATE_NAMES = ['tax return 2025 FINAL.pdf', 'اردو فائل.txt', 'emoji 😀 notes.txt', 'diary-of-ana.txt'];
const OWNER = 'ana.private@example.test';
let gateway, agent, ipc, root, drop, state, pcId, owner, inbox, dropId;

const parse = (out) => out.match(/\(([A-Za-z0-9_-]+)\)/)[1];
const list = async (rootId, folderEntryId) => (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, folderEntryId, pageSize: 100 })).body.entries;
const rootId = async () => (await owner.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
async function download(name) {
  const entry = (await list(await rootId())).find((e) => e.name === name);
  const session = await owner.post('/v1/download-sessions', { pcId, entryId: entry.entryId });
  assert.equal(session.status, 201, JSON.stringify(session.body));
  return Buffer.from(await (await fetch(gateway.url + session.body.urlPath)).arrayBuffer());
}
const activity = async (count = 500) => (await ipc.send('activity', { count })).data.entries;
const everythingVisible = async () => {
  const state_ = JSON.stringify((await ipc.send('status')).data);
  const preview = JSON.stringify((await ipc.send('support_bundle_preview')).data);
  const history = JSON.stringify(await activity());
  const onDisk = readdirSync(join(state, 'activity')).map((f) => readFileSync(join(state, 'activity', f), 'utf8')).join('\n');
  return { history, preview, onDisk };
};

before(async () => {
  root = tempDir('diag-root'); drop = tempDir('diag-drop'); state = tempDir('diag-state');
  for (const name of PRIVATE_NAMES) writeFileSync(join(root, name), `contents of ${name}`);
  writeFileSync(join(root, 'data.bin'), Buffer.alloc(200_000, 7));
  mkdirSync(join(drop, 'Inbox'));
  agentCli(state, 'roots', 'add', 'Ana private documents', root);
  dropId = parse(agentCli(state, 'roots', 'add', 'Drop', drop));
  agentCli(state, 'roots', 'uploads', dropId, 'on', '200', '8');
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Ana laptop');
  pcId = agent.identity.pcId;
  ipc = await connectIpc(agent.ready.pipe);
  owner = await new Browser(gateway, OWNER).signIn();
  await pair(agent, owner, "Ana's private phone");
  inbox = (await list(dropId)).find((e) => e.name === 'Inbox').entryId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

describe('what the history records', () => {
  test('connecting, pairing, sharing, downloads and uploads are all recorded, newest first, with sizes and outcomes', async () => {
    for (const name of [...PRIVATE_NAMES, 'data.bin']) assert.ok((await download(name)).length > 0);
    const data = Buffer.from('uploaded private content');
    const started = await owner.post(`/v1/pcs/${pcId}/uploads`, { collision: 'reject', rootId: dropId, folderEntryId: inbox, name: 'my secret plan.docx', size: String(data.length) });
    assert.equal(started.status, 201, JSON.stringify(started.body));
    const put = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${started.body.uploadId}/chunk?offset=0`, { method: 'PUT', headers: owner.headers({ 'x-chunk-sha256': sha256(data), 'content-type': 'application/octet-stream' }), body: data });
    assert.equal(put.status, 200);
    assert.equal((await owner.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId: started.body.uploadId })).status, 200);
    await waitFor(async () => (await activity()).some((e) => e.kind === 'upload'), 10_000, 'the upload to be recorded');
    await waitFor(async () => (await activity()).filter((e) => e.kind === 'download').length >= 5, 10_000, 'five downloads to be recorded');

    const entries = await activity();
    const kinds = new Set(entries.map((e) => e.kind));
    for (const expected of ['agent_started', 'connected', 'pairing_started', 'pairing_requested', 'pairing_finished', 'download', 'upload']) assert.ok(kinds.has(expected), `missing ${expected}: ${[...kinds]}`);
    const times = entries.map((e) => Date.parse(e.time));
    assert.deepEqual([...times].sort((a, b) => b - a), times, 'newest first');
    const downloads = entries.filter((e) => e.kind === 'download');
    assert.ok(downloads.every((e) => /sent_to_relay|interrupted|cancelled/.test(e.detail)), JSON.stringify(downloads[0]));
    assert.ok(downloads.some((e) => Number(e.bytes) === 200_000), 'the size of the big download is recorded');
    assert.match(entries.find((e) => e.kind === 'pairing_finished').detail, /approved/);
    assert.equal(Number(entries.find((e) => e.kind === 'upload').bytes), data.length);
  });

  test('a failed upload is recorded as a failure with a code, and an owner action (revoking a browser) is recorded', async () => {
    const bad = await owner.post(`/v1/pcs/${pcId}/uploads`, { collision: 'reject', rootId: dropId, folderEntryId: inbox, name: 'big.bin', size: String(10 * 1024 * 1024 * 1024) });
    assert.ok(bad.status >= 400);
    const second = new Browser(gateway, OWNER); await second.signIn();
    await pair(agent, second, 'Second phone');
    const devices = (await ipc.send('devices')).data;
    assert.ok((await ipc.send('revoke_device', { deviceId: devices.find((d) => d.name === 'Second phone').deviceId })).ok);
    await waitFor(async () => (await activity()).some((e) => e.kind === 'browser_revoked'), 10_000, 'the revocation to be recorded');
  });
});

describe('nothing private is ever recorded', () => {
  test('no file name, folder name, path, e-mail address, browser name, token or secret appears in the history, on disk, or in a bundle preview', async () => {
    const { history, preview, onDisk } = await everythingVisible();
    const forbidden = [...PRIVATE_NAMES, 'my secret plan', 'plan.docx', 'diary', 'tax return', 'اردو', 'emoji', 'Ana private documents', "Ana's private phone", 'Second phone', 'Ana laptop',
      OWNER, 'ana.private', root, drop, state, owner.token, owner.device.secret, owner.device.id, 'private phone', 'data.bin', 'Inbox'];
    for (const [where, text] of Object.entries({ history, preview, onDisk }))
      for (const piece of forbidden) assert.ok(!text.includes(piece), `${where} contains ${JSON.stringify(piece)}`);
    assert.ok(!/[A-Za-z]:\\|\/home\/|\/tmp\//.test(history + preview + onDisk), 'no path of any kind');
  });

  test('an error event is recorded, and the private name in the request that provoked it never reaches the history', async () => {
    const before = (await activity()).length;
    const refused = await owner.post('/v1/download-sessions', { pcId, entryId: 'ce-not-a-real-entry-diary-of-ana.txt' });
    assert.ok(refused.status >= 400);
    agent.send('this-is-not-a-command');                                 // makes the agent emit a plain error event
    await waitFor(async () => (await activity()).length > before, 10_000, 'the error to be recorded');
    const text = JSON.stringify(await activity());
    assert.ok(!text.includes('diary-of-ana'));
  });
});

describe('the support bundle', () => {
  test('previewing shows what would be included and writes nothing; creating needs a clear yes', async () => {
    const supportDir = join(state, 'support');
    const preview = (await ipc.send('support_bundle_preview')).data.items;
    assert.deepEqual(preview.map((i) => i.name), ['activity.jsonl', 'about.txt', 'README.txt']);
    assert.ok(preview.every((i) => i.description.length > 10 && Number(i.bytes) > 0));
    assert.match(preview.find((i) => i.name === 'about.txt').preview, /app_version: /);
    assert.ok(!existsSync(supportDir) || readdirSync(supportDir).length === 0, 'a preview must write nothing');
    for (const args of [undefined, {}, { consent: false }, { consent: 'yes' }, { consent: 1 }]) {
      const refused = await ipc.send('support_bundle_create', args);
      assert.equal(refused.ok, false, JSON.stringify(args));
    }
    assert.ok(!existsSync(supportDir) || readdirSync(supportDir).length === 0, 'no bundle without a clear yes');
  });

  test('a created bundle is a local file holding exactly the previewed files, and nothing private', async () => {
    const created = await ipc.send('support_bundle_create', { consent: true });
    assert.equal(created.ok, true, JSON.stringify(created));
    const path = created.data.path;
    assert.ok(existsSync(path) && path.startsWith(join(state, 'support')));
    const files = new Map(readZip(readFileSync(path), { requireUtf8Flag: false }).map((entry) => [entry.name, entry.data()]));   // plain ASCII names: the UTF-8 flag only matters for the archive feature
    assert.deepEqual([...files.keys()].sort(), ['README.txt', 'about.txt', 'activity.jsonl']);
    const everything = [...files.values()].map((b) => Buffer.from(b).toString('utf8')).join('\n');
    for (const piece of [...PRIVATE_NAMES, OWNER, 'my secret plan', root, drop, state, owner.token, owner.device.secret, "Ana's private phone", 'Ana laptop', 'Ana private documents'])
      assert.ok(!everything.includes(piece), `the bundle contains ${JSON.stringify(piece)}`);
    assert.ok(everything.includes('"kind":"download"'));
    assert.match(files.get('README.txt').toString(), /not sent anywhere/);
    for (let i = 0; i < 8; i++) await ipc.send('support_bundle_create', { consent: true });
    assert.ok(readdirSync(join(state, 'support')).filter((f) => f.endsWith('.zip')).length <= 5, 'old bundles are removed');
  });

  test('clearing the history removes it', async () => {
    assert.equal((await ipc.send('clear_activity')).ok, true);
    const left = await activity();
    assert.ok(left.length <= 1, `${left.length} entries left`);
  });
});

describe('the history stays small', () => {
  test('with a tiny limit, heavy use rotates the files, keeps the newest entries, and never exceeds the ceiling', async () => {
    process.env.ORBIT_ACTIVITY_MAX_BYTES = '2048';
    const smallState = tempDir('diag-small'); agentCli(smallState, 'roots', 'add', 'Share', root);
    const small = await startAgent(gateway, smallState, 'Small PC');
    delete process.env.ORBIT_ACTIVITY_MAX_BYTES;
    const smallIpc = await connectIpc(small.ready.pipe);
    try {
      for (let i = 0; i < 150; i++) small.send('reload');                  // each reload is one recorded event
      await waitFor(async () => (await smallIpc.send('activity', { count: 5 })).data.entries.filter((e) => e.kind === 'shares_changed').length >= 5, 30_000, 'many events');
      await new Promise((r) => setTimeout(r, 1500));
      const files = readdirSync(join(smallState, 'activity'));
      const bytes = files.reduce((sum, f) => sum + statSync(join(smallState, 'activity', f)).size, 0);
      assert.ok(files.length <= 5, `${files.length} files`);
      assert.ok(bytes <= 5 * (2048 + 400), `${bytes} bytes on disk`);
      const newest = (await smallIpc.send('activity', { count: 3 })).data.entries;
      assert.ok(newest.length === 3 && newest[0].kind === 'shares_changed');
      assert.ok(files.length >= 2, 'rotation actually happened');
    } finally { smallIpc.close(); await small.stop(); }
  });
});
