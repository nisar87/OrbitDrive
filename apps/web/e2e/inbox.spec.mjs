// W6/D6 in a real browser against the real gateway and agent: a file the owner picks on the PC appears in this browser's Inbox,
// accepting it is a user action that downloads the exact bytes, and everything private goes away on sign-out.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { createHash, randomBytes } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, connectIpc, agentCli, tempDir, makeFixtureRoot, repoRoot, sleep } from '../../../tests/integration/harness.mjs';

let gateway, agent, ipc, link, state, outside;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
const NAME = "Ünï côdé – résumé (final) 'v2'.txt";

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  const root = makeFixtureRoot();
  outside = tempDir('e2e-inbox-outside');
  state = tempDir('e2e-inbox-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  link = await startProxy(gateway, 5199);
  agent = await startAgent(gateway, state, 'Tower PC');
  ipc = await connectIpc(agent.ready.pipe);
});
test.afterAll(async () => { ipc?.close(); await agent?.stop(); link?.stop(); await gateway?.stop(); });

const inboxWindow = (page) => page.getByRole('dialog', { name: 'Inbox' });
const taskbar = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name });

async function pair(page, browserName) {
  await page.goto('/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  const name = page.getByLabel(/name/i).first();
  if (await name.isVisible().catch(() => false)) await name.fill(browserName).catch(() => undefined);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await expect(page.getByRole('region', { name: "This account's PCs" })).toBeVisible();
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  await expect(page.getByRole('dialog', { name: 'File Explorer' })).toBeVisible();
}

/** The receiver id of the browser that was paired last (the PC's own list of paired browsers). */
async function lastReceiver() {
  const devices = (await ipc.send('devices')).data;
  return devices[devices.length - 1].deviceId;
}
async function sendFile(receiverId, name, content) {
  const path = join(outside, name);
  writeFileSync(path, content);
  const reply = await ipc.send('send_files', { receiverId, paths: [path] });
  expect(reply.ok, JSON.stringify(reply)).toBe(true);
  expect(reply.data.results[0].ok, JSON.stringify(reply)).toBe(true);
  return path;
}

test.describe.configure({ mode: 'serial' });

test('a file sent from the PC appears with a notice and a badge; accepting downloads the exact bytes; the state stays honest', async ({ page }, info) => {
  await pair(page, 'Test phone');
  const receiver = await lastReceiver();
  await taskbar(page, 'Inbox').click();
  await expect(inboxWindow(page).getByText('Nothing is waiting.')).toBeVisible();

  const content = randomBytes(150_000);
  await sendFile(receiver, NAME, content);
  await expect(page.getByTestId('inbox-toast')).toContainText(`New file from Tower PC: ${NAME}`, { timeout: 15_000 });
  await expect(taskbar(page, 'Inbox, 1 waiting')).toBeVisible();
  const card = inboxWindow(page).getByTestId('invitation').filter({ hasText: NAME });
  await expect(card).toContainText('nothing has been copied anywhere yet');
  await expect(card).toContainText('146 KB');
  await page.screenshot({ path: join(shots, `inbox-pending-${info.project.name}.png`) });

  const downloading = page.waitForEvent('download');
  await card.getByRole('button', { name: 'Accept and download' }).click();
  const download = await downloading;
  // WebKit's own download handling (Playwright on Linux) turns spaces into underscores; every other character must survive exactly.
  expect(download.suggestedFilename()).toBe(info.project.name.includes('webkit') ? NAME.replaceAll(' ', '_') : NAME);
  const path = await download.path();
  expect(createHash('sha256').update(readFileSync(path)).digest('hex')).toBe(createHash('sha256').update(content).digest('hex'));
  await expect(card).toHaveAttribute('data-state', 'accepted');
  await expect(taskbar(page, 'Inbox')).toBeVisible();   // no badge once nothing is pending

  // The accepted download is an ordinary managed transfer too: its page says what the relay saw, not "saved".
  await page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: 'Transfers', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Transfers' }).getByTestId('transfer').filter({ hasText: NAME })).toContainText(/browser|relay/i);
});

test('declining removes the offer from the waiting list; a PC that is away is shown as away, and accepting works when it returns', async ({ page }, info) => {
  await pair(page, 'Second phone');
  const receiver = await lastReceiver();
  await sendFile(receiver, 'decline-me.txt', 'no thanks');
  await taskbar(page, /Inbox/).click();
  const decline = inboxWindow(page).getByTestId('invitation').filter({ hasText: 'decline-me.txt' });
  await decline.getByRole('button', { name: 'Decline' }).click();
  await expect(decline).toHaveAttribute('data-state', 'declined');
  await expect(decline.getByRole('button', { name: /Accept/ })).toHaveCount(0);

  await sendFile(receiver, 'while-away.txt', 'still on the PC');
  const away = inboxWindow(page).getByTestId('invitation').filter({ hasText: 'while-away.txt' });
  await expect(away).toContainText('nothing has been copied', { timeout: 15_000 });
  ipc.close();
  await agent.stop();
  await expect(away).toContainText(/off, asleep or offline/, { timeout: 30_000 });
  await page.screenshot({ path: join(shots, `inbox-pc-away-${info.project.name}.png`) });
  await away.getByRole('button', { name: 'Accept and download' }).click();
  await expect(away.getByRole('alert')).toContainText(/offline|off/i);
  await expect(away).toHaveAttribute('data-state', 'pending');

  agent = await startAgent(gateway, state, 'Tower PC');
  ipc = await connectIpc(agent.ready.pipe);
  await expect(away).toContainText(/still on Tower PC/, { timeout: 30_000 });
  const downloading = page.waitForEvent('download');
  await away.getByRole('button', { name: 'Accept and download' }).click();
  const download = await downloading;
  expect(readFileSync(await download.path(), 'utf8')).toBe('still on the PC');
});

test('a changed file is reported as changed, not delivered; notifications can be refused and the Inbox still works', async ({ page }) => {
  await pair(page, 'Third phone');
  const receiver = await lastReceiver();
  const path = await sendFile(receiver, 'edited.txt', 'first version');
  writeFileSync(path, 'second version, longer');
  await taskbar(page, /Inbox/).click();
  const card = inboxWindow(page).getByTestId('invitation').filter({ hasText: 'edited.txt' });
  await card.getByRole('button', { name: 'Accept and download' }).click();
  await expect(card.getByRole('alert')).toContainText(/changed/i);
  await expect(card).toHaveAttribute('data-state', 'cancelled');
  await expect(card).toContainText('Ask for it to be sent again');

  // Permission is requested only from this click, and a refusal leaves everything working. Browsers without the
  // Notification API at all (an iPhone in a normal tab) get a plain explanation instead of a button.
  const supported = await page.evaluate(() => typeof Notification !== 'undefined');
  if (supported) {
    await page.context().clearPermissions();
    await page.evaluate(() => { Notification.requestPermission = async () => 'denied'; });
    await inboxWindow(page).getByRole('button', { name: 'Notify me about new files' }).click();
    await expect(inboxWindow(page).getByRole('status').filter({ hasText: /blocked|cannot show/ })).toBeVisible();
  } else {
    await expect(inboxWindow(page).getByRole('button', { name: /notif/i })).toHaveCount(0);
    await expect(inboxWindow(page)).toContainText('This browser cannot show notifications');
  }
  await sendFile(receiver, 'after-denied.txt', 'x');
  await expect(inboxWindow(page).getByTestId('invitation').filter({ hasText: 'after-denied.txt' })).toBeVisible({ timeout: 15_000 });
});

test('signing out removes every trace of what was offered: page, storage and caches; the installed-app files are static only', async ({ page }) => {
  await pair(page, 'Fourth phone');
  const receiver = await lastReceiver();
  await sendFile(receiver, 'private-name.txt', 'secret');
  await taskbar(page, /Inbox/).click();
  await expect(inboxWindow(page).getByText('private-name.txt')).toBeVisible({ timeout: 15_000 });

  // The manifest is valid and its icons exist.
  const manifest = await page.evaluate(async () => (await fetch('/manifest.webmanifest')).json());
  expect(manifest).toMatchObject({ display: 'standalone', start_url: '/', scope: '/' });
  for (const icon of manifest.icons) expect((await page.request.get(icon.src)).headers()['content-type']).toContain('image/png');
  expect(manifest.icons.map((i) => i.sizes)).toEqual(expect.arrayContaining(['192x192', '512x512']));

  // One service worker, and nothing private in its caches or in storage.
  const worker = await page.evaluate(async () => { const r = await navigator.serviceWorker.ready; return { scope: r.scope, active: !!r.active }; });
  expect(worker.active).toBe(true);
  const stored = async () => page.evaluate(async () => {
    const urls = [];
    for (const name of await caches.keys()) for (const request of await (await caches.open(name)).keys()) urls.push(request.url);
    return { urls, storage: JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) };
  });
  let now = await stored();
  expect(now.urls.every((u) => /\/(assets|icons)\//.test(new URL(u).pathname))).toBe(true);
  expect(now.storage).not.toContain('private-name');

  await page.getByRole('button', { name: 'Account', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByLabel('Email')).toBeVisible();
  await expect(page.getByText('private-name.txt')).toHaveCount(0);
  now = await stored();
  expect(now.storage).not.toContain('private-name');
  expect(now.storage).not.toContain('invitation');
  expect(now.urls.every((u) => /\/(assets|icons)\//.test(new URL(u).pathname))).toBe(true);
  // A different account on the same browser starts empty and has no paired device, so it cannot reach anyone's Inbox.
  await page.getByLabel('Email').fill('someone-else@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByText('private-name.txt')).toHaveCount(0);
  await expect(page.getByLabel('Pairing code')).toBeVisible();
});
