// D8/W8: folder and multi-selection ZIPs (staged, resumable), filename search, entry checks and two PCs — real gateway and agent processes.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, readFileSync, readdirSync, mkdirSync, linkSync, existsSync, renameSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, connectIpc, agentCli, waitFor, tempDir, Browser, pair, readZip, sha256 } from './harness.mjs';

let gateway, agent, ipc, phone, other, stranger, pcId, rootId, root, state;
const deep = ['Photos', 'ünï 文書', 'a', 'b', 'c', 'd', 'e', 'f'];

const list = async (browser, entryId = null, pc = pcId, root_ = rootId) => (await browser.post(`/v1/pcs/${pc}/directory-queries`, { rootId: root_, entryId, pageSize: 200 }));
const entry = async (browser, name, parent = null, pc = pcId, root_ = rootId) => {
  const page = await list(browser, parent, pc, root_);
  assert.equal(page.status, 200, JSON.stringify(page.body));
  const found = page.body.entries.find((e) => e.name === name);
  assert.ok(found, `${name} is not listed`);
  return found;
};
const startArchive = (browser, ids, strict = false, pc = pcId) => browser.post(`/v1/pcs/${pc}/archives`, { entryIds: ids, strict });
const status = (browser, id, pc = pcId) => browser.post(`/v1/pcs/${pc}/archive-status`, { archiveId: id });
const finished = async (browser, id, pc = pcId) => {
  let last;
  await waitFor(async () => { last = await status(browser, id, pc); return last.status !== 200 || ['ready', 'failed', 'cancelled'].includes(last.body.state); }, 60000, 'the archive to finish');
  return last;
};
const download = async (browser, archiveId, headers = {}) => {
  const session = await browser.post('/v1/download-sessions', { pcId, entryId: archiveId, variant: 'archive' });
  assert.equal(session.status, 201, JSON.stringify(session.body));
  const response = await fetch(gateway.url + session.body.urlPath, { headers });
  return { session: session.body, response, bytes: Buffer.from(await response.arrayBuffer()) };
};
const stagedFiles = () => (existsSync(join(state, 'archives')) ? readdirSync(join(state, 'archives')) : []);

before(async () => {
  root = tempDir('archive-root');
  state = tempDir('archive-state');
  mkdirSync(join(root, ...deep), { recursive: true });
  writeFileSync(join(root, 'Photos', 'cover.jpg'), randomBytes(150_000));
  writeFileSync(join(root, 'Photos', 'notes é.txt'), 'hello\n'.repeat(4000));
  writeFileSync(join(root, ...deep, 'leaf.bin'), randomBytes(90_000));
  mkdirSync(join(root, 'Photos', 'Empty'));
  writeFileSync(join(root, 'loose.txt'), 'a loose file');
  writeFileSync(join(root, 'report 2023.txt'), 'x');
  mkdirSync(join(root, 'Work'));
  writeFileSync(join(root, 'Work', 'Report Final.docx'), 'x');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Archive PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  other = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, other, 'Other');
  rootId = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

test('a folder and a file become one archive; an independent unzip equals the originals; resume works with ranges', async () => {
  const photos = await entry(phone, 'Photos'); const loose = await entry(phone, 'loose.txt');
  const started = await startArchive(phone, [photos.entryId, loose.entryId]);
  assert.equal(started.status, 202, JSON.stringify(started.body));
  const done = await finished(phone, started.body.archiveId);
  assert.equal(done.body.state, 'ready', JSON.stringify(done.body));
  assert.equal(done.body.omittedCount, 0);
  assert.equal(done.body.resumable, true);
  assert.equal(done.body.filesDone, done.body.filesTotal);

  const { session, response, bytes } = await download(phone, started.body.archiveId);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('content-type'), 'application/octet-stream', 'a download, never rendered by the browser');
  assert.match(response.headers.get('content-disposition'), /attachment/);
  assert.equal(session.revision, 'sha256-' + sha256(bytes), 'the hash promised is the hash of the bytes delivered');
  assert.equal(Number(session.size), bytes.length);

  const entries = readZip(bytes);
  const byName = Object.fromEntries(entries.map((e) => [e.name, e]));
  assert.deepEqual(Object.keys(byName).filter((n) => !n.endsWith('/')).sort(), [
    'Photos/cover.jpg', `Photos/${deep.slice(1).join('/')}/leaf.bin`, 'Photos/notes é.txt', 'loose.txt',
  ].sort());
  assert.ok(byName['Photos/Empty/'], 'an empty folder is kept');
  assert.equal(sha256(byName['Photos/cover.jpg'].data()), sha256(readFileSync(join(root, 'Photos', 'cover.jpg'))));
  assert.equal(sha256(byName[`Photos/${deep.slice(1).join('/')}/leaf.bin`].data()), sha256(readFileSync(join(root, ...deep, 'leaf.bin'))));
  assert.equal(byName['Photos/notes é.txt'].data().toString(), 'hello\n'.repeat(4000));
  assert.ok(byName['Photos/notes é.txt'].method === 8 && byName['Photos/cover.jpg'].method === 0, 'text is deflated, an already-compressed picture is stored');

  // Resume: the same archive can be fetched in pieces (what a resumed save does).
  const half = Math.floor(bytes.length / 2);
  const first = await fetch(gateway.url + session.urlPath, { headers: { range: `bytes=0-${half - 1}` } });
  const rest = await fetch(gateway.url + session.urlPath, { headers: { range: `bytes=${half}-` } });
  assert.equal(first.status, 206); assert.equal(rest.status, 206);
  assert.ok(Buffer.concat([Buffer.from(await first.arrayBuffer()), Buffer.from(await rest.arrayBuffer())]).equals(bytes));
  assert.equal((await fetch(gateway.url + session.urlPath, { headers: { range: `bytes=${bytes.length + 5}-` } })).status, 416);
  assert.ok(stagedFiles().length >= 1);

  // The originals were never touched.
  assert.equal(readFileSync(join(root, 'loose.txt'), 'utf8'), 'a loose file');
  const cancelled = await phone.post(`/v1/pcs/${pcId}/archive-cancel`, { archiveId: started.body.archiveId });
  assert.equal(cancelled.status, 200);
  await waitFor(() => stagedFiles().length === 0, 10000, 'the staged archive to be removed');
  assert.notEqual((await status(phone, started.body.archiveId)).status, 200, 'a discarded archive is gone');
});

