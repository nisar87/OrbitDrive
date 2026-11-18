// W8/D8 in a real browser against the real gateway and agent: ZIP download of a folder and a multi-selection (extracted and compared
// with an independent reader), honest omissions, cross-PC filename search, favorites that become "Unavailable", and one browser on two PCs.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync, readFileSync, renameSync, symlinkSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, agentCli, tempDir, makeFixtureRoot, repoRoot, readZip } from '../../../tests/integration/harness.mjs';

let gateway, link, agentA, agentB, rootA, rootB, stateA, stateB;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
const sha = (b) => createHash('sha256').update(b).digest('hex');
const rows = (page) => page.locator('tbody tr');
/** On a narrow screen the folder tree (with Favorites) is a drawer behind the Folders button; on a wide one it is always there. */
const showTree = async (page) => {
  const tree = explorer(page).getByRole('navigation', { name: 'PCs and shared folders' });
  if (!(await tree.isVisible())) await explorer(page).getByRole('button', { name: 'Folders', exact: true }).click();
  await expect(tree).toBeVisible();
};
const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  rootA = makeFixtureRoot();
  rootB = tempDir('e2e-archive-rootB');
  writeFileSync(join(rootB, 'report.txt'), 'the other PC');
  mkdirSync(join(rootB, 'Projects'));
  writeFileSync(join(rootB, 'Projects', 'plan.txt'), 'plan');
  stateA = tempDir('e2e-archive-stateA'); stateB = tempDir('e2e-archive-stateB');
  agentCli(stateA, 'roots', 'add', 'Share', rootA);
  agentCli(stateB, 'roots', 'add', 'Laptop files', rootB);
  gateway = await startGateway();
  link = await startProxy(gateway, 5199);
  agentA = await startAgent(gateway, stateA, 'Tower PC');
  agentB = await startAgent(gateway, stateB, 'Laptop');
});
test.afterAll(async () => { await agentA?.stop(); await agentB?.stop(); link?.stop(); await gateway?.stop(); });

async function pairWith(page, agent, first) {
  if (first) {
    await page.goto('/');
    await page.getByLabel('Email').fill('owner@example.test');
    await page.getByRole('button', { name: 'Sign in' }).click();
  }
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  const name = page.getByLabel(/name/i).first();
  if (first && await name.isVisible().catch(() => false)) await name.fill('Test phone').catch(() => undefined);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
}
const openPc = async (page, pc, share) => {
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: new RegExp(share) }).click();
  await expect(explorer(page)).toBeVisible();
};

test.describe.configure({ mode: 'serial' });

test('a folder and a multi-selection become ZIPs whose contents match the PC byte for byte; omissions are shown', async ({ page }) => {
  await pairWith(page, agentA, true);
  await openPc(page, 'Tower PC', 'Share');
  await expect(rows(page).filter({ hasText: 'Documents' })).toBeVisible();

  // One folder via the context menu.
  await rows(page).filter({ hasText: 'Documents' }).locator('td.size').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Download as ZIP' }).click();
  const job = page.getByTestId('archive-job').first();
  await expect(job.getByText(/The ZIP is ready/)).toBeVisible({ timeout: 60_000 });
  await expect(job.getByTestId('archive-omissions')).toHaveCount(0);
  let downloading = page.waitForEvent('download');
  await job.getByRole('button', { name: 'Download' }).click();
  let zip = readZip(readFileSync(await (await downloading).path()));
  expect(zip.map((e) => e.name).sort()).toEqual(['Documents/', 'Documents/Nested/', 'Documents/Nested/Deeper/', 'Documents/Nested/Deeper/deep.txt', 'Documents/report.txt']);
  const report = zip.find((e) => e.name === 'Documents/report.txt');
  expect(sha(report.data())).toBe(sha(readFileSync(join(rootA, 'Documents', 'report.txt'))));
  await job.getByRole('button', { name: 'Discard' }).click();
  await expect(page.getByTestId('archive-job')).toHaveCount(0);

  // Several items, including a file with non-ASCII and emoji names, and a folder.
  await rows(page).filter({ hasText: 'data.bin' }).locator('td.size').click();
  await rows(page).filter({ hasText: 'اردو' }).locator('td.size').click({ modifiers: ['Control'] });
  await rows(page).filter({ hasText: 'emoji' }).locator('td.size').click({ modifiers: ['Control'] });
  await page.getByRole('button', { name: 'Download as ZIP' }).click();
  const multi = page.getByTestId('archive-job').first();
  await expect(multi.getByText(/The ZIP is ready/)).toBeVisible({ timeout: 60_000 });
  downloading = page.waitForEvent('download');
  await multi.getByRole('button', { name: 'Download' }).click();
  zip = readZip(readFileSync(await (await downloading).path()));
  expect(zip.map((e) => e.name).sort()).toEqual(['data.bin', 'emoji 😀 notes.txt', 'اردو فائل.txt']);
  expect(sha(zip.find((e) => e.name === 'data.bin').data())).toBe(sha(readFileSync(join(rootA, 'data.bin'))));
  await multi.getByRole('button', { name: 'Discard' }).click();

  // A link cannot be followed safely: the ZIP is still offered, but it says plainly that it is incomplete.
  mkdirSync(join(rootA, 'Linked'), { recursive: true });
  writeFileSync(join(rootA, 'Linked', 'kept.txt'), 'kept');
  symlinkSync(join(rootA, 'Linked', 'kept.txt'), join(rootA, 'Linked', 'a-link.txt'));
  await page.getByRole('button', { name: 'Refresh' }).click();
  await rows(page).filter({ hasText: 'Linked' }).locator('td.size').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Download as ZIP' }).click();
  const partial = page.getByTestId('archive-job').first();
  await expect(partial.getByText(/incomplete/)).toBeVisible({ timeout: 60_000 });
  const omissions = partial.getByTestId('archive-omissions');
  await expect(omissions).toContainText('could not be included');
  await expect(omissions).toContainText('_OrbitDrive_omitted.txt');
  downloading = page.waitForEvent('download');
  await partial.getByRole('button', { name: 'Download' }).click();
  zip = readZip(readFileSync(await (await downloading).path()));
  expect(zip.map((e) => e.name).sort()).toEqual(['Linked/', 'Linked/kept.txt', '_OrbitDrive_omitted.txt']);
  expect(zip.find((e) => e.name === '_OrbitDrive_omitted.txt').data().toString('utf8')).toMatch(/could NOT be included[\s\S]*Linked - 1 link\(s\)/);
  await page.screenshot({ path: join(shots, `archive-ready-${test.info().project.name}.png`) });
});

