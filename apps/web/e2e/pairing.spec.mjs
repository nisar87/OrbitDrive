// W2 in a real browser against the real gateway and agent: in-site QR scanning through the camera API, the
// camera-denied fallback, and renaming an approved browser.
// The "camera" is Chromium's fake capture device playing a video of the QR that the real Windows-console encoder
// produced for a live pairing code. It exercises getUserMedia -> video -> canvas -> decoder -> claim, but it is NOT a
// physical phone camera (no focus, glare, angle or real lens), and it is Chromium only.
import { test, expect, chromium } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, agentCliRaw, tempDir, makeFixtureRoot, repoRoot } from '../../../tests/integration/harness.mjs';

let gateway, agent, stateDir;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  stateDir = tempDir('e2e-pair-state');
  agentCli(stateDir, 'roots', 'add', 'Scan Share', makeFixtureRoot());
  gateway = await startGateway({}, 5199);
  agent = await startAgent(gateway, stateDir, 'Scan PC');
});
test.afterAll(async () => { await agent?.stop(); await gateway?.stop(); });

async function ipcDevices() {
  const ipc = await connectIpc(agent.ready.pipe);
  try { return (await ipc.send('devices')).data.map((d) => d.name); } finally { ipc.close(); }
}

async function signIn(page, email) {
  await page.goto('/');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Pair with your PC' }).first()).toBeVisible();
}

/** A short uncompressed video (Y4M) showing the given text as a QR code, drawn by the agent's own encoder. */
function qrVideo(text) {
  const result = agentCliRaw(stateDir, 'qr', text);
  if (result.status !== 0) throw new Error(result.stderr);
  const qr = JSON.parse(result.stdout);
  const width = 640, height = 480, scale = Math.floor(360 / (qr.size + 8));
  const side = qr.size * scale, left = (width - side) >> 1, top = (height - side) >> 1;
  const luma = Buffer.alloc(width * height, 235);
  for (let y = 0; y < qr.size; y++) for (let x = 0; x < qr.size; x++) {
    if (qr.rows[y][x] !== '1') continue;
    for (let dy = 0; dy < scale; dy++) luma.fill(16, (top + y * scale + dy) * width + left + x * scale, (top + y * scale + dy) * width + left + (x + 1) * scale);
  }
  const frame = Buffer.concat([Buffer.from('FRAME\n'), luma, Buffer.alloc(width * height / 2, 128)]);
  const file = join(tempDir('qr-video'), 'qr.y4m');
  writeFileSync(file, Buffer.concat([Buffer.from(`YUV4MPEG2 W${width} H${height} F10:1 Ip A1:1 C420jpeg\n`), ...Array(10).fill(frame)]));
  return file;
}

async function withCamera(info, videoFile, run) {
  const browser = await chromium.launch({
    channel: info.project.use.channel,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', `--use-file-for-fake-video-capture=${videoFile}`],
  });
  try {
    const { defaultBrowserType, channel, ...device } = info.project.use;
    const context = await browser.newContext({ ...device, baseURL: 'http://localhost:5173' });
    await run(await context.newPage());
  } finally { await browser.close(); }
}

