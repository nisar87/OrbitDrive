// D11: reproducible measurements of the real gateway and agent on THIS machine. It reports numbers; it does not claim a universal speed.
// Budgets (below) are deliberately loose guards against regressions of an order of magnitude, set from measurements on a 2-core Linux sandbox
// and GitHub-hosted runners; tighten them from the recorded reports, not from guesses. Output: artifacts/perf-report-<platform>.json
import { writeFileSync, mkdirSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { cpus, totalmem } from 'node:os';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, writeLargeFile, Browser, pair, sleep, rssBytes, repoRoot } from '../integration/harness.mjs';

const BUDGET = { idleRelayMiB: 200, idleAgentMiB: 250, listingP95Ms: 1500, firstByteP95Ms: 2000, cancelMs: 3000, minThroughputMiBps: 2, searchP95Ms: 2000, idleCpuPercent: 25 };
const quantile = (values, q) => { const sorted = [...values].sort((a, b) => a - b); return sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))]; };
const time = async (work) => { const t = performance.now(); const value = await work(); return [performance.now() - t, value]; };
const cpuSeconds = (pid) => {                                       // Linux only: utime+stime from /proc; other platforms report null
  try { const f = readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1].split(' '); return (Number(f[11]) + Number(f[12])) / 100; } catch { return null; }
};

const root = makeFixtureRoot(), state = tempDir('perf-state'), drop = tempDir('perf-drop');
const sizeMiB = Number(process.env.ORBIT_PERF_MIB ?? 64);
await writeLargeFile(join(root, 'big.bin'), sizeMiB);
agentCli(state, 'roots', 'add', 'Share', root);
const dropId = agentCli(state, 'roots', 'add', 'Drop', drop).match(/\(([A-Za-z0-9_-]+)\)/)[1];
agentCli(state, 'roots', 'uploads', dropId, 'on', String(sizeMiB * 2), '8');

const gateway = await startGateway({ OrbitDrive__MaxRelayBytesPerOwnerPerDay: '0' });
const agent = await startAgent(gateway, state, 'Perf PC');
const pcId = agent.identity.pcId;
const owner = await new Browser(gateway, 'perf@example.test').signIn();
await pair(agent, owner, 'Perf phone');
const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
const shareId = roots.body.roots.find((r) => r.rootId !== dropId).rootId;
const list = async (folderEntryId) => (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: shareId, folderEntryId, pageSize: 500 })).body.entries;
const top = await list();
const many = top.find((e) => e.name === 'Many'), big = top.find((e) => e.name === 'big.bin');

const report = { kind: 'measured on one machine over loopback (no real network); throughput is a ceiling for the software, not a network speed; not a device or production measurement', platform: process.platform, node: process.version, cores: cpus().length, totalMemMiB: Math.round(totalmem() / 1048576), sizeMiB, results: {}, budgets: BUDGET, violations: [] };
const check = (label, value, limit, unit) => { if (value > limit) report.violations.push(`${label}: ${value.toFixed?.(1) ?? value} ${unit} is over the budget of ${limit}`); };

// idle: let startup settle, then sample memory and CPU over 20 s with nothing happening
await sleep(5000);
const c0 = { relay: cpuSeconds(gateway.pid), agent: cpuSeconds(agent.pid) }; const t0 = performance.now();
await sleep(20_000);
const wall = (performance.now() - t0) / 1000;
report.results.idle = {
  relayMiB: +(rssBytes(gateway.pid) / 1048576).toFixed(1), agentMiB: +(rssBytes(agent.pid) / 1048576).toFixed(1),
  relayCpuPercentOfOneCore: c0.relay === null ? null : +(((cpuSeconds(gateway.pid) - c0.relay) / wall) * 100).toFixed(2),
  agentCpuPercentOfOneCore: c0.agent === null ? null : +(((cpuSeconds(agent.pid) - c0.agent) / wall) * 100).toFixed(2),
};
check('idle relay memory', report.results.idle.relayMiB, BUDGET.idleRelayMiB, 'MiB'); check('idle agent memory', report.results.idle.agentMiB, BUDGET.idleAgentMiB, 'MiB');
for (const k of ['relayCpuPercentOfOneCore', 'agentCpuPercentOfOneCore']) if (report.results.idle[k] !== null) check(`idle ${k}`, report.results.idle[k], BUDGET.idleCpuPercent, '%');

