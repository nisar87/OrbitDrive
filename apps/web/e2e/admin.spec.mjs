// W10 in a real browser against the real gateway and agents: the operator area exists only for operators and works; owners control and can
// delete their own account; nothing on a PC is ever touched.
import { test, expect } from '@playwright/test';
import { mkdirSync, readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, agentCli, tempDir, makeFixtureRoot, Browser, pair, repoRoot } from '../../../tests/integration/harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OPERATOR = 'operator@example.test', OWNER = 'owner@example.test', TARGET = 'target@example.test';
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
let gateway, link, agentOp, agentOwn, agentTarget, ownRoot, target, operatorApi;

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  ownRoot = makeFixtureRoot();
  const states = [tempDir('adm-op'), tempDir('adm-own'), tempDir('adm-tgt')];
  agentCli(states[0], 'roots', 'add', 'Operator files', makeFixtureRoot());
  agentCli(states[1], 'roots', 'add', 'My files', ownRoot);
  agentCli(states[2], 'roots', 'add', 'Target files', makeFixtureRoot());
  gateway = await startGateway({ OrbitDrive__DataDirectory: tempDir('adm-data'), OrbitDrive__OperatorUids__0: uidOf(OPERATOR) });
  link = await startProxy(gateway, 5199);
  agentOp = await startAgent(gateway, states[0], 'Operator PC');
  agentOwn = await startAgent(gateway, states[1], 'Owner PC');
  agentTarget = await startAgent(gateway, states[2], 'Target PC');
  target = await new Browser(gateway, TARGET).signIn();
  await pair(agentTarget, target, 'Target phone');
  operatorApi = await new Browser(gateway, OPERATOR).signIn();
});
test.afterAll(async () => { await agentOp?.stop(); await agentOwn?.stop(); await agentTarget?.stop(); link?.stop(); await gateway?.stop(); });

async function signInAndPair(page, email, agent) {
  await page.goto('/');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
}
async function openApp(page, name) {
  const apps = page.getByRole('button', { name: 'Apps', exact: true });
  await apps.click();
  await page.getByRole('menuitem', { name, exact: true }).click();
  return page.getByRole('dialog', { name });
}

test.describe.configure({ mode: 'serial' });

test('an operator sees the administration area, with measured numbers, and can suspend and restore an account with a recorded reason', async ({ page }, info) => {
  await signInAndPair(page, OPERATOR, agentOp);
  const admin = await openApp(page, 'Administration');
  await expect(admin.getByTestId('admin-metrics')).toBeVisible();
  await expect(admin.getByTestId('m-users')).not.toHaveText('');
  await expect(admin.getByTestId('m-pcs')).toHaveText(/^\d+ of \d+$/);
  // The console scrolls inside its window even when the window is short (it once clipped everything below the fold).
  const original = page.viewportSize();
  await page.setViewportSize({ width: 1000, height: 520 });
  await admin.getByRole('button', { name: /^Billing/ }).click();
  await admin.getByRole('tab', { name: 'Plans and prices' }).click();
  const scroller = admin.locator('.con-box');
  await expect(admin.getByText('Plan limits')).toBeVisible();
  expect(await scroller.evaluate((e) => e.scrollHeight > e.clientHeight)).toBe(true);
  expect(await scroller.evaluate((e) => getComputedStyle(e).overflowY)).toMatch(/auto|scroll/);   // the box scrolls itself; its window body clips
  if (!info.project.name.includes('webkit-emulated')) {   // mobile WebKit has no mouse wheel; everywhere else a real wheel must move it
    await scroller.hover();
    await page.mouse.wheel(0, 600);
    await expect.poll(() => scroller.evaluate((e) => e.scrollTop)).toBeGreaterThan(0);
  }
  if (original) await page.setViewportSize(original);
  await admin.getByRole('button', { name: 'Accounts', exact: true }).click();
  const row = admin.getByTestId('admin-users').getByRole('row').filter({ hasText: uidOf(TARGET) });
  await expect(row).toBeVisible();
  const text = await admin.innerText();
  for (const secret of [TARGET, 'Target phone', 'Target PC', 'Target files', 'Operator files']) expect(text).not.toContain(secret);
  await page.screenshot({ path: join(shots, `admin-overview-${info.project.name}.png`) });

  // Search finds the account, and a search that matches nothing says so.
  await admin.getByRole('searchbox').fill('no-such-account-zzz');
  await expect(admin.getByText('Nothing matches.')).toBeVisible();
  await admin.getByRole('searchbox').fill(uidOf(TARGET).slice(0, 8));
  await expect(row).toBeVisible();

  const openActions = async () => {
    await row.getByRole('button', { name: /^Open account/ }).click();
    const drawer = page.getByTestId('admin-account-detail');
    await drawer.getByRole('tab', { name: 'Actions' }).click();
    return drawer;
  };
  let drawer = await openActions();
  await drawer.getByRole('button', { name: 'Suspend', exact: true }).click();
  const confirm = page.getByTestId('admin-confirm');
  await expect(confirm.getByRole('button', { name: 'Suspend account' })).toBeDisabled();       // a reason is required
  await confirm.getByRole('button', { name: 'Cancel' }).click();
  await expect(confirm).toHaveCount(0);
  await expect(drawer).toBeVisible();                                                           // Escape or Cancel in the dialog leaves the drawer open
  await drawer.getByRole('button', { name: 'Suspend', exact: true }).click();
  await confirm.getByLabel('Reason (required)').fill('abuse report 7');
  await confirm.getByRole('button', { name: 'Suspend account' }).click();
  await expect(drawer.getByRole('button', { name: 'Lift suspension' })).toBeVisible();
  await drawer.getByRole('button', { name: 'Close' }).click();
  await expect(row).toContainText('Suspended');
  await admin.getByRole('button', { name: 'Audit log', exact: true }).click();
  const newest = admin.getByTestId('admin-audit').getByRole('row').nth(1);
  await expect(newest).toContainText('suspend');
  await expect(newest).toContainText('abuse report 7');
  expect((await target.get('/v1/pcs')).status).toBe(403);
  await page.screenshot({ path: join(shots, `admin-suspended-${info.project.name}.png`) });

  await admin.getByRole('button', { name: 'Accounts', exact: true }).click();
  await admin.getByRole('searchbox').fill(uidOf(TARGET).slice(0, 8));
  drawer = await openActions();
  await drawer.getByRole('button', { name: 'Lift suspension' }).click();
  await page.getByTestId('admin-confirm').getByLabel('Reason (required)').fill('resolved');
  await page.getByTestId('admin-confirm').getByRole('button', { name: 'Lift suspension' }).click();
  await expect(drawer.getByRole('button', { name: 'Suspend', exact: true })).toBeVisible();
  await drawer.getByRole('button', { name: 'Close' }).click();
  await expect(row).not.toContainText('Suspended');
  expect((await target.get('/v1/pcs')).status).toBe(200);
});

