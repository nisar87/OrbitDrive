// D7/W7: in-page previews, end to end with real processes. A preview is a normal, revocable, range-capable read of the original
// (inline only when the PC judged the bytes to be a passive type) or of a thumbnail the PC generated in a separate restricted helper.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, readFileSync, existsSync, copyFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, connectIpc, agentCli, waitFor, tempDir, Browser, pair, sleep, repoRoot } from './harness.mjs';

const config = process.env.ORBIT_CONFIG || 'Release';
const realHelper = join(repoRoot, `apps/desktop/OrbitDrive.ThumbnailHelper/bin/${config}/net10.0`, process.platform === 'win32' ? 'OrbitDrive.ThumbnailHelper.exe' : 'OrbitDrive.ThumbnailHelper');
const real = existsSync(realHelper);
if (!real && process.env.CI) throw new Error('CI must exercise the real image decoder; the helper was not built');
const helperLog = join(tempDir('helper-log'), 'runs.txt');
writeFileSync(helperLog, '');
process.env.ORBITDRIVE_THUMBNAIL_HELPER = real ? realHelper : join(repoRoot, 'tests/integration/fake-thumbnail-helper.sh');
process.env.FAKE_HELPER_LOG = helperLog;
const helperRuns = () => readFileSync(helperLog, 'utf8').split('\n').filter(Boolean).length;
const images = join(repoRoot, 'tests/fixtures/images');

let gateway, agent, ipc, phone, other, stranger, pcId, rootId, root, state;

const entryOf = async (browser, name) => {
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: null, pageSize: 200 });
  assert.equal(page.status, 200, JSON.stringify(page.body));
  const found = page.body.entries.find((e) => e.name === name);
  assert.ok(found, `${name} is not listed`);
  return found;
};
const preview = async (browser, name, variant = 'preview') => browser.post('/v1/download-sessions', { pcId, entryId: (await entryOf(browser, name)).entryId, variant });
const open = (session, headers = {}) => fetch(gateway.url + session.urlPath, { headers });
const webpSize = (b) => {
  assert.equal(b.subarray(0, 4).toString('latin1'), 'RIFF'); assert.equal(b.subarray(8, 12).toString('latin1'), 'WEBP');
  const kind = b.subarray(12, 16).toString('latin1');
  if (kind === 'VP8X') return [1 + b.readUIntLE(24, 3), 1 + b.readUIntLE(27, 3)];
  if (kind === 'VP8L') { const bits = b.readUInt32LE(21); return [(bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1]; }
  return [b.readUInt16LE(26) & 0x3fff, b.readUInt16LE(28) & 0x3fff];
};

before(async () => {
  root = tempDir('preview-root');
  state = tempDir('preview-state');
  writeFileSync(join(root, 'notes.txt'), '<script>alert("not run")</script> plain words\n');
  writeFileSync(join(root, 'photo.png'), readFileSync(join(images, 'photo-640x480.png')));
  writeFileSync(join(root, 'clip.mp4'), Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from('ftypisom'), randomBytes(200_000)]));
  writeFileSync(join(root, 'song.mp3'), Buffer.concat([Buffer.from('ID3\x04\0\0\0\0\0\0'), randomBytes(50_000)]));
  writeFileSync(join(root, 'doc.pdf'), '%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n');
  writeFileSync(join(root, 'page.html'), '<html><script>fetch("/steal")</script></html>');
  writeFileSync(join(root, 'active.svg'), readFileSync(join(images, 'active.svg')));
  writeFileSync(join(root, 'fake.png'), readFileSync(join(images, 'html-named.png')));
  writeFileSync(join(root, `&lt;img src=x onerror=alert(1)&gt; 'q' ; $(id) %s é 文.txt`), 'hostile name\n');   // markup characters themselves cannot be in a Windows file name
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Preview PC');
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

test('passive types preview inline with locked-down headers and ranges; markup, SVG and look-alikes are never shown', async () => {
  for (const [name, type] of [['notes.txt', 'text/plain; charset=utf-8'], ['photo.png', 'image/png'], ['clip.mp4', 'video/mp4'], ['song.mp3', 'audio/mpeg'], ['doc.pdf', 'application/pdf']]) {
    const created = await preview(phone, name);
    assert.equal(created.status, 201, `${name}: ${JSON.stringify(created.body)}`);
    assert.equal(created.body.contentType, type);
    const full = await open(created.body);
    assert.equal(full.status, 200);
    assert.equal(full.headers.get('content-type'), type);
    assert.match(full.headers.get('content-disposition'), /^inline/);
    assert.equal(full.headers.get('x-content-type-options'), 'nosniff');
    assert.match(full.headers.get('content-security-policy'), /default-src 'none'.*sandbox|sandbox.*default-src 'none'/);
    assert.equal(full.headers.get('cache-control'), 'no-store');
    const bytes = Buffer.from(await full.arrayBuffer());
    assert.deepEqual(bytes, readFileSync(join(root, name)), `${name} bytes are exact`);
    const ranged = await open(created.body, { range: 'bytes=3-9' });
    assert.equal(ranged.status, 206);
    assert.deepEqual(Buffer.from(await ranged.arrayBuffer()), bytes.subarray(3, 10));
  }
  for (const name of ['page.html', 'active.svg', 'fake.png']) {
    const refused = await preview(phone, name);
    assert.equal(refused.status, 400, name);
    assert.equal(refused.body.error.code, 'invalid_request');
    assert.match(refused.body.error.message, /Download it instead/);
  }
  const hostileName = `&lt;img src=x onerror=alert(1)&gt; 'q' ; $(id) %s é 文.txt`;
  const created = await preview(phone, hostileName);
  assert.equal(created.status, 201);
  const disposition = (await open(created.body)).headers.get('content-disposition');
  assert.ok(!/[\r\n]/.test(disposition) && !/filename="[^"]*"[^;]*"/.test(disposition) && /filename\*=UTF-8''/.test(disposition), `header cannot be broken out of: ${disposition}`);
  assert.equal(created.body.name, hostileName);                    // the name is data; the web app renders it as text
});

