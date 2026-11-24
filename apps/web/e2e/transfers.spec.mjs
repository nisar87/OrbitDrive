// W5/D5 in a real browser against the real gateway and agent: honest status for browser downloads, and
// "Save to a file…" with real progress, interruption (the relay is killed), resume, source change and cancel.
// The save-location picker cannot be driven by automation, so the tests replace ONLY the picker with a real file
// handle from the browser's private file system; streaming, writing, resuming and closing are the product's own code.
// The browser reaches the relay through a small TCP proxy that can slow the link and drop it, so that interruptions
// land mid-transfer (on a loopback connection a whole file arrives before anything can be interrupted).
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { startGateway, startAgent, startProxy, connectIpc, agentCli, tempDir, makeFixtureRoot, writeLargeFile, rssBytes, repoRoot } from '../../../tests/integration/harness.mjs';

let gateway, agent, root, bigHash, ipc, link;
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
const BIG_MIB = 24;

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  root = makeFixtureRoot();
  bigHash = await writeLargeFile(join(root, 'big.bin'), BIG_MIB);
  writeFileSync(join(root, 'changing.bin'), Buffer.alloc(6 * (1 << 20), 'A'));
  writeFileSync(join(root, 'cancel-me.bin'), Buffer.alloc(6 * (1 << 20), 'C'));
  const state = tempDir('e2e-transfers-state');
  agentCli(state, 'roots', 'add', 'Downloads Share', root);
  gateway = await startGateway();
  link = await startProxy(gateway, 5199);                          // the web app is built to talk to port 5199
  agent = await startAgent(gateway, state, 'Tower PC');
  ipc = await connectIpc(agent.ready.pipe);
});
test.afterAll(async () => { ipc?.close(); await agent?.stop(); link?.stop(); await gateway?.stop(); });
test.beforeEach(() => link.throttle(0));

const explorer = (page) => page.getByRole('dialog', { name: 'File Explorer' });
const transfersWindow = (page) => page.getByRole('dialog', { name: 'Transfers' });
const taskbar = (page, name) => page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name, exact: true });
const item = (page, name) => transfersWindow(page).getByTestId('transfer').filter({ hasText: name });

async function pairAndOpen(page) {
  await page.goto('/');
  await page.getByLabel('Email').fill('owner@example.test');
  await page.getByRole('button', { name: 'Sign in' }).click();
  agent.drain();
  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.getByLabel('Pairing code').fill(pairing.code);
  await page.getByRole('button', { name: 'Pair', exact: true }).click();
  await agent.next('pairing_claimed');
  agent.send('approve');
  await page.getByRole('region', { name: "This account's PCs" }).getByRole('button', { name: /Downloads Share/ }).click();
  await expect(explorer(page).getByRole('button', { name: 'Download big.bin' })).toBeVisible();
}

/** Replaces the save-location picker with a real private-file-system handle whose writer is slowed down. */
async function installPicker(page, delayMs) {
  await page.addInitScript((delay) => {
    window.showSaveFilePicker = async ({ suggestedName }) => {
      const directory = await navigator.storage.getDirectory();
      const handle = await directory.getFileHandle(suggestedName, { create: true });
      return {
        remove: handle.remove ? () => handle.remove() : undefined,
        async createWritable(options) {
          const real = await handle.createWritable(options);
          return {
            // window.__orbitWriteRate (bytes per second) lets a test make this page a slow receiver for a while.
            write: async (data) => {
              const wait = data instanceof Uint8Array && window.__orbitWriteRate > 0 ? (data.length / window.__orbitWriteRate) * 1000 : data instanceof Uint8Array ? delay : 0;
              if (wait > 0) await new Promise((r) => setTimeout(r, wait));
              await real.write(data);
            },
            seek: (position) => real.seek(position),
            close: () => real.close(),
          };
        },
      };
    };
  }, delayMs);
}
// Really tries to create and write a file: some engines expose the API but refuse it (for example in private sessions).
const canWriteFiles = (page) => page.evaluate(async () => {
  try {
    const directory = await navigator.storage.getDirectory();
    const handle = await directory.getFileHandle('orbit-probe.bin', { create: true });
    const writable = await handle.createWritable();
    await writable.write(new Uint8Array([1]));
    await writable.close();
    await directory.removeEntry('orbit-probe.bin');
    return true;
  } catch { return false; }
});

