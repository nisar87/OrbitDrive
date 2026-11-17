// D6/W6: "Send to my phone" invitations, end to end with real processes. A file the owner picked on the PC (outside every shared
// folder) is offered to ONE named paired browser; the browser's Inbox shows it; accepting starts a normal checksum-verified download.
// Nothing is copied to the relay until the receiver accepts, and an offer is not a transfer.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdirSync, readFileSync, existsSync, utimesSync, statSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, connectIpc, agentCli, waitFor, tempDir, makeFixtureRoot, Browser, pair, sleep, sha256, freePort } from './harness.mjs';

const dataDir = tempDir('invite-data');
const secret = randomBytes(32).toString('hex');
const port = await freePort();
const boot = (extra = {}) => startGateway({ OrbitDrive__DataDirectory: dataDir, OrbitDrive__DevIdentity__Secret: secret, OrbitDrive__MaxPendingInvitationsPerReceiver: '6', ...extra }, port);

let gateway, agent, ipc, phone, tablet, stranger, pcId, state, outside, sharedRoot;
const send = async (receiver, paths, requestId) => {
  const reply = await ipc.send('send_files', { receiverId: receiver.device.id, paths, ...(requestId ? { requestId } : {}) });
  assert.ok(reply.ok, JSON.stringify(reply));
  return reply.data.results;
};
const inbox = async (browser = phone) => (await browser.get('/v1/invitations')).body.invitations;
const write = (name, content) => { const path = join(outside, name); writeFileSync(path, content); return path; };

before(async () => {
  sharedRoot = makeFixtureRoot();
  outside = tempDir('outside-the-shared-folder');
  state = tempDir('invite-state');
  agentCli(state, 'roots', 'add', 'Share', sharedRoot);
  gateway = await boot();
  agent = await startAgent(gateway, state, 'Invite PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
  phone = await new Browser(gateway, 'owner@example.test').signIn();
  tablet = await new Browser(gateway, 'owner@example.test').signIn();
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agent, phone, 'Phone');
  await pair(agent, tablet, 'Tablet');
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

test('the receiver list names paired browsers and says when each was last seen, without secrets', async () => {
  await phone.get('/v1/pcs');
  const reply = await ipc.send('devices');
  assert.ok(reply.ok);
  const names = reply.data.map((d) => d.name).sort();
  assert.deepEqual(names, ['Phone', 'Tablet']);
  assert.ok(reply.data.find((d) => d.name === 'Phone').lastSeenUnixMs, 'last-seen is reported');
  assert.ok(!JSON.stringify(reply).includes(phone.device.secret));
  // The Explorer menu reads this small file instead of doing network or pipe work while the menu is being built.
  const cache = JSON.parse(readFileSync(join(state, 'receivers.json'), 'utf8'));
  assert.deepEqual(cache.receivers.map((r) => r.name).sort(), ['Phone', 'Tablet']);
  assert.ok(!JSON.stringify(cache).includes(phone.device.secret) && !JSON.stringify(cache).includes(phone.token));
  const lines = readFileSync(join(state, 'receivers.txt'), 'utf8').split('\n');
  assert.deepEqual([lines[0], lines[2]], ['v1', cache.pipe], 'the line format the native Explorer command reads');
  assert.deepEqual(lines.slice(3).filter(Boolean).map((l) => l.split('\t')[2]).sort(), ['Phone', 'Tablet']);
});

test('an offer reaches only the chosen receiver; accepting downloads exactly that file and exposes nothing else', async () => {
  const hostile = "my file ; & $(touch pwn) ‘quoted’ 'single' é 文件.txt";
  const content = randomBytes(200_000);
  const results = await send(phone, [write('plain.txt', 'hello phone\n'), write(hostile, content)]);
  assert.deepEqual(results.map((r) => r.ok), [true, true], JSON.stringify(results));
  assert.ok(!existsSync(join(outside, 'pwn')) && !existsSync('pwn'), 'a file name is never interpreted by a shell');

  const mine = await inbox(phone);
  assert.deepEqual(mine.map((i) => i.name).sort(), [hostile, 'plain.txt'].sort());
  for (const item of mine) assert.deepEqual([item.state, item.pcName, item.receiverName, item.sourceOnline], ['pending', 'Invite PC', 'Phone', true]);
  assert.ok(!JSON.stringify(mine).includes('grant') && !JSON.stringify(mine).includes(outside), 'no grant and no source path is ever shown to a browser');
  assert.deepEqual(await inbox(tablet), [], 'another approved browser of the same account sees nothing');
  assert.equal((await stranger.get('/v1/invitations')).status, 403);

  const target = mine.find((i) => i.name === hostile);
  assert.equal((await tablet.post(`/v1/invitations/${target.invitationId}/accept`)).status, 404, 'the wrong receiver cannot accept');
  assert.equal((await tablet.post(`/v1/invitations/${target.invitationId}/decline`)).status, 404);
  assert.equal((await stranger.post(`/v1/invitations/${target.invitationId}/accept`)).status, 403);

  const accepted = await phone.post(`/v1/invitations/${target.invitationId}/accept`);
  assert.equal(accepted.status, 201, JSON.stringify(accepted.body));
  assert.deepEqual([accepted.body.name, accepted.body.size], [hostile, String(content.length)]);
  const response = await fetch(gateway.url + accepted.body.urlPath);
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-disposition'), /attachment/);
  assert.equal(sha256(Buffer.from(await response.arrayBuffer())), sha256(content), 'bytes identical to the source');
  assert.equal((await inbox(phone)).find((i) => i.invitationId === target.invitationId).state, 'accepted');

  // The one-file grant did not become a folder: the shared folder is unchanged and the file's parent is not listable.
  const roots = (await phone.get(`/v1/pcs/${pcId}/roots`)).body.roots;
  assert.equal(roots.length, 1);
  const names = (await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots[0].rootId, pageSize: 200 })).body.entries.map((e) => e.name);
  assert.ok(!names.includes('plain.txt') && !names.includes(hostile));
  // The download link is a bearer for that file only; changing it never reaches a sibling.
  assert.equal((await fetch(gateway.url + accepted.body.urlPath.replace(/.$/, 'x'))).status, 404);
  await phone.post(`/v1/invitations/${mine.find((i) => i.name === 'plain.txt').invitationId}/decline`);
});