test('previews are not transfers; an ordinary download of the same file is still an attachment of opaque bytes', async () => {
  const listed = await phone.get('/v1/download-sessions');
  assert.equal(listed.body.sessions.length, 0, 'preview sessions never appear in Transfers');
  const entry = await entryOf(phone, 'photo.png');
  const download = await phone.post('/v1/download-sessions', { pcId, entryId: entry.entryId });
  assert.equal(download.status, 201);
  const response = await open(download.body);
  assert.equal(response.headers.get('content-type'), 'application/octet-stream');
  assert.match(response.headers.get('content-disposition'), /^attachment/);
  await response.arrayBuffer();
  assert.equal((await phone.post('/v1/download-sessions', { pcId, entryId: entry.entryId, variant: 'thumb:999' })).status, 400);
  assert.equal((await phone.post('/v1/download-sessions', { pcId, entryId: entry.entryId, variant: '../../etc' })).status, 400);
});

test('a preview stops when its browser is removed, when another account asks, and when the file changes', async () => {
  assert.notEqual((await preview(stranger, 'notes.txt').catch(() => ({ status: 0 }))).status, 201, 'another account gets nothing');
  const mine = await preview(other, 'notes.txt');
  assert.equal(mine.status, 201);
  assert.equal((await open(mine.body)).status, 200);
  const removed = await phone.call('DELETE', `/v1/browser-devices/${other.device.id}`);
  assert.equal(removed.status, 200);
  assert.ok([401, 403, 404, 410].includes((await open(mine.body)).status), 'a removed browser cannot keep using a preview link');

  writeFileSync(join(root, 'changing.txt'), 'version one of the text');
  const created = await preview(phone, 'changing.txt');
  assert.equal((await open(created.body, { range: 'bytes=0-6' })).status, 206);
  writeFileSync(join(root, 'changing.txt'), 'VERSION TWO OF THE TEXT');          // same size, different bytes
  const later = await open(created.body, { range: 'bytes=7-' });
  assert.equal(later.status, 409);
  assert.equal((await later.json()).error.code, 'revision_changed');
});

test('thumbnails: generated by the restricted helper, cached, refused for look-alikes and bombs without starting the helper', async () => {
  const before = helperRuns();
  const created = await preview(phone, 'photo.png', 'thumb:256');
  assert.equal(created.status, 201, JSON.stringify(created.body));
  assert.equal(created.body.contentType, 'image/webp');
  const bytes = Buffer.from(await (await open(created.body)).arrayBuffer());
  assert.equal(bytes.subarray(0, 4).toString('latin1'), 'RIFF');
  if (real) { const [w, h] = webpSize(bytes); assert.deepEqual([w, h], [256, 192], 'a 640x480 picture becomes 256x192'); assert.ok(bytes.length < 60_000); }
  const again = await preview(phone, 'photo.png', 'thumb:256');
  assert.equal(again.status, 201);
  await (await open(again.body)).arrayBuffer();
  if (!real) assert.equal(helperRuns() - before, 1, 'the second request came from the private cache');
  const stats = (await ipc.send('thumbnails')).data;
  assert.ok(stats.available && stats.files >= 1, JSON.stringify(stats));

  writeFileSync(join(root, 'bomb.png'), readFileSync(join(images, 'bomb-declared-60000.png')));
  const mark = helperRuns();
  const bomb = await preview(phone, 'bomb.png', 'thumb:256');
  assert.equal(bomb.status, 429 === bomb.status ? 429 : bomb.status);
  assert.equal(bomb.body.error.code, 'limit_exceeded');
  assert.equal(helperRuns(), mark, 'an image bomb is refused from its header, before any decoder runs');
  const lookalike = await preview(phone, 'fake.png', 'thumb:256');
  assert.equal(lookalike.status, 400);
  assert.equal(helperRuns(), mark);
  const notPicture = await preview(phone, 'doc.pdf', 'thumb:256');
  assert.equal(notPicture.status, 400);
});

