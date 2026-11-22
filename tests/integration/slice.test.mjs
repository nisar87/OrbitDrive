// First vertical slice: pair -> browse an approved root -> download with ranges, against real gateway + agent processes.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync, symlinkSync, linkSync, renameSync, utimesSync, statSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { startGateway, startAgent, agentCli, waitFor, tempDir, makeFixtureRoot, Browser, pair, sha256, sleep, rssBytes, writeLargeFile, WEB_ORIGIN, repoRoot } from './harness.mjs';

const LARGE_MIB = Number(process.env.ORBIT_LARGE_MIB || 64);
let gateway, agent, owner, stranger, root, outside, stateDir, pcId, rootId, pairingInfo;
const secretsSeen = [];

async function list(browser, entryId = null, extra = {}) {
  return browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId, pageSize: 200, ...extra });
}
async function entry(browser, ...path) {
  let entryId = null, found = null;
  for (const name of path) {
    const page = await list(browser, entryId);
    assert.equal(page.status, 200, JSON.stringify(page.body));
    found = page.body.entries.find((e) => e.name === name);
    assert.ok(found, `entry ${name} not listed`);
    entryId = found.entryId;
  }
  return found;
}
async function session(browser, ...path) {
  const target = await entry(browser, ...path);
  const created = await browser.post('/v1/download-sessions', { pcId, entryId: target.entryId });
  assert.equal(created.status, 201, JSON.stringify(created.body));
  secretsSeen.push(created.body.capability);
  return created.body;
}
// The agent holds a share-read handle while it streams. After a transfer ends or is cut, the handle must be
// released promptly; on Windows an open handle blocks deletion, so this also proves the release (within 10 s).
async function removeWhenReleased(path) {
  for (let attempt = 0; ; attempt++) {
    try { rmSync(path); return; }
    catch (e) { if (e.code !== 'EBUSY' && e.code !== 'EPERM' || attempt >= 50) throw e; await sleep(200); }
  }
}
const download = (s, headers = {}, method = 'GET') => fetch(gateway.url + s.urlPath, { method, headers });

