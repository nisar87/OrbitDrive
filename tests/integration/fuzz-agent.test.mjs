// R47/D11 (continued): seeded fuzzing of what the PC-side agent accepts from outside its own code, with real processes:
//  (1) the owner console's local channel (framing and every command's arguments), (2) the upload path (names, sizes, collision modes, chunk offsets/hashes/lengths)
//  that ends in real file-system writes. Invariants: the agent never dies or wedges, replies stay well-formed with no internals in the error text, nothing is ever
//  created outside the shared folder, folders stay read-only unless the owner enabled uploads, and legitimate work still succeeds afterwards.
//   ORBIT_FUZZ_SEED=<n> replays a run; ORBIT_FUZZ_ROUNDS=<n> scales it.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, sha256, sleep, repoRoot } from './harness.mjs';

const SEED = Number(process.env.ORBIT_FUZZ_SEED ?? 20261119), ROUNDS = Number(process.env.ORBIT_FUZZ_ROUNDS ?? 40);
const rng = (() => { let a = SEED >>> 0; return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; })();
const int = (n) => Math.floor(rng() * n), pick = (l) => l[int(l.length)];
const LEAK = /\bat [A-Z][\w.]+\(.*\)|\.cs:line|System\.|Microsoft\.|Exception|StackTrace|[A-Z]:\\(Users|Windows|home)|\/home\/|\/tmp\/orbit/;

const NASTY = ['', ' ', '.', '..', '...', '../x', '..\\x', '/etc/passwd', 'C:\\Windows\\win.ini', '\\\\server\\share\\x', '\\\\?\\C:\\x', '\\\\.\\pipe\\x', 'CON', 'nul', 'COM1', 'LPT9.txt', 'aux.txt', 'a:b', 'a:stream', 'file.', 'file ', ' file', 'a/b', 'a\\b', 'a\0b', '\u202etxt.exe', '\u0645\u0631\u062d\u0628\u0627', '🙂'.repeat(60), 'A'.repeat(255), 'A'.repeat(256), 'A'.repeat(5000), '%2e%2e', '%00', '~', '*', '?', '<>', '|', '"', '.orbitdrive-partial', '.orbitdrive-partial/x', 'desktop.ini', '$MFT', '__proto__', 'null', '0', '-1', '9'.repeat(30), '\ud800', '\n', '\r\n'];
const WEIRD = () => [null, true, false, 0, -1, 1.5, 2 ** 31, 2 ** 53, 1e309 === Infinity ? 1e308 : 1, '', [], {}, [[]], { a: { b: {} } }, [null], pick(NASTY), pick(NASTY), 'x'.repeat(30_000)];   // below the 64 KiB line limit: bigger lines close the connection by design (covered in ipc.test.mjs)

let gateway, agent, ipc, state, fixtureRoot;
let dropAgent, dropIpc, box, drop, dropState, dropId, phone, pcId, inbox;

before(async () => {
  gateway = await startGateway();
  fixtureRoot = makeFixtureRoot(); state = tempDir('fuzzagent-state');
  agent = await startAgent(null, state, 'Fuzz IPC PC');
  ipc = await connectIpc(agent.ready.pipe);
  // second agent: one folder with uploads ON inside a sandbox that holds a canary sibling, so any write outside the folder is visible
  box = tempDir('fuzzagent-box'); drop = join(box, 'drop'); mkdirSync(drop); mkdirSync(join(drop, 'Inbox'));
  writeFileSync(join(box, 'canary.txt'), 'untouched'); writeFileSync(join(drop, 'keep.txt'), 'keep');
  dropState = tempDir('fuzzagent-drop-state');
  dropId = agentCli(dropState, 'roots', 'add', 'Drop', drop).match(/\(([A-Za-z0-9_-]+)\)/)[1];
  agentCli(dropState, 'roots', 'uploads', dropId, 'on', '200', '5');
  dropAgent = await startAgent(gateway, dropState, 'Fuzz Upload PC');
  pcId = dropAgent.identity.pcId;
  dropIpc = await connectIpc(dropAgent.ready.pipe);
  phone = await new Browser(gateway, 'fuzz-agent@example.test').signIn();
  await pair(dropAgent, phone, 'Fuzz phone');
  const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: dropId, pageSize: 50 });
  inbox = page.body.entries.find((e) => e.name === 'Inbox').entryId;
});
after(async () => { ipc?.close(); dropIpc?.close(); await agent?.stop(); await dropAgent?.stop(); await gateway?.stop(); });