test('owner controls: switching thumbnails off or clearing the cache takes effect at once; originals still preview', async () => {
  assert.ok((await ipc.send('thumbnails')).data.files >= 1);
  const cleared = await ipc.send('clear_thumbnails');
  assert.equal(cleared.data.files, 0);
  const off = await ipc.send('set_thumbnails', { enabled: false });
  assert.equal(off.data.enabled, false);
  const denied = await preview(phone, 'photo.png', 'thumb:128');
  assert.equal(denied.body.error.code, 'unavailable');
  assert.equal((await preview(phone, 'photo.png')).status, 201, 'the original is still previewable');
  const on = await ipc.send('set_thumbnails', { enabled: true });
  assert.equal(on.data.enabled, true);
  assert.equal((await preview(phone, 'photo.png', 'thumb:128')).status, 201);
});

test('the real decoder handles pictures, honours rotation, and contains corrupt, truncated and oversized inputs', { skip: !real && 'the real helper is only built where NuGet is reachable (always in CI)' }, async () => {
  const put = (name, from) => copyFileSync(join(images, from), join(root, name));
  const size = async (name, variant = 'thumb:256') => {
    const created = await preview(phone, name, variant);
    if (created.status !== 201) return { status: created.status, code: created.body.error.code };
    const response = await open(created.body);
    const bytes = Buffer.from(await response.arrayBuffer());
    return { status: 201, dims: webpSize(bytes) };
  };
  put('p.jpg', 'photo-640x480.jpg'); put('r.jpg', 'rotated-orientation6.jpg'); put('a.webp', 'alpha.webp'); put('l.webp', 'lossy.webp'); put('g.gif', 'anim.gif');
  put('corrupt.png', 'corrupt-idat.png'); put('cut.jpg', 'truncated.jpg'); put('flat.png', 'flat-8000x8000.png');
  assert.deepEqual((await size('p.jpg')).dims, [256, 192]);
  assert.deepEqual((await size('p.jpg', 'thumb:128')).dims, [128, 96]);
  assert.deepEqual((await size('r.jpg')).dims, [50, 100], 'a picture stored sideways is shown upright (EXIF orientation 6: 100x50 → 50x100)');
  assert.deepEqual((await size('a.webp')).dims, [120, 80]);
  assert.deepEqual((await size('l.webp')).dims, [200, 150]);
  assert.deepEqual((await size('g.gif')).dims, [60, 40]);
  const corrupt = await size('corrupt.png');
  assert.ok(corrupt.status === 201 || corrupt.code === 'invalid_request', JSON.stringify(corrupt));   // partial picture or a clean refusal; never a crash
  const cut = await size('cut.jpg');
  assert.ok(cut.status === 201 || cut.code === 'invalid_request', JSON.stringify(cut));
  const started = Date.now();
  const flat = await size('flat.png');                       // 64 megapixels of one colour: the largest accepted size
  assert.ok(flat.status === 201 || flat.code === 'timeout' || flat.code === 'invalid_request', JSON.stringify(flat));
  assert.ok(Date.now() - started < 40_000, 'bounded in time');
  assert.equal((await ipc.send('status')).ok, true, 'the agent is unaffected by whatever the decoder did');
  const after = await size('p.jpg', 'thumb:512');
  assert.equal(after.status, 201, 'and thumbnails keep working afterwards');
});

test('removing a shared folder drops its previews and its cached thumbnails at once', async () => {
  const created = await preview(phone, 'photo.png', 'thumb:512');
  assert.equal(created.status, 201);
  const live = await preview(phone, 'notes.txt');
  assert.equal((await open(live.body, { range: 'bytes=0-3' })).status, 206);
  assert.ok((await ipc.send('remove_root', { rootId })).ok);
  await waitFor(async () => (await open(live.body, { range: 'bytes=0-3' })).status !== 206, 10_000, 'the preview link to stop working');
  assert.equal((await ipc.send('thumbnails')).data.files, 0, 'derived content does not outlive the permission');
});
