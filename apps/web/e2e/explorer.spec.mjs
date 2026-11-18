// W4: the desktop shell and the real File Explorer, in a real browser against the real gateway and agent.
// Mobile projects are viewport/touch emulation only — not physical-phone evidence.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, repoRoot } from '../../../tests/integration/harness.mjs';

let gateway, agent, root;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
// About 160 characters and just under the 255-byte file-name limit of common filesystems.
const longName = 'بہت لمبا فائل نام '.repeat(3) + '📁'.repeat(3) + ' ' + 'very-long-file-name-without-any-break-opportunity_'.repeat(2) + '.txt';
// The agent never lists names containing < > " (not valid on Windows), so the HTML-looking name uses entities and
// attribute-like text that are valid file names everywhere and would be dangerous only if rendered as markup.
const xssName = "&lt;img src=x onerror=window.__xss=1&gt; &amp; {{constructor}} 'q'.txt";

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  root = makeFixtureRoot();
  mkdirSync(join(root, 'Edge'));
  writeFileSync(join(root, 'Edge', longName), 'long');
  writeFileSync(join(root, 'Edge', xssName), 'x');
  const state = tempDir('e2e-explorer-state');
  agentCli(state, 'roots', 'add', 'Work Files', root);
  const big = tempDir('e2e-big');
  for (let i = 0; i < 10_000; i++) writeFileSync(join(big, `f${String(i).padStart(5, '0')}.dat`), '');
  agentCli(state, 'roots', 'add', 'Big Archive', big);
  gateway = await startGateway({}, 5199);
  agent = await startAgent(gateway, state, 'Studio PC');
});
test.afterAll(async () => { await agent?.stop(); await gateway?.stop(); });

const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });
const rows = (page) => explorer(page).locator('tbody tr');
const names = async (page) => (await explorer(page).locator('tbody .row-button bdi').allTextContents());
const taskbar = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name, exact: true });
const isPhone = (info) => info.project.name.startsWith('mobile');

async function pairAndOpen(page, info, email = 'owner@example.test') {
  await page.goto('/');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Work Files/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Open folder Documents' })).toBeVisible();
}

test('sort, filter, views and status bar show the real folder truthfully', async ({ page }, info) => {
  await pairAndOpen(page, info);
  const win = explorer(page);
  expect(await names(page)).toEqual(['Documents', 'Edge', 'Many', 'data.bin', 'emoji 😀 notes.txt', 'empty.bin', 'hello.txt', 'اردو فائل.txt']);
  await expect(win.getByText('8 items')).toBeVisible();

  await win.getByLabel('Sort by').selectOption('size');
  expect((await names(page)).slice(3)).toEqual(['empty.bin', 'emoji 😀 notes.txt', 'hello.txt', 'اردو فائل.txt', 'data.bin']);   // 0, 6, 22, 41, 300000 bytes
  await win.getByRole('button', { name: /Ascending\. Switch to descending/ }).click();
  expect((await names(page)).slice(0, 4)).toEqual(['Many', 'Edge', 'Documents', 'data.bin']);
  if (!isPhone(info)) {
    await expect(win.getByRole('columnheader', { name: /Size/ })).toHaveAttribute('aria-sort', 'descending');
    await win.getByRole('columnheader', { name: /Name/ }).getByRole('button').click();
    await expect(win.getByRole('columnheader', { name: /Name/ })).toHaveAttribute('aria-sort', 'ascending');
  } else {
    await win.getByLabel('Sort by').selectOption('name');
    await win.getByRole('button', { name: /Descending\. Switch to ascending/ }).click();
  }

  await win.getByLabel('Filter this folder').fill('  EMOJI ');
  expect(await names(page)).toEqual(['emoji 😀 notes.txt']);
  await expect(win.getByText('1 item', { exact: true })).toBeVisible();
  await win.getByLabel('Filter this folder').fill('no-such-thing');
  await expect(win.getByText(/Nothing in this folder matches/)).toBeVisible();
  await win.getByLabel('Filter this folder').fill('');

  for (const view of ['List', 'Icons']) {
    await win.getByRole('group', { name: 'View' }).getByRole('button', { name: view }).click();
    await expect(win.getByRole('button', { name: 'Download data.bin' })).toBeVisible();
    await page.screenshot({ path: join(shots, `${info.project.name}-20-explorer-${view.toLowerCase()}.png`) });
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), `${view}: no horizontal page overflow`).toBeLessThanOrEqual(0);
  }
  await page.reload();                                           // the chosen view is remembered; nothing about files is stored
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Work Files/ }).click();
  await expect(explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Icons' })).toHaveAttribute('aria-pressed', 'true');
  const stored = await page.evaluate(() => localStorage.getItem('orbitdrive.prefs.v1'));
  expect(stored).not.toContain('data.bin');
  expect(stored).not.toContain('Work Files');
  await explorer(page).getByRole('group', { name: 'View' }).getByRole('button', { name: 'Details' }).click();
});