test('the same browser pairs a second PC; search covers both, shows an offline PC as offline, and favorites turn Unavailable after a rename', async ({ page }) => {
  await pairWith(page, agentA, true);
  // Second PC from the same signed-in browser: Pair a PC → code → approve on the PC. The browser keeps its single device identity.
  await page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: 'Devices', exact: true }).click();
  await page.getByRole('button', { name: 'Pair a PC' }).first().click();
  await pairWith(page, agentB, false);
  const list = page.getByRole('region', { name: "This account's PCs" });
  await expect(list.getByRole('button', { name: /Laptop files/ })).toBeVisible();
  await expect(list.getByRole('button', { name: /Share/ })).toBeVisible();

  await openPc(page, 'Tower PC', 'Share');
  await explorer(page).getByRole('button', { name: 'Search' }).click();
  const panel = explorer(page).getByRole('search', { name: 'Search by file name' });
  await panel.getByRole('combobox', { name: 'Where to search' }).selectOption({ value: 'all' });
  await panel.getByRole('searchbox', { name: 'Search by file name' }).fill('report');
  await panel.getByRole('button', { name: 'Search', exact: true }).click();
  // Both PCs answer with a file of the same name; they are kept apart by PC.
  await expect(panel.getByRole('heading', { name: /Tower PC/ })).toBeVisible({ timeout: 30_000 });
  await expect(panel.getByRole('heading', { name: /Laptop/ })).toBeVisible({ timeout: 30_000 });
  await expect(panel.getByRole('button', { name: /^Open report\.txt in .* on Tower PC$/ })).toHaveCount(1);
  await expect(panel.getByRole('button', { name: /^Open report\.txt in .* on Laptop$/ })).toHaveCount(1);
  await page.screenshot({ path: join(shots, `search-two-pcs-${test.info().project.name}.png`) });

  // The laptop goes away: the search says so instead of reporting "no matches".
  await agentB.stop();
  await panel.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(panel.getByText(/offline/i).first()).toBeVisible({ timeout: 30_000 });
  await expect(panel.getByText(/No matches/i)).toHaveCount(0);
  await panel.getByRole('button', { name: 'Close search' }).click();

  // Favorites: add, rename on the PC, see "Unavailable", remove.
  await rows(page).filter({ hasText: 'hello.txt' }).locator('td.size').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Add to favorites' }).click();
  await showTree(page);
  const favorites = explorer(page).getByRole('group', { name: 'Favorites' });
  await expect(favorites.getByRole('button', { name: /^hello\.txt Tower PC/ })).toBeEnabled();
  renameSync(join(rootA, 'hello.txt'), join(rootA, 'hello-renamed.txt'));
  await page.reload();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await showTree(page);
  const after = explorer(page).getByRole('group', { name: 'Favorites' });
  await expect(after.getByText('Unavailable')).toBeVisible({ timeout: 30_000 });
  await expect(after.getByRole('button', { name: /^hello\.txt Tower PC/ })).toBeDisabled();
  await after.getByRole('button', { name: /Remove hello\.txt from favorites/ }).click();
  await expect(explorer(page).getByRole('group', { name: 'Favorites' })).toHaveCount(0);
});
