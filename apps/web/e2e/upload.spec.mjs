// W9/D9 in a real browser against the real gateway and agent: uploads only into owner-enabled folders, picked with the system file
// picker, verified byte for byte on the PC's disk; names that exist are never overwritten; interruptions resume; turning uploads off stops them.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync, readFileSync, readdirSync, existsSync } from 'node:fs';
import { createHash, randomBytes } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, connectIpc, agentCli, tempDir, repoRoot } from '../../../tests/integration/harness.mjs';

let gateway, link, agent, drop, docs, state, dropId;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
const sha = (b) => createHash('sha256').update(b).digest('hex');
const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });
const rows = (page) => page.locator('tbody tr');
const parse = (out) => out.match(/\(([A-Za-z0-9_-]+)\)/)[1];
const upload = (name, data) => ({ name, mimeType: 'application/octet-stream', buffer: Buffer.isBuffer(data) ? data : Buffer.from(data) });

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  drop = tempDir('e2e-upload-drop'); docs = tempDir('e2e-upload-docs'); state = tempDir('e2e-upload-state');
  mkdirSync(join(drop, 'Inbox'));
  writeFileSync(join(drop, 'Inbox', 'existing.txt'), 'original');
  writeFileSync(join(docs, 'private.txt'), 'read only');
  dropId = parse(agentCli(state, 'roots', 'add', 'Drop box', drop));
  agentCli(state, 'roots', 'add', 'Read only docs', docs);
  agentCli(state, 'roots', 'uploads', dropId, 'on', '200', '8');
  gateway = await startGateway();
  link = await startProxy(gateway, 5199);
  agent = await startAgent(gateway, state, 'Tower PC');
});
test.afterAll(async () => { await agent?.stop(); link?.stop(); await gateway?.stop(); });