test('retrying the same send is idempotent; folders, missing files and odd paths are reported per file and block nothing else', async () => {
  const file = write('retry.txt', 'once\n');
  const first = await send(phone, [file], 'request-1');
  const again = await send(phone, [file], 'request-1');
  assert.equal(first[0].invitationId, again[0].invitationId, 'same request, same invitation');
  assert.equal((await inbox(phone)).filter((i) => i.name === 'retry.txt').length, 1);

  mkdirSync(join(outside, 'a folder'), { recursive: true });
  const mixed = await send(phone, [join(outside, 'a folder'), join(outside, 'missing.txt'), 'relative/path.txt', file + '\0x', write('fine.txt', 'x')]);
  assert.deepEqual(mixed.map((r) => r.ok), [false, false, false, false, true]);
  assert.match(mixed[0].message, /Folders cannot be sent yet/);
  assert.equal(mixed[1].code, 'not_found');
  assert.equal(mixed[2].code, 'invalid_request');
  assert.ok(!(await inbox(phone)).some((i) => i.name === 'a folder' || i.name === 'missing.txt'));
  if (process.platform === 'win32') assert.equal((await send(phone, ['\\\\server\\share\\x.txt']))[0].code, 'invalid_request', 'network paths are not offered');
  assert.equal((await ipc.send('send_files', { receiverId: phone.device.id, paths: [] })).ok, false);
  assert.equal((await ipc.send('send_files', { receiverId: 'nobody', paths: [file] })).data.results[0].code, 'not_found', 'only a paired receiver can be named');
});

