// D9/W9: uploads into a folder the owner enabled — real gateway and agent processes, real files, nothing mocked.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { writeFileSync, readFileSync, readdirSync, mkdirSync, existsSync, statSync, createReadStream } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { startGateway, startAgent, connectIpc, agentCli, waitFor, tempDir, Browser, pair, sha256, rssBytes, kill, sleep, repoRoot } from './harness.mjs';

const LARGE_MIB = Number(process.env.ORBIT_LARGE_MIB || 48);
let gateway, agent, ipc, phone, other, stranger, pcId, dropId, docsId, drop, docs, state, inboxEntry, privateEntry;
const partial = () => join(drop, '.orbitdrive-partial');
const partialFiles = () => (existsSync(partial()) ? readdirSync(partial()) : []);

const entry = async (browser, rootId, name, parent = null) => {
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: parent, pageSize: 200 });
  assert.equal(page.status, 200, JSON.stringify(page.body));
  const found = page.body.entries.find((e) => e.name === name);
  assert.ok(found, `${name} is not listed`);
  return found;
};
const startUpload = (browser, body, pc = pcId) => browser.post(`/v1/pcs/${pc}/uploads`, { collision: 'reject', ...body });
const begin = async (browser, name, size, extra = {}) => {
  const started = await startUpload(browser, { rootId: dropId, folderEntryId: inboxEntry, name, size: String(size), ...extra });
  assert.equal(started.status, 201, JSON.stringify(started.body));
  return started.body;
};
const statusOf = (browser, uploadId) => browser.post(`/v1/pcs/${pcId}/upload-status`, { uploadId });
const finish = (browser, uploadId) => browser.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId });
const cancel = (browser, uploadId) => browser.post(`/v1/pcs/${pcId}/upload-cancel`, { uploadId });

