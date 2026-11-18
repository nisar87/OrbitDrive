// D8/W8: filename search, entry checks (favorites/recents) and two PCs that look identical — real gateway and agent processes.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdirSync, symlinkSync, renameSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, Browser, pair } from './harness.mjs';

let gateway, agentA, agentB, ipcA, phoneA, phoneB, stranger, pcA, pcB, rootA, rootB, dirA, dirB, stateA, stateB;
const win = process.platform === 'win32';
const search = (browser, pc, body) => browser.post(`/v1/pcs/${pc}/searches`, { searchId: 's' + Math.random().toString(36).slice(2, 12), limit: 50, ...body });
const entryOf = async (browser, pc, root, name, parent = null) => {
  const page = await browser.post(`/v1/pcs/${pc}/directory-queries`, { rootId: root, entryId: parent, pageSize: 200 });
  assert.equal(page.status, 200, JSON.stringify(page.body));
  const found = page.body.entries.find((e) => e.name === name);
  assert.ok(found, `${name} is not listed`);
  return found;
};

function fill(dir, hostile) {
  mkdirSync(join(dir, 'Taxes', '2023'), { recursive: true });
  writeFileSync(join(dir, 'Taxes', '2023', 'Return FINAL.pdf'), 'pdf');
  writeFileSync(join(dir, 'report.txt'), 'same name on both PCs');
  writeFileSync(join(dir, 'Ünïcode 文書.txt'), 'x');
  writeFileSync(join(dir, 'secret-visible.txt'), 'x');
  const hidden = join(dir, win ? 'secret-hidden.txt' : '.secret-hidden.txt');
  writeFileSync(hidden, 'x');
  if (win) execFileSync('attrib', ['+h', hidden]);
  if (hostile && !win) {
    const outside = tempDir('search-outside');
    writeFileSync(join(outside, 'secret-outside.txt'), 'x');
    symlinkSync(outside, join(dir, 'escape'));
    symlinkSync(join(outside, 'secret-outside.txt'), join(dir, 'secret-link.txt'));
  }
}

before(async () => {
  dirA = tempDir('search-a'); dirB = tempDir('search-b'); stateA = tempDir('search-state-a'); stateB = tempDir('search-state-b');
  fill(dirA, true); fill(dirB, false);
  writeFileSync(join(dirB, 'only-on-b.txt'), 'x');
  agentCli(stateA, 'roots', 'add', 'Docs', dirA);
  agentCli(stateB, 'roots', 'add', 'Docs', dirB);                    // the same name on another PC
  gateway = await startGateway();
  agentA = await startAgent(gateway, stateA, 'Tower');
  agentB = await startAgent(gateway, stateB, 'Laptop');
  ipcA = await connectIpc(agentA.ready.pipe);
  pcA = agentA.identity.pcId; pcB = agentB.identity.pcId;
  phoneA = await new Browser(gateway, 'owner@example.test').signIn();
  phoneB = await new Browser(gateway, 'owner@example.test').signIn();     // the same account, paired with the other PC
  stranger = await new Browser(gateway, 'stranger@example.test').signIn();
  await pair(agentA, phoneA, 'Phone for Tower');
  await pair(agentB, phoneB, 'Phone for Laptop');
  rootA = (await phoneA.get(`/v1/pcs/${pcA}/roots`)).body.roots[0].rootId;
  rootB = (await phoneB.get(`/v1/pcs/${pcB}/roots`)).body.roots[0].rootId;
});
after(async () => { ipcA?.close(); await agentA?.stop(); await agentB?.stop(); await gateway?.stop(); });

test('search finds by any case and every word, says where each hit is, and the identifiers work like browsed ones', async () => {
  const r = await search(phoneA, pcA, { query: 'return final' });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.equal(r.body.hits.length, 1);
  assert.deepEqual(r.body.hits[0].path, ['Taxes', '2023']);
  assert.equal(r.body.hits[0].rootName, 'Docs');
  assert.equal(r.body.complete, true);
  assert.equal((await search(phoneA, pcA, { query: 'ÜNÏCODE 文書' })).body.hits.length, 1);
  assert.equal((await search(phoneA, pcA, { query: 'return nothing' })).body.hits.length, 0);
  const hit = r.body.hits[0].entry;
  const session = await phoneA.post('/v1/download-sessions', { pcId: pcA, entryId: hit.entryId });
  assert.equal(session.status, 201, JSON.stringify(session.body));
  assert.equal((await fetch(gateway.url + session.body.urlPath)).status, 200);
});

test('hidden items, links and anything outside the shared folder never appear', async () => {
  const names = (await search(phoneA, pcA, { query: 'secret' })).body.hits.map((h) => h.entry.name);
  assert.deepEqual(names, ['secret-visible.txt']);
});

test('limits and bad requests are explicit; a search can be cancelled and cancelling the unknown is harmless', async () => {
  const limited = await search(phoneA, pcA, { query: 't', limit: 1 });
  assert.equal(limited.body.hits.length, 1);
  assert.equal(limited.body.complete, false);
  assert.equal(limited.body.reason, 'limit');
  assert.equal((await search(phoneA, pcA, { query: '   ' })).status, 400);
  assert.equal((await search(phoneA, pcA, { query: 'x'.repeat(101) })).status, 400);
  assert.equal((await phoneA.post(`/v1/pcs/${pcA}/searches`, { searchId: 'bad id', query: 'x', limit: 5 })).status, 400);
  assert.equal((await search(phoneA, pcA, { query: 'x', rootId: 'no-such-root' })).status, 404);
  assert.equal((await phoneA.post(`/v1/pcs/${pcA}/search-cancel`, { searchId: 'nothing-running-1' })).status, 200);
});