/** A command that never answers is a finding, not a test timeout: say which one. */
const timed = async (channel, cmd, args) => {
  let timer; const slow = new Promise((resolve) => { timer = setTimeout(() => resolve({ hung: true }), 8000); });
  const reply = await Promise.race([channel.send(cmd, args), slow]); clearTimeout(timer);
  assert.ok(!reply.hung, `command ${cmd} never answered for args ${JSON.stringify(args)?.slice(0, 200)} seed=${SEED}`);
  return reply;
};
const alive = async (channel) => { const r = await channel.send('status'); assert.equal(r.ok, true, 'the agent still answers status'); return r.data; };

// ------------------------------------------------------------------ (1) local channel
const SKIP = new Set(['quit', 'pair', 'approve', 'deny', 'set_gateway', 'support_bundle_create']);
const hostSource = readFileSync(join(repoRoot, 'apps/desktop/OrbitDrive.Agent/AgentHost.cs'), 'utf8');
const dispatcher = hostSource.slice(hostSource.indexOf('case "status":'), hostSource.indexOf('case "quit":'));   // only the local-command switch
const commands = [...new Set([...dispatcher.matchAll(/case "([a-z_]+)":/g)].map((m) => m[1]))].filter((c) => !SKIP.has(c));

test('the command list was found in the source (the fuzz is not silently empty)', () => { assert.ok(commands.length >= 15, commands.join()); assert.ok(commands.includes('add_root') && commands.includes('set_root_uploads')); });

test('every local command survives hostile arguments: well-formed reply, no internals, agent alive', async () => {
  for (const cmd of commands) for (let i = 0; i < ROUNDS; i++) {
    const args = {};
    for (let n = int(4); n > 0; n--) args[pick(['name', 'path', 'rootId', 'enabled', 'quotaMiB', 'maxFileMiB', 'resetUsed', 'deviceId', 'invitationId', 'paths', 'maxUploadKBps', 'maxConcurrentTransfers', 'hidden', 'devicePolicy', 'enable', 'value', 'days', 'id'])] = pick(WEIRD());
    const reply = await timed(ipc, cmd, int(5) === 0 ? pick(WEIRD()) : args);
    assert.equal(typeof reply.ok, 'boolean', `${cmd}: ${JSON.stringify(reply).slice(0, 200)} seed=${SEED}`);
    if (!reply.ok) { assert.equal(typeof reply.error, 'string'); assert.doesNotMatch(reply.error, LEAK, `${cmd} leaked internals: ${reply.error.slice(0, 200)} seed=${SEED}`); }
    else assert.doesNotMatch(JSON.stringify(reply), LEAK, `${cmd} reply leaked internals seed=${SEED}`);
  }
  const s = await alive(ipc);
  assert.ok(s.roots.every((r) => r.readOnly === true), 'no folder became writable through junk arguments');
});

test('hostile folder paths never add a folder that is not a real directory the owner pointed at', async () => {
  for (const path of NASTY) { const r = await ipc.send('add_root', { name: 'F', path }); assert.equal(r.ok, false, `accepted ${JSON.stringify(path).slice(0, 60)}`); assert.doesNotMatch(r.error ?? '', LEAK); }
  const before = (await alive(ipc)).roots.length;
  assert.equal((await ipc.send('add_root', { name: 'Real', path: fixtureRoot })).ok, true);
  assert.equal((await alive(ipc)).roots.length, before + 1);
});