/** SHA-256 and size of a file the page saved, hashed in Node chunk by chunk (the page never holds the whole file). */
async function savedFile(page, name) {
  const hash = createHash('sha256');
  let size = 0;
  await page.exposeFunction(`orbitChunk_${name.replace(/\W/g, '_')}`, (base64) => { const chunk = Buffer.from(base64, 'base64'); hash.update(chunk); size += chunk.length; }).catch(() => undefined);
  const exists = await page.evaluate(async ([fileName, sink]) => {
    let handle;
    try { handle = await (await navigator.storage.getDirectory()).getFileHandle(fileName); } catch { return false; }
    const file = await handle.getFile();
    for (let offset = 0; offset < file.size; offset += 1 << 20) {
      const bytes = new Uint8Array(await file.slice(offset, offset + (1 << 20)).arrayBuffer());
      let binary = '';
      for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      await window[sink](btoa(binary));
    }
    return true;
  }, [name, `orbitChunk_${name.replace(/\W/g, '_')}`]);
  return exists ? { size, sha256: hash.digest('hex') } : null;
}

async function saveToFile(page, name) {
  await explorer(page).locator('tbody tr').filter({ hasText: name }).locator('td.size').click({ button: 'right' });
  await page.getByRole('menu', { name: 'Actions' }).getByRole('menuitem', { name: 'Save to a file…' }).click();
  await expect(transfersWindow(page)).toBeVisible();              // Transfers opens by itself
}

test('a browser download is described as handed over and sent by the relay, never as saved', async ({ page }, info) => {
  await pairAndOpen(page);
  const [download] = await Promise.all([page.waitForEvent('download'), explorer(page).getByRole('button', { name: 'Download data.bin' }).click()]);
  await download.path();
  await taskbar(page, 'Transfers').click();
  const row = item(page, 'data.bin');
  await expect(row).toContainText('Browser download');
  await expect(row).toContainText('left the relay for your browser', { timeout: 15_000 });
  await expect(row).toContainText('cannot see whether your browser finished saving');
  await expect(row.getByRole('progressbar')).toHaveAttribute('value', '100');
  await expect(row).not.toContainText(/\bsaved to\b|\bcomplete\b|\bdownloaded\b/i);
  await page.screenshot({ path: join(shots, `${info.project.name}-30-transfers-browser-download.png`) });
  await row.getByRole('button', { name: 'Remove from list' }).click();
  await expect(item(page, 'data.bin')).toHaveCount(0);
  await expect(transfersWindow(page).getByText('No downloads yet.')).toBeVisible();
});