test('decline and sender cancel end an offer; neither can be accepted afterwards', async () => {
  const [a, b] = await send(phone, [write('decline-me.txt', 'a'), write('cancel-me.txt', 'b')]);
  assert.equal((await phone.post(`/v1/invitations/${a.invitationId}/decline`)).status, 200);
  assert.equal((await phone.post(`/v1/invitations/${a.invitationId}/accept`)).status, 409);
  assert.equal((await phone.post(`/v1/invitations/${a.invitationId}/decline`)).status, 409, 'a finished offer cannot be declined again');

  assert.equal((await ipc.send('cancel_invitation', { invitationId: b.invitationId })).ok, true);
  const gone = (await inbox(phone)).find((i) => i.invitationId === b.invitationId);
  assert.equal(gone.state, 'cancelled');
  assert.match(gone.failure, /withdrew/);
  assert.equal((await phone.post(`/v1/invitations/${b.invitationId}/accept`)).status, 409);
  assert.equal((await ipc.send('cancel_invitation', { invitationId: 'not-mine' })).ok, false);
  const listed = (await ipc.send('invitations')).data;
  assert.equal(listed.find((i) => i.invitationId === b.invitationId).state, 'cancelled', 'the PC sees the same truth');
});

test('a file changed, replaced or deleted after it was offered fails recoverably and can be sent again', async () => {
  const changed = write('changes.txt', 'original');
  const resized = write('same-size.txt', 'AAAAAAAA');
  const deleted = write('deleted.txt', 'soon gone');
  const results = await send(phone, [changed, resized, deleted]);
  const id = (name) => results.find((r) => r.name === name).invitationId;

  writeFileSync(changed, 'original, then edited');
  const edited = await phone.post(`/v1/invitations/${id('changes.txt')}/accept`);
  assert.equal(edited.status, 409);
  assert.equal(edited.body.error.code, 'revision_changed');

  const before = statSync(resized);
  writeFileSync(resized, 'BBBBBBBB');                                   // same size...
  utimesSync(resized, before.atime, before.mtime);                     // ...and the modified time put back
  assert.equal((await phone.post(`/v1/invitations/${id('same-size.txt')}/accept`)).status, 409, 'a same-size, same-time rewrite is still caught');

  rmSync(deleted);
  assert.equal((await phone.post(`/v1/invitations/${id('deleted.txt')}/accept`)).status, 404);

  const list = await inbox(phone);
  for (const name of ['changes.txt', 'same-size.txt', 'deleted.txt']) {
    const item = list.find((i) => i.invitationId === id(name));
    assert.equal(item.state, 'cancelled');
    assert.match(item.failure, /sent again|no longer available/);
  }
  // Recovery: sending the changed file again works and delivers the NEW content only.
  const [fresh] = await send(phone, [changed]);
  const accepted = await phone.post(`/v1/invitations/${fresh.invitationId}/accept`);
  assert.equal(accepted.status, 201);
  assert.equal(await (await fetch(gateway.url + accepted.body.urlPath)).text(), 'original, then edited');
});

test('a link or second hard link is not offered; a replaced symlink is not followed at accept time', { skip: process.platform === 'win32' && 'needs symlink privilege on Windows' }, async () => {
  const { symlinkSync, linkSync } = await import('node:fs');
  const real = write('real.txt', 'real');
  symlinkSync(real, join(outside, 'link.txt'));
  assert.equal((await send(phone, [join(outside, 'link.txt')]))[0].code, 'link_not_allowed');
  linkSync(real, join(outside, 'hard.txt'));
  assert.equal((await send(phone, [real]))[0].code, 'link_not_allowed', 'a file with two names is refused');
  rmSync(join(outside, 'hard.txt'));
  const [offered] = await send(phone, [real]);
  rmSync(real);
  symlinkSync(join(sharedRoot, 'hello.txt'), real);                      // swapped for a link to somewhere else
  assert.notEqual((await phone.post(`/v1/invitations/${offered.invitationId}/accept`)).status, 201);
});

