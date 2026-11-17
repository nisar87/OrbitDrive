// D5/W5: what the relay and the PC report about transfers, cancelling, quotas and source changes between ranges.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { statSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, writeLargeFile, Browser, pair, sleep } from './harness.mjs';

let gateway, agent, ipc, phone, tablet, stranger, pcId, root, rootId, bigHash;
const BIG_MIB = 24;

before(async () => {
  root = makeFixtureRoot();
  bigHash = await writeLargeFile(join(root, 'big.bin'), BIG_MIB);
  const state = tempDir('transfers-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway({ OrbitDrive__MaxDownloadSessionsPerDevice: '6' });
  agent = await startAgent(gateway, state, 'Transfer PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  tablet = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, tablet, 'Tablet');
  rootId = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].rootId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

const entry = async (name) => (await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, pageSize: 200 })).body.entries.find((e) => e.name === name);
async function prepare(name, browser = phone) {
  const created = await browser.post('/v1/download-sessions', { pcId, entryId: (await entry(name)).entryId });
  assert.equal(created.status, 201, JSON.stringify(created.body));
  return created.body;
}
const sessions = async (browser = phone) => (await browser.get('/v1/download-sessions')).body.sessions;
const find = async (id, browser = phone) => (await sessions(browser)).find((s) => s.sessionId === id);
async function until(check, ms, what) {
  const start = Date.now();
  for (;;) { const value = await check(); if (value) return value; if (Date.now() - start > ms) assert.fail(`${what} did not happen within ${ms} ms`); await sleep(100); }
}

test('the relay reports prepared, sent and interrupted truthfully and never lists the secret link', async () => {
  const session = await prepare('hello.txt');
  assert.match(session.sessionId, /^[A-Za-z0-9_-]{12,}$/);
  const listed = await sessions();
  assert.ok(!JSON.stringify(listed).includes(session.capability), 'the capability is never returned again');
  let mine = await find(session.sessionId);
  assert.deepEqual([mine.state, mine.bytesSentToBrowser, mine.bytesCovered, mine.name, mine.size, mine.revision], ['prepared', '0', '0', 'hello.txt', '22', session.revision]);

  assert.equal((await fetch(gateway.url + session.urlPath, { method: 'HEAD' })).status, 200);
  assert.equal((await find(session.sessionId)).state, 'prepared', 'looking at the headers sends no file bytes');

  const first = await fetch(gateway.url + session.urlPath, { headers: { range: 'bytes=0-9' } });
  assert.equal((await first.text()).length, 10);
  mine = await until(async () => { const s = await find(session.sessionId); return s.state === 'interrupted' ? s : null; }, 5000, 'state after a partial range');
  assert.deepEqual([mine.bytesSentToBrowser, mine.bytesCovered], ['10', '10'], 'part of the file went out: not "sent"');

  const again = await fetch(gateway.url + session.urlPath, { headers: { range: 'bytes=5-14' } });   // overlapping retry
  await again.text();
  const rest = await fetch(gateway.url + session.urlPath, { headers: { range: 'bytes=15-' } });
  await rest.text();
  mine = await until(async () => { const s = await find(session.sessionId); return s.state === 'sent_to_browser' ? s : null; }, 5000, 'state after every byte went out');
  assert.deepEqual([mine.bytesSentToBrowser, mine.bytesCovered], ['27', '22'], 'bytes sent counts repeats; coverage counts each byte once');

  const empty = await prepare('empty.bin');
  assert.equal((await find(empty.sessionId)).state, 'prepared');
  assert.equal((await (await fetch(gateway.url + empty.urlPath)).arrayBuffer()).byteLength, 0);
  await until(async () => (await find(empty.sessionId)).state === 'sent_to_browser', 5000, 'empty file reported as sent');
});

test('a download the browser abandons half way is reported as interrupted on the relay and on the PC', { timeout: 120_000 }, async () => {
  agent.drain();
  const session = await prepare('big.bin');
  const controller = new AbortController();
  const response = await fetch(gateway.url + session.urlPath, { signal: controller.signal });
  const reader = response.body.getReader();
  let received = 0;
  while (received < 2 * (1 << 20)) received += (await reader.read()).value.length;
  let live = await find(session.sessionId);
  assert.equal(live.state, 'sending');
  const running = (await ipc.send('status')).data.transfers[0];
  assert.deepEqual([running.name, running.outcome, running.offset, running.length], ['big.bin', 'sending', '0', String(BIG_MIB * (1 << 20))]);
  assert.ok(Number(running.sent) >= received, 'the PC has sent at least what the browser received');
  controller.abort();
  const finished = await agent.next('stream_finished', 30_000);
  assert.notEqual(finished.outcome, 'sent_to_relay');
  assert.equal(finished.name, 'big.bin');
  assert.ok(Number(finished.bytes) < BIG_MIB * (1 << 20), 'the PC reports the bytes it really sent, not the whole length');
  live = await until(async () => { const s = await find(session.sessionId); return s.state === 'interrupted' ? s : null; }, 10_000, 'interrupted state');
  assert.ok(Number(live.bytesCovered) >= received && Number(live.bytesCovered) < BIG_MIB * (1 << 20));
  const record = (await ipc.send('status')).data.transfers[0];
  assert.ok(['interrupted', 'cancelled'].includes(record.outcome));
  assert.equal(record.sent, finished.bytes);

  // Resume from where the browser stopped: the combined bytes equal the source exactly.
  const hash = createHash('sha256');
  const head = await fetch(gateway.url + session.urlPath, { headers: { range: `bytes=0-${received - 1}` } });
  hash.update(Buffer.from(await head.arrayBuffer()));
  const tail = await fetch(gateway.url + session.urlPath, { headers: { range: `bytes=${received}-`, 'if-range': `"${session.revision}"` } });
  assert.equal(tail.status, 206);
  for await (const chunk of tail.body) hash.update(chunk);
  assert.equal(hash.digest('hex'), bigHash);
  await until(async () => (await find(session.sessionId)).state === 'sent_to_browser', 10_000, 'sent after resume');
});

test('cancelling: only the browser that started a download can see or cancel it, and the link dies at once', { timeout: 120_000 }, async () => {
  const session = await prepare('big.bin');
  assert.deepEqual((await sessions(tablet)).filter((s) => s.sessionId === session.sessionId), [], 'another approved browser of the same account does not see it');
  assert.equal((await tablet.call('DELETE', `/v1/download-sessions/${session.sessionId}`)).status, 404);
  assert.equal((await stranger.call('DELETE', `/v1/download-sessions/${session.sessionId}`)).status, 403);
  assert.equal((await stranger.get('/v1/download-sessions')).status, 403);
  assert.equal((await phone.call('DELETE', '/v1/download-sessions/does-not-exist')).status, 404);

  // Cancel while bytes are flowing: the stream is cut and the partial body is an error, not a short file.
  const response = await fetch(gateway.url + session.urlPath);
  const reader = response.body.getReader();
  let received = (await reader.read()).value.length, failed = false;
  assert.equal((await phone.call('DELETE', `/v1/download-sessions/${session.sessionId}`)).status, 200);
  try { for (;;) { const { done, value } = await reader.read(); if (done) break; received += value.length; } } catch { failed = true; }
  assert.ok(failed && received < BIG_MIB * (1 << 20), `stream was cut (${received} bytes)`);
  assert.equal((await find(session.sessionId)).state, 'cancelled');
  assert.equal((await fetch(gateway.url + session.urlPath)).status, 404);
  assert.equal((await fetch(gateway.url + session.urlPath, { method: 'HEAD' })).status, 404);
  await until(async () => ['cancelled', 'interrupted'].includes((await ipc.send('status')).data.transfers[0].outcome), 10_000, 'the PC stops reading');
  assert.equal((await ipc.send('status')).data.activeStreams, 0);
});

test('a browser cannot pile up unlimited prepared downloads', async () => {
  const tabletSessions = [];
  for (let i = 0; i < 6; i++) tabletSessions.push(await prepare('hello.txt', tablet));
  const refused = await tablet.post('/v1/download-sessions', { pcId, entryId: (await entry('hello.txt')).entryId });
  assert.deepEqual([refused.status, refused.body.error.code], [429, 'limit_exceeded']);
  assert.match(refused.body.error.message, /Transfers/);
  assert.equal((await fetch(gateway.url + tabletSessions[0].urlPath)).status, 200, 'existing downloads still work');
});

test('a file rewritten between two ranges (same size, old date restored) is never spliced', async () => {
  const path = join(root, 'Documents', 'report.txt');
  const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId: (await entry('Documents')).entryId, pageSize: 50 });
  const report = page.body.entries.find((e) => e.name === 'report.txt');
  const session = (await phone.post('/v1/download-sessions', { pcId, entryId: report.entryId })).body;
  const firstHalf = await (await fetch(gateway.url + session.urlPath, { headers: { range: 'bytes=0-7' } })).text();
  assert.equal(firstHalf, 'quarterl');

  const before = statSync(path, { bigint: true });
  await sleep(50);
  // Same 17 bytes, different content, and the old modified time put back EXACTLY (as an editor or sync tool can).
  if (process.platform === 'win32') {
    execFileSync('powershell', ['-NoProfile', '-Command', `$f = Get-Item -LiteralPath '${path}'; $t = $f.LastWriteTimeUtc; [IO.File]::WriteAllText($f.FullName, "QUARTERLY REPORT\`n"); (Get-Item -LiteralPath '${path}').LastWriteTimeUtc = $t`]);
  } else {
    execFileSync('sh', ['-c', 'cp -p "$1" "$1.ref" && printf "QUARTERLY REPORT\\n" > "$1" && touch -r "$1.ref" "$1" && rm "$1.ref"', 'sh', path]);
  }
  const after = statSync(path, { bigint: true });
  assert.equal(after.mtimeNs, before.mtimeNs, 'modified time is identical to the nanosecond');
  assert.equal(after.size, before.size);
  assert.equal(readFileSync(path, 'utf8'), 'QUARTERLY REPORT\n');

  const stale = await fetch(gateway.url + session.urlPath, { headers: { range: 'bytes=8-', 'if-range': `"${session.revision}"` } });
  assert.equal(stale.status, 409, 'the old session refuses to continue with changed content');
  assert.equal((await stale.json()).error.code, 'revision_changed');

  const fresh = (await phone.post('/v1/download-sessions', { pcId, entryId: report.entryId })).body;
  assert.notEqual(fresh.revision, session.revision);
  const retried = await fetch(gateway.url + fresh.urlPath, { headers: { range: 'bytes=8-', 'if-range': `"${session.revision}"` } });
  assert.equal(retried.status, 200, 'a resume carrying the old validator gets the whole new file, not a 206 tail');
  assert.equal(await retried.text(), 'QUARTERLY REPORT\n');
});