test('save to a file: real progress, the connection drops mid-transfer, resume completes a byte-exact file', async ({ page }, info) => {
  test.setTimeout(240_000);
  await installPicker(page, 0);
  await pairAndOpen(page);
  link.throttle(3 * (1 << 20));                                  // about 3 MB/s: the 24 MB file takes several seconds
  test.skip(!(await canWriteFiles(page)), 'this browser engine cannot write files from a page');
  // Page memory after a forced garbage collection: JavaScript heap plus the storage behind byte buffers, read
  // through the browser's debugging protocol (Chromium only; other engines report 0 and the check is skipped).
  let cdp = null;
  try { cdp = await page.context().newCDPSession(page); } catch { /* not Chromium */ }
  const heap = async () => {
    if (!cdp) return 0;
    await cdp.send('HeapProfiler.collectGarbage');
    const usage = await cdp.send('Runtime.getHeapUsage');
    return usage.usedSize + (usage.backingStorageSize ?? 0);
  };
  const baseline = await heap();
  let peakHeap = baseline;

  await saveToFile(page, 'big.bin');
  const row = item(page, 'big.bin');
  await expect(row).toContainText('Written by MyOrbitDrive');
  await expect(row).toContainText(/Saving… \d/, { timeout: 30_000 });
  for (const percent of [10, 25, 40]) {
    await expect.poll(async () => Number(await row.getByRole('progressbar').getAttribute('value')), { timeout: 60_000 }).toBeGreaterThanOrEqual(percent);
    peakHeap = Math.max(peakHeap, await heap());                 // sampled while megabytes are flowing through the page
  }
  await page.screenshot({ path: join(shots, `${info.project.name}-31-transfers-saving.png`) });

  link.blackhole();                                              // packets just stop arriving: nothing is closed, no error is raised
  await expect(row).toContainText(/Interrupted at .* of 24 MB/, { timeout: 60_000 });   // noticed by the page's own stall watch
  peakHeap = Math.max(peakHeap, await heap());
  await expect(row).toContainText('What was saved so far is kept');
  const partial = await savedFile(page, 'big.bin');
  expect(partial.size).toBeGreaterThan(0);
  expect(partial.size).toBeLessThan(BIG_MIB * (1 << 20));
  await page.screenshot({ path: join(shots, `${info.project.name}-32-transfers-interrupted.png`) });

  link.heal();                                                   // the network is back
  link.throttle(0);
  // It retries by itself a few times; the Resume button covers the case where those attempts ran out.
  await expect(async () => {
    const resume = row.getByRole('button', { name: 'Resume' });
    // The button can vanish between the check and the click when an automatic retry starts: do not wait for it.
    if (await resume.isVisible()) await resume.click({ timeout: 2000 }).catch(() => undefined);
    await expect(row).toContainText('Saved to the place you chose (24 MB)', { timeout: 8000 });
  }).toPass({ timeout: 120_000 });
  const saved = await page.evaluate(async () => (await (await (await navigator.storage.getDirectory()).getFileHandle('big.bin')).getFile()).size);
  expect(saved).toBe(BIG_MIB * (1 << 20));
  const hashed = createHash('sha256');
  await page.exposeFunction('orbitFinal', (base64) => hashed.update(Buffer.from(base64, 'base64')));
  await page.evaluate(async () => {
    const file = await (await (await navigator.storage.getDirectory()).getFileHandle('big.bin')).getFile();
    for (let offset = 0; offset < file.size; offset += 1 << 20) {
      const bytes = new Uint8Array(await file.slice(offset, offset + (1 << 20)).arrayBuffer());
      let binary = '';
      for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      await window.orbitFinal(btoa(binary));
    }
  });
  expect(hashed.digest('hex'), 'first part + resumed part is exactly the source file').toBe(bigHash);
  await page.screenshot({ path: join(shots, `${info.project.name}-33-transfers-saved.png`) });
  if (baseline > 0) expect(peakHeap - baseline, 'the page never holds more than a small part of the 24 MB file in memory').toBeLessThan(8 * (1 << 20));
  console.log(`TRANSFER-HEAP ${info.project.name} baseline=${baseline} peak=${peakHeap}`);
});

test('save to a file: a source that changes during an interruption is never mixed; cancel removes the partial file', async ({ page }, info) => {
  test.setTimeout(240_000);
  await installPicker(page, 0);
  await pairAndOpen(page);
  test.skip(!(await canWriteFiles(page)), 'this browser engine cannot write files from a page');
  link.throttle(1 << 20);                                        // about 1 MB/s for the 6 MB files

  await saveToFile(page, 'changing.bin');
  const row = item(page, 'changing.bin');
  await expect.poll(async () => Number(await row.getByRole('progressbar').getAttribute('value').catch(() => 0)), { timeout: 60_000 }).toBeGreaterThanOrEqual(10);
  link.cut(); link.blackhole();                                  // the connection drops and the network stays down
  await expect(row).toContainText('Interrupted', { timeout: 30_000 });
  writeFileSync(join(root, 'changing.bin'), Buffer.alloc(6 * (1 << 20), 'B'));     // the owner replaces the file meanwhile
  link.heal();
  await expect(async () => {
    const resume = row.getByRole('button', { name: 'Resume' });
    // The button can vanish between the check and the click when an automatic retry starts: do not wait for it.
    if (await resume.isVisible()) await resume.click({ timeout: 2000 }).catch(() => undefined);
    await expect(row).toContainText('the file changed on the PC', { timeout: 8000 });
  }).toPass({ timeout: 90_000 });
  await expect(row).toContainText('never mixed');
  await page.screenshot({ path: join(shots, `${info.project.name}-34-transfers-source-changed.png`) });
  link.throttle(0);
  await row.getByRole('button', { name: 'Start again' }).click();
  await expect(row).toContainText('Saved to the place you chose (6 MB)', { timeout: 120_000 });
  const content = await page.evaluate(async () => {
    const bytes = new Uint8Array(await (await (await (await navigator.storage.getDirectory()).getFileHandle('changing.bin')).getFile()).arrayBuffer());
    const counts = {};
    for (const byte of bytes) counts[String.fromCharCode(byte)] = (counts[String.fromCharCode(byte)] ?? 0) + 1;
    return counts;
  });
  expect(content, 'only the new version, no leftover bytes of the old one').toEqual({ B: 6 * (1 << 20) });

  // Cancel a running save: the relay link dies, the PC stops reading, the partial file does not stay behind.
  await taskbar(page, 'File Explorer').click();
  link.throttle(1 << 20);
  await saveToFile(page, 'cancel-me.bin');
  const cancelRow = item(page, 'cancel-me.bin');
  await expect(cancelRow).toContainText(/Saving… \d/, { timeout: 30_000 });
  await cancelRow.getByRole('button', { name: 'Cancel' }).click();
  await expect(cancelRow).toContainText('Cancelled');
  await expect(cancelRow).toContainText(/partly saved file was (removed|emptied)/, { timeout: 15_000 });
  const leftover = await page.evaluate(async () => {
    try { return (await (await (await navigator.storage.getDirectory()).getFileHandle('cancel-me.bin')).getFile()).size; } catch { return -1; }
  });
  expect(leftover).toBeLessThanOrEqual(0);
  await expect.poll(async () => (await ipc.send('status')).data.activeStreams, { timeout: 15_000 }).toBe(0);
  await expect(cancelRow.getByRole('button', { name: 'Resume' })).toHaveCount(0);
  await page.screenshot({ path: join(shots, `${info.project.name}-35-transfers-cancelled.png`) });
});