test('history: back, forward, up, breadcrumbs, the browser Back button and the folder tree', async ({ page }, info) => {
  await pairAndOpen(page, info);
  const win = explorer(page);
  const crumbs = win.getByRole('navigation', { name: 'Breadcrumb' });
  await win.getByRole('button', { name: 'Open folder Documents' }).click();
  await win.getByRole('button', { name: 'Open folder Nested' }).click();
  await expect(crumbs).toContainText('Nested');
  await page.goBack();                                           // the browser's own Back goes up the visit history, not off the site
  await expect(crumbs.getByRole('button').last()).toHaveText('Documents');
  await expect(win.getByRole('button', { name: 'Download report.txt' })).toBeVisible();
  await win.getByRole('button', { name: 'Forward' }).click();
  await expect(crumbs.getByRole('button').last()).toHaveText('Nested');
  await win.getByRole('button', { name: 'Up one level' }).click();
  await expect(crumbs.getByRole('button').last()).toHaveText('Documents');
  await win.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(crumbs.getByRole('button').last()).toHaveText('Nested');
  await expect(win.getByRole('button', { name: 'Forward' })).toBeEnabled();
  await crumbs.getByRole('button', { name: 'Work Files' }).click();
  await expect(win.getByRole('button', { name: 'Forward' })).toBeDisabled();   // a new visit drops the forward branch

  if (isPhone(info)) {
    await expect(win.getByRole('navigation', { name: 'PCs and shared folders' })).toBeHidden();
    await win.getByRole('button', { name: 'Folders' }).click();
  }
  const tree = win.getByRole('navigation', { name: 'PCs and shared folders' });
  await expect(tree.getByRole('button', { name: 'Work Files' })).toHaveAttribute('aria-current', 'true');
  await page.screenshot({ path: join(shots, `${info.project.name}-21-explorer-tree.png`) });
  await tree.getByRole('button', { name: "This account's PCs" }).click();
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
  if (isPhone(info)) await expect(tree).toBeHidden();            // the drawer closes after choosing
});

// Regression for a CI failure on Firefox: opening a folder with Enter must leave keyboard focus in the list however long the browser takes to
// draw the new rows. Focus used to be restored one animation frame after the click, which can come before React has drawn the new place.
// Chromium lets the test make the machine slow on purpose (CDP CPU throttling), so the gap is wide instead of a matter of luck.
test('keyboard focus stays in the list after opening and leaving a folder, even on a very slow machine', async ({ page, browserName }, info) => {
  test.skip(isPhone(info) || browserName !== 'chromium', 'keyboard test; CPU throttling is a Chromium feature');
  await pairAndOpen(page, info);
  const win = explorer(page);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 20 });
  try {
    for (let round = 0; round < 4; round++) {
      await win.getByRole('button', { name: 'Open folder Documents' }).focus();
      await page.keyboard.press('Enter');
      await expect(win.getByRole('button', { name: 'Download report.txt' })).toBeVisible({ timeout: 30_000 });
      await expect(win.getByRole('button', { name: 'Open folder Nested' }), `round ${round + 1}: focus was lost after opening a folder`).toBeFocused({ timeout: 15_000 });
      await page.keyboard.press('Backspace');
      await expect(win.getByRole('button', { name: 'Open folder Documents' }), `round ${round + 1}: focus was lost after going up`).toBeVisible({ timeout: 30_000 });
    }
  } finally { await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 }); }
});