async function pairAndOpen(page, share) {
  await page.goto('/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: new RegExp(share) }).click();
  await expect(explorer(page)).toBeVisible();
}

test.describe.configure({ mode: 'serial' });
test.beforeEach(() => link.throttleUploads(0));

test('only an owner-enabled folder offers Upload; a picked file arrives byte for byte; existing names are never replaced', async ({ page }, info) => {
  await pairAndOpen(page, 'Drop box');
  await expect(rows(page).filter({ hasText: 'Inbox' })).toBeVisible();
  const button = page.getByRole('button', { name: 'Upload files here' });
  await expect(button).toBeVisible();

  // Into a sub-folder, with a name that is taken: refused by default.
  await rows(page).filter({ hasText: 'Inbox' }).locator('td.size').dblclick();
  await expect(rows(page).filter({ hasText: 'existing.txt' })).toBeVisible();
  const photo = randomBytes(5 * 1024 * 1024 + 123);
  await page.getByTestId('upload-input').setInputFiles([upload('photo.jpg', photo), upload('existing.txt', 'REPLACEMENT')]);
  const confirm = page.getByTestId('upload-confirm');
  await expect(confirm).toBeVisible();
  await expect(page.getByTestId('upload-destination')).toContainText('Inbox');
  await expect(confirm).toContainText('2 files');
  await expect(confirm.getByRole('radio', { name: /Do not send it/ })).toBeChecked();
  await page.screenshot({ path: join(shots, `upload-confirm-${info.project.name}.png`) });
  await confirm.getByRole('button', { name: /Upload 2 files/ }).click();
  const jobs = page.getByTestId('upload-job');
  await expect(jobs.filter({ hasText: 'photo.jpg' }).getByTestId('upload-saved')).toBeVisible({ timeout: 60_000 });
  await expect(jobs.filter({ hasText: 'existing.txt' }).getByRole('alert')).toContainText(/already/i, { timeout: 30_000 });
  await expect(rows(page).filter({ hasText: 'photo.jpg' })).toBeVisible();     // the listing refreshed by itself
  expect(sha(readFileSync(join(drop, 'Inbox', 'photo.jpg')))).toBe(sha(photo));
  expect(readFileSync(join(drop, 'Inbox', 'existing.txt'), 'utf8')).toBe('original');
  await page.screenshot({ path: join(shots, `upload-done-${info.project.name}.png`) });
  await page.getByRole('button', { name: 'Clear finished' }).click();

  // The same name again, this time keeping both.
  await page.getByTestId('upload-input').setInputFiles([upload('existing.txt', 'second copy')]);
  await page.getByTestId('upload-confirm').getByRole('radio', { name: /Keep both/ }).check();
  await page.getByTestId('upload-confirm').getByRole('button', { name: /Upload 1 file/ }).click();
  await expect(page.getByTestId('upload-saved')).toContainText('existing (2).txt', { timeout: 30_000 });
  expect(readFileSync(join(drop, 'Inbox', 'existing.txt'), 'utf8')).toBe('original');
  expect(readFileSync(join(drop, 'Inbox', 'existing (2).txt'), 'utf8')).toBe('second copy');
  expect(readdirSync(drop).filter((n) => n.startsWith('.orbitdrive'))).toEqual([]);    // no partial files are left behind
  await page.getByRole('button', { name: 'Clear finished' }).click();

  // A file over the owner's limit is not even offered.
  await page.getByTestId('upload-input').setInputFiles([upload('huge.bin', Buffer.alloc(9 * 1024 * 1024))]);
  await expect(page.getByTestId('upload-toobig')).toContainText('huge.bin');
  await expect(page.getByRole('button', { name: /Upload 0 files/ })).toBeDisabled();
  await page.getByTestId('upload-confirm').getByRole('button', { name: 'Cancel' }).click();
  expect(existsSync(join(drop, 'Inbox', 'huge.bin'))).toBe(false);
});

test('a read-only folder has no Upload button', async ({ page }) => {
  await pairAndOpen(page, 'Read only docs');
  await expect(rows(page).filter({ hasText: 'private.txt' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload files here' })).toHaveCount(0);
  await expect(page.getByTestId('upload-input')).toHaveCount(0);
});

test('a dropped connection is resumed without sending the file again from the start', async ({ page }) => {
  await pairAndOpen(page, 'Drop box');
  const data = randomBytes(7 * 1024 * 1024);
  link.throttleUploads(1 << 20);                                 // about 1 MB/s: the transfer is certainly still running when the link drops
  await page.getByTestId('upload-input').setInputFiles([upload('resumed.bin', data)]);
  await page.getByTestId('upload-confirm').getByRole('button', { name: /Upload 1 file/ }).click();
  const job = page.getByTestId('upload-job').filter({ hasText: 'resumed.bin' });
  await expect(job.getByRole('progress').or(job.locator('progress'))).toBeVisible({ timeout: 30_000 });
  link.cut();
  link.throttleUploads(0);                                       // the connection comes back at full speed
  await expect(job.getByTestId('upload-saved')).toBeVisible({ timeout: 90_000 });
  expect(sha(readFileSync(join(drop, 'resumed.bin')))).toBe(sha(data));
});

test('turning uploads off on the PC stops an upload in progress and keeps nothing', async ({ page }) => {
  await pairAndOpen(page, 'Drop box');
  const data = randomBytes(6 * 1024 * 1024);
  await page.getByTestId('upload-input').setInputFiles([upload('stopped.bin', data)]);
  const ipc = await connectIpc(agent.ready.pipe);               // the running agent is told, like the owner console does
  const reply = await ipc.send('set_root_uploads', { rootId: dropId, enabled: false });
  expect(reply.ok).toBe(true);
  ipc.close?.();
  await page.getByTestId('upload-confirm').getByRole('button', { name: /Upload 1 file/ }).click();
  const job = page.getByTestId('upload-job').filter({ hasText: 'stopped.bin' });
  await expect(job.getByRole('alert')).toContainText(/not allowed uploads|turned them off|turned off/i, { timeout: 30_000 });
  await expect(job.getByTestId('upload-saved')).toHaveCount(0);
  expect(existsSync(join(drop, 'stopped.bin'))).toBe(false);
  expect(readdirSync(drop).filter((n) => n.startsWith('.orbitdrive'))).toEqual([]);
});