test('scan the PC\'s QR code inside the site, approve on the PC, then rename this browser', async ({ browserName }, info) => {
  test.skip(browserName !== 'chromium', 'the fake camera device exists only in Chromium');
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await withCamera(info, qrVideo(pairing.url), async (page) => {
    const uploads = [];
    page.on('request', (request) => { if (request.method() !== 'GET' && request.method() !== 'OPTIONS') uploads.push(`${request.method()} ${new URL(request.url()).pathname} ${(request.postData() ?? '').length}`); });
    await signIn(page, 'scanner@example.test');
    await page.getByRole('button', { name: 'Scan QR code with camera' }).click();
    const code = page.getByTestId('confirmation-code');
    await expect(code).toBeVisible({ timeout: 20_000 });
    const shown = await agent.next('pairing_claimed');
    expect(await code.textContent()).toBe(shown.confirmationCode);
    expect(shown.accountLabel).toBe('scanner@example.test');
    expect(page.url()).not.toContain(pairing.code.split('-')[1]);
    await expect(page.getByTestId('qr-scanner')).toHaveCount(0);
    expect(await page.evaluate(() => [...document.querySelectorAll('video')].length), 'camera preview is gone after the scan').toBe(0);
    // Camera frames stay on the device: the only non-GET traffic is sign-in and the small pairing claim.
    expect(uploads.every((u) => Number(u.split(' ')[2]) < 400), uploads.join('\n')).toBe(true);
    expect(uploads.some((u) => u.includes('/claim'))).toBe(true);

    agent.send('approve');
    await expect(page.getByRole('button', { name: /Scan Share/ })).toBeVisible();

    // Rename this browser; the PC owner's device list shows the new name.
    await page.getByRole('button', { name: /^Rename/ }).click();
    const field = page.getByLabel(/New name for/);
    await field.fill('Pocket phone');
    await page.screenshot({ path: join(shots, `${info.project.name}-10-rename-device.png`) });
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.locator('.device-list strong', { hasText: 'Pocket phone' })).toBeVisible();
    await page.getByRole('button', { name: /^Rename/ }).click();
    await page.getByLabel(/New name for/).fill('Changed my mind');
    await page.getByLabel(/New name for/).press('Escape');
    await expect(page.locator('.device-list strong', { hasText: 'Pocket phone' })).toBeVisible();
    await expect(page.getByText('Changed my mind')).toHaveCount(0);
    expect((await ipcDevices()).sort()).toContain('Pocket phone');
    await page.reload();
    await expect(page.locator('.device-list strong', { hasText: 'Pocket phone' })).toBeVisible();
  });
});

test('a QR code that is not an OrbitDrive pairing code is explained and never opened', async ({ browserName }, info) => {
  test.skip(browserName !== 'chromium', 'the fake camera device exists only in Chromium');
  await withCamera(info, qrVideo('https://evil.example/pair#id=ABCDEF&s=GHJKMNPQRS'), async (page) => {
    const elsewhere = [];
    page.on('request', (request) => { if (!/^http:\/\/(localhost|127\.0\.0\.1)[:/]/.test(request.url())) elsewhere.push(request.url()); });
    await signIn(page, 'scanner2@example.test');
    await page.getByRole('button', { name: 'Scan QR code with camera' }).click();
    await expect(page.getByRole('status')).toContainText('evil.example', { timeout: 20_000 });
    await page.screenshot({ path: join(shots, `${info.project.name}-11-scan-wrong-site.png`) });
    expect(elsewhere, 'the scanned address is never contacted').toEqual([]);
    await page.getByRole('button', { name: 'Stop camera' }).click();
    await expect(page.getByTestId('qr-scanner')).toHaveCount(0);
    await expect(page.getByLabel('Pairing code')).toBeVisible();
  });
});

test('camera permission denied: a clear message, and typing the code still pairs', async ({ page, browserName }, info) => {
  test.skip(browserName !== 'chromium', 'permission prompts cannot be driven reliably in the other engines');
  await signIn(page, 'scanner@example.test');             // same account that owns the PC; default context has no camera permission
  await page.getByRole('button', { name: 'Scan QR code with camera' }).click();
  await expect(page.getByRole('alert')).toContainText(/camera/i, { timeout: 20_000 });
  await expect(page.getByTestId('qr-scanner')).toHaveCount(0);
  await page.screenshot({ path: join(shots, `${info.project.name}-12-camera-denied.png`) });
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code.toLowerCase());
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await expect(page.getByTestId('confirmation-code')).toBeVisible();
  await agent.next('pairing_claimed');
  agent.send('deny');
  await expect(page.getByRole('alert')).toContainText('denied');
});