async function putChunk(browser, uploadId, offset, data, { sha, pc = pcId, headers = {} } = {}) {
  const response = await fetch(`${gateway.url}/v1/pcs/${pc}/uploads/${uploadId}/chunk?offset=${offset}`, {
    method: 'PUT', headers: browser.headers({ 'x-chunk-sha256': sha ?? sha256(data), 'content-type': 'application/octet-stream', ...headers }), body: data,
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}
async function sendAll(browser, uploadId, data, chunk) {
  for (let offset = 0; offset < data.length; offset += chunk) {
    const part = data.subarray(offset, Math.min(offset + chunk, data.length));
    const result = await putChunk(browser, uploadId, offset, part);
    assert.equal(result.status, 200, JSON.stringify(result.body));
    assert.equal(result.body.accepted, true);
  }
}
const randomData = (n, seed = 1) => { const b = Buffer.alloc(n); let h = createHash('sha256').update('seed' + seed).digest(); for (let o = 0; o < n; o += 32) { h = createHash('sha256').update(h).digest(); h.copy(b, o, 0, Math.min(32, n - o)); } return b; };
const setUploads = async (rootId, enabled, quotaMiB = 200, maxFileMiB = 100) => {
  const reply = await ipc.send('set_root_uploads', { rootId, enabled, quotaMiB, maxFileMiB });
  assert.equal(reply.ok, true, JSON.stringify(reply));
};
const names = (dir) => readdirSync(dir).sort();

before(async () => {
  drop = tempDir('upload-drop'); docs = tempDir('upload-docs'); state = tempDir('upload-state');
  mkdirSync(join(drop, 'Inbox')); mkdirSync(join(docs, 'Private'));
  writeFileSync(join(docs, 'Private', 'secret.txt'), 'read only');
  const parse = (out) => out.match(/\(([A-Za-z0-9_-]+)\)/)[1];
  dropId = parse(agentCli(state, 'roots', 'add', 'Drop', drop));
  docsId = parse(agentCli(state, 'roots', 'add', 'Docs', docs));
  assert.match(agentCli(state, 'roots', 'uploads', dropId, 'on', '200', '100'), /Uploads are ON/);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Upload PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  other = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, other, 'Other');
  inboxEntry = (await entry(phone, dropId, 'Inbox')).entryId;
  privateEntry = (await entry(phone, docsId, 'Private')).entryId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

describe('which folders accept uploads', () => {
  test('only the folder the owner enabled is offered for upload; the other stays read-only', async () => {
    const roots = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots;
    const dropRoot = roots.find((r) => r.rootId === dropId), docsRoot = roots.find((r) => r.rootId === docsId);
    assert.deepEqual([dropRoot.canUpload, dropRoot.readOnly], [true, false]);
    assert.deepEqual([docsRoot.canUpload, docsRoot.readOnly], [false, true]);
    assert.equal(dropRoot.maxUploadBytes, String(100 * 2 ** 20));
    assert.equal(dropRoot.uploadQuotaLeftBytes, String(200 * 2 ** 20));
    assert.equal(docsRoot.maxUploadBytes, '0');
  });

  test('a hidden endpoint cannot write a read-only folder, with a real or a forged folder id', async () => {
    const attempts = [
      { rootId: docsId, folderEntryId: privateEntry, name: 'evil.txt', size: '3' },
      { rootId: docsId, folderEntryId: null, name: 'evil.txt', size: '3' },
      { rootId: dropId, folderEntryId: privateEntry, name: 'evil.txt', size: '3' },     // a folder of the read-only share, claimed to be in the Drop share
      { rootId: docsId, folderEntryId: inboxEntry, name: 'evil.txt', size: '3' },
    ];
    for (const attempt of attempts) {
      const result = await startUpload(phone, attempt);
      assert.ok([400, 403].includes(result.status), `${JSON.stringify(attempt)} -> ${result.status} ${JSON.stringify(result.body)}`);
    }
    assert.deepEqual(names(join(docs, 'Private')), ['secret.txt']);
    assert.deepEqual(names(docs), ['Private']);
    assert.equal(existsSync(join(docs, '.orbitdrive-partial')), false);
    for (const forged of ['', 'x', 'e'.repeat(300), '../../etc', Buffer.from('{"t":"e"}').toString('base64url')]) {
      const result = await startUpload(phone, { rootId: dropId, folderEntryId: forged, name: 'f.txt', size: '1' });
      assert.ok([400, 403, 404].includes(result.status), `forged ${forged.slice(0, 20)} -> ${result.status}`);
    }
    assert.deepEqual(names(join(drop, 'Inbox')), []);
  });

  test('a signed-out caller, another account, and an unpaired call are refused before the PC is asked', async () => {
    const body = { rootId: dropId, folderEntryId: inboxEntry, name: 'x.txt', size: '1', collision: 'reject' };
    const anonymous = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
    assert.equal(anonymous.status, 401);
    assert.ok([403, 404].includes((await startUpload(stranger, body)).status));
    const real = await begin(phone, 'mine.bin', 4);
    const chunk = await putChunk(stranger, real.uploadId, 0, Buffer.from([1, 2, 3, 4]));
    assert.ok([401, 403, 404].includes(chunk.status), `stranger chunk ${chunk.status}`);
    await cancel(phone, real.uploadId);
    assert.deepEqual(names(join(drop, 'Inbox')), []);
  });
});

describe('receiving a file', () => {
  test('a multi-piece upload arrives byte for byte, is committed only at the end, and leaves nothing behind', async () => {
    const data = randomData(5 * 2 ** 20 + 1234, 11);
    const started = await begin(phone, 'holiday photo.jpg', data.length);
    assert.equal(started.chunkBytes, 2 * 2 ** 20);
    assert.deepEqual(started.destination, ['Drop', 'Inbox']);
    await sendAll(phone, started.uploadId, data.subarray(0, 4 * 2 ** 20), started.chunkBytes);
    assert.equal(existsSync(join(drop, 'Inbox', 'holiday photo.jpg')), false, 'nothing under the real name before the commit');
    assert.equal(partialFiles().length, 1);
    const listing = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: dropId, entryId: null, pageSize: 200 });
    assert.deepEqual(listing.body.entries.map((e) => e.name), ['Inbox']);        // the private folder is never listed
    assert.equal(listing.body.skippedLinks, 0);
    const mid = await statusOf(phone, started.uploadId);
    assert.deepEqual([mid.body.state, mid.body.committedBytes], ['receiving', String(4 * 2 ** 20)]);
    for (let offset = 4 * 2 ** 20; offset < data.length; offset += started.chunkBytes) {
      const result = await putChunk(phone, started.uploadId, offset, data.subarray(offset, Math.min(offset + started.chunkBytes, data.length)));
      assert.equal(result.body.accepted, true);
    }
    const done = await finish(phone, started.uploadId);
    assert.equal(done.status, 200, JSON.stringify(done.body));
    assert.deepEqual([done.body.state, done.body.finalName, done.body.renamed], ['committed', 'holiday photo.jpg', false]);
    assert.equal(done.body.sha256, sha256(data));
    assert.equal(sha256(readFileSync(join(drop, 'Inbox', 'holiday photo.jpg'))), sha256(data));
    assert.equal(existsSync(partial()), false);
    assert.equal(done.body.originMarked, process.platform === 'win32');
    const again = await finish(phone, started.uploadId);                         // the commit is idempotent
    assert.deepEqual([again.status, again.body.finalName, again.body.sha256], [200, 'holiday photo.jpg', sha256(data)]);
    assert.deepEqual(names(join(drop, 'Inbox')), ['holiday photo.jpg']);
    const roots = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots.find((r) => r.rootId === dropId);
    assert.equal(roots.uploadQuotaLeftBytes, String(200 * 2 ** 20 - data.length));   // counted once
  });

  test('an empty file and a file into the top of the shared folder', async () => {
    const empty = await startUpload(phone, { rootId: dropId, folderEntryId: null, name: 'empty.txt', size: '0' });
    assert.equal(empty.status, 201);
    assert.equal((await finish(phone, empty.body.uploadId)).status, 200);
    assert.equal(statSync(join(drop, 'empty.txt')).size, 0);
    assert.deepEqual(empty.body.destination, ['Drop']);
  });

  test('a damaged piece, a repeated piece and a skipped piece are never stored, and the upload still completes', async () => {
    const data = randomData(3000, 21);
    const started = await begin(phone, 'careful.bin', data.length);
    const bad = Buffer.from(data.subarray(0, 1000)); bad[7] ^= 0xff;
    const damaged = await putChunk(phone, started.uploadId, 0, bad, { sha: sha256(data.subarray(0, 1000)) });
    assert.deepEqual([damaged.status, damaged.body.error.code], [422, 'checksum_mismatch']);
    assert.equal((await statusOf(phone, started.uploadId)).body.committedBytes, '0');
    assert.equal(statSync(join(partial(), partialFiles()[0])).size, 0);
    assert.equal((await putChunk(phone, started.uploadId, 0, data.subarray(0, 1000))).body.accepted, true);
    const repeated = await putChunk(phone, started.uploadId, 0, data.subarray(0, 1000));
    assert.deepEqual([repeated.status, repeated.body.accepted, repeated.body.status.committedBytes], [200, false, '1000']);
    const skipped = await putChunk(phone, started.uploadId, 2000, data.subarray(2000));
    assert.deepEqual([skipped.status, skipped.body.accepted, skipped.body.status.committedBytes], [200, false, '1000']);
    assert.equal(statSync(join(partial(), partialFiles()[0])).size, 1000);
    const early = await finish(phone, started.uploadId);
    assert.deepEqual([early.status, early.body.error.code], [409, 'conflict']);
    assert.equal(existsSync(join(drop, 'Inbox', 'careful.bin')), false);
    assert.equal((await putChunk(phone, started.uploadId, 1000, data.subarray(1000))).body.accepted, true);
    const done = await finish(phone, started.uploadId);
    assert.equal(sha256(readFileSync(join(drop, 'Inbox', 'careful.bin'))), sha256(data));
    assert.equal(done.body.sha256, sha256(data));
    // A replay after the commit changes nothing.
    const replay = await putChunk(phone, started.uploadId, 0, data.subarray(0, 1000));
    assert.equal(replay.status, 409);
    assert.equal(sha256(readFileSync(join(drop, 'Inbox', 'careful.bin'))), sha256(data));
  });

  test('a piece with the wrong checksum header, size or position is refused by the relay or the PC without effect', async () => {
    const started = await begin(phone, 'shape.bin', 5000);
    const data = randomData(1000, 31);
    const noHeader = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${started.uploadId}/chunk?offset=0`, { method: 'PUT', headers: phone.headers({ 'content-type': 'application/octet-stream' }), body: data });
    assert.equal(noHeader.status, 400);
    for (const offset of ['-1', 'abc', '', '1e3', '99999999999999999999']) {
      const bad = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${started.uploadId}/chunk?offset=${offset}`, { method: 'PUT', headers: phone.headers({ 'x-chunk-sha256': sha256(data) }), body: data });
      assert.equal(bad.status, 400, `offset ${offset}`);
    }
    const shortHash = await putChunk(phone, started.uploadId, 0, data, { sha: 'abc' });
    assert.equal(shortHash.status, 400);
    const notHex = await putChunk(phone, started.uploadId, 0, data, { sha: 'z'.repeat(64) });
    assert.equal(notHex.status, 400);
    const badId = await putChunk(phone, 'not-an-id', 0, data);
    assert.equal(badId.status, 400);
    const unknown = await putChunk(phone, '0'.repeat(32), 0, data);
    assert.equal(unknown.status, 404);
    const empty = await putChunk(phone, started.uploadId, 0, Buffer.alloc(0));
    assert.equal(empty.status, 400);
    const tooBig = await putChunk(phone, started.uploadId, 0, Buffer.alloc(8 * 2 ** 20 + 1));
    assert.ok([400, 413].includes(tooBig.status), `${tooBig.status}`);
    const pastEnd = await putChunk(phone, started.uploadId, 0, randomData(6000, 32));                // more than the declared 5000
    assert.equal(pastEnd.status, 400);
    assert.equal((await statusOf(phone, started.uploadId)).body.committedBytes, '0');
    await cancel(phone, started.uploadId);
  });

  test('a piece cut off half way stores nothing of it and the upload carries on from where it really was', async () => {
    const data = randomData(2 * 2 ** 20, 41);
    const started = await begin(phone, 'cut.bin', data.length);
    assert.equal((await putChunk(phone, started.uploadId, 0, data.subarray(0, 2 ** 20))).body.accepted, true);
    // Send half of the next piece, then drop the connection (a phone going through a tunnel).
    const second = data.subarray(2 ** 20);
    await new Promise((resolve) => {
      const request = http.request(`${gateway.url}/v1/pcs/${pcId}/uploads/${started.uploadId}/chunk?offset=${2 ** 20}`, {
        method: 'PUT', headers: phone.headers({ 'x-chunk-sha256': sha256(second), 'content-length': second.length }),
      });
      request.on('error', () => resolve());
      request.write(second.subarray(0, second.length / 2), () => setTimeout(() => { request.destroy(); resolve(); }, 300));
    });
    await sleep(500);
    const status = await statusOf(phone, started.uploadId);
    assert.deepEqual([status.body.state, status.body.committedBytes], ['receiving', String(2 ** 20)]);
    assert.equal(statSync(join(partial(), partialFiles()[0])).size, 2 ** 20);
    assert.equal((await putChunk(phone, started.uploadId, 2 ** 20, second)).body.accepted, true);
    const done = await finish(phone, started.uploadId);
    assert.equal(sha256(readFileSync(join(drop, 'Inbox', 'cut.bin'))), sha256(data));
    assert.equal(done.body.state, 'committed');
  });
});

