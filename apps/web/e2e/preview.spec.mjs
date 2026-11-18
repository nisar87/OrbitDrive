// W7/D7 in a real browser against the real gateway and agent: pictures, text, PDF and media preview without downloading;
// hostile names and content stay text; unsupported or damaged files fall back to Download; thumbnails are lazy and optional.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync, readFileSync, copyFileSync } from 'node:fs';
import { createHash, randomBytes } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, connectIpc, agentCli, tempDir, repoRoot, useThumbnailHelper } from '../../../tests/integration/harness.mjs';

const realDecoder = useThumbnailHelper();
let gateway, agent, ipc, link, state, root;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
const images = join(repoRoot, 'tests/fixtures/images');
const documents = join(repoRoot, 'tests/fixtures/documents');
const HOSTILE = `&lt;img src=x onerror=window.__xss=1&gt; 'quote' ; é 文.txt`;
const sha = (b) => createHash('sha256').update(b).digest('hex');

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  root = tempDir('e2e-preview-root');
  state = tempDir('e2e-preview-state');
  copyFileSync(join(images, 'photo-640x480.png'), join(root, 'photo.png'));
  copyFileSync(join(images, 'photo-640x480.jpg'), join(root, 'second.jpg'));
  copyFileSync(join(documents, 'sample.pdf'), join(root, 'sample.pdf'));
  copyFileSync(join(documents, 'malformed.pdf'), join(root, 'broken.pdf'));
  writeFileSync(join(root, 'notes.txt'), '<img src=x onerror="window.__xss=1"><script>window.__xss=2</script>\nplain line two\n');
  writeFileSync(join(root, HOSTILE), 'a hostile name is only text\n');
  writeFileSync(join(root, 'big.log'), 'line of a very large log\n'.repeat(120_000));       // ~3 MB
  writeFileSync(join(root, 'clip.mp4'), Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from('ftypisom'), randomBytes(100_000)]));
  writeFileSync(join(root, 'data.bin'), randomBytes(50_000));
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  link = await startProxy(gateway, 5199);
  agent = await startAgent(gateway, state, 'Tower PC');
  ipc = await connectIpc(agent.ready.pipe);
});
test.afterAll(async () => { ipc?.close(); await agent?.stop(); link?.stop(); await gateway?.stop(); });

const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });
const previewWindow = (page) => page.getByRole('dialog', { name: 'Preview' });
const taskbar = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name });

async function pair(page) {
  await page.goto('/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(explorer(page)).toBeVisible();
  await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
}
async function backToExplorer(page) { await taskbar(page, 'File Explorer').click(); await expect(explorer(page)).toBeVisible(); }

test.describe.configure({ mode: 'serial' });

test('a picture opens in Preview without a download, steps to the next picture and zooms; the original is only downloaded on request', async ({ page }, info) => {
  const transferStarts = [];
  page.on('request', (r) => { if (r.url().includes('/v1/download-sessions') && r.method() === 'POST' && !(r.postData() ?? '').includes('variant')) transferStarts.push(r.url()); });
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Preview photo.png' }).click();
  await expect(previewWindow(page)).toBeVisible();
  const picture = previewWindow(page).locator('img[data-full="true"]');
  await expect(picture).toBeVisible({ timeout: 20_000 });
  await expect.poll(() => picture.evaluate((img) => img.naturalWidth)).toBe(640);
  await expect(previewWindow(page)).toContainText('photo.png');
  await expect(previewWindow(page)).toContainText('1 of 2');                 // the two pictures of this folder
  await page.screenshot({ path: join(shots, `preview-image-${info.project.name}.png`) });
  const before = (await picture.boundingBox()).width;
  await previewWindow(page).getByRole('button', { name: '100%' }).click();
  await expect.poll(async () => (await picture.boundingBox()).width).toBe(640);
  await previewWindow(page).getByRole('button', { name: 'Zoom in' }).click();
  await expect.poll(async () => (await picture.boundingBox()).width).toBeGreaterThan(640);
  void before;
  await previewWindow(page).getByRole('button', { name: 'Next file' }).click();
  await expect(previewWindow(page)).toContainText('second.jpg');
  await expect(previewWindow(page).locator('img[data-full="true"]')).toBeVisible({ timeout: 20_000 });
  expect(transferStarts, 'previewing never starts a transfer').toEqual([]);

  const downloading = page.waitForEvent('download');
  await previewWindow(page).getByRole('button', { name: 'Download original' }).click();
  const download = await downloading;
  expect(sha(readFileSync(await download.path()))).toBe(sha(readFileSync(join(images, 'photo-640x480.jpg'))));
});

test('text is shown as characters only, however hostile its content or name; a huge file shows just its start', async ({ page }, info) => {
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Download notes.txt' }).click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Preview' }).click();
  await expect(previewWindow(page).locator('pre')).toContainText('plain line two');
  await expect(previewWindow(page).locator('pre')).toContainText('<script>window.__xss=2</script>');   // literal characters
  expect(await previewWindow(page).locator('pre img, pre script').count()).toBe(0);
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();
  await page.screenshot({ path: join(shots, `preview-text-${info.project.name}.png`) });

  await backToExplorer(page);
  await explorer(page).getByRole('button', { name: `Download ${HOSTILE}` }).click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Preview' }).click();
  await expect(previewWindow(page).locator('.preview-name bdi')).toHaveText(HOSTILE);
  await expect(previewWindow(page).locator('pre')).toContainText('only text');
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();

  await backToExplorer(page);
  await explorer(page).getByRole('button', { name: 'Download big.log' }).click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Preview' }).click();
  await expect(previewWindow(page)).toContainText('Showing the first 256 KB');
  const shown = await previewWindow(page).locator('pre').evaluate((pre) => pre.textContent.length);
  expect(shown).toBeLessThanOrEqual(262_144);
});