test('a large file (1 GiB in CI) is saved byte-exact with a slow receiver, and memory stays bounded everywhere', async ({ page }, info) => {
  test.skip(!/^desktop-chrom/.test(info.project.name), 'run once, in desktop Chromium, where page memory can be measured');
  const largeMib = Number(process.env.ORBIT_E2E_LARGE_MIB ?? 96);
  test.setTimeout(900_000);
  const largeHash = await writeLargeFile(join(root, 'huge.bin'), largeMib);
  page.on('console', (message) => { if (message.text().includes('[OrbitDrive]')) console.log(`PAGE: ${message.text()}`); });
  await installPicker(page, 0);
  await pairAndOpen(page);
  const cdp = await page.context().newCDPSession(page);
  // Test browsers get a small private-storage allowance; raise it so the large file fits (real saves go to disk).
  await cdp.send('Storage.overrideQuotaForOrigin', { origin: new URL(page.url()).origin, quotaSize: (largeMib + 512) * (1 << 20) });
  const pageMemory = async () => { await cdp.send('HeapProfiler.collectGarbage'); const usage = await cdp.send('Runtime.getHeapUsage'); return usage.usedSize + (usage.backingStorageSize ?? 0); };
  const processes = { relay: gateway.child.pid, pc: agent.child.pid };
  const baseline = { page: await pageMemory(), relay: rssBytes(processes.relay), pc: rssBytes(processes.pc) };
  const peak = { ...baseline };
  const sample = async () => {
    peak.page = Math.max(peak.page, await pageMemory());
    peak.relay = Math.max(peak.relay, rssBytes(processes.relay));
    peak.pc = Math.max(peak.pc, rssBytes(processes.pc));
  };

  await explorer(page).getByRole('button', { name: 'Refresh' }).click();
  await expect(explorer(page).getByRole('button', { name: 'Download huge.bin' })).toBeVisible();
  await page.evaluate(() => { window.__orbitWriteRate = 8 << 20; });  // a slow receiver: it can only save about 8 MiB per second
  const started = Date.now();
  await saveToFile(page, 'huge.bin');
  const row = item(page, 'huge.bin');
  await expect(row).toContainText(/Saving… \d/, { timeout: 120_000 });
  // While the receiver is slow the sender must be held back (backpressure), not buffered in the relay or the page.
  const slowPhase = [];
  for (let i = 0; i < 8; i++) {
    await page.waitForTimeout(1000);
    await sample();
    slowPhase.push({ sentByPc: Number((await ipc.send('status')).data.transfers[0].sent) });
  }
  const last = slowPhase[slowPhase.length - 1];
  const received = Number(await row.getByRole('progressbar').getAttribute('value'));
  expect(received, 'the transfer is under way but far from finished after 8 slow seconds').toBeGreaterThan(0);
  expect(received).toBeLessThan(100);
  // The PC may be ahead of the receiver only by what sits in network buffers, not by the rest of the file.
  const writtenByPage = (received / 100) * largeMib * (1 << 20);
  expect(last.sentByPc, 'the PC did not send the whole file while the receiver was slow').toBeLessThan(largeMib * (1 << 20));
  expect(last.sentByPc - writtenByPage, 'the sender is held back to the receiver\'s pace (bounded lead)').toBeLessThan(160 * (1 << 20));
  await page.evaluate(() => { window.__orbitWriteRate = 0; });        // receiver back to full speed
  for (;;) {
    await sample();
    if (/Saved to the place you chose/.test(await row.textContent())) break;
    if (/Interrupted|Could not|changed/.test(await row.textContent())) throw new Error(`transfer stopped: ${await row.textContent()}`);
    await page.waitForTimeout(1500);
  }
  const seconds = (Date.now() - started) / 1000;
  await sample();

  const hashed = createHash('sha256');
  let hashedBytes = 0;
  await page.exposeFunction('orbitHugeChunk', (base64) => { const chunk = Buffer.from(base64, 'base64'); hashed.update(chunk); hashedBytes += chunk.length; });
  await page.evaluate(async () => {
    const file = await (await (await navigator.storage.getDirectory()).getFileHandle('huge.bin')).getFile();
    const step = 4 << 20;
    for (let offset = 0; offset < file.size; offset += step) {
      const bytes = new Uint8Array(await file.slice(offset, offset + step).arrayBuffer());
      let binary = '';
      for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      await window.orbitHugeChunk(btoa(binary));
    }
  });
  expect(hashedBytes).toBe(largeMib * (1 << 20));
  expect(hashed.digest('hex'), 'the saved file is exactly the source').toBe(largeHash);

  const mib = (bytes) => Math.round(bytes / (1 << 20));
  const growth = { page: mib(peak.page - baseline.page), relay: mib(peak.relay - baseline.relay), pc: mib(peak.pc - baseline.pc) };
  console.log(`LARGE-SAVE ${info.project.name} size=${largeMib}MiB seconds=${seconds.toFixed(1)} slowPhase: receiverWrote=${mib(writtenByPage)}MiB pcSent=${mib(last.sentByPc)}MiB growthMiB=${JSON.stringify(growth)}`);
  info.annotations.push({ type: 'large-save', description: JSON.stringify({ largeMib, seconds, growth }) });
  expect(growth.page, 'page memory growth').toBeLessThan(24);
  expect(growth.relay, 'relay memory growth').toBeLessThan(96);
  expect(growth.pc, 'PC agent memory growth').toBeLessThan(96);
  await page.evaluate(async () => { await (await navigator.storage.getDirectory()).removeEntry('huge.bin'); });
});