describe('names, collisions and limits', () => {
  test('an existing file is never replaced; "keep both" numbers the copy predictably and says so', async () => {
    writeFileSync(join(drop, 'Inbox', 'Report.txt'), 'original');
    for (const name of ['Report.txt', 'REPORT.TXT']) {
      const refused = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name, size: '3' });
      assert.deepEqual([refused.status, refused.body.error.code], [409, 'name_exists']);
    }
    const first = await begin(phone, 'Report.txt', 3, { collision: 'rename' });
    await sendAll(phone, first.uploadId, Buffer.from('one'), 2 ** 20);
    const a = await finish(phone, first.uploadId);
    assert.deepEqual([a.body.finalName, a.body.renamed], ['Report (2).txt', true]);
    const second = await begin(phone, 'Report.txt', 3, { collision: 'rename' });
    await sendAll(phone, second.uploadId, Buffer.from('two'), 2 ** 20);
    assert.equal((await finish(phone, second.uploadId)).body.finalName, 'Report (3).txt');
    assert.equal(readFileSync(join(drop, 'Inbox', 'Report.txt'), 'utf8'), 'original');
    assert.equal(readFileSync(join(drop, 'Inbox', 'Report (2).txt'), 'utf8'), 'one');
    assert.equal(readFileSync(join(drop, 'Inbox', 'Report (3).txt'), 'utf8'), 'two');
  });

  test('traversal, reserved, hidden-direction and auto-run names are refused and nothing is written', async () => {
    const before = names(join(drop, 'Inbox'));
    const bad = ['../escape.txt', '..\\escape.txt', 'a/b.txt', '/etc/passwd', 'C:\\Windows\\x.txt', 'CON', 'nul.txt', 'x.', 'x ', 'a:b', '', '.', '..', '.orbitdrive-partial',
      'evil.lnk', 'x.URL', 'desktop.ini', 'Thumbs.db', 'gp\u202Etxt.exe', 'a'.repeat(256), 'tab\there.txt'];
    for (const name of bad) {
      const result = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name, size: '1' });
      assert.equal(result.status, 400, `${JSON.stringify(name)} -> ${result.status}`);
      assert.equal(result.body.error.code, 'invalid_request');
    }
    assert.deepEqual(names(join(drop, 'Inbox')), before);
    assert.deepEqual(names(drop).filter((n) => n !== 'Inbox' && n !== 'empty.txt'), []);
    assert.equal(existsSync(partial()), false);
    for (const collision of ['overwrite', '', 'REJECT']) {
      assert.equal((await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'ok.txt', size: '1', collision })).status, 400);
    }
    for (const size of ['-1', '1.5', 'abc', '01']) assert.equal((await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'ok.txt', size })).status, 400, size);
  });

  test('an oversized file, a file over the owner allowance and a file too big for the disk are refused before any data', async () => {
    assert.deepEqual([(await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'huge.bin', size: String(101 * 2 ** 20) })).status], [413]);
    try {
      const room = async () => (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots.find((r) => r.rootId === dropId).uploadQuotaLeftBytes;
      const used = 200n * 2n ** 20n - BigInt(await room());                          // what earlier uploads already took
      await setUploads(dropId, true, Number((used >> 20n) + 2n), 100);               // an allowance that leaves between 1 and 2 MiB
      const left = await room();
      assert.ok(BigInt(left) > 2n ** 20n && BigInt(left) <= 2n * 2n ** 20n, `left ${left}`);
      const tooMuch = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'quota.bin', size: String(2 * 2 ** 20) });
      assert.deepEqual([tooMuch.status, tooMuch.body.error.code], [413, 'too_large']);
      const fits = await begin(phone, 'fits.bin', 1000);
      await cancel(phone, fits.uploadId);
      await setUploads(dropId, true, 0, 1 << 30);                                  // no allowance, a 1 PiB per-file limit
      const noRoom = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'disk.bin', size: String(2 ** 49) });
      assert.deepEqual([noRoom.status, noRoom.body.error.code], [507, 'disk_full']);
    } finally { await setUploads(dropId, true, 200, 100); }
    assert.equal(existsSync(partial()), false);
  });

  test('only a few uploads run at once', async () => {
    const open = [];
    let refused;
    for (let i = 0; i < 4; i++) {
      const result = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: `parallel-${i}.bin`, size: '10' });
      if (result.status === 201) open.push(result.body.uploadId); else refused = result;
    }
    assert.equal(open.length, 3);
    assert.deepEqual([refused.status, refused.body.error.code], [429, 'limit_exceeded']);
    for (const id of open) await cancel(phone, id);
    assert.equal(existsSync(partial()), false);
  });
});

