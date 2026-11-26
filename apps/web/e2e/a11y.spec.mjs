// W11: accessibility checks in a real browser, on the real screens, in both themes. axe-core covers WCAG 2.0/2.1/2.2 A and AA rules it can
// decide automatically; the rest of this file checks what axe cannot (reduced motion, reflow at 320 px, large text, keyboard reach).
// Automated checks are NOT a full accessibility audit: screen-reader and real-device review remain manual items in the evidence.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdirSync, writeFileSync, copyFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, agentCli, tempDir, repoRoot, useThumbnailHelper } from '../../../tests/integration/harness.mjs';

useThumbnailHelper();
let gateway, link, agent, root, state, drop;
const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OWNER = 'owner@example.test';
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });
const taskbar = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name });

test.beforeAll(async () => {
  root = tempDir('e2e-a11y-root'); state = tempDir('e2e-a11y-state');
  copyFileSync(join(repoRoot, 'tests/fixtures/images/photo-640x480.png'), join(root, 'photo.png'));
  writeFileSync(join(root, 'notes.txt'), 'plain text\n');
  writeFileSync(join(root, 'a-rather-long-file-name-that-keeps-going-and-going-and-going-until-it-must-be-shortened-somewhere-sensible.txt'), 'long name\n');
  agentCli(state, 'roots', 'add', 'Share', root);
  drop = tempDir('e2e-a11y-drop');
  const dropId = agentCli(state, 'roots', 'add', 'Drop box', drop).match(/\(([A-Za-z0-9_-]+)\)/)[1];
  agentCli(state, 'roots', 'uploads', dropId, 'on', '200', '8');
  gateway = await startGateway({ OrbitDrive__OperatorUids__0: uidOf(OWNER) });   // the test account is also an operator, so Administration can be checked
  link = await startProxy(gateway, 5199);
  agent = await startAgent(gateway, state, 'Tower PC');
});
test.afterAll(async () => { await agent?.stop(); link?.stop(); await gateway?.stop(); });
test.describe.configure({ mode: 'serial' });

const setTheme = (page, theme) => page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);

/** Fails with a readable list of what is wrong and where. */
async function expectAccessible(page, where) {
  // Drawers and dialogs fade in; axe must measure the finished colours, not a frame mid-fade (that gave a false 1.3:1 contrast on slower runners).
  await page.evaluate(() => Promise.all(document.getAnimations().map((a) => a.finished.catch(() => undefined))));
  const { violations } = await new AxeBuilder({ page }).withTags(TAGS).analyze();
  const report = violations.map((v) => `${v.impact} ${v.id}: ${v.help}\n${v.nodes.slice(0, 4).map((n) => `    ${n.target.join(' ')}  ${n.failureSummary?.split('\n')[1] ?? ''}`).join('\n')}`).join('\n');
  expect(violations.length, `${where}\n${report}`).toBe(0);
}

async function signInAndPair(page) {
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
}

for (const theme of ['light', 'dark']) {
  test(`signed out: the sign-in screen has no automatically detectable violations (${theme})`, async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
    await setTheme(page, theme);
    await expectAccessible(page, `sign-in, ${theme}`);
  });

  test(`signed in: PCs, Explorer (details, icons, menu), Preview, Transfers and Settings (${theme})`, async ({ page }) => {
    await signInAndPair(page);
    await setTheme(page, theme);
    await expectAccessible(page, `desktop with PC list, ${theme}`);

    await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
    await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
    await expectAccessible(page, `Explorer details view, ${theme}`);

    const thumbnail = page.waitForResponse((r) => r.url().includes('/v1/downloads/') && r.status() === 200, { timeout: 20_000 });
    await explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Icons' }).click();
    await thumbnail;                                                  // the picture was requested and delivered (where the helper cannot decode it, the plain icon stays: both states are checked)
    await page.waitForTimeout(500);
    await expectAccessible(page, `Explorer icons view with thumbnails, ${theme}`);
    await explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Details' }).click();

    await explorer(page).getByRole('button', { name: 'Download notes.txt' }).click({ button: 'right' });
    await expect(page.getByRole('menu')).toBeVisible();
    await expectAccessible(page, `Explorer context menu, ${theme}`);
    await page.keyboard.press('Escape');

    await explorer(page).getByRole('button', { name: 'Preview photo.png' }).click();
    const preview = page.getByRole('dialog', { name: 'Preview' });
    await expect.poll(() => preview.locator('img[data-full="true"]').evaluate((i) => i.naturalWidth), { timeout: 20_000 }).toBe(640);
    await expectAccessible(page, `Preview, ${theme}`);
    await preview.getByRole('button', { name: 'Close Preview' }).click();                  // one window at a time: a window half-covered by another is not what is being judged

    await taskbar(page, 'Transfers').click();
    const transfers = page.getByRole('dialog', { name: 'Transfers' });
    await expect(transfers).toBeVisible();
    await expectAccessible(page, `Transfers, ${theme}`);
    await transfers.getByRole('button', { name: 'Close Transfers' }).click();
    await taskbar(page, 'Settings').click();
    await expect(page.getByRole('dialog', { name: 'Settings' })).toBeVisible();
    await expectAccessible(page, `Settings and account, ${theme}`);
  });
}