before(async () => {
  root = makeFixtureRoot();
  outside = tempDir('outside');
  writeFileSync(join(outside, 'secret.txt'), 'TOP SECRET outside the shared root\n');
  stateDir = tempDir('state');
  agentCli(stateDir, 'roots', 'add', 'Test Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, stateDir);
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
});

after(async () => {
  await agent?.stop();
  await gateway?.stop();
});

describe('identity and pairing (R05, R06, R53)', () => {
  test('service states its transport honestly: TLS terminates at the relay, no E2EE claim (R39)', async () => {
    const meta = await (await fetch(`${gateway.url}/v1/meta`)).json();
    assert.equal(meta.relayTerminatesTls, true);
    assert.equal(meta.endToEndEncrypted, false);
  });

  test('requests without a valid account are rejected', async () => {
    assert.equal((await fetch(`${gateway.url}/v1/pcs`)).status, 401);
    assert.equal((await fetch(`${gateway.url}/v1/pcs`, { headers: { authorization: 'Bearer dev.e30.AAAA' } })).status, 401);
    const forged = owner.token.split('.'); forged[1] = Buffer.from(JSON.stringify({ uid: 'someone-else', email: 'x@y', exp: 9999999999 })).toString('base64url');
    assert.equal((await fetch(`${gateway.url}/v1/pcs`, { headers: { authorization: `Bearer ${forged.join('.')}` } })).status, 401);
  });

  test('a signed-in but unpaired browser gets nothing', async () => {
    assert.equal((await owner.get('/v1/pcs')).status, 403);
    assert.equal((await owner.get(`/v1/pcs/${pcId}/roots`)).status, 403);
  });

  test('wrong pairing secret is refused and five failures burn the request', async () => {
    agent.send('pair');
    const pairing = await agent.next('pairing');
    const [id, secret] = pairing.code.split('-');
    secretsSeen.push(secret);
    for (let i = 0; i < 5; i++) assert.equal((await owner.post(`/v1/pairing-requests/${id}/claim`, { secret: 'WRONGWRONG', browserName: 'x' })).status, 403);
    assert.equal((await owner.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'x' })).status, 410, 'correct secret no longer works after lockout');
    assert.equal((await owner.post('/v1/pairing-requests/NOSUCH/claim', { secret, browserName: 'x' })).status, 404);
  });

  test('claim alone grants nothing; access starts only after local PC approval', async () => {
    agent.send('pair');
    const pairing = await agent.next('pairing');
    assert.ok(pairing.url.startsWith(`${WEB_ORIGIN}/pair#`), 'secret must live in the URL fragment');
    const [id, secret] = pairing.code.split('-');
    secretsSeen.push(secret);
    const claim = await owner.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'Owner phone' }, { origin: WEB_ORIGIN });
    assert.equal(claim.status, 200);
    secretsSeen.push(claim.body.deviceSecret);
    const shown = await agent.next('pairing_claimed');
    assert.equal(shown.confirmationCode, claim.body.confirmationCode);
    assert.equal(shown.accountLabel, 'owner@example.test');
    assert.equal(shown.browserOrigin, WEB_ORIGIN, 'the PC is told which web app address asked (R53)');
    assert.match(shown.confirmationCode, /^\d{6}$/);

    owner.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
    assert.equal((await owner.get('/v1/pcs')).status, 403, 'pending device must not work before approval');
    assert.equal((await owner.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'again' })).status, 409, 'one-use');
    assert.equal((await stranger.get(`/v1/pairing-requests/${id}`)).status, 404, 'other accounts cannot observe the request');
    assert.equal((await owner.get(`/v1/pairing-requests/${id}`)).body.state, 'claimed');

    agent.send('approve');
    assert.equal((await agent.next('pairing_result')).state, 'approved');
    const status = await owner.get(`/v1/pairing-requests/${id}`);
    assert.deepEqual([status.body.state, status.body.pcId], ['approved', pcId]);
    const pcs = await owner.get('/v1/pcs');
    assert.equal(pcs.status, 200);
    assert.deepEqual(pcs.body.pcs.map((p) => [p.pcId, p.name, p.online]), [[pcId, 'Test PC', true]]);
    assert.equal(typeof pcs.body.pcs[0].lastSeenUnixMs, 'string', '64-bit values travel as strings');
  });

  test('a denied request creates no access', async () => {
    const second = await new Browser(gateway, 'owner@example.test').signIn();
    agent.send('pair');
    const [id, secret] = (await agent.next('pairing')).code.split('-');
    const claim = await second.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'Denied tablet' });
    await agent.next('pairing_claimed');
    agent.send('deny');
    assert.equal((await agent.next('pairing_result')).state, 'denied');
    second.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
    assert.equal((await second.get('/v1/pcs')).status, 403);
  });

  test('another account cannot claim a PC that already has an owner, nor reach it', async () => {
    agent.send('pair');
    const [id, secret] = (await agent.next('pairing')).code.split('-');
    assert.equal((await stranger.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'Evil' })).status, 403);
    assert.equal((await stranger.get(`/v1/pcs/${pcId}/roots`)).status, 403);
    // Even with the owner's device credential, a different account is refused.
    const hijack = await fetch(`${gateway.url}/v1/pcs/${pcId}/roots`, { headers: { authorization: `Bearer ${stranger.token}`, 'x-orbitdrive-device': `${owner.device.id}.${owner.device.secret}` } });
    assert.equal(hijack.status, 403);
  });

  test('agent channel rejects browsers and unverifiable identities', async () => {
    const viaBrowser = await new Promise((resolve, reject) => {
      const request = httpRequest(`${gateway.url}/v1/agent-control`, { headers: { Origin: 'https://evil.example', Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==' } });
      request.on('response', (r) => { r.resume(); resolve(r.statusCode); });
      request.on('upgrade', () => resolve(101));
      request.on('error', reject);
      request.end();
    });
    assert.equal(viaBrowser, 400, 'a browser-originated WebSocket must not be upgraded');
    const ws = new WebSocket(gateway.url.replace('http', 'ws') + '/v1/agent-control');
    const messages = [];
    const closed = new Promise((resolve) => { ws.onclose = resolve; ws.onerror = resolve; });
    ws.onmessage = (m) => {
      messages.push(JSON.parse(m.data));
      if (messages.length === 1) ws.send(JSON.stringify({ v: 1, id: 'hello', kind: 'req', action: 'hello', payload: { protocolVersion: 1, pcId, publicKeySpki: 'AAAA', signature: 'AAAA', name: 'Impostor', agentVersion: '0', capabilities: [] } }));
    };
    await closed;
    assert.equal(messages[0].action, 'challenge');
    assert.equal(messages[1].error.code, 'unauthenticated');
    assert.equal((await owner.get('/v1/pcs')).body.pcs[0].name, 'Test PC', 'impostor did not replace the real PC');
  });

  test('an agent speaking an unsupported protocol version is told to update (R50)', async () => {
    const ws = new WebSocket(gateway.url.replace('http', 'ws') + '/v1/agent-control');
    const messages = [];
    const closed = new Promise((resolve) => { ws.onclose = resolve; ws.onerror = resolve; });
    ws.onmessage = (m) => {
      messages.push(JSON.parse(m.data));
      if (messages.length === 1) ws.send(JSON.stringify({ v: 1, id: 'hello', kind: 'req', action: 'hello', payload: { protocolVersion: 99, pcId: 'x', publicKeySpki: 'AAAA', signature: 'AAAA', name: 'Future', agentVersion: '9', capabilities: [] } }));
    };
    await closed;
    assert.equal(messages[1].error.code, 'unsupported_version');
  });
});

describe('browsing an approved root (R08, R09, R15, R17)', () => {
  test('only the owner-approved root is exposed, read-only', async () => {
    const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
    assert.equal(roots.status, 200);
    assert.equal(roots.body.roots.length, 1);
    assert.deepEqual([roots.body.roots[0].name, roots.body.roots[0].readOnly, roots.body.roots[0].available], ['Test Share', true, true]);
    rootId = roots.body.roots[0].rootId;
  });

  test('listing matches the real folder, including Unicode, with folders first', async () => {
    const page = await list(owner);
    assert.equal(page.status, 200);
    const real = readdirSync(root, { withFileTypes: true });
    assert.deepEqual(page.body.entries.map((e) => e.name).sort(), real.map((e) => e.name).sort());
    assert.deepEqual(page.body.entries.slice(0, 2).map((e) => e.kind), ['directory', 'directory']);
    const data = page.body.entries.find((e) => e.name === 'data.bin');
    assert.equal(data.size, String(statSync(join(root, 'data.bin')).size));
    assert.ok(Math.abs(Number(data.modifiedUnixMs) - statSync(join(root, 'data.bin')).mtimeMs) < 2000);
    assert.ok(page.body.entries.some((e) => e.name === 'اردو فائل.txt') && page.body.entries.some((e) => e.name === 'emoji 😀 notes.txt'));
    assert.ok(!JSON.stringify(page.body).includes(root), 'no absolute Windows/Unix path leaves the PC');
  });

  test('nested navigation returns breadcrumbs', async () => {
    const deeper = await entry(owner, 'Documents', 'Nested', 'Deeper');
    const page = await list(owner, deeper.entryId);
    assert.deepEqual(page.body.entries.map((e) => e.name), ['deep.txt']);
    assert.deepEqual(page.body.breadcrumbs.map((b) => b.name), ['Test Share', 'Documents', 'Nested', 'Deeper']);
    const up = await list(owner, page.body.breadcrumbs[1].entryId);
    assert.ok(up.body.entries.some((e) => e.name === 'report.txt'));
  });

  test('large folders page without loss or duplication', async () => {
    const many = await entry(owner, 'Many');
    const names = [];
    let cursor = null, pages = 0;
    do {
      const page = await list(owner, many.entryId, { cursor });
      assert.equal(page.status, 200);
      assert.ok(page.body.entries.length <= 200);
      names.push(...page.body.entries.map((e) => e.name));
      cursor = page.body.nextCursor ?? null;
      pages++;
    } while (cursor);
    assert.equal(pages, 3);
    assert.equal(new Set(names).size, 450);
    assert.equal((await list(owner, many.entryId, { pageSize: 100000 })).body.entries.length, 200, 'page size is clamped');
  });

  test('forged, tampered and path-shaped identifiers are refused', async () => {
    const real = (await entry(owner, 'Documents')).entryId;
    const tampered = real.slice(0, -2) + (real.endsWith('AA') ? 'BB' : 'AA');
    for (const bad of [tampered, '../../etc/passwd', 'C:\\Windows\\System32', '/etc', outside, '\\\\server\\share', Buffer.from(JSON.stringify({ t: 'e', r: rootId, s: ['..'], d: true })).toString('base64url'), 'x'.repeat(9000)]) {
      const response = await list(owner, bad);
      assert.equal(response.status, 400, `identifier ${bad.slice(0, 30)} must be rejected`);
      assert.equal(response.body.error.code, 'invalid_request');
    }
    assert.equal((await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: 'not-a-root', pageSize: 10 })).status, 404);
    assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: outside })).status, 400);
    assert.equal((await owner.post(`/v1/pcs/${pcId}/directory-queries`, '{"rootId":')).status, 400);
  });

  test('links are never listed or followed, even when they point outside the root', async (t) => {
    try {
      symlinkSync(join(outside, 'secret.txt'), join(root, 'Documents', 'link-to-secret.txt'), 'file');
      symlinkSync(outside, join(root, 'Documents', 'link-to-outside'), process.platform === 'win32' ? 'junction' : 'dir');
    } catch (e) { return t.skip(`cannot create links here: ${e.code}`); }
    const documents = await entry(owner, 'Documents');
    const page = await list(owner, documents.entryId);
    assert.deepEqual(page.body.entries.map((e) => e.name).sort(), ['Nested', 'report.txt']);
    assert.equal(page.body.skippedLinks, 2);
  });

  test('hard-linked files are refused on Windows, where one file can surface under several paths', async (t) => {
    if (process.platform !== 'win32') return t.skip('link-count check uses Win32 handle information; non-Windows hosts are development-only');
    linkSync(join(outside, 'secret.txt'), join(root, 'hardlink.txt'));
    const target = await entry(owner, 'hardlink.txt');
    const response = await owner.post('/v1/download-sessions', { pcId, entryId: target.entryId });
    assert.deepEqual([response.status, response.body.error.code], [403, 'link_not_allowed']);
    rmSync(join(root, 'hardlink.txt'));
  });

  test('ancestor swapped for a link after authorisation cannot redirect a prepared download', async (t) => {
    mkdirSync(join(root, 'Swap'));
    writeFileSync(join(root, 'Swap', 'target.txt'), 'inside-content!!');
    mkdirSync(join(outside, 'decoy'));
    writeFileSync(join(outside, 'decoy', 'target.txt'), 'OUTSIDE-SECRET!!'); // same size
    const stamp = new Date('2026-01-01T00:00:00Z');
    utimesSync(join(root, 'Swap', 'target.txt'), stamp, stamp);
    utimesSync(join(outside, 'decoy', 'target.txt'), stamp, stamp);
    const prepared = await session(owner, 'Swap', 'target.txt');
    renameSync(join(root, 'Swap'), join(root, 'Swap-original'));
    try { symlinkSync(join(outside, 'decoy'), join(root, 'Swap'), process.platform === 'win32' ? 'junction' : 'dir'); }
    catch (e) { return t.skip(`cannot create links here: ${e.code}`); }
    const response = await download(prepared);
    assert.equal(response.status, 403);
    assert.equal((await response.json()).error.code, 'link_not_allowed');
    rmSync(join(root, 'Swap'), { recursive: false, force: true });
  });
});