test('selection, keyboard, context menu and properties', async ({ page }, info) => {
  await pairAndOpen(page, info);
  const win = explorer(page);
  if (!isPhone(info)) {
    // Keyboard only: arrows move, Space selects, Shift+arrows extend, Ctrl+A selects all, Escape clears.
    await win.getByRole('button', { name: 'Open folder Documents' }).focus();
    await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown');
    await expect(win.getByRole('button', { name: 'Download data.bin' })).toBeFocused();
    await page.keyboard.press('Space');
    await expect(rows(page).filter({ hasText: 'data.bin' })).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Shift+ArrowDown');
    await expect(win.getByText('2 selected')).toBeVisible();
    await expect(win.getByText(/2 selected · 292 KB/)).toBeVisible();
    await page.keyboard.press('Control+a');
    await expect(win.getByText('8 selected')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(win.getByText(/selected/)).toHaveCount(0);
    await page.keyboard.press('End');
    await expect(win.getByRole('button', { name: 'Download اردو فائل.txt' })).toBeFocused();
    await page.keyboard.press('Home');
    await expect(win.getByRole('button', { name: 'Open folder Documents' })).toBeFocused();
    await page.keyboard.press('Enter');                          // Enter opens, like a double-click
    await expect(win.getByRole('button', { name: 'Download report.txt' })).toBeVisible();
    await expect(win.getByRole('button', { name: 'Open folder Nested' })).toBeFocused();   // focus stays in the list
    await page.keyboard.press('Backspace');
    await expect(win.getByRole('button', { name: 'Download data.bin' })).toBeVisible();

    // Mouse: click selects, Ctrl+click adds, double-click on the row opens, right-click shows only real actions.
    await rows(page).filter({ hasText: 'hello.txt' }).locator('td.size').click();
    await rows(page).filter({ hasText: 'empty.bin' }).locator('td.size').click({ modifiers: ['Control'] });
    await expect(win.getByText('2 selected')).toBeVisible();
    await rows(page).filter({ hasText: 'data.bin' }).locator('td.size').click({ button: 'right' });
  } else {
    // Touch: a long press opens the actions; explicit Select mode gives checkboxes for several items.
    await win.getByRole('button', { name: 'Select', exact: true }).click();
    await win.getByRole('checkbox', { name: 'Select hello.txt' }).check();
    await win.getByRole('checkbox', { name: 'Select empty.bin' }).check();
    await expect(win.getByText('2 selected')).toBeVisible();
    await win.getByRole('button', { name: 'Select', exact: true }).click();
    await expect(win.getByText(/selected/)).toHaveCount(0);
    const target = win.getByRole('button', { name: 'Download data.bin' });
    const box = await target.boundingBox();
    await target.dispatchEvent('pointerdown', { pointerType: 'touch', clientX: box.x + 20, clientY: box.y + 10, bubbles: true });
    await page.waitForTimeout(700);
    await target.dispatchEvent('pointerup', { pointerType: 'touch', bubbles: true });
  }
  const menu = page.getByRole('menu', { name: 'Actions' });
  // "Save to a file…" is offered only where the browser can write a file the reader chooses.
  const canSave = await page.evaluate(() => typeof window.showSaveFilePicker === 'function');
  await expect(menu.getByRole('menuitem')).toHaveText(canSave ? ['Download', 'Save to a file…', 'Add to favorites', 'Properties'] : ['Download', 'Add to favorites', 'Properties']);
  await page.screenshot({ path: join(shots, `${info.project.name}-22-context-menu.png`) });
  const box = await menu.boundingBox();
  const viewport = page.viewportSize();
  expect(box.x >= 0 && box.y >= 0 && box.x + box.width <= viewport.width && box.y + box.height <= viewport.height, 'the menu is fully on screen').toBe(true);
  await page.keyboard.press('End');
  await expect(menu.getByRole('menuitem', { name: 'Properties' })).toBeFocused();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'data.bin' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('BIN file');
  await expect(dialog).toContainText('292 KB (300,000 bytes)');   // the exact size, not a rounded guess
  await expect(dialog).toContainText('Studio PC › Work Files');
  await expect(dialog).toContainText('Read-only access');
  await page.screenshot({ path: join(shots, `${info.project.name}-23-properties.png`) });
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(menu).toHaveCount(0);
});

test('awkward names: very long, right-to-left, emoji and HTML-looking names stay text and stay inside the window', async ({ page }, info) => {
  await pairAndOpen(page, info);
  const win = explorer(page);
  await win.getByRole('button', { name: 'Open folder Edge' }).click();
  await expect(rows(page)).toHaveCount(2);
  for (const view of ['Details', 'List', 'Icons']) {
    await win.getByRole('group', { name: 'View' }).getByRole('button', { name: view }).click();
    const overflow = await page.evaluate(() => {
      const content = document.querySelector('.explorer .content');
      return Math.max(document.documentElement.scrollWidth - document.documentElement.clientWidth, content.scrollWidth - content.clientWidth);
    });
    expect(overflow, `${view}: a very long name wraps instead of pushing the layout sideways`).toBeLessThanOrEqual(0);
  }
  await page.screenshot({ path: join(shots, `${info.project.name}-24-long-names.png`) });
  await expect(win.getByRole('button', { name: `Download ${xssName}` })).toBeVisible();
  await expect(win.locator('tbody .row-button bdi').filter({ hasText: '&lt;img' })).toHaveText(xssName);   // shown literally, entities not decoded
  expect(await page.evaluate(() => [window.__xss ?? null, document.querySelectorAll('.explorer img').length])).toEqual([null, 0]);
  await win.getByRole('group', { name: 'View' }).getByRole('button', { name: 'Details' }).click();
});

test('desktop shell: windows, taskbar, launcher, account menu, theme', async ({ page }, info) => {
  await pairAndOpen(page, info);
  const win = explorer(page);
  const start = page.getByRole('button', { name: 'Apps', exact: true });

  // Launcher lists only apps that exist; Escape closes it and focus returns to where it was.
  await start.click();
  await expect(page.getByRole('menu', { name: 'Apps' }).getByRole('menuitem')).toHaveText(['File Explorer', 'Inbox', 'Transfers', 'Devices', 'Settings']);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu', { name: 'Apps' })).toHaveCount(0);
  await expect(start).toBeFocused();

  await start.click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  const settings = page.getByRole('dialog', { name: 'Settings' });
  await expect(settings).toBeVisible();
  await expect(taskbar(page, 'Settings')).toHaveAttribute('aria-pressed', 'true');

  if (isPhone(info)) {
    // One maximised window at a time; no drag/resize/maximise controls.
    await expect(win).toBeHidden();
    await expect(settings.getByRole('button', { name: /Maximize|Restore/ })).toHaveCount(0);
    const box = await settings.boundingBox();
    expect([Math.round(box.x), Math.round(box.y), Math.round(box.width)]).toEqual([0, 0, page.viewportSize().width]);
  } else {
    await expect(win).toBeVisible();                             // both windows on screen, Settings in front
    const before = await settings.boundingBox();
    await page.mouse.move(before.x + 200, before.y + 18);
    await page.mouse.down(); await page.mouse.move(before.x + 80, before.y - 22, { steps: 5 }); await page.mouse.up();
    const moved = await settings.boundingBox();
    expect([Math.round(moved.x - before.x), Math.round(moved.y - before.y)]).toEqual([-120, -40]);
    await page.mouse.move(moved.x + moved.width - 6, moved.y + moved.height - 6);
    await page.mouse.down(); await page.mouse.move(moved.x + moved.width - 106, moved.y + moved.height - 56, { steps: 5 }); await page.mouse.up();
    const resized = await settings.boundingBox();
    expect([Math.round(resized.width - moved.width), Math.round(resized.height - moved.height)]).toEqual([-100, -50]);
    // Grab the title bar near its right end and pull it to the top-left corner of the screen: most of the window
    // would leave the desktop. (Kept inside the viewport: test drivers do not deliver moves outside it.)
    await page.mouse.move(resized.x + resized.width - 160, resized.y + 18);
    await page.mouse.down(); await page.mouse.move(1, 1, { steps: 4 }); await page.mouse.up();
    const pinned = await settings.boundingBox();
    expect(pinned.y).toBeGreaterThanOrEqual(0);
    expect(Math.round(pinned.x + pinned.width)).toBe(200);         // buttons plus room to grab the title bar stay reachable
    await page.mouse.move(20, pinned.y + 18);                      // ...so it can be dragged back
    await page.mouse.down(); await page.mouse.move(720, pinned.y + 60, { steps: 5 }); await page.mouse.up();
    expect((await settings.boundingBox()).x).toBeGreaterThan(-300);

    await settings.getByRole('button', { name: 'Maximize Settings' }).click();
    const full = await settings.boundingBox();
    expect([Math.round(full.x), Math.round(full.y), Math.round(full.width)]).toEqual([0, 0, page.viewportSize().width]);
    await settings.getByRole('button', { name: 'Restore Settings' }).click();
    // Clicking any visible part of a window behind brings it to the front.
    const front = await settings.boundingBox(), behind = await win.boundingBox();
    const spot = [[behind.x + 12, behind.y + behind.height - 12], [behind.x + behind.width - 12, behind.y + behind.height - 12], [behind.x + 60, behind.y + 12], [behind.x + behind.width - 200, behind.y + 12]]
      .find(([x, y]) => x > 0 && y > 0 && x < 1280 && y < 660 && !(x >= front.x && x <= front.x + front.width && y >= front.y && y <= front.y + front.height));
    expect(spot, 'part of the Explorer window is visible behind Settings').toBeTruthy();
    await page.mouse.click(spot[0], spot[1]);
    await expect(taskbar(page, 'File Explorer')).toHaveAttribute('aria-pressed', 'true');
    await taskbar(page, 'File Explorer').click();                // clicking the active app's taskbar button minimises it
    await expect(win).toBeHidden();
    await taskbar(page, 'File Explorer').click();
    await expect(win).toBeVisible();
    await taskbar(page, 'Settings').click();
    await settings.getByRole('button', { name: 'Maximize Settings' }).click();
  }

  // Theme: explicit Dark overrides the device setting and is remembered.
  const bodyColor = () => page.evaluate(() => getComputedStyle(document.querySelector('[role="dialog"]:not([hidden]) .window-body')).color);
  await settings.getByRole('radio', { name: 'Dark' }).check();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await bodyColor()).toBe('rgb(255, 255, 255)');
  await page.screenshot({ path: join(shots, `${info.project.name}-25-settings-dark.png`) });
  await taskbar(page, 'File Explorer').click();
  await expect(win.getByRole('button', { name: 'Download data.bin' })).toBeVisible();
  await page.screenshot({ path: join(shots, `${info.project.name}-26-explorer-dark.png`) });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await taskbar(page, 'Settings').click();
  await page.getByRole('dialog', { name: 'Settings' }).getByRole('radio', { name: 'Light' }).check();
  expect(await bodyColor()).toBe('rgb(27, 27, 27)');
  await page.getByRole('dialog', { name: 'Settings' }).getByRole('radio', { name: 'Same as this device' }).check();
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/);

  // Closing every window leaves a usable desktop, and the account menu states how files travel.
  await page.getByRole('dialog', { name: 'Settings' }).getByRole('button', { name: 'Close Settings' }).click();
  await explorer(page).getByRole('button', { name: 'Close File Explorer' }).click();   // on a phone it became the visible window
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.screenshot({ path: join(shots, `${info.project.name}-27-empty-desktop.png`) });
  await page.getByRole('button', { name: 'Account', exact: true }).click();
  const account = page.getByRole('menu', { name: 'Account' });
  await expect(account).toContainText('owner@example.test');
  await expect(account).toContainText('not end-to-end encryption');
  await page.keyboard.press('Escape');
  await page.locator('.desktop-icon', { hasText: 'File Explorer' }).click();
  await expect(explorer(page)).toBeVisible();
});

