import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test('no third-party requests, no console errors, no sideways scroll, all images load', async ({ page }, info) => {
  const external = [], errors = [];
  page.on('request', (r) => { const u = new URL(r.url()); if (!['127.0.0.1', 'localhost'].includes(u.hostname) && u.protocol.startsWith('http')) external.push(r.url()); });
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
  const all = await page.locator('#gal button').count(); expect(all).toBe(15);
  await page.getByRole('button', { name: 'Windows app', exact: true }).click();
  expect(await page.locator('#gal button:not([hidden])').count()).toBe(3);
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