describe('who owns an upload, and when access ends', () => {
  test('another paired browser cannot see, continue, finish or cancel it', async () => {
    const started = await begin(phone, 'private.bin', 4);
    assert.equal((await statusOf(other, started.uploadId)).status, 404);
    assert.equal((await putChunk(other, started.uploadId, 0, Buffer.from([1, 2, 3, 4]))).status, 404);
    assert.equal((await finish(other, started.uploadId)).status, 404);
    assert.equal((await cancel(other, started.uploadId)).status, 404);
    assert.equal((await statusOf(phone, started.uploadId)).status, 200);
    await cancel(phone, started.uploadId);
  });

  test('cancelling removes the partial file at once', async () => {
    const started = await begin(phone, 'gone.bin', 2000);
    await putChunk(phone, started.uploadId, 0, randomData(1000, 51));
    assert.equal(partialFiles().length, 1);
    assert.equal((await cancel(phone, started.uploadId)).status, 200);
    assert.equal(existsSync(partial()), false);
    assert.equal((await statusOf(phone, started.uploadId)).body.state, 'cancelled');
    assert.equal(existsSync(join(drop, 'Inbox', 'gone.bin')), false);
  });

  test('turning uploads off on the PC stops an upload in flight and removes its partial file', async () => {
    const started = await begin(phone, 'mid.bin', 3000);
    await putChunk(phone, started.uploadId, 0, randomData(1000, 61));
    await setUploads(dropId, false);
    const next = await putChunk(phone, started.uploadId, 1000, randomData(1000, 62));
    assert.ok([403, 409].includes(next.status), `${next.status}`);
    await waitFor(() => !existsSync(partial()), 5000, 'the partial file to be removed');
    assert.equal((await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'after.txt', size: '1' })).status, 403);
    assert.equal(existsSync(join(drop, 'Inbox', 'mid.bin')), false);
    assert.equal((await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots.find((r) => r.rootId === dropId).canUpload, false);
    await setUploads(dropId, true);
  });

  test('revoking the browser on the PC discards what it was sending and refuses everything after', async () => {
    const victim = await new Browser(gateway, 'owner@example.test').signIn();
    await pair(agent, victim, 'Victim');
    const started = await begin(victim, 'victim.bin', 3000);
    await putChunk(victim, started.uploadId, 0, randomData(1000, 71));
    assert.equal(partialFiles().length, 1);
    assert.equal((await ipc.send('revoke_device', { deviceId: victim.device.id })).ok, true);
    await waitFor(() => !existsSync(partial()), 5000, 'the partial file to be removed');
    const after_ = await putChunk(victim, started.uploadId, 1000, randomData(1000, 72));
    assert.ok([401, 403, 404].includes(after_.status), `${after_.status}`);   // the PC no longer knows this browser
    assert.equal((await startUpload(victim, { rootId: dropId, folderEntryId: inboxEntry, name: 'late.txt', size: '1' })).status >= 400, true);
    assert.equal(existsSync(join(drop, 'Inbox', 'victim.bin')), false);
  });

  test('revoking the browser from another browser of the same account does the same', async () => {
    const victim = await new Browser(gateway, 'owner@example.test').signIn();
    await pair(agent, victim, 'Victim two');
    const started = await begin(victim, 'victim2.bin', 3000);
    await putChunk(victim, started.uploadId, 0, randomData(1000, 73));
    assert.equal(partialFiles().length, 1);
    assert.equal((await phone.call('DELETE', `/v1/browser-devices/${victim.device.id}`)).status, 200);
    await waitFor(() => !existsSync(partial()), 5000, 'the partial file to be removed');
    assert.ok((await putChunk(victim, started.uploadId, 1000, randomData(1000, 74))).status >= 400);
    assert.equal(existsSync(join(drop, 'Inbox', 'victim2.bin')), false);
  });

  test('pausing remote access on the PC refuses uploads', async () => {
    await ipc.send('pause');
    try {
      const refused = await startUpload(phone, { rootId: dropId, folderEntryId: inboxEntry, name: 'paused.txt', size: '1' });
      assert.ok(refused.status >= 400, `${refused.status}`);
    } finally { await ipc.send('resume'); }
    await waitFor(async () => (await phone.get(`/v1/pcs/${pcId}/roots`)).status === 200, 10000, 'the PC to be back');
  });
});

