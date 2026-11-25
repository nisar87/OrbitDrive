import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// The only request a page may make outside this site: the public price list. It is mocked here so the tests never depend on the live service.
const PLANS_URL = 'https://relay.myorbitdrive.app/v1/plans';
const LIVE = {
  currency: 'USD', pkrPerUsd: 300, rateUpdatedAt: '2026-12-01T00:00:00+00:00', limitsEnabled: false,
  plans: [
    { id: 'free', name: 'Free', purchasable: false, limits: {}, prices: [] },
    { id: 'plus', name: 'Plus', purchasable: true, limits: {}, prices: [{ months: 1, usd: 4, pkr: 1200, pkrApprox: true }, { months: 3, usd: 10, pkr: 2500, pkrApprox: false }, { months: 12, usd: 29.5, pkr: 8850, pkrApprox: true }] },
    { id: 'office', name: 'Office', purchasable: true, limits: {}, prices: [{ months: 1, usd: 12, pkr: 3600, pkrApprox: true }, { months: 3, usd: 27, pkr: 7500, pkrApprox: false }, { months: 12, usd: 86, pkr: 24000, pkrApprox: false }] },
  ],
};
test.beforeEach(async ({ page }) => {
  await page.route(PLANS_URL, (route) => route.fulfill({ status: 200, contentType: 'application/json', headers: { 'access-control-allow-origin': '*' }, body: JSON.stringify(LIVE) }));
});

test('live prices replace the printed ones: dollars first, rupees beside them, approximate marked', async ({ page }) => {
  await page.goto('/services.html');
  const cell = (plan, months) => page.locator(`[data-plan="${plan}"][data-months="${months}"]`).first();
  await expect(cell('plus', 1)).toHaveText('$4 (\u2248 PKR 1,200)');
  await expect(cell('plus', 3)).toHaveText('$10 (PKR 2,500)');
  await expect(cell('plus', 12)).toHaveText('$29.50 (\u2248 PKR 8,850)');
  await expect(cell('office', 12)).toHaveText('$86 (PKR 24,000)');
});

test('if the price list cannot be reached or is malformed, the printed prices stay', async ({ page }) => {
  await page.unroute(PLANS_URL);
  await page.route(PLANS_URL, (route) => route.abort());
  await page.goto('/services.html');
  await page.waitForTimeout(500);
  await expect(page.locator('[data-plan="plus"][data-months="1"]').first()).toHaveText('$3.50 (PKR 1,000)');
  await page.unroute(PLANS_URL);
  await page.route(PLANS_URL, (route) => route.fulfill({ status: 200, contentType: 'application/json', headers: { 'access-control-allow-origin': '*' }, body: JSON.stringify({ plans: [{ id: 'plus', prices: [{ months: 1, usd: -5, pkr: 'free' }, { months: 3, usd: 0, pkr: 0 }] }] }) }));
  await page.goto('/services.html');
  await page.waitForTimeout(500);
  await expect(page.locator('[data-plan="plus"][data-months="1"]').first()).toHaveText('$3.50 (PKR 1,000)');
  await expect(page.locator('[data-plan="plus"][data-months="3"]').first()).toHaveText('$9 (PKR 2,500)');
});

test('the home page plan cards show dollars with rupees', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('#plans')).toContainText('$4 (\u2248 PKR 1,200)');
});

test('no third-party requests, no console errors, no sideways scroll, all images load', async ({ page }, info) => {
  const external = [], errors = [];
  page.on('request', (r) => { const u = new URL(r.url()); if (!['127.0.0.1', 'localhost'].includes(u.hostname) && u.protocol.startsWith('http') && r.url() !== PLANS_URL) external.push(r.url()); });
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto('/');
  for (const y of [0, 800, 1600, 2400, 3200, 4800, 6400, 8000, 12000]) { await page.evaluate((v) => scrollTo(0, v), y); await page.waitForTimeout(80); }
  await page.locator('img[loading=lazy]').evaluateAll((a) => a.forEach((i) => { i.loading = 'eager'; }));
  await page.waitForFunction(() => [...document.images].every((i) => i.complete), null, { timeout: 15000 });
  expect(external).toEqual([]); expect(errors).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const broken = await page.evaluate(() => [...document.images].filter((i) => !i.complete || i.naturalWidth === 0).map((i) => i.src));
  expect(broken).toEqual([]);
  await page.screenshot({ path: `/tmp/site-${info.project.name}.png`, fullPage: true });
});