test('an offline PC leaves the offer waiting (nothing is copied to the relay); it works again when the PC returns', { timeout: 120_000 }, async () => {
  const [offer] = await send(phone, [write('while-away.txt', 'still on the PC')]);
  ipc.close();
  await agent.stop();
  const waiting = await phone.post(`/v1/invitations/${offer.invitationId}/accept`);
  assert.equal(waiting.status, 503);
  assert.equal(waiting.body.error.code, 'pc_offline');
  const item = (await inbox(phone)).find((i) => i.invitationId === offer.invitationId);
  assert.deepEqual([item.state, item.sourceOnline], ['pending', false], 'still waiting, and honest about the source being away');

  agent = await startAgent(gateway, state, 'Invite PC');
  ipc = await connectIpc(agent.ready.pipe);
  await waitFor(async () => (await inbox(phone)).find((i) => i.invitationId === offer.invitationId)?.sourceOnline, 30_000, 'PC back online');
  const accepted = await phone.post(`/v1/invitations/${offer.invitationId}/accept`);
  assert.equal(accepted.status, 201, 'the grant survived the agent restart');
  assert.equal(await (await fetch(gateway.url + accepted.body.urlPath)).text(), 'still on the PC');
});

test('the Inbox survives a gateway restart and shows no duplicates; no grant is written in clear', { timeout: 120_000 }, async () => {
  const [offer] = await send(phone, [write('across-restart.txt', 'durable')]);
  const stored = readFileSync(join(dataDir, 'invitations.json'), 'utf8');
  assert.ok(!stored.includes(outside) && !stored.includes('across-restart.txt'.repeat(2)), 'the source path is sealed inside the grant, never stored in clear');
  if (process.platform !== 'win32') assert.equal(statSync(join(dataDir, 'invitations.json')).mode & 0o077, 0);
  const before = (await inbox(phone)).filter((i) => i.state === 'pending').map((i) => i.invitationId).sort();
  await gateway.stop();
  agent.drain();
  gateway = await boot();
  await agent.next('connected', 60_000);
  const after = (await inbox(phone)).filter((i) => i.state === 'pending').map((i) => i.invitationId).sort();
  assert.deepEqual(after, before, 'same pending offers, none duplicated, none lost');
  assert.equal((await phone.post(`/v1/invitations/${offer.invitationId}/accept`)).status, 201);
});

test('limits: a PC cannot flood an Inbox, and removing the receiver withdraws what was offered', async () => {
  const names = Array.from({ length: 8 }, (_, i) => write(`flood-${i}.txt`, String(i)));
  const results = await send(tablet, names);
  assert.equal(results.filter((r) => r.ok).length, 6, 'at most MaxPendingInvitationsPerReceiver open offers');
  assert.equal(results.find((r) => !r.ok).code, 'limit_exceeded');

  const reply = await ipc.send('revoke_device', { deviceId: tablet.device.id });
  assert.ok(reply.ok);
  assert.deepEqual(await inbox(tablet), [], 'a receiver removed from this PC no longer sees its offers');
  const mine = (await ipc.send('invitations')).data.filter((i) => i.name.startsWith('flood-'));
  assert.equal(mine.length, 6);
  assert.ok(mine.every((i) => i.state === 'cancelled'), 'offers to a removed receiver are cancelled');
  assert.equal((await ipc.send('send_files', { receiverId: tablet.device.id, paths: [write('late.txt', 'x')] })).data.results[0].code, 'not_found');
});

test('offers expire on their own and an expired offer cannot be accepted', { timeout: 120_000 }, async () => {
  const shortData = tempDir('invite-short');
  const shortState = tempDir('invite-short-state');
  const short = await startGateway({ OrbitDrive__DataDirectory: shortData, OrbitDrive__InvitationTtlSeconds: '2' });
  const shortAgent = await startAgent(short, shortState, 'Short PC');
  const shortIpc = await connectIpc(shortAgent.ready.pipe);
  try {
    const user = await new Browser(short, 'owner@example.test').signIn();
    await pair(shortAgent, user, 'Phone');
    const reply = await shortIpc.send('send_files', { receiverId: user.device.id, paths: [write('expires.txt', 'x')] });
    const id = reply.data.results[0].invitationId;
    assert.equal((await user.get('/v1/invitations')).body.invitations[0].state, 'pending');
    await sleep(2600);
    assert.equal((await user.get('/v1/invitations')).body.invitations[0].state, 'expired');
    const late = await user.post(`/v1/invitations/${id}/accept`);
    assert.equal(late.status, 410);
    assert.equal(late.body.error.code, 'expired');
  } finally { shortIpc.close(); await shortAgent.stop(); await short.stop(); }
});

