// W11 in a real browser: the PRODUCTION bundle, served with exactly the headers that firebase.json gets (scripts/web-security-headers.mjs),
// works end to end with zero Content-Security-Policy violations, and the policy really is enforced (inline script, foreign network and
// foreign pictures are blocked).
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync, readFileSync, copyFileSync, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { execFileSync } from 'node:child_process';
import { join, extname, normalize, sep } from 'node:path';
import { startGateway, startAgent, startProxy, agentCli, tempDir, repoRoot, useThumbnailHelper } from '../../../tests/integration/harness.mjs';
import { pageHeaders, buildCsp } from '../../../scripts/web-security-headers.mjs';

useThumbnailHelper();
const WEB_PORT = 5175, GATEWAY_PORT = 5196;
const RELAY = `http://127.0.0.1:${GATEWAY_PORT}`;
const ORIGIN = `http://localhost:${WEB_PORT}`;
const dist = join(repoRoot, 'apps', 'web', 'dist-csptest');
const options = { allowLoopbackHttp: true, upgradeInsecure: false };      // plain-http loopback test servers cannot be upgraded to https
const TYPES = { '.mjs': 'text/javascript; charset=utf-8', '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
let gateway, link, agent, web, root, state;

function buildProductionBundle() {
  if (existsSync(join(dist, 'index.html')) && !process.env.CSP_REBUILD) return;
  execFileSync('npx', ['vite', 'build', '--mode', 'csptest', '--outDir', 'dist-csptest', '--emptyOutDir'], {
    cwd: join(repoRoot, 'apps', 'web'), stdio: 'inherit', shell: process.platform === 'win32',
    env: { ...process.env, VITE_AUTH_MODE: 'dev', VITE_GATEWAY_URL: RELAY, VITE_REGISTER_SW: 'true' },
  });
}

function serveBundle() {
  const headers = Object.fromEntries(pageHeaders(RELAY, options).map((h) => [h.key, h.value]));
  const server = createServer((req, res) => {
    const path = decodeURIComponent(new URL(req.url, ORIGIN).pathname);
    if (path === '/__eval-probe.js') {                                    // a same-origin script (allowed by script-src 'self') that tries eval: only the page's own code is subject to the policy
      res.writeHead(200, { ...headers, 'Content-Type': TYPES['.js'] });
      res.end("try { new Function('return 1')(); window.__evalRan = true; } catch { window.__evalBlocked = true; } try { eval('1'); window.__evalRan2 = true; } catch { /* blocked */ }");
      return;
    }
    let file = normalize(join(dist, path));
    if (!file.startsWith(dist + sep) || !existsSync(file) || !statSync(file).isFile()) file = join(dist, 'index.html');     // single-page fallback
    res.writeHead(200, { ...headers, 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream' });
    res.end(readFileSync(file));
  });
  return new Promise((resolve) => server.listen(WEB_PORT, 'localhost', () => resolve(server)));
}

test.beforeAll(async () => {
  buildProductionBundle();
  root = tempDir('e2e-csp-root'); state = tempDir('e2e-csp-state');
  copyFileSync(join(repoRoot, 'tests/fixtures/images/photo-640x480.png'), join(root, 'photo.png'));
  writeFileSync(join(root, 'notes.txt'), 'plain text for the preview\n');
  copyFileSync(join(repoRoot, 'tests/fixtures/documents/sample.pdf'), join(root, 'sample.pdf'));
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway({ OrbitDrive__AllowedWebOrigins__0: ORIGIN }, GATEWAY_PORT);
  agent = await startAgent(gateway, state, 'Tower PC');
  web = await serveBundle();
});
test.afterAll(async () => { await agent?.stop(); await gateway?.stop(); await new Promise((r) => web?.close(r)); });

test.describe.configure({ mode: 'serial' });

function watch(page) {
  const seen = [];
  page.on('console', (m) => { if (/content security policy|violates the following/i.test(m.text())) seen.push(`console: ${m.text()}`); });
  page.addInitScript(() => { document.addEventListener('securitypolicyviolation', (e) => (window.__csp ??= []).push(`${e.effectiveDirective} blocked ${e.blockedURI}`)); });
  return { seen, fromPage: async () => (await page.evaluate(() => window.__csp ?? []).catch(() => [])) };
}

test('the page itself carries the full header set, exactly as built', async ({ page }) => {
  const response = await page.goto(ORIGIN + '/');
  const got = response.headers();
  for (const { key, value } of pageHeaders(RELAY, options)) expect(got[key.toLowerCase()], key).toBe(value);
  expect(got['content-security-policy']).toBe(buildCsp(RELAY, options));
});

test('sign-in, pairing, browsing, thumbnails, preview, download and the service worker all run with zero policy violations', async ({ page }, info) => {
  const csp = watch(page);
  await page.goto(ORIGIN + '/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  const pcs = page.getByRole('region', { name: "This account's PCs" });
  await expect(pcs).toBeVisible();
  await pcs.getByRole('button', { name: /Share/ }).click();
  const explorer = page.getByRole('dialog', { name: 'File Explorer' });
  await expect(explorer.getByRole('button', { name: 'Preview photo.png' })).toBeVisible();

  const thumbnail = page.waitForResponse((r) => r.url().startsWith(RELAY + '/v1/downloads/') && r.status() === 200, { timeout: 20_000 });
  await explorer.getByRole('group', { name: 'View' }).getByRole('button', { name: 'Icons' }).click();          // thumbnails come from the relay origin
  await thumbnail;                                                                                              // a policy block would give no response at all
  await explorer.getByRole('button', { name: 'Preview photo.png' }).click();                                  // full picture from the relay origin
  const preview = page.getByRole('dialog', { name: 'Preview' });
  await expect.poll(() => preview.locator('img[data-full="true"]').evaluate((img) => img.naturalWidth), { timeout: 20_000 }).toBe(640);
  await page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: 'File Explorer' }).click();
  const [download] = await Promise.all([page.waitForEvent('download'), explorer.getByRole('button', { name: 'Download notes.txt' }).click()]);   // bytes via the relay
  expect(download.suggestedFilename()).toBe('notes.txt');
  await expect.poll(async () => (await page.evaluate(() => navigator.serviceWorker?.getRegistration().then((r) => !!r)))).toBe(true);   // worker-src 'self'

  expect(await csp.fromPage(), 'violations seen by the page').toEqual([]);
  expect(csp.seen, 'violations logged by the browser').toEqual([]);
  mkdirSync(join(repoRoot, 'artifacts', 'web-screenshots'), { recursive: true });
  await page.screenshot({ path: join(repoRoot, 'artifacts', 'web-screenshots', `csp-production-bundle-${info.project.name}.png`) });
});

test('a PDF opens in Preview under the strict policy (the PDF reader runs in a same-origin worker, with no eval), zero violations', async ({ page }) => {
  const csp = watch(page);
  const failures = [];
  page.on('pageerror', (e) => failures.push(e.message));
  await page.goto(ORIGIN + '/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain(); agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Share/ }).click();
  const explorer = page.getByRole('dialog', { name: 'File Explorer' });
  await explorer.getByRole('button', { name: 'Preview sample.pdf' }).click();
  const preview = page.getByRole('dialog', { name: 'Preview' });
  await expect(preview).toContainText('Page 1 of 2', { timeout: 30_000 });
  await expect.poll(async () => preview.locator('canvas').evaluate((c) => { const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let dark = 0; for (let i = 0; i < d.length; i += 4) if (d[i] < 100) dark++; return dark; }), { timeout: 20_000 }).toBeGreaterThan(50);   // text was actually drawn
  await preview.getByRole('button', { name: 'Next page' }).click();
  await expect(preview).toContainText('Page 2 of 2');
  expect(failures, 'script errors on the page').toEqual([]);
  expect(await csp.fromPage(), 'violations seen by the page').toEqual([]);
  expect(csp.seen, 'violations logged by the browser').toEqual([]);
});

test('the policy is really enforced: inline script, foreign network and foreign pictures are blocked', async ({ page }) => {
  await page.addInitScript(() => { document.addEventListener('securitypolicyviolation', (e) => (window.__csp ??= []).push(`${e.effectiveDirective} blocked ${e.blockedURI}`)); });
  await page.goto(ORIGIN + '/');
  await page.evaluate(() => { const s = document.createElement('script'); s.textContent = 'window.__inlineRan = true'; document.head.append(s); });
  expect(await page.evaluate(() => window.__inlineRan)).toBeUndefined();
  await page.evaluate(() => { document.body.insertAdjacentHTML('beforeend', '<img id="x" src="https://evil.example.invalid/pixel.png" onerror="window.__imgFailed=1">'); });
  const reached = await page.evaluate(() => fetch('https://evil.example.invalid/steal').then(() => true, () => false));
  expect(reached).toBe(false);
  await page.evaluate(() => new Promise((resolve) => { const s = document.createElement('script'); s.src = '/__eval-probe.js'; s.onload = s.onerror = resolve; document.head.append(s); }));
  expect(await page.evaluate(() => [window.__evalRan, window.__evalRan2, window.__evalBlocked])).toEqual([undefined, undefined, true]);
  const seen = await page.evaluate(() => window.__csp);
  expect(seen.some((v) => v.startsWith('script-src'))).toBe(true);
  expect(seen.some((v) => v.includes('eval'))).toBe(true);
  expect(seen.some((v) => v.startsWith('img-src') && v.includes('evil.example.invalid'))).toBe(true);
  expect(seen.some((v) => v.startsWith('connect-src') && v.includes('evil.example.invalid'))).toBe(true);
});

test('a page on another origin that tries to embed the app is refused (framing)', async ({ page }) => {
  const parent = 'http://localhost:5177/';                               // a real local server on a different origin (different port) than the app
  const other = createServer((req, res) => { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end(`<iframe id="f" src="${ORIGIN}/"></iframe>`); });
  await new Promise((resolve) => other.listen(5177, 'localhost', resolve));
  try {
    await page.goto(parent);
    await page.waitForTimeout(2000);
    const embedded = page.frames().find((f) => f !== page.mainFrame());
    const appLoaded = embedded && embedded.url().startsWith(ORIGIN) ? await embedded.locator('#root').count() : 0;
    expect(appLoaded, "the app must not render inside another site's frame").toBe(0);
  } finally { await new Promise((resolve) => other.close(resolve)); }
});