test('every image has alt text, one h1, landmarks, links go somewhere', async ({ page }) => {
  await page.goto('/');
  expect(await page.locator('h1').count()).toBe(1);
  expect(await page.locator('img:not([alt])').count()).toBe(0);
  for (const l of ['main', 'header', 'footer', 'nav[aria-label=Main]']) expect(await page.locator(l).count()).toBeGreaterThan(0);
  for (const href of await page.locator('a[href^="#"]').evaluateAll((a) => a.map((x) => x.getAttribute('href')))) if (href.length > 1) expect(await page.locator(href).count(), href).toBe(1);
});

for (const scheme of ['light']) test(`axe: WCAG 2.2 AA rules, ${scheme}`, async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => document.querySelectorAll('.reveal').forEach((e) => e.classList.add('in')));
  await page.waitForTimeout(800);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  expect(r.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
});

test('tabs work by keyboard and show one panel', async ({ page }) => {
  await page.goto('/');
  const first = page.getByRole('tab', { name: 'Explorer' });
  await first.focus(); await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Preview' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[role=tabpanel]:not([hidden])')).toHaveCount(1);
  await page.keyboard.press('End'); await expect(page.getByRole('tab', { name: 'On your phone' })).toHaveAttribute('aria-selected', 'true');
});

test('gallery filter and lightbox (Escape closes, focus returns)', async ({ page }) => {
  await page.goto('/');
  const all = await page.locator('#gal button').count(); expect(all).toBe(18);
  await page.getByRole('button', { name: 'Windows app', exact: true }).click();
  expect(await page.locator('#gal button:not([hidden])').count()).toBe(4);
  const b = page.locator('#gal button:not([hidden])').first(); await b.click();
  await expect(page.locator('#lb')).toBeVisible(); await page.keyboard.press('Escape');
  await expect(page.locator('#lb')).toBeHidden(); await expect(b).toBeFocused();
});

test('reduced motion: content visible immediately, no reveal transforms', async ({ browser }) => {
  const ctx = await browser.newContext({ reducedMotion: 'reduce' }); const page = await ctx.newPage(); await page.goto('http://127.0.0.1:5180/');
  expect(await page.locator('.reveal:not(.in)').count()).toBe(0);
  expect(await page.evaluate(() => getComputedStyle(document.querySelector('.reveal')).transitionDuration)).toMatch(/^0s/);
  await ctx.close();
});

test('pairing simulation is labelled and generates a six digit code', async ({ page }) => {
  await page.goto('/'); await expect(page.getByText('Illustrative simulation')).toBeVisible();
  await page.getByRole('button', { name: 'Show a demo code' }).click(); await expect(page.locator('#simcode')).toHaveText(/^\d{6}$/);
});

test('claims guard: nothing promises end-to-end, zero-knowledge, guarantees or comparisons', async ({ page }) => {
  await page.goto('/');
  const text = (await page.locator('main').innerText()).toLowerCase();
  for (const bad of ['zero-knowledge', 'military', 'guarantee', 'unhackable', 'folderbird', 'dropbox', 'better than', 'fastest']) expect(text, bad).not.toContain(bad);
  // "end-to-end" may appear only in a negation.
  for (const m of text.matchAll(/.{0,40}end-to-end.{0,30}/g)) expect(m[0]).toMatch(/not|no|never|n't|\?/);
});

for (const slug of ['services', 'privacy', 'terms', 'refund', 'delivery', 'contact']) {
  test(`${slug} page: loads, has contact footer, no accessibility violations`, async ({ page }) => {
    await page.goto(`/${slug}.html`);
    await expect(page.locator('h1')).toBeVisible();
    await expect(page.locator('footer')).toContainText('+92 321 8554745');
    await expect(page.locator('footer')).toContainText('Fateh Jang');
    const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag22aa']).analyze();
    expect(r.violations.map((v) => v.id)).toEqual([]);
  });
}
test('services page lists eight services and no end-to-end claim', async ({ page }) => {
  await page.goto('/services.html');
  await expect(page.locator('.card')).toHaveCount(8);
  const t = (await page.locator('main').innerText()).toLowerCase();
  for (const bad of ['zero-knowledge', 'guarantee', 'unhackable']) expect(t).not.toContain(bad);
});