test('save to a file: a full disk is reported as such, the PC stops sending, and nothing is retried in a loop', async ({ page }, info) => {
  test.skip(!/chrom/.test(info.project.name), 'the storage allowance can only be lowered through the Chromium debugging protocol');
  await installPicker(page, 0);
  await pairAndOpen(page);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Storage.overrideQuotaForOrigin', { origin: new URL(page.url()).origin, quotaSize: 3 * (1 << 20) });   // room for 3 MB only
  await saveToFile(page, 'big.bin');                                 // 24 MB
  const row = item(page, 'big.bin');
  await expect(row).toContainText('Could not download', { timeout: 60_000 });
  await expect(row).toContainText('not enough free space');
  await expect(row).not.toContainText('connection was lost');
  await page.screenshot({ path: join(shots, `${info.project.name}-36-transfers-disk-full.png`) });
  await expect.poll(async () => (await ipc.send('status')).data.activeStreams, { timeout: 15_000 }).toBe(0);
  await page.waitForTimeout(6000);                                   // long enough for an automatic retry, if there wrongly were one
  await expect(row).toContainText('not enough free space');
  expect((await ipc.send('status')).data.activeStreams).toBe(0);
  await expect(row.getByRole('button', { name: 'Start again' })).toBeVisible();
  await cdp.send('Storage.overrideQuotaForOrigin', { origin: new URL(page.url()).origin, quotaSize: 512 * (1 << 20) });  // space is freed
  await row.getByRole('button', { name: 'Start again' }).click();
  await expect(row).toContainText('Saved to the place you chose (24 MB)', { timeout: 120_000 });
});