// ---------------------------------------------------------------- what axe cannot decide
test('reduced motion: with the device asking for less motion, nothing animates or transitions for longer than a blink', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await signInAndPair(page);
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
  await taskbar(page, 'Settings').click();
  const offenders = await page.evaluate(() => {
    const seconds = (value) => Math.max(0, ...value.split(',').map((v) => (v.trim().endsWith('ms') ? parseFloat(v) / 1000 : parseFloat(v) || 0)));
    return [...document.querySelectorAll('body *')].flatMap((el) => {
      const style = getComputedStyle(el);
      const animates = style.animationName !== 'none' && seconds(style.animationDuration) > 0.05;
      const transitions = seconds(style.transitionDuration) > 0.05 && style.transitionProperty !== 'none';
      return animates || transitions ? [`${el.tagName.toLowerCase()}.${String(el.className).slice(0, 40)} anim=${style.animationName}/${style.animationDuration} trans=${style.transitionProperty}/${style.transitionDuration}`] : [];
    });
  });
  expect(offenders, 'elements still moving with reduced motion requested').toEqual([]);
});

test('reflow: at 320 CSS px wide, and with text enlarged to 200%, the page never needs sideways scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 });
  await page.goto('/');
  const overflow = () => page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  let size = await overflow(); expect(size.scroll, 'sign-in at 320px').toBeLessThanOrEqual(size.inner);
  await page.addStyleTag({ content: 'html { font-size: 200% !important; }' });
  size = await overflow(); expect(size.scroll, 'sign-in at 320px, text 200%').toBeLessThanOrEqual(size.inner);
  await page.addStyleTag({ content: 'html { font-size: 100% !important; }' });
  await signInAndPair(page);
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
  size = await overflow(); expect(size.scroll, 'Explorer at 320px').toBeLessThanOrEqual(size.inner);
  await page.addStyleTag({ content: 'html { font-size: 200% !important; }' });
  size = await overflow(); expect(size.scroll, 'Explorer at 320px, text 200%').toBeLessThanOrEqual(size.inner);
  // The long file name is shortened, never allowed to push the layout wider than the screen.
  await expect(explorer(page).getByRole('button', { name: /^Download a-rather-long-file-name/ })).toBeVisible();
});

test('keyboard: Tab reaches controls, never gets stuck, and every focused control shows where focus is', async ({ page }) => {
  await signInAndPair(page);
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
  await page.evaluate(() => (document.activeElement instanceof HTMLElement) && document.activeElement.blur());
  const seen = new Set(); const invisible = [];
  for (let i = 0; i < 70; i++) {
    await page.keyboard.press('Tab');
    const info = await page.evaluate(() => {
      const el = document.activeElement; if (!el || el === document.body) return null;
      const s = getComputedStyle(el);
      const outline = s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0;
      const shadow = s.boxShadow !== 'none';
      const border = el.matches(':focus-visible') && (s.borderColor !== getComputedStyle(el).borderColor);
      return { key: `${el.tagName}|${el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 30)}|${el.className}`, visible: outline || shadow, label: el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 40) ?? '', tag: el.tagName, border };
    });
    if (!info) continue;
    seen.add(info.key);
    if (!info.visible) invisible.push(`${info.tag} "${info.label}"`);
  }
  expect(seen.size, 'Tab should move through many different controls (no keyboard trap)').toBeGreaterThan(12);
  expect(invisible, 'controls that took keyboard focus without any visible focus indicator').toEqual([]);
});