test('an ordinary account never sees administration, and the operator code is never even downloaded for it', async ({ page }) => {
  const chunks = [];
  page.on('request', (request) => { if (/AdminApp/.test(request.url())) chunks.push(request.url()); });
  await signInAndPair(page, OWNER, agentOwn);
  await page.getByRole('button', { name: 'Apps', exact: true }).click();
  await expect(page.getByRole('menu', { name: 'Apps' }).getByRole('menuitem')).toHaveText(['File Explorer', 'Inbox', 'Transfers', 'Devices', 'Settings']);
  await page.keyboard.press('Escape');
  expect(chunks).toEqual([]);
});

test('owner controls: remembered choices, a download of what the service holds, sign out everywhere, then delete the account without touching the PC', async ({ page }, info) => {
  await signInAndPair(page, OWNER, agentOwn);
  const settings = await openApp(page, 'Settings');
  const panel = settings.getByTestId('account-panel');
  await expect(panel.getByTestId('account-browsers')).toHaveText('2');                      // the earlier test's browser of this account is still approved, and is listed
  await expect(panel.getByTestId('account-pcs')).toContainText('Owner PC (online)');
  await panel.getByLabel('Keep finished transfers in the list for').selectOption('30');
  await expect(panel.getByText('Saved.')).toBeVisible();
  await panel.scrollIntoViewIfNeeded();
  await page.screenshot({ path: join(shots, `account-panel-${info.project.name}.png`) });

  const downloading = page.waitForEvent('download');
  await panel.getByRole('button', { name: 'Download what the service holds about me' }).click();
  const exported = JSON.parse(readFileSync(await (await downloading).path(), 'utf8'));
  expect(exported.preferences.historyRetentionDays).toBe(30);
  expect(JSON.stringify(exported)).not.toMatch(/hello\.txt|My files|secret/i);

  // Delete needs the exact phrase.
  const del = panel.getByRole('button', { name: 'Delete my account' });
  await expect(del).toBeDisabled();
  await panel.getByLabel(/Type “delete my account”/).fill('delete');
  await expect(del).toBeDisabled();
  await panel.getByLabel(/Type “delete my account”/).fill('delete my account');
  await expect(del).toBeEnabled();
  const filesBefore = readFileSync(join(ownRoot, 'hello.txt'));
  await del.click();
  await expect(page.getByLabel('Email')).toBeVisible({ timeout: 20_000 });                    // signed out
  expect(readFileSync(join(ownRoot, 'hello.txt'))).toEqual(filesBefore);
  expect(existsSync(join(ownRoot, 'Documents', 'report.txt'))).toBe(true);
  const trail = (await operatorApi.get('/v1/admin/overview')).status;
  expect(trail).toBe(200);
  const audit = (await operatorApi.get('/v1/admin/audit')).body.entries;
  expect(audit.some((e) => e.action === 'account-deleted' && e.target === uidOf(OWNER))).toBe(true);
});