test('another account, another PC and a forged identifier cannot search or reach this PC', async () => {
  const refused = (r) => [403, 404].includes(r.status);          // an unknown PC and a forbidden one look the same
  assert.ok(refused(await search(stranger, pcA, { query: 'report' })));
  assert.ok(refused(await search(phoneA, pcB, { query: 'report' })), 'a browser paired with one PC has no access to the other');
  assert.equal((await search(phoneA, pcA, { query: 'report', entryId: 'AAAA' })).status, 400);
  const taxesOnB = await entryOf(phoneB, pcB, rootB, 'Taxes');
  assert.equal((await search(phoneA, pcA, { query: 'return', entryId: taxesOnB.entryId })).status, 400, 'an identifier from another PC is not valid here');
});

test('two PCs with identical names and paths stay separate: results, identifiers and permissions never cross', async () => {
  const a = (await search(phoneA, pcA, { query: 'report' })).body;
  const b = (await search(phoneB, pcB, { query: 'report' })).body;
  assert.equal(a.hits.length, 1); assert.equal(b.hits.length, 1);
  assert.equal(a.hits[0].entry.name, b.hits[0].entry.name);
  assert.notEqual(a.hits[0].entry.entryId, b.hits[0].entry.entryId);
  assert.equal((await search(phoneA, pcA, { query: 'only-on-b' })).body.hits.length, 0, 'PC B\'s files are not found through PC A');
  assert.equal((await search(phoneB, pcB, { query: 'only-on-b' })).body.hits.length, 1);
  // B's identifier is refused by A in every route; A's by B.
  const aId = a.hits[0].entry.entryId, bId = b.hits[0].entry.entryId;
  assert.equal((await phoneA.post(`/v1/pcs/${pcA}/directory-queries`, { rootId: rootA, entryId: bId, pageSize: 5 })).status, 400);
  assert.notEqual((await phoneA.post('/v1/download-sessions', { pcId: pcA, entryId: bId })).status, 201);
  assert.notEqual((await phoneB.post('/v1/download-sessions', { pcId: pcB, entryId: aId })).status, 201);
  const crossCheck = await phoneB.post(`/v1/pcs/${pcB}/entry-checks`, { entryIds: [aId, bId] });
  assert.deepEqual(crossCheck.body.entries.map((e) => e.available), [false, true]);
  assert.notEqual((await phoneA.post(`/v1/pcs/${pcB}/entry-checks`, { entryIds: [bId] })).status, 200);
  const crossArchive = await phoneB.post(`/v1/pcs/${pcB}/archives`, { entryIds: [aId], strict: false });
  assert.equal(crossArchive.status, 400);
});

test('entry checks tell stale favorites and recents from live ones; nothing is returned for the unavailable', async () => {
  const report = await entryOf(phoneA, pcA, rootA, 'report.txt');
  const taxes = await entryOf(phoneA, pcA, rootA, 'Taxes');
  let r = await phoneA.post(`/v1/pcs/${pcA}/entry-checks`, { entryIds: [report.entryId, taxes.entryId, 'forged'] });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.deepEqual(r.body.entries.map((e) => e.available), [true, true, false]);
  assert.equal(r.body.entries[0].entry.name, 'report.txt');
  assert.ok(r.body.entries[2].entry == null);
  renameSync(join(dirA, 'report.txt'), join(dirA, 'report-renamed.txt'));
  r = await phoneA.post(`/v1/pcs/${pcA}/entry-checks`, { entryIds: [report.entryId] });
  assert.equal(r.body.entries[0].available, false);
  assert.ok(r.body.entries[0].entry == null);
  assert.equal((await phoneA.post(`/v1/pcs/${pcA}/entry-checks`, { entryIds: Array.from({ length: 101 }, () => 'x') })).status, 400);
  assert.ok([403, 404].includes((await stranger.post(`/v1/pcs/${pcA}/entry-checks`, { entryIds: [report.entryId] })).status));
  renameSync(join(dirA, 'report-renamed.txt'), join(dirA, 'report.txt'));
});

test('an offline PC is reported as offline, never as having no matches; unsharing a folder stops searching it', async () => {
  const before_ = await search(phoneA, pcA, { query: 'report' });
  assert.equal(before_.body.hits.length, 1);
  assert.ok((await ipcA.send('remove_root', { rootId: rootA })).ok);
  const gone = await search(phoneA, pcA, { query: 'report' });
  assert.equal(gone.status, 200);
  assert.equal(gone.body.hits.length, 0);
  assert.equal(gone.body.rootsSearched, 0, 'no shared folder is left to search');
  assert.equal((await search(phoneA, pcA, { query: 'report', rootId: rootA })).status, 404);
  assert.equal((await phoneA.post(`/v1/pcs/${pcA}/entry-checks`, { entryIds: [(before_.body.hits[0].entry.entryId)] })).body.entries[0].available, false);

  await agentB.stop();
  const offline = await search(phoneB, pcB, { query: 'report' });
  assert.notEqual(offline.status, 200);
  assert.equal(offline.body.error.code, 'pc_offline');
});