test('broken framing never wedges or kills the agent', async () => {
  const frames = () => [Buffer.from('{"id":"1","cmd":"status"'), Buffer.from('{"id":"1","cmd":"status"}\r\n'), Buffer.from('\0\0\0\n'), Buffer.from([0xff, 0xfe, 0xc3, 0x28, 10]), Buffer.from('\ufeff{"id":"1","cmd":"status"}\n'), Buffer.from('{"id":"1","cmd":"status"}\n'.repeat(200)), Buffer.from('['.repeat(5000) + '\n'), Buffer.from('{"id":' + '9'.repeat(5000) + ',"cmd":"status"}\n'), Buffer.from('x'.repeat(65_535) + '\n'), Buffer.from(Array.from({ length: 4000 }, () => int(256)))];
  for (let i = 0; i < ROUNDS; i++) {
    const c = await connectIpc(agent.ready.pipe);
    for (let n = 1 + int(3); n > 0; n--) c.socket.write(pick(frames()));
    if (int(2)) c.socket.destroy(); else c.close();
  }
  const many = await Promise.all(Array.from({ length: 40 }, () => connectIpc(agent.ready.pipe).then((c) => { c.socket.destroy(); }).catch(() => {})));
  await sleep(300);
  const fresh = await connectIpc(agent.ready.pipe);
  assert.equal((await fresh.send('status')).ok, true); fresh.close();
  await alive(ipc);
});

