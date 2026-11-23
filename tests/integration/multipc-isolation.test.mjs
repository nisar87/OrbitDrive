// R32: one browser, two PCs. Listing, switching, and independence: revoking one leaves the other, and one PC's ids never open the other's files.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, agentCli, tempDir, Browser, pair, waitFor } from './harness.mjs';

let gateway, a, b, browser, rootA, rootB, stateA, stateB;
const folder = (tag) => { const d = tempDir(`multi-${tag}`); mkdirSync(join(d, 'docs')); writeFileSync(join(d, 'docs', `${tag}-only.txt`), `${tag} data`); return d; };
const list = (pcId, rootId, entryId = null) => browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, entryId, pageSize: 200 });
const rootOf = async (pcId) => (await browser.get(`/v1/pcs/${pcId}/roots`)).body.roots[0];
const names = (r) => r.body.entries.map((e) => e.name);

before(async () => {
  rootA = folder('alpha'); rootB = folder('beta');
  stateA = tempDir('multi-a'); stateB = tempDir('multi-b');
  agentCli(stateA, 'roots', 'add', 'Share', rootA);
  agentCli(stateB, 'roots', 'add', 'Share', rootB);   // deliberately the SAME root name on both PCs
  gateway = await startGateway();
  a = await startAgent(gateway, stateA, 'Office PC');
  b = await startAgent(gateway, stateB, 'Home PC');
  browser = await new Browser(gateway, 'multi@example.test').signIn();
  await pair(a, browser, 'One browser');
  await pair(b, browser, 'One browser');
});
after(async () => { await a?.stop(); await b?.stop(); await gateway?.stop(); });

test('the browser lists both PCs and switching between them shows each PC\'s own files', async () => {
  const pcs = (await browser.get('/v1/pcs')).body.pcs;
  assert.deepEqual(pcs.map((p) => p.name).sort(), ['Home PC', 'Office PC']);
  assert.ok(pcs.every((p) => p.online));
  const ra = await rootOf(a.identity.pcId), rb = await rootOf(b.identity.pcId);
  const topA = await list(a.identity.pcId, ra.rootId), topB = await list(b.identity.pcId, rb.rootId);
  assert.deepEqual(names(topA), ['docs']); assert.deepEqual(names(topB), ['docs']);
  const docsA = await list(a.identity.pcId, ra.rootId, topA.body.entries[0].entryId);
  const docsB = await list(b.identity.pcId, rb.rootId, topB.body.entries[0].entryId);
  assert.deepEqual(names(docsA), ['alpha-only.txt']);
  assert.deepEqual(names(docsB), ['beta-only.txt']);
});

test('same root names and same paths on both PCs do not let one PC\'s ids open the other', async () => {
  const ra = await rootOf(a.identity.pcId), rb = await rootOf(b.identity.pcId);
  const topA = await list(a.identity.pcId, ra.rootId);
  const entryA = topA.body.entries[0].entryId;
  const crossRoot = await list(b.identity.pcId, ra.rootId);
  assert.ok(crossRoot.status >= 400, `PC B must not accept PC A's root id (got ${crossRoot.status})`);
  const crossEntry = await list(b.identity.pcId, rb.rootId, entryA);
  assert.ok(crossEntry.status >= 400 || crossEntry.body?.entries?.length === 0, 'PC B must not resolve PC A\'s sealed entry id');
  if (crossEntry.status === 200) assert.ok(!names(crossEntry).includes('alpha-only.txt'));
});

test('revoking one PC leaves the other working, and the revoked one is refused', async () => {
  const idA = a.identity.pcId, idB = b.identity.pcId;
  const ra = await rootOf(idA), rb = await rootOf(idB);
  const revoked = await browser.call('DELETE', `/v1/pcs/${idA}`);
  assert.equal(revoked.status, 200);
  await waitFor(async () => (await list(idA, ra.rootId)).status >= 400, 15000, 'the revoked PC is refused');
  assert.equal((await list(idB, rb.rootId)).status, 200, 'the other PC is untouched');
  const remaining = (await browser.get('/v1/pcs')).body.pcs.map((p) => p.pcId);
  assert.deepEqual(remaining, [idB]);
});
