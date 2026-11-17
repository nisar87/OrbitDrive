// Browser end-to-end test against the REAL gateway and agent processes (no mocked API).
// Mobile projects are viewport/touch emulation only — not physical-phone evidence.
import { test, expect } from '@playwright/test';
import { readFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, sha256, repoRoot, Browser, pair } from '../../../tests/integration/harness.mjs';

let gateway, agent, root, stateDir;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  root = makeFixtureRoot();
  stateDir = tempDir('e2e-state');
  agentCli(stateDir, 'roots', 'add', 'Test Share', root);
  gateway = await startGateway({}, 5199);
  agent = await startAgent(gateway, stateDir, 'Office PC');
});

test.afterAll(async () => {
  await agent?.stop();
  await gateway?.stop();
});

const home = (page) => page.getByRole('region', { name: "This account's PCs" });
const openApp = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name, exact: true }).click();
async function signOut(page) {
  await page.getByRole('button', { name: 'Account', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
}

async function signIn(page, email) {
  await page.goto('/');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Pair with your PC' }).first()).toBeVisible();
}

test('pair by QR link, browse the real folder and download real files', async ({ page }, info) => {
  const shot = (name) => page.screenshot({ path: join(shots, `${info.project.name}-${name}.png`), fullPage: false });
  await signIn(page, 'owner@example.test');
  await shot('01-pairing-empty');

  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.goto(pairing.url); // what the phone camera opens after scanning the PC's QR code
  const code = page.getByTestId('confirmation-code');
  await expect(code).toBeVisible();
  expect(page.url()).toBe('http://localhost:5173/pair'); // secret scrubbed from the address bar
  const shown = await agent.next('pairing_claimed');
  expect(await code.textContent()).toBe(shown.confirmationCode);
  expect(shown.accountLabel).toBe('owner@example.test');
  await shot('02-confirm-on-pc');

  agent.send('approve');
  const share = home(page).getByRole('button', { name: /Test Share/ });
  await expect(share).toBeVisible();
  await expect(page.getByText('Online')).toBeVisible();
  await shot('03-pcs-and-roots');

  await share.click();
  await expect(page.getByRole('button', { name: 'Open folder Documents' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download اردو فائل.txt' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download emoji 😀 notes.txt' })).toBeVisible();
  await shot('04-explorer-root');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'no horizontal page overflow').toBeLessThanOrEqual(0);

  const [dataDownload] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download data.bin' }).click()]);
  expect(dataDownload.suggestedFilename()).toBe('data.bin');
  expect(sha256(readFileSync(await dataDownload.path()))).toBe(sha256(readFileSync(join(root, 'data.bin'))));
  await expect(page.getByTestId('transfer-status')).toContainText('handed to your browser');
  await expect(page.getByTestId('transfer-status')).not.toContainText(/saved|complete/i);

  await page.getByRole('button', { name: 'Open folder Documents' }).click();
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Documents');
  const [report] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download report.txt' }).click()]);
  expect(readFileSync(await report.path(), 'utf8')).toBe('quarterly report\n');
  await shot('05-explorer-nested-download');

  // Paged folder: 450 real files, 100 per page.
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Test Share' }).click();
  await page.getByRole('button', { name: 'Open folder Many' }).click();
  await expect(page.getByRole('button', { name: /^Download item-/ })).toHaveCount(200);
  await page.getByRole('button', { name: 'Load more' }).click();
  await expect(page.getByRole('button', { name: /^Download item-/ })).toHaveCount(400);

  // PC goes away: truthful offline state, no canned data.
  await agent.stop();
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByRole('alert')).toContainText(/offline/i);
  await shot('06-pc-offline');
  agent = await startAgent(gateway, stateDir, 'Office PC');
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByRole('button', { name: /^Download item-/ })).toHaveCount(200);

  // Signing out revokes this browser on the service and clears local credentials.
  const stored = await page.evaluate(() => localStorage.getItem('orbitdrive.session.v1'));
  await signOut(page);
  await expect(page.getByRole('heading', { name: 'Sign in' }).first()).toBeVisible();
  expect(await page.evaluate(() => localStorage.getItem('orbitdrive.session.v1'))).toBeNull();
  const old = JSON.parse(stored);
  const replay = await page.request.get('http://127.0.0.1:5199/v1/pcs', { headers: { Authorization: `Bearer ${old.idToken}`, 'X-OrbitDrive-Device': `${old.device.id}.${old.device.secret}` } });
  expect(replay.status()).toBe(403);
});