// ------------------------------------------------------------------ (2) uploads into a real folder
const tree = (dir, base = dir) => readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? [join(dir, e.name).slice(base.length) + '/', ...tree(join(dir, e.name), base)] : [join(dir, e.name).slice(base.length)])).sort();
const SAFE_NAME = /^(?!\.{1,2}$)(?!\.orbitdrive)[^\\/:*?"<>|\0\r\n]{1,255}$/;
const chunk = (uploadId, offset, data, headers = {}) => fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${uploadId}/chunk?offset=${offset}`, { method: 'PUT', headers: phone.headers({ 'content-type': 'application/octet-stream', 'x-chunk-sha256': sha256(data), ...headers }), body: data }).then(async (r) => ({ status: r.status, text: await r.text() }));

test('hostile names, sizes and collision modes: typed refusals, and every file that is created is inside the folder with a safe name', async () => {
  const outside = () => readdirSync(box).sort().join();
  const beforeOutside = outside();
  let created = 0;
  for (let i = 0; i < ROUNDS * 3; i++) {
    const body = { rootId: pick([dropId, dropId, dropId, dropId, dropId, dropId, 'nope', pick(NASTY)]), folderEntryId: pick([inbox, inbox, inbox, inbox, inbox, null, pick(NASTY)]), name: pick([...NASTY, ...Array(24).fill('ok-' + i + '.txt')]), size: pick(['5', '5', '5', '0', '1', '-1', 'abc', '', '9223372036854775807', '99999999999999999999', '1e3', 5, '5.5', '6291456', ' 5']), collision: pick(['reject', 'rename', 'rename', 'overwrite', 'bogus', '', null]) };
    if (i < 6) Object.assign(body, { rootId: dropId, folderEntryId: inbox, name: `base-${i}.txt`, size: '5', collision: i % 2 ? 'rename' : 'reject' });   // a guaranteed legitimate core, whatever the seed
    if (process.env.FUZZ_TRACE) console.log('START', JSON.stringify(body).slice(0, 200));
    const r = await phone.post(`/v1/pcs/${pcId}/uploads`, body);
    assert.ok(r.status < 500, `start ${JSON.stringify(body).slice(0, 160)} -> ${r.status} seed=${SEED}`);
    if (r.status !== 201) { assert.ok(r.body?.error?.code, `untyped refusal ${r.status}`); assert.doesNotMatch(JSON.stringify(r.body), LEAK); continue; }
    created++;
    const id = r.body.uploadId, size = Number(body.size);
    if (Number.isInteger(size) && size >= 0 && size <= 5) {
      const data = Buffer.from('hello'.slice(0, size));
      if (size > 0) { const put = await chunk(id, 0, data); assert.ok(put.status < 500, put.text); }
      const fin = await phone.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId: id });
      assert.ok(fin.status < 500, JSON.stringify(fin.body));
    } else { await phone.post(`/v1/pcs/${pcId}/upload-cancel`, { uploadId: id }); }
  }
  assert.ok(created > 0, 'some uploads were legitimately accepted');
  assert.equal(outside(), beforeOutside, 'nothing was created beside the shared folder');
  assert.equal(readFileSync(join(box, 'canary.txt'), 'utf8'), 'untouched');
  for (const path of tree(drop)) for (const part of path.split('/').filter(Boolean)) assert.match(part, SAFE_NAME, `unsafe name on disk: ${JSON.stringify(path)}`);
  assert.equal(readFileSync(join(drop, 'keep.txt'), 'utf8'), 'keep', 'an existing file survives (only the explicit overwrite mode may replace names the owner can see)');
});

test('chunk offsets, hashes and lengths: typed refusals, never a wrong file', async () => {
  const start = await phone.post(`/v1/pcs/${pcId}/uploads`, { rootId: dropId, folderEntryId: inbox, name: 'chunky.bin', size: '10', collision: 'reject' });
  assert.equal(start.status, 201, JSON.stringify(start.body)); const id = start.body.uploadId;
  const good = Buffer.from('0123456789');
  for (let i = 0; i < ROUNDS * 2; i++) {
    const offset = pick(['-1', '0', '5', '9', '10', '11', '99999999999999999999', 'abc', '', '1.5', '0x10', '1e2', '5 ']);
    const data = pick([Buffer.alloc(0), good.subarray(0, 5), good, Buffer.alloc(11, 1), Buffer.alloc(100_000, 2)]);
    const headers = pick([{}, {}, { 'x-chunk-sha256': 'zz' }, { 'x-chunk-sha256': sha256(Buffer.from('other')) }, { 'x-chunk-sha256': '' }, { 'content-length': '3' }]);
    if (process.env.FUZZ_TRACE) console.log('CHUNK', JSON.stringify({ offset, len: data.length, headers }));
    let r; try { r = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${id}/chunk?offset=${encodeURIComponent(offset)}`, { signal: AbortSignal.timeout(15000), method: 'PUT', headers: phone.headers({ 'content-type': 'application/octet-stream', 'x-chunk-sha256': sha256(data), ...headers }), body: data }); } catch { continue; }
    const text = await r.text();
    assert.ok(r.status < 500, `offset=${offset} len=${data.length} -> ${r.status} ${text.slice(0, 160)} seed=${SEED}`);
    assert.doesNotMatch(text, LEAK);
  }
  const done = await phone.post(`/v1/pcs/${pcId}/upload-status`, { uploadId: id });
  assert.ok(done.status < 500);
  assert.equal((await chunk(id, 0, good)).status < 500, true);
  const fin = await phone.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId: id });
  if (fin.status === 200) assert.equal(readFileSync(join(drop, 'Inbox', 'chunky.bin'), 'utf8'), '0123456789', 'a finished file has exactly the bytes that were declared and hashed');
  else assert.ok(fin.status < 500);
});

test('afterwards both agents are healthy, folders are still read-only where they were, and a normal upload still works', async () => {
  const a = await alive(ipc); assert.ok(a.roots.every((r) => r.readOnly));
  await alive(dropIpc);
  const start = await phone.post(`/v1/pcs/${pcId}/uploads`, { rootId: dropId, folderEntryId: inbox, name: 'final-check.txt', size: '5', collision: 'reject' });
  assert.equal(start.status, 201, JSON.stringify(start.body));
  assert.equal((await chunk(start.body.uploadId, 0, Buffer.from('hello'))).status, 200);
  assert.equal((await phone.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId: start.body.uploadId })).status, 200);
  assert.equal(readFileSync(join(drop, 'Inbox', 'final-check.txt'), 'utf8'), 'hello');
  assert.ok(!existsSync(join(drop, '.orbitdrive-partial')) || readdirSync(join(drop, '.orbitdrive-partial')).length < 50, 'the partial-upload area does not pile up');
});