describe('downloads, ranges and revisions (R18-R21, R59)', () => {
  let data, dataSession;
  before(async () => {
    data = readFileSync(join(root, 'data.bin'));
    dataSession = await session(owner, 'data.bin');
  });

  test('session describes the exact file revision', () => {
    assert.equal(dataSession.size, String(data.length));
    assert.equal(dataSession.revision, `sha256-${sha256(data)}`);
    assert.equal(dataSession.name, 'data.bin');
  });

  test('HEAD first, then full GET: correct headers and identical bytes', async () => {
    const head = await download(dataSession, {}, 'HEAD');
    assert.equal(head.status, 200);
    assert.equal(head.headers.get('content-length'), String(data.length));
    assert.equal(head.headers.get('accept-ranges'), 'bytes');
    assert.equal(head.headers.get('etag'), `"${dataSession.revision}"`);
    const full = await download(dataSession);
    assert.equal(full.status, 200, 'HEAD must not consume the capability');
    assert.equal(full.headers.get('content-length'), String(data.length));
    assert.equal(full.headers.get('content-type'), 'application/octet-stream');
    assert.match(full.headers.get('content-disposition'), /^attachment; filename="?data\.bin"?/);
    assert.equal(full.headers.get('cache-control'), 'no-store');
    assert.equal(full.headers.get('referrer-policy'), 'no-referrer');
    assert.equal(full.headers.get('x-content-type-options'), 'nosniff');
    assert.equal(full.headers.get('content-encoding'), null);
    assert.equal(sha256(Buffer.from(await full.arrayBuffer())), sha256(data));
  });

  test('single ranges: explicit, open-ended, suffix and clamped', async () => {
    const cases = [['bytes=0-99', 0, 100], ['bytes=1000-', 1000, data.length - 1000], ['bytes=-100', data.length - 100, 100], ['bytes=299990-999999999', 299990, 10], ['bytes=5-5', 5, 1]];
    for (const [header, start, length] of cases) {
      const response = await download(dataSession, { range: header });
      assert.equal(response.status, 206, header);
      assert.equal(response.headers.get('content-range'), `bytes ${start}-${start + length - 1}/${data.length}`);
      assert.equal(response.headers.get('content-length'), String(length));
      assert.deepEqual(Buffer.from(await response.arrayBuffer()), data.subarray(start, start + length), header);
    }
  });

  test('unsatisfiable ranges answer 416 with the full length', async () => {
    for (const header of [`bytes=${data.length}-`, 'bytes=999999999-1000000000', 'bytes=-0']) {
      const response = await download(dataSession, { range: header });
      assert.equal(response.status, 416, header);
      assert.equal(response.headers.get('content-range'), `bytes */${data.length}`);
    }
  });

  test('malformed, overflowing and multi-range headers fall back to the full file, never a wrong slice', async () => {
    for (const header of ['bytes=abc-def', 'bytes=10-5', 'bytes=0-10,20-30', 'items=0-5', 'bytes=99999999999999999999999-', 'bytes=-']) {
      const response = await download(dataSession, { range: header });
      assert.equal(response.status, 200, header);
      assert.equal(sha256(Buffer.from(await response.arrayBuffer())), sha256(data), header);
    }
  });

  test('If-Range resumes only against the same strong validator', async () => {
    const match = await download(dataSession, { range: 'bytes=100-199', 'if-range': `"${dataSession.revision}"` });
    assert.equal(match.status, 206);
    await match.arrayBuffer();
    const stale = await download(dataSession, { range: 'bytes=100-199', 'if-range': '"sha256-0000"' });
    assert.equal(stale.status, 200, 'validator mismatch must restart, not splice');
    assert.equal((await stale.arrayBuffer()).byteLength, data.length);
  });

  test('zero-byte and Unicode-named files download correctly', async () => {
    const empty = await session(owner, 'empty.bin');
    const emptyResponse = await download(empty);
    assert.deepEqual([emptyResponse.status, emptyResponse.headers.get('content-length')], [200, '0']);
    assert.equal((await emptyResponse.arrayBuffer()).byteLength, 0);
    assert.equal((await download(empty, { range: 'bytes=0-0' })).status, 416);
    const urdu = await session(owner, 'اردو فائل.txt');
    const response = await download(urdu);
    assert.ok(response.headers.get('content-disposition').includes(`filename*=UTF-8''${encodeURIComponent('اردو فائل.txt')}`));
    assert.equal(Buffer.from(await response.arrayBuffer()).toString(), 'یہ ایک آزمائشی فائل ہے\n');
  });

  test('capabilities cannot be guessed, and folders or foreign accounts cannot start downloads', async () => {
    assert.equal((await fetch(`${gateway.url}/v1/downloads/${'A'.repeat(43)}`)).status, 404);
    assert.equal((await fetch(`${gateway.url}/v1/downloads/short`)).status, 404);
    const folder = await entry(owner, 'Documents');
    assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: folder.entryId })).status, 400);
    const file = await entry(owner, 'hello.txt');
    assert.equal((await stranger.post('/v1/download-sessions', { pcId, entryId: file.entryId })).status, 403);
    assert.equal((await fetch(`${gateway.url}/v1/agent-streams/nope`, { method: 'POST', headers: { authorization: 'Bearer nope' }, body: 'x' })).status, 403);
  });

  test('a changed source is never appended to an old download (R19)', async () => {
    writeFileSync(join(root, 'mutable.txt'), 'version one of the file');
    const prepared = await session(owner, 'mutable.txt');
    assert.equal((await download(prepared, { range: 'bytes=0-6' })).status, 206);
    // Windows refuses to write a file the agent still has open; the agent closes its read handle just after the response ends.
    await waitFor(() => { try { writeFileSync(join(root, 'mutable.txt'), 'VERSION TWO OF THE FILE'); return true; } catch (e) { if (e.code === 'EBUSY' || e.code === 'EPERM') return false; throw e; } }, 10_000, 'the agent to release the file'); // same size, new content and mtime
    utimesSync(join(root, 'mutable.txt'), new Date(), new Date(Date.now() + 5000));
    const resumed = await download(prepared, { range: 'bytes=7-', 'if-range': `"${prepared.revision}"` });
    assert.equal(resumed.status, 409);
    assert.equal((await resumed.json()).error.code, 'revision_changed');
    const fresh = await session(owner, 'mutable.txt');
    assert.notEqual(fresh.revision, prepared.revision);
    assert.equal(await (await download(fresh)).text(), 'VERSION TWO OF THE FILE');
  });

  test('CORS allows only the configured web origin', async () => {
    const preflight = (origin) => fetch(`${gateway.url}/v1/pcs`, { method: 'OPTIONS', headers: { origin, 'access-control-request-method': 'GET', 'access-control-request-headers': 'authorization' } });
    assert.equal((await preflight(WEB_ORIGIN)).headers.get('access-control-allow-origin'), WEB_ORIGIN);
    assert.equal((await preflight('https://evil.example')).headers.get('access-control-allow-origin'), null);
  });

  test(`large file (${LARGE_MIB} MiB) streams intact through a slow receiver with bounded memory (R18, R20, R45)`, { timeout: 30 * 60_000 }, async () => {
    const path = join(root, 'large.bin');
    const expected = await writeLargeFile(path, LARGE_MIB);
    const started = Date.now();
    const prepared = await session(owner, 'large.bin');
    const prepareMs = Date.now() - started;
    assert.equal(prepared.revision, `sha256-${expected}`);
    const baseline = { gateway: rssBytes(gateway.pid), agent: rssBytes(agent.pid) };
    const peak = { ...baseline };
    const sampler = setInterval(() => { peak.gateway = Math.max(peak.gateway, rssBytes(gateway.pid)); peak.agent = Math.max(peak.agent, rssBytes(agent.pid)); }, 250);
    const transferStart = Date.now();
    agent.drain();
    const response = await download(prepared);
    assert.equal(response.status, 200);
    const hash = createHash('sha256');
    let received = 0, stalled = false;
    for await (const chunk of response.body) {
      hash.update(chunk);
      received += chunk.length;
      if (!stalled && received > (LARGE_MIB << 20) / 4) { stalled = true; await sleep(4000); } // receiver stops reading: senders must wait, not buffer
    }
    clearInterval(sampler);
    const seconds = (Date.now() - transferStart - 4000) / 1000;
    const metrics = {
      sizeMiB: LARGE_MIB, prepareHashMs: prepareMs, transferSeconds: seconds, throughputMiBps: +(LARGE_MIB / seconds).toFixed(1),
      gatewayRssGrowthMiB: +((peak.gateway - baseline.gateway) / 2 ** 20).toFixed(1), agentRssGrowthMiB: +((peak.agent - baseline.agent) / 2 ** 20).toFixed(1),
      platform: process.platform, node: process.version,
    };
    console.log('METRICS ' + JSON.stringify(metrics));
    mkdirSync(join(repoRoot, 'artifacts'), { recursive: true });
    writeFileSync(join(repoRoot, 'artifacts', `transfer-metrics-${process.platform}.json`), JSON.stringify(metrics, null, 2));
    assert.equal(received, LARGE_MIB << 20);
    assert.equal(hash.digest('hex'), expected, 'received bytes must hash to the source');
    assert.ok(metrics.gatewayRssGrowthMiB < 96, `gateway memory grew ${metrics.gatewayRssGrowthMiB} MiB`);
    assert.ok(metrics.agentRssGrowthMiB < 96, `agent memory grew ${metrics.agentRssGrowthMiB} MiB`);
    const finished = await agent.next('stream_finished');
    assert.deepEqual([finished.outcome, finished.bytes], ['sent_to_relay', String(LARGE_MIB << 20)]);
    await removeWhenReleased(path);
  });
});