test('a PDF is painted page by page without scripts or links; a damaged PDF falls back to Download', async ({ page }, info) => {
  const failures = [];
  page.on('pageerror', (e) => failures.push(e.message));
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Preview sample.pdf' }).click();
  await expect(previewWindow(page)).toContainText('Page 1 of 2', { timeout: 30_000 });
  const canvas = previewWindow(page).locator('canvas');
  await expect.poll(async () => canvas.evaluate((c) => { const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let dark = 0; for (let i = 0; i < d.length; i += 4) if (d[i] < 100) dark++; return dark; }), { timeout: 30_000 }).toBeGreaterThan(200);
  await page.screenshot({ path: join(shots, `preview-pdf-${info.project.name}.png`) });
  await previewWindow(page).getByRole('button', { name: 'Next page' }).click();
  await expect(previewWindow(page)).toContainText('Page 2 of 2');
  expect(await previewWindow(page).locator('a, iframe, embed, object, form').count()).toBe(0);

  await backToExplorer(page);
  await explorer(page).getByRole('button', { name: 'Preview broken.pdf' }).click();
  await expect(previewWindow(page).getByRole('alert')).toContainText(/could not be opened|could not be drawn/, { timeout: 30_000 });
  await expect(previewWindow(page).getByRole('button', { name: 'Download the original' })).toBeVisible();
  expect(failures.filter((m) => /xss|alert/i.test(m))).toEqual([]);
});

test('media the browser cannot play, and files with no preview, are explained and offer Download; originals stay untouched', async ({ page }) => {
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Preview clip.mp4' }).click();
  await expect(previewWindow(page).getByRole('alert')).toContainText(/cannot play/, { timeout: 30_000 });
  await expect(previewWindow(page).getByRole('button', { name: 'Download the original' })).toBeVisible();

  await backToExplorer(page);
  await explorer(page).getByRole('button', { name: 'Download data.bin' }).click({ button: 'right' });
  await expect(page.getByRole('menuitem', { name: 'Preview' })).toHaveCount(0);        // no preview offered for an unknown type
  await page.keyboard.press('Escape');
  expect(sha(readFileSync(join(root, 'data.bin'))).length).toBe(64);
  expect(readFileSync(join(root, 'photo.png')).equals(readFileSync(join(images, 'photo-640x480.png')))).toBe(true);
});

test('Icons view shows thumbnails lazily and quietly gives up when the PC has them switched off', async ({ page }, info) => {
  const thumbRequests = [];
  page.on('request', (r) => { if (r.url().includes('/v1/download-sessions') && r.method() === 'POST' && (r.postData() ?? '').includes('thumb:')) thumbRequests.push(r.postData()); });
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Icons', exact: true }).click();
  const tile = explorer(page).locator('tr', { hasText: 'photo.png' });
  if (realDecoder) {
    const img = tile.locator('img');
    await expect(img).toBeVisible({ timeout: 30_000 });
    await expect.poll(() => img.evaluate((i) => i.naturalWidth)).toBeGreaterThan(0);
    expect(await img.evaluate((i) => Math.max(i.naturalWidth, i.naturalHeight))).toBeLessThanOrEqual(256);
    await page.screenshot({ path: join(shots, `preview-icons-${info.project.name}.png`) });
  } else {
    await expect.poll(() => thumbRequests.length, { timeout: 15_000 }).toBeGreaterThan(0);   // requested, though the stand-in output is not a real picture
  }
  expect(thumbRequests.length, 'only pictures in view are requested, a few at a time').toBeLessThanOrEqual(4);

  expect((await ipc.send('set_thumbnails', { enabled: false })).ok).toBe(true);
  await page.reload();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await explorer(page).getByRole('button', { name: 'Icons', exact: true }).click();
  const before = thumbRequests.length;
  await expect(explorer(page).locator('tr', { hasText: 'photo.png' })).toBeVisible();
  await page.waitForTimeout(1500);
  expect(thumbRequests.length - before, 'a PC without thumbnails is asked once, not once per picture').toBeLessThanOrEqual(2);
  await expect(explorer(page).locator('tr', { hasText: 'photo.png' }).locator('img')).toHaveCount(0);
  expect((await ipc.send('set_thumbnails', { enabled: true })).ok).toBe(true);
});

test('a revoked browser can no longer open previews', async ({ page }) => {
  await pair(page);
  await explorer(page).getByRole('button', { name: 'Preview photo.png' }).click();
  await expect(previewWindow(page).locator('img[data-full="true"]')).toBeVisible({ timeout: 20_000 });
  const devices = (await ipc.send('devices')).data;
  expect((await ipc.send('revoke_device', { deviceId: devices[devices.length - 1].deviceId })).ok).toBe(true);
  await previewWindow(page).getByRole('button', { name: 'Next file' }).click();
  await expect(previewWindow(page).getByRole('alert')).toBeVisible({ timeout: 20_000 });
});