test('an item that cannot be included is reported, carried in a report file, and strict mode refuses to finish', async () => {
  mkdirSync(join(root, 'Mixed'), { recursive: true });
  writeFileSync(join(root, 'Mixed', 'fine.txt'), 'fine');
  writeFileSync(join(root, 'Mixed', 'original.txt'), 'two names');
  linkSync(join(root, 'Mixed', 'original.txt'), join(root, 'Mixed', 'hardlinked.txt'));      // hard-linked files are never shared (D4)
  const mixed = await entry(phone, 'Mixed');
  const normal = await startArchive(phone, [mixed.entryId]);
  const done = await finished(phone, normal.body.archiveId);
  assert.equal(done.body.state, 'ready');
  assert.ok(done.body.omittedCount >= 1, JSON.stringify(done.body));
  assert.ok(done.body.omitted.some((o) => o.path.startsWith('Mixed/')));
  const { bytes } = await download(phone, normal.body.archiveId);
  const names = readZip(bytes).map((e) => e.name);
  assert.ok(names.includes('Mixed/fine.txt'));
  assert.ok(names.includes('_OrbitDrive_omitted.txt'), 'whoever unzips it is told it is incomplete');
  assert.ok(!names.includes('Mixed/hardlinked.txt') && !names.includes('Mixed/original.txt'));
  assert.match(readZip(bytes).find((e) => e.name === '_OrbitDrive_omitted.txt').data().toString(), /incomplete/);

  const strict = await startArchive(phone, [mixed.entryId], true);
  const failed = await finished(phone, strict.body.archiveId);
  assert.equal(failed.body.state, 'failed');
  assert.match(failed.body.error, /partial archives are not allowed/);
  const refused = await phone.post('/v1/download-sessions', { pcId, entryId: strict.body.archiveId, variant: 'archive' });
  assert.notEqual(refused.status, 201);
  await phone.post(`/v1/pcs/${pcId}/archive-cancel`, { archiveId: normal.body.archiveId });
  await phone.post(`/v1/pcs/${pcId}/archive-cancel`, { archiveId: strict.body.archiveId });
  await waitFor(() => stagedFiles().length === 0, 10000, 'staging to be empty');
});

test('forged, foreign and unauthorised references never start, show or serve an archive', async () => {
  const loose = await entry(phone, 'loose.txt');
  assert.equal((await startArchive(stranger, [loose.entryId])).status, 403, 'another account cannot use this PC');
  const bad = await startArchive(phone, ['AAAA']);
  assert.equal(bad.status, 400);
  assert.equal((await startArchive(phone, [])).status, 400);
  const started = await startArchive(phone, [loose.entryId]);
  await finished(phone, started.body.archiveId);
  assert.equal((await status(stranger, started.body.archiveId)).status, 403);
  assert.equal((await stranger.post('/v1/download-sessions', { pcId, entryId: started.body.archiveId, variant: 'archive' })).status, 403);
  // An archive is not an entry and an entry is not an archive.
  assert.equal((await status(phone, loose.entryId)).status, 400);
  assert.notEqual((await phone.post('/v1/download-sessions', { pcId, entryId: loose.entryId, variant: 'archive' })).status, 201);
  // Only the closed set of variants is accepted.
  assert.equal((await phone.post('/v1/download-sessions', { pcId, entryId: started.body.archiveId, variant: 'zip' })).status, 400);
  // A revoked browser stops at once.
  const devices = (await ipc.send('devices')).data;
  const revokedId = other.device.id;
  assert.equal((await status(other, started.body.archiveId)).status, 200);
  assert.ok((await ipc.send('revoke_device', { deviceId: devices.find((d) => d.deviceId === revokedId).deviceId })).ok);
  const denied = (r) => r.status >= 400 && r.status < 500;
  assert.ok(denied(await status(other, started.body.archiveId)));
  assert.ok(denied(await startArchive(other, [loose.entryId])));
  await phone.post(`/v1/pcs/${pcId}/archive-cancel`, { archiveId: started.body.archiveId });
});

test('removing the shared folder ends its archives and downloads; nothing stays staged', async () => {
  const loose = await entry(phone, 'loose.txt');
  const started = await startArchive(phone, [loose.entryId]);
  await finished(phone, started.body.archiveId);
  const session = await phone.post('/v1/download-sessions', { pcId, entryId: started.body.archiveId, variant: 'archive' });
  assert.equal(session.status, 201);
  assert.ok((await ipc.send('remove_root', { rootId })).ok);
  const refused = await fetch(gateway.url + session.body.urlPath);
  assert.notEqual(refused.status, 200, 'the archive is not served once the folder is no longer shared');
  assert.notEqual((await status(phone, started.body.archiveId)).status, 200);
  await waitFor(() => stagedFiles().length === 0, 10000, 'staging to be empty');
  agentCli(state, 'roots', 'add', 'Share', root);
});