describe('pause, removal, restart and revocation (R06, R14, R51)', () => {
  test('pausing on the PC stops access until resumed', async () => {
    agent.send('pause'); await agent.next('paused');
    const paused = await list(owner);
    assert.deepEqual([paused.status, paused.body.error.code], [503, 'unavailable']);
    agent.send('resume'); await agent.next('resumed');
    assert.equal((await list(owner)).status, 200);
  });

  test('agent restart keeps the same identity and the grant; while down the PC is reported offline', async () => {
    const prepared = await session(owner, 'hello.txt');
    await agent.stop();
    await sleep(300);
    assert.equal((await owner.get('/v1/pcs')).body.pcs[0].online, false);
    const offline = await list(owner);
    assert.deepEqual([offline.status, offline.body.error.code], [503, 'pc_offline']);
    assert.equal((await download(prepared)).status, 503, 'no fictitious cloud copy while the PC is off');
    agent = await startAgent(gateway, stateDir);
    assert.equal(agent.identity.pcId, pcId);
    assert.equal(agent.identity.ownerBound, true);
    assert.equal((await list(owner)).status, 200, 'no re-pairing needed');
    const afterRestart = (await owner.get('/v1/pcs')).body.pcs;
    assert.deepEqual(afterRestart.map((p) => [p.pcId, p.online]), [[pcId, true]], 'reconnect must not create a duplicate PC');
    assert.ok(Number(agent.identity.generation) > 1, 'a reconnect is a new connection generation');
    assert.equal(await (await download(prepared)).text(), 'Hello from OrbitDrive\n');
  });

  test('resource limits: per-owner stream cap, request body cap and control-message cap (R42)', async () => {
    const path = join(root, 'slots.bin');
    await writeLargeFile(path, 48);
    const prepared = await session(owner, 'slots.bin');
    const aborts = [], open = [];
    for (let i = 0; i < 4; i++) {
      const controller = new AbortController();
      aborts.push(controller);
      const response = await fetch(gateway.url + prepared.urlPath, { signal: controller.signal }); // body deliberately left unread
      assert.equal(response.status, 200);
      open.push(response);
    }
    const fifth = await download(prepared);
    assert.equal(fifth.status, 429);
    assert.equal((await fifth.json()).error.code, 'limit_exceeded');
    aborts.forEach((a) => a.abort());
    let recovered = 0;
    for (let i = 0; i < 50 && recovered !== 206; i++) { await sleep(100); const retry = await download(prepared, { range: 'bytes=0-9' }); recovered = retry.status; await retry.arrayBuffer(); }
    assert.equal(recovered, 206, 'slots are released when receivers go away');
    await removeWhenReleased(path);

    const oversized = await fetch(`${gateway.url}/v1/download-sessions`, { method: 'POST', headers: owner.headers({ 'content-type': 'application/json' }), body: JSON.stringify({ pcId, entryId: 'x'.repeat(100_000) }) });
    assert.equal(oversized.status, 413);

    const ws = new WebSocket(gateway.url.replace('http', 'ws') + '/v1/agent-control');
    const messages = [];
    const closed = new Promise((resolve) => { ws.onclose = resolve; ws.onerror = resolve; });
    ws.onmessage = (m) => { messages.push(JSON.parse(m.data)); if (messages.length === 1) ws.send(JSON.stringify({ v: 1, id: 'hello', kind: 'req', action: 'hello', payload: { junk: 'x'.repeat(400_000) } })); };
    await closed;
    assert.equal(messages.length, 1, 'an oversized control message ends the connection without a reply');
  });

  test('revoking the browser cuts an active transfer and all later access', async () => {
    const path = join(root, 'revoke.bin');
    await writeLargeFile(path, 48);
    const prepared = await session(owner, 'revoke.bin');
    agent.drain();
    const response = await download(prepared);
    assert.equal(response.status, 200);
    const reader = response.body.getReader();
    let received = (await reader.read()).value.length;
    const revoked = await owner.call('DELETE', '/v1/browser-devices/current');
    assert.equal(revoked.status, 200);
    let failed = false;
    try { for (;;) { const { done, value } = await reader.read(); if (done) break; received += value.length; } }
    catch { failed = true; }
    assert.ok(failed && received < 48 << 20, `stream must be cut short (got ${received} bytes, failed=${failed})`);
    assert.equal((await download(prepared)).status, 404);
    assert.equal((await owner.get('/v1/pcs')).status, 403);
    assert.equal((await list(owner)).status, 403);
    assert.notEqual((await agent.next('stream_finished')).outcome, 'sent_to_relay', 'the PC must not report a revoked transfer as sent');
    await removeWhenReleased(path);
  });

  test('removing a root on the PC ends access to it, including prepared downloads', async () => {
    const again = await new Browser(gateway, 'owner@example.test').signIn();
    pairingInfo = await pair(agent, again, 'Second phone');
    secretsSeen.push(again.device.secret, pairingInfo.pairing.code.split('-')[1]);
    const prepared = await session(again, 'hello.txt');
    agentCli(stateDir, 'roots', 'remove', rootId);
    agent.send('reload'); await agent.next('roots_reloaded');
    assert.equal((await again.get(`/v1/pcs/${pcId}/roots`)).body.roots.length, 0);
    assert.equal((await list(again)).status, 404);
    assert.equal((await download(prepared)).status, 404);
  });

  test('no pairing secret, device secret, account token or download capability reaches the logs (R38)', () => {
    const logs = gateway.log + agent.log;
    for (const secret of [...secretsSeen, owner.token, stranger.token]) assert.ok(secret && !gateway.log.includes(secret), 'secret leaked into gateway log');
    assert.ok(!logs.includes('TOP SECRET'));
  });
});