test('a 10,000-item folder loads completely and stays usable', async ({ page }, info) => {
  test.setTimeout(300_000);
  await pairAndOpen(page, info);
  const win = explorer(page);
  await win.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button').first().click();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Big Archive/ }).click();
  await expect(rows(page)).toHaveCount(200);
  await expect(win.getByText('200 items loaded (more available)')).toBeVisible();
  const timings = {};
  const timed = async (label, action) => { const start = Date.now(); await action(); timings[label] = Date.now() - start; };

  await timed('loadAll', async () => {
    await win.getByRole('button', { name: 'Load all' }).click();
    await expect(win.getByText('10,000 items', { exact: true })).toBeVisible({ timeout: 240_000 });
  });
  await expect(win.getByRole('button', { name: 'Load more' })).toHaveCount(0);
  expect(await rows(page).count(), 'only part of the folder is drawn at once').toBeLessThanOrEqual(1200);

  await timed('filter', async () => {
    await win.getByLabel('Filter this folder').fill('f09999');
    await expect(rows(page)).toHaveCount(1, { timeout: 30_000 });
  });
  await timed('clearFilter', async () => {
    await win.getByLabel('Filter this folder').fill('');
    await expect(win.getByText('10,000 items', { exact: true })).toBeVisible({ timeout: 30_000 });
  });
  await timed('sortDescending', async () => {
    await win.getByRole('button', { name: /Ascending\. Switch to descending/ }).click();
    await expect(win.locator('tbody tr').first()).toContainText('f09999.dat', { timeout: 30_000 });
  });
  await win.getByRole('button', { name: /Descending\. Switch to ascending/ }).click();
  await expect(win.locator('tbody tr').first()).toContainText('f00000.dat', { timeout: 30_000 });

  if (!isPhone(info)) {
    await win.getByRole('button', { name: 'Download f00000.dat' }).focus();
    await timed('tenArrowKeys', async () => {
      for (let i = 0; i < 10; i++) await page.keyboard.press('ArrowDown');
      await expect(win.getByRole('button', { name: 'Download f00010.dat' })).toBeFocused({ timeout: 30_000 });
    });
    await timed('selectOne', async () => {
      await page.keyboard.press('Space');
      await expect(win.getByText('1 selected', { exact: false })).toBeVisible({ timeout: 30_000 });
    });
    await timed('selectAll', async () => {
      await page.keyboard.press('Control+a');
      await expect(win.getByText('10,000 selected', { exact: false })).toBeVisible({ timeout: 30_000 });   // the whole folder, not just drawn rows
      await page.keyboard.press('Escape');
    });
    await timed('endKey', async () => {
      await page.keyboard.press('End');
      await expect(win.getByRole('button', { name: 'Download f09999.dat' })).toBeFocused({ timeout: 30_000 });
    });
    await expect(win.getByRole('button', { name: 'Download f09999.dat' })).toBeInViewport();
    await page.keyboard.press('ArrowUp');
    await expect(win.getByRole('button', { name: 'Download f09998.dat' })).toBeFocused();
    await timed('homeKey', async () => {
      await page.keyboard.press('Home');
      await expect(win.getByRole('button', { name: 'Download f00000.dat' })).toBeFocused({ timeout: 30_000 });
    });
    expect(await rows(page).count()).toBeLessThanOrEqual(1200);
  } else {
    await timed('scrollLoadsMore', async () => {
      const before = await rows(page).count();
      await win.locator('.content').evaluate((element) => { element.scrollTop = element.scrollHeight; });
      await expect.poll(() => rows(page).count(), { timeout: 30_000 }).toBeGreaterThan(before);   // scrolling draws further rows
    });
  }
  console.log(`LARGE-FOLDER-TIMINGS ${info.project.name} ${JSON.stringify(timings)}`);
  info.annotations.push({ type: 'timings-ms', description: JSON.stringify(timings) });
  // "Usable" bounds for single interactions on a 10,000-row list (load-all is network-bound and reported, not gated here).
  for (const [label, ms] of Object.entries(timings)) if (label !== 'loadAll') expect(ms, `${label} took ${ms} ms`).toBeLessThan(6000);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
});