test('the Explorer hand-over spool: a selection written while the app was not running is sent; a stale one is discarded, never sent late', { timeout: 90_000 }, async () => {
  const { renameSync } = await import('node:fs');
  const outbox = join(state, 'outbox');
  const spool = (name, request, ageMs = 0) => {
    const temp = join(outbox, name + '.tmp');
    writeFileSync(temp, JSON.stringify(request));
    if (ageMs) { const when = new Date(Date.now() - ageMs); utimesSync(temp, when, when); }
    renameSync(temp, join(outbox, name + '.send'));
  };
  spool('fresh', { receiverId: phone.device.id, paths: [write('from-spool.txt', 'spooled')], requestId: 'spool-1' });
  const item = await waitFor(async () => (await inbox(phone)).find((i) => i.name === 'from-spool.txt'), 20_000, 'spooled selection offered');
  assert.equal(item.state, 'pending');
  await waitFor(() => !readdirSync(outbox).some((f) => f.endsWith('.send')), 10_000, 'spool file consumed');
  let offers = (await ipc.send('status')).data.offers;
  assert.ok(offers.some((o) => o.name === 'from-spool.txt' && o.ok), 'the PC records what it did');

  spool('stale', { receiverId: phone.device.id, paths: [write('too-late.txt', 'late')], requestId: 'spool-2' }, 10 * 60_000);
  await waitFor(() => !readdirSync(outbox).some((f) => f.endsWith('.send')), 20_000, 'stale spool removed');
  assert.ok(!(await inbox(phone)).some((i) => i.name === 'too-late.txt'), 'a stale selection is never sent');
  offers = (await ipc.send('status')).data.offers;
  assert.ok(offers.some((o) => o.name === 'too-late.txt' && !o.ok && /Send it again/.test(o.message)));

  writeFileSync(join(outbox, 'junk.send'), '{ not json');
  const when = new Date(Date.now() - 60_000); utimesSync(join(outbox, 'junk.send'), when, when);
  await waitFor(() => !existsSync(join(outbox, 'junk.send')), 20_000, 'malformed spool removed');
});

test('the command-line hand-over (classic Send to) while the app is not running leaves data in the spool, and the app sends it when it starts', { timeout: 90_000 }, async () => {
  const { agentCliRaw } = await import('./harness.mjs');
  const lonely = tempDir('invite-lonely-state');
  const path = write(process.platform === 'win32' ? "with spaces & 'odd' ; name.txt" : 'with spaces & "odd" name.txt', 'x'); // a double quote cannot be part of a Windows file name
  const result = agentCliRaw(lonely, 'ctl', 'send_files', 'some-receiver', path);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /not running/);
  const files = readdirSync(join(lonely, 'outbox')).filter((f) => f.endsWith('.send'));
  assert.equal(files.length, 1);
  const request = JSON.parse(readFileSync(join(lonely, 'outbox', files[0]), 'utf8'));
  assert.deepEqual(request.paths, [path], 'the path is carried as data, unchanged');
  const other = await startAgent(gateway, lonely, 'Unpaired PC');
  const otherIpc = await connectIpc(other.ready.pipe);
  try {
    const offers = await waitFor(async () => { const o = (await otherIpc.send('status')).data.offers; return o.length ? o : null; }, 30_000, 'the spooled selection to be handled');
    assert.equal(offers[0].ok, false, 'an unpaired receiver is reported, not silently dropped');
    assert.equal(readdirSync(join(lonely, 'outbox')).filter((f) => f.endsWith('.send')).length, 0);
  } finally { otherIpc.close(); await other.stop(); }
});

test('the local command set is closed: unknown commands are refused', async () => {
  assert.equal((await ipc.send('run', { command: 'calc.exe' })).ok, false);
  assert.equal((await ipc.send('send_files', { receiverId: phone.device.id, paths: 'not-a-list' })).ok, false);
});