// listing a 450-entry folder, 40 times
const listing = []; for (let i = 0; i < 40; i++) listing.push((await time(() => list(many.entryId)))[0]);
report.results.listing450 = { p50Ms: +quantile(listing, 0.5).toFixed(1), p95Ms: +quantile(listing, 0.95).toFixed(1) };
check('listing p95', report.results.listing450.p95Ms, BUDGET.listingP95Ms, 'ms');

// search over the share, 30 times
const searches = [];
for (let i = 0; i < 30; i++) searches.push((await time(() => owner.post(`/v1/pcs/${pcId}/searches`, { searchId: 's' + i + Math.random().toString(36).slice(2, 8), limit: 50, query: 'item-04' })))[0]);
report.results.search = { p50Ms: +quantile(searches, 0.5).toFixed(1), p95Ms: +quantile(searches, 0.95).toFixed(1) };
check('search p95', report.results.search.p95Ms, BUDGET.searchP95Ms, 'ms');

// time to first byte of a download, 15 times (session + first chunk)
const firstBytes = [];
for (let i = 0; i < 15; i++) {
  const [ms] = await time(async () => {
    const session = await owner.post('/v1/download-sessions', { pcId, entryId: top.find((e) => e.name === 'hello.txt').entryId });
    const response = await fetch(gateway.url + session.body.urlPath); await response.arrayBuffer();
  });
  firstBytes.push(ms);
}
report.results.smallDownload = { p50Ms: +quantile(firstBytes, 0.5).toFixed(1), p95Ms: +quantile(firstBytes, 0.95).toFixed(1) };
check('small download p95', report.results.smallDownload.p95Ms, BUDGET.firstByteP95Ms, 'ms');

// throughput, and memory while moving it
const peak = { relay: 0, agent: 0 };
const watch = setInterval(() => { peak.relay = Math.max(peak.relay, rssBytes(gateway.pid)); peak.agent = Math.max(peak.agent, rssBytes(agent.pid)); }, 250);
const session = await owner.post('/v1/download-sessions', { pcId, entryId: big.entryId });
const [downMs] = await time(async () => { let n = 0; for await (const chunk of (await fetch(gateway.url + session.body.urlPath)).body) n += chunk.length; if (n !== sizeMiB * 1048576) throw new Error('short download'); });
report.results.download = { MiB: sizeMiB, seconds: +(downMs / 1000).toFixed(2), MiBps: +(sizeMiB / (downMs / 1000)).toFixed(1) };
if (report.results.download.MiBps < BUDGET.minThroughputMiBps) report.violations.push(`download throughput ${report.results.download.MiBps} MiB/s is under the floor of ${BUDGET.minThroughputMiBps}`);
clearInterval(watch);
report.results.memoryWhileTransferringMiB = { relayPeak: +(peak.relay / 1048576).toFixed(1), agentPeak: +(peak.agent / 1048576).toFixed(1), note: 'peak resident memory while one file of sizeMiB moved; read it against the file size and the idle figures' };

// cancel latency: start a big download, read a little, cancel it, and time until the client's stream ends
{
  const s = await owner.post('/v1/download-sessions', { pcId, entryId: big.entryId });
  const response = await fetch(gateway.url + s.body.urlPath);
  const reader = response.body.getReader(); await reader.read();
  const [ms] = await time(async () => {
    await owner.call('DELETE', `/v1/download-sessions/${s.body.sessionId}`);
    try { for (;;) { const { done } = await reader.read(); if (done) break; } } catch { /* the stream was cut: that is the cancel */ }
  });
  report.results.cancelLatencyMs = +ms.toFixed(1);
  check('cancel latency', ms, BUDGET.cancelMs, 'ms');
}

mkdirSync(join(repoRoot, 'artifacts'), { recursive: true });
report.verdict = report.violations.length === 0 ? 'PASS' : 'FAIL';
writeFileSync(join(repoRoot, 'artifacts', `perf-report-${process.platform}.json`), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
await agent.stop(); await gateway.stop();
process.exit(report.violations.length ? 1 : 0);