describe('large files and crashes', () => {
  test(`a ${LARGE_MIB} MiB upload is checksummed end to end with bounded memory on both sides`, async () => {
    await setUploads(dropId, true, 0, 4096);
    const total = LARGE_MIB * 2 ** 20;
    const started = await begin(phone, 'large.bin', total);
    const whole = createHash('sha256');
    const baseline = { gateway: rssBytes(gateway.pid), agent: rssBytes(agent.pid) };
    const peak = { ...baseline };
    const sampler = setInterval(() => { peak.gateway = Math.max(peak.gateway, rssBytes(gateway.pid)); peak.agent = Math.max(peak.agent, rssBytes(agent.pid)); }, 250);
    const startedAt = Date.now();
    try {
      for (let offset = 0, i = 0; offset < total; offset += started.chunkBytes, i++) {
        const part = randomData(Math.min(started.chunkBytes, total - offset), 1000 + (i % 7));
        whole.update(part);
        const result = await putChunk(phone, started.uploadId, offset, part);
        assert.equal(result.body?.accepted, true, JSON.stringify(result));
      }
    } finally { clearInterval(sampler); }
    const seconds = (Date.now() - startedAt) / 1000;
    const done = await finish(phone, started.uploadId);
    assert.equal(done.status, 200, JSON.stringify(done.body));
    const expected = whole.digest('hex');
    assert.equal(done.body.sha256, expected);
    const onDisk = createHash('sha256');
    for await (const block of createReadStream(join(drop, 'Inbox', 'large.bin'))) onDisk.update(block);
    assert.equal(onDisk.digest('hex'), expected);
    const metrics = {
      sizeMiB: LARGE_MIB, seconds: +seconds.toFixed(1), throughputMiBps: +(LARGE_MIB / seconds).toFixed(1),
      gatewayRssGrowthMiB: +((peak.gateway - baseline.gateway) / 2 ** 20).toFixed(1), agentRssGrowthMiB: +((peak.agent - baseline.agent) / 2 ** 20).toFixed(1),
      platform: process.platform, node: process.version,
    };
    console.log('METRICS ' + JSON.stringify(metrics));
    mkdirSync(join(repoRoot, 'artifacts'), { recursive: true });
    writeFileSync(join(repoRoot, 'artifacts', `upload-metrics-${process.platform}.json`), JSON.stringify(metrics, null, 2));
    assert.ok(metrics.gatewayRssGrowthMiB < 96, `gateway memory grew ${metrics.gatewayRssGrowthMiB} MiB`);
    assert.ok(metrics.agentRssGrowthMiB < 96, `agent memory grew ${metrics.agentRssGrowthMiB} MiB`);
    await setUploads(dropId, true, 200, 100);
  });

  test('after the PC crashes mid-upload, no half-received file is kept and no final file exists', async () => {
    const started = await begin(phone, 'crash.bin', 4 * 2 ** 20);
    await putChunk(phone, started.uploadId, 0, randomData(2 ** 20, 81));
    assert.equal(partialFiles().length, 1);
    ipc.close();
    await kill(agent);
    assert.equal(partialFiles().length, 1, 'a crash leaves the partial file until the next start');
    agent = await startAgent(gateway, state, 'Upload PC');
    ipc = await connectIpc(agent.ready.pipe);
    assert.equal(existsSync(partial()), false, 'the restart removed it');
    assert.equal(existsSync(join(drop, 'Inbox', 'crash.bin')), false);
    await waitFor(async () => (await phone.get(`/v1/pcs/${pcId}/roots`)).status === 200, 20000, 'the PC to reconnect');
    assert.equal((await statusOf(phone, started.uploadId)).status, 404);
    assert.equal((await putChunk(phone, started.uploadId, 2 ** 20, randomData(2 ** 20, 82))).status, 404);
    // The owner's setting survived the restart, and a new upload works.
    const fresh = await begin(phone, 'after-crash.bin', 10);
    await sendAll(phone, fresh.uploadId, randomData(10, 83), 2 ** 20);
    assert.equal((await finish(phone, fresh.uploadId)).status, 200);
    assert.equal(statSync(join(drop, 'Inbox', 'after-crash.bin')).size, 10);
  });
});