test('pairing requests expire (separate short-TTL gateway)', async () => {
  const shortGateway = await startGateway({ OrbitDrive__PairingTtlSeconds: '1', OrbitDrive__DownloadSessionTtlSeconds: '1' });
  const state = tempDir('state2');
  const share = makeFixtureRoot();
  agentCli(state, 'roots', 'add', 'Short', share);
  const shortAgent = await startAgent(shortGateway, state, 'Short PC');
  try {
    const user = await new Browser(shortGateway, 'ttl@example.test').signIn();
    shortAgent.send('pair');
    const [id, secret] = (await shortAgent.next('pairing')).code.split('-');
    await sleep(1300);
    assert.equal((await user.post(`/v1/pairing-requests/${id}/claim`, { secret, browserName: 'late' })).status, 410);
    // Claimed but approved too late: still no access.
    shortAgent.send('pair');
    const [id2, secret2] = (await shortAgent.next('pairing')).code.split('-');
    const claim = await user.post(`/v1/pairing-requests/${id2}/claim`, { secret: secret2, browserName: 'slow approval' });
    assert.equal(claim.status, 200);
    await shortAgent.next('pairing_claimed');
    await sleep(1300);
    shortAgent.send('approve');
    assert.equal((await shortAgent.next('error')).code, 'Expired');
    user.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
    assert.equal((await user.get('/v1/pcs')).status, 403);
  } finally { await shortAgent.stop(); await shortGateway.stop(); }
});