test('manual code entry, a wrong code, and refusal of a second account', async ({ page }, info) => {
  await signIn(page, 'second@example.test');
  await page.getByLabel('Pairing code').fill('nonsense');
  await page.getByRole('button', { name: 'Pair' }).click();
  await expect(page.getByRole('alert')).toContainText('Enter the code exactly');

  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code.replace(/.$/, (c) => (c === 'A' ? 'B' : 'A')));
  await page.getByRole('button', { name: 'Pair' }).click();
  await expect(page.getByRole('alert')).toContainText(/not correct|different account/);
  await page.screenshot({ path: join(shots, `${info.project.name}-07-pairing-error.png`) });

  await page.getByLabel('Pairing code').fill(pairing.code.toLowerCase());
  await page.getByRole('button', { name: 'Pair' }).click();
  // This PC already belongs to owner@example.test, so a different account is refused outright.
  await expect(page.getByRole('alert')).toContainText('different account');
  await expect(page.getByTestId('confirmation-code')).toHaveCount(0);
});

test('devices page: see approved browsers, remove another browser, disconnect the PC', async ({ page }, info) => {
  await signIn(page, 'owner@example.test');
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.goto(pairing.url);
  await expect(page.getByTestId('confirmation-code')).toBeVisible();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await openApp(page, 'Devices');
  const devices = page.getByRole('dialog', { name: 'Devices' });
  const browsers = devices.getByRole('article', { name: 'Approved browsers' });
  await expect(browsers.getByText('(this browser)')).toBeVisible();

  // A second browser of the same account, paired through the real API.
  const tablet = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, tablet, 'Kitchen tablet');
  await devices.getByRole('button', { name: 'Refresh' }).click();
  await expect(browsers.getByText('Kitchen tablet', { exact: true })).toBeVisible();
  await expect(browsers.getByText('Can reach Office PC').first()).toBeVisible();
  await page.screenshot({ path: join(shots, `${info.project.name}-08-devices.png`) });

  await browsers.getByRole('button', { name: 'Remove Kitchen tablet' }).click();
  expect((await tablet.get('/v1/pcs')).status, 'nothing happens until the second, explicit confirmation').toBe(200);
  await browsers.getByRole('button', { name: 'Cancel' }).click();
  await browsers.getByRole('button', { name: 'Remove Kitchen tablet' }).click();
  await browsers.getByRole('button', { name: 'Yes, remove its access' }).click();
  await expect(browsers.getByText('Kitchen tablet', { exact: true })).toHaveCount(0);
  expect((await tablet.get('/v1/pcs')).status).toBe(403);
  await expect(browsers.getByRole('button', { name: /^Remove/ })).toHaveCount(0); // the current browser is removed by signing out

  await devices.getByRole('button', { name: 'Disconnect this PC' }).click();
  await devices.getByRole('button', { name: 'Yes, disconnect Office PC' }).click();
  await expect(devices.getByText('No PC is paired.')).toBeVisible();
  await expect(browsers.getByText('No PC access')).toBeVisible();
  await openApp(page, 'File Explorer');                         // the Explorer window learns about it without a reload
  await expect(page.getByText('No PCs are paired with this browser yet.')).toBeVisible();
  await openApp(page, 'Devices');
  await page.screenshot({ path: join(shots, `${info.project.name}-09-pc-disconnected.png`) });
});