test('hidden items: shown only when the PC owner shares them and this browser asks to see them', async ({ page }, info) => {
  test.skip(process.platform === 'win32', 'the fixture marks the file hidden with a leading dot (Unix convention)');
  writeFileSync(join(root, '.private-notes'), 'hidden');
  const ipc = await connectIpc(agent.ready.pipe);
  try {
    const rootId = (await ipc.send('status')).data.roots.find((r) => r.name === 'Work Files').rootId;
    await pairAndOpen(page, info);
    const win = explorer(page);
    await expect(win.getByRole('button', { name: /private-notes/ })).toHaveCount(0);       // the PC does not send it at all
    await expect(win.getByText(/hidden/)).toHaveCount(0);

    await ipc.send('set_root_hidden', { rootId, showHidden: true });
    await win.getByRole('button', { name: 'Refresh' }).click();
    await expect(win.getByText(/1 hidden/)).toBeVisible();                                   // sent, but this browser keeps hidden items out of view
    await expect(win.getByRole('button', { name: /private-notes/ })).toHaveCount(0);
    await taskbar(page, 'Settings').click();
    await page.getByRole('dialog', { name: 'Settings' }).getByRole('checkbox', { name: 'Show hidden items' }).check();
    await taskbar(page, 'File Explorer').click();
    await expect(win.getByRole('button', { name: 'Download .private-notes (hidden item)' })).toBeVisible();
    await page.screenshot({ path: join(shots, `${info.project.name}-28-hidden-items.png`) });
    await page.getByRole('button', { name: 'Account', exact: true }).click();                // preference is per browser profile: reset for other tests
    await page.keyboard.press('Escape');
    await taskbar(page, 'Settings').click();
    await page.getByRole('dialog', { name: 'Settings' }).getByRole('checkbox', { name: 'Show hidden items' }).uncheck();
  } finally {
    await ipc.send('set_root_hidden', { rootId: (await ipc.send('status')).data.roots.find((r) => r.name === 'Work Files').rootId, showHidden: false });
    ipc.close();
  }
});
