// Reproducible marketing screenshots of the REAL web app against the REAL gateway and agent, with a demo
// account and disposable sample files. Browser captures only: the phone shots are a phone-sized viewport in
// desktop Chrome, NOT a physical phone, and are labelled that way in the manifest.
// Run from apps/web:  PW_LOCAL_CHROME=1 node ../site/capture/capture.mjs <outdir>
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createRequire } from 'node:module';
const repo = resolve(import.meta.dirname, '../../..');
const require = createRequire(join(repo, 'apps/web/package.json'));
const { chromium } = require('@playwright/test');
const { startGateway, startAgent, agentCli, tempDir, sleep } = await import(join(repo, 'tests/integration/harness.mjs'));

const out = resolve(process.argv[2] ?? join(repo, 'apps/site/screenshots'));
mkdirSync(out, { recursive: true });
const root = tempDir('demo-root');
execFileSync('python3', [join(repo, 'apps/site/capture/make-demo-files.py'), root]);
const state = tempDir('demo-state');
agentCli(state, 'roots', 'add', 'Home Documents', root);

const env = { ...process.env, VITE_GATEWAY_URL: 'http://127.0.0.1:5199', VITE_AUTH_MODE: 'dev', VITE_REGISTER_SW: 'false' };
const vite = spawn('npx', ['vite', '--mode', 'e2e', '--port', '5173', '--strictPort'], { cwd: join(repo, 'apps/web'), env, stdio: 'ignore' });
for (let i = 0; i < 60; i++) { try { if ((await fetch('http://localhost:5173')).ok) break; } catch { /* starting */ } await sleep(500); }
const gateway = await startGateway({}, 5199);
const agent = await startAgent(gateway, state, 'Home PC');
const sha = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: repo }).toString().trim();
const manifest = { generatedBy: 'apps/site/capture/capture.mjs', commit: sha, webVersion: '0.1.0', account: 'demo@example.test (dev auth, local only)',
  dataset: 'synthetic images and documents from make-demo-files.py; no real data', captures: [], failed: [] };

const launch = { channel: process.env.PW_LOCAL_CHROME ? 'chrome' : undefined };
const browser = await chromium.launch(launch);
const explorer = (p) => p.getByRole('dialog', { name: 'File Explorer' });
const taskbar = (p, n) => p.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: n, exact: true });

async function run(kind) {
  const phone = kind === 'phone';
  const ctx = await browser.newContext(phone
    ? { viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, colorScheme: 'light' }
    : { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await ctx.newPage();
  const shot = async (id, what) => {
    await sleep(500);
    const file = `${kind}-${id}.png`;
    await page.screenshot({ path: join(out, file) });
    manifest.captures.push({ file, id, what, surface: phone ? 'web app, phone-sized viewport (browser emulation, not a physical phone)' : 'web app in desktop Chrome', viewport: phone ? '390x844 @3x' : '1440x900 @2x', theme: await page.locator('html').getAttribute('data-theme') ?? 'light' });
  };
  const step = async (id, what, fn) => { try { await fn(); await shot(id, what); } catch (e) { manifest.failed.push({ kind, id, error: String(e.message).split('\n')[0] }); } };

  await page.goto('http://localhost:5173/');
  await step('01-sign-in', 'Sign-in screen', async () => { await page.getByLabel('Email').waitFor(); });
  await page.getByLabel('Email').fill('demo@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByRole('heading', { name: 'Pair with your PC' }).first().waitFor();
  await step('02-pair-prompt', 'Pairing prompt before a PC is paired', async () => {});
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  await step('03-confirm-on-pc', 'Browser asks you to confirm the code on the PC', async () => {});
  agent.send('approve');
  const home = page.getByRole('region', { name: "This account's PCs" });
  await home.getByRole('button', { name: /Home Documents/ }).waitFor();
  await step('04-pc-online', 'The PC is online with its approved folder', async () => {});
  await home.getByRole('button', { name: /Home Documents/ }).click();
  await explorer(page).getByRole('button', { name: 'Open folder Documents' }).waitFor();
  await step('05-explorer', 'Browsing the approved folder', async () => {});
  await step('06-photos', 'Pictures folder', async () => { await explorer(page).getByRole('button', { name: 'Open folder Photos' }).click(); await explorer(page).getByRole('button', { name: 'Open folder Trip to the north' }).click(); await explorer(page).getByRole('button', { name: /Preview Sunrise/ }).waitFor(); });
  await step('07-preview-image', 'Picture preview, no download needed', async () => { await explorer(page).getByRole('button', { name: /Preview Sunrise/ }).click(); await page.getByRole('dialog', { name: 'Preview' }).locator('img[data-full="true"]').waitFor({ timeout: 20000 }); });
  const preview = page.getByRole('dialog', { name: 'Preview' });
  const closePreview = async () => { await preview.getByRole('button', { name: /^Close/ }).click(); await preview.waitFor({ state: 'hidden' }); };
  const phoneSafe = async (fn) => { try { await fn(); } catch { /* optional */ } };
  await closePreview();
  await step('08-preview-pdf', 'Document preview', async () => {
    await explorer(page).getByRole('button', { name: /^Home Documents$|Back to/ }).first().click().catch(() => {});
    await page.getByRole('button', { name: 'Home Documents' }).first().click();
    await explorer(page).getByRole('button', { name: 'Open folder Documents' }).click();
    await explorer(page).getByRole('button', { name: 'Open folder Invoices' }).click();
    await explorer(page).getByRole('button', { name: /Preview Invoice 1042/ }).click();
    await preview.getByText('Invoice 1042.pdf').first().waitFor({ timeout: 20000 });
    await sleep(2500);
  });
  await closePreview();
  await step('09-icons', 'Icon view of a folder', async () => {
    await page.getByRole('button', { name: 'Home Documents' }).first().click();
    await explorer(page).getByRole('button', { name: 'Open folder Photos' }).click();
    await explorer(page).getByRole('button', { name: 'Open folder Trip to the north' }).click();
    await explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Icons' }).click();
  });
  await explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Details' }).click();
  await step('10-transfers', 'Transfers: a browser download is described honestly', async () => {
    await explorer(page).locator('tbody tr').filter({ hasText: 'Lake at noon' }).locator('td.size').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Download', exact: true }).click();
    await taskbar(page, 'Transfers').click();
    await page.getByRole('dialog', { name: 'Transfers' }).getByTestId('transfer').first().waitFor();
  });
  await step('11-dark', 'Dark theme (real Settings toggle)', async () => {
    await taskbar(page, 'Settings').click();
    await page.getByRole('dialog', { name: 'Settings' }).getByRole('radio', { name: 'Dark' }).check();
    await taskbar(page, 'File Explorer').click();
  });
  await ctx.close();
}
try { await run('desktop'); await run('phone'); }
finally {
  writeFileSync(join(out, 'manifest.json'), JSON.stringify(manifest, null, 2));
  await browser.close(); await agent.stop(); await gateway.stop(); vite.kill();
}
console.log(`captured ${manifest.captures.length}, failed ${manifest.failed.length}`, manifest.failed);