test('Upload (confirmation and finished) and Administration have no automatically detectable violations, light and dark', async ({ page }) => {
  await signInAndPair(page);
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Drop box/ }).click();
  await expect(explorer(page)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload files here' })).toBeVisible();
  await page.getByTestId('upload-input').setInputFiles([{ name: 'a11y.bin', mimeType: 'application/octet-stream', buffer: Buffer.from('upload for the accessibility check') }]);
  await expect(page.getByTestId('upload-confirm')).toBeVisible();
  for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `upload confirmation, ${theme}`); }
  await page.getByTestId('upload-confirm').getByRole('button', { name: /Upload 1 file/ }).click();
  await expect(page.getByTestId('upload-saved')).toBeVisible({ timeout: 30_000 });
  for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `finished upload, ${theme}`); }
  await page.getByRole('dialog', { name: 'File Explorer' }).getByRole('button', { name: 'Close File Explorer' }).click();

  await page.getByRole('button', { name: 'Apps', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Administration', exact: true }).click();
  const admin = page.getByRole('dialog', { name: 'Administration' });
  await expect(admin.getByTestId('admin-metrics')).toBeVisible();
  for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `Administration, ${theme}`); }
  for (const section of ['Accounts', 'Organisations', 'Billing', 'Audit log', 'Settings']) {
    await admin.getByRole('button', { name: new RegExp(`^${section}`) }).click();
    for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `Administration ${section}, ${theme}`); }
  }
  await admin.getByRole('button', { name: 'Accounts', exact: true }).click();
  await admin.getByTestId('admin-users').getByRole('button', { name: /^Open account/ }).first().click();
  await expect(page.getByTestId('admin-account-detail')).toBeVisible();
  for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `Administration account details, ${theme}`); }
  await page.getByTestId('admin-account-detail').getByRole('tab', { name: 'Actions' }).click();
  await page.getByTestId('admin-account-detail').getByRole('button', { name: 'Sign out all browsers' }).click();
  await expect(page.getByTestId('admin-confirm')).toBeVisible();
  await expect(page.getByTestId('admin-confirm')).toContainText('@');   // the dialog names the account by its email
  for (const theme of ['light', 'dark']) { await setTheme(page, theme); await expectAccessible(page, `Administration reason dialog, ${theme}`); }
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('admin-confirm')).toHaveCount(0);
  await expect(page.getByTestId('admin-account-detail')).toBeVisible();   // Escape closed only the dialog on top
});

// R33: tablets and landscape phones. Emulated viewports in a real browser; physical devices remain a manual check.
const VIEWPORTS = [
  { name: 'tablet portrait', width: 820, height: 1180 },
  { name: 'tablet landscape', width: 1180, height: 820 },
  { name: 'phone landscape', width: 915, height: 412 },
  { name: 'small phone landscape', width: 667, height: 375 },
];
test('layout: tablets and landscape phones keep the window on screen, never scroll sideways and stay accessible', async ({ page }) => {
  await signInAndPair(page);
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' })).toBeVisible();
  for (const { name, width, height } of VIEWPORTS) {
    await page.setViewportSize({ width, height });   // the same window stays open while the "screen" changes size, like rotating a tablet
    await expect(explorer(page).getByRole('button', { name: 'Preview photo.png' }), `${name}: Explorer opens`).toBeVisible();
    // The app re-fits its windows a frame after the screen changes size, so the position is polled rather than read once.
    const edges = async () => { const box = await explorer(page).boundingBox(); return box ? { left: box.x, top: box.y, right: box.x + box.width, bottom: box.y + box.height } : null; };
    await expect.poll(async () => { const e = await edges(); return e !== null && e.left >= -1 && e.top >= -1 && e.right <= width + 1 && e.bottom <= height + 1; }, { message: `${name}: the Explorer window fits on the screen`, timeout: 5000 }).toBe(true);
    const size = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
    expect(size.scroll, `${name}: no sideways scrolling`).toBeLessThanOrEqual(size.inner);
    await expectAccessible(page, `Explorer at ${name}`);
  }
});