test('download capabilities expire', async () => {
  const shortGateway = await startGateway({ OrbitDrive__DownloadSessionTtlSeconds: '1' });
  const state = tempDir('state3');
  const share = makeFixtureRoot();
  agentCli(state, 'roots', 'add', 'Short', share);
  const shortAgent = await startAgent(shortGateway, state, 'Short PC');
  try {
    const user = await new Browser(shortGateway, 'ttl@example.test').signIn();
    await pair(shortAgent, user);
    const id = shortAgent.identity.pcId;
    const roots = await user.get(`/v1/pcs/${id}/roots`);
    const page = await user.post(`/v1/pcs/${id}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
    const file = page.body.entries.find((e) => e.name === 'hello.txt');
    const created = await user.post('/v1/download-sessions', { pcId: id, entryId: file.entryId });
    assert.equal((await fetch(shortGateway.url + created.body.urlPath)).status, 200);
    await sleep(1300);
    assert.equal((await fetch(shortGateway.url + created.body.urlPath)).status, 410);
  } finally { await shortAgent.stop(); await shortGateway.stop(); }
});

test('production refuses development identities and refuses to start without a real verifier (R57)', async () => {
  await assert.rejects(startGateway({ ASPNETCORE_ENVIRONMENT: 'Production' }), /Development identities cannot be enabled in Production/);
  await assert.rejects(startGateway({ ASPNETCORE_ENVIRONMENT: 'Production', OrbitDrive__DevIdentity__Enabled: 'false' }), /No production identity verifier/);
  await assert.rejects(startGateway({ ASPNETCORE_ENVIRONMENT: 'Production', OrbitDrive__DevIdentity__Enabled: 'false', OrbitDrive__Firebase__ProjectId: 'orbitdrive-dev', OrbitDrive__Firebase__KeysUrl: 'http://127.0.0.1:1/keys', OrbitDrive__AllowedWebOrigins__0: 'https://orbitdrive.example' }), /Google's signing keys/);
});
