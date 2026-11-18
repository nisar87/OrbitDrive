// D11: bounded soak. Real gateway + real agent + a real browser identity, with downloads, uploads and browsing running at the same
// time while the network and the relay are disturbed on a schedule (relay crash and restart, dropped connections, silent network loss,
// a slow link). Not a unit test: it prints a report and exits non-zero when an invariant breaks.
//
// Invariants (these are the pass/fail rules; the measurements are reported, not judged):
//   1. A transfer that reports success is byte-exact (sha-256). One mismatch fails the run, during a disturbance or not.
//   2. An upload that reports success exists on the PC with the exact bytes; a failed upload leaves no final file.
//   3. After the last disturbance everything works again without anyone restarting anything (no failures in the quiet tail).
//   4. Every disturbance is followed by a first success of each kind of work (recovery is measured and has a ceiling).
//   5. Memory of the relay and the agent does not keep climbing: the last quarter's peak may not exceed the first quarter's peak plus a margin.
//
// Run:  node tests/soak/soak.mjs            (ORBIT_SOAK_SECONDS, default 90; the nightly/manual workflow uses longer)
// Output: artifacts/soak-report.json
import { writeFileSync, mkdirSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createHash, randomBytes } from 'node:crypto';
import { startGateway, startProxy, startAgent, kill, agentCli, tempDir, Browser, pair, sleep, freePort, rssBytes, repoRoot } from '../integration/harness.mjs';

const SECONDS = Number(process.env.ORBIT_SOAK_SECONDS ?? 90);
const RECOVERY_CEILING_MS = Number(process.env.ORBIT_SOAK_RECOVERY_MS ?? 45_000);
const GROWTH_MARGIN_MIB = Number(process.env.ORBIT_SOAK_GROWTH_MIB ?? 60);
const TAIL_QUIET_MS = 15_000;
const sha = (buffer) => createHash('sha256').update(buffer).digest('hex');
const started = Date.now();
const since = () => Date.now() - started;

// ---- fixture: deterministic files of mixed sizes, so a wrong byte is detectable
const root = tempDir('soak-root'), drop = tempDir('soak-drop'), state = tempDir('soak-state'), data = tempDir('soak-data');
const files = new Map();
for (const [name, size] of [['small-a.bin', 3_000], ['small-b.bin', 70_000], ['medium-a.bin', 1_500_000], ['medium-b.bin', 4_000_000], ['large.bin', 12_000_000]]) {
  const bytes = randomBytes(size); writeFileSync(join(root, name), bytes); files.set(name, sha(bytes));
}
mkdirSync(join(drop, 'Inbox'));
agentCli(state, 'roots', 'add', 'Soak share', root);
const dropId = agentCli(state, 'roots', 'add', 'Soak drop', drop).match(/\(([A-Za-z0-9_-]+)\)/)[1];
agentCli(state, 'roots', 'uploads', dropId, 'on', '500', '8');

// ---- processes. The relay has a fixed port and data directory so it can be crashed and restarted; the proxy is the network.
const secret = randomBytes(32).toString('hex');
const relayPort = await freePort();
const boot = () => startGateway({ OrbitDrive__DataDirectory: data, OrbitDrive__DevIdentity__Secret: secret, OrbitDrive__MaxRelayBytesPerOwnerPerDay: '0' /* the daily allowance has its own tests (admin.test.mjs) and would end a long soak */ }, relayPort);
let gateway = await boot();
const proxy = await startProxy(gateway);
const agent = await startAgent(proxy, state, 'Soak PC');
const pcId = agent.identity.pcId;
const owner = await new Browser(proxy, 'soak@example.test').signIn();
await pair(agent, owner, 'Soak phone');

// ---- bookkeeping
const stats = { download: { ok: 0, failed: 0 }, browse: { ok: 0, failed: 0 }, upload: { ok: 0, failed: 0 } };
const violations = [];
const okTimes = { download: [], browse: [], upload: [] };           // when each success finished
const events = [];
const note = (type, extra = {}) => events.push({ type, at: since(), ...extra });
if (process.env.ORBIT_SOAK_VERBOSE) setInterval(() => {}, 1 << 30).unref();
const quietFrom = { t: Infinity };
let stopping = false;

const idOf = async () => {
  const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
  const r = roots.body.roots.find((x) => x.rootId !== dropId);
  const page = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: r.rootId, pageSize: 100 });
  return { rootId: r.rootId, entries: page.body.entries };
};
const dropInbox = async () => {
  const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
  const r = roots.body.roots.find((x) => x.rootId === dropId);
  const page = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: r.rootId, pageSize: 100 });
  return { rootId: r.rootId, inbox: page.body.entries.find((e) => e.name === 'Inbox').entryId };
};
const guard = async (kind, work) => {
  try { await work(); stats[kind].ok++; okTimes[kind].push(since()); }
  catch (e) { if (e.integrity) { violations.push(`${kind}: ${e.message}`); } stats[kind].failed++; note('failure', { kind, why: String(e.message).slice(0, 120) }); await sleep(250); }
};

async function downloadOne(readDelayMs = 0) {
  const { entries } = await idOf();
  const entry = entries[Math.floor(Math.random() * entries.length)];
  const session = await owner.post('/v1/download-sessions', { pcId, entryId: entry.entryId });
  if (session.status !== 201) throw new Error(`session ${session.status}: ${session.body?.error?.message ?? session.body?.message ?? ''}`);
  try {
    const response = await fetch(proxy.url + session.body.urlPath);
    if (!response.ok) throw new Error(`download ${response.status}`);
    const hash = createHash('sha256'); let total = 0;
    for await (const chunk of response.body) { hash.update(chunk); total += chunk.length; if (readDelayMs) await sleep(readDelayMs); }
    if (total !== Number(entry.size)) throw new Error(`short body ${total}/${entry.size}`);        // a cut stream is a failure, not corruption
    if (hash.digest('hex') !== files.get(entry.name)) { const e = new Error(`HASH MISMATCH for a ${entry.size}-byte file`); e.integrity = true; throw e; }
  } catch (e) {
    // What the web app does when a transfer fails and the person gives up: cancel it so it stops counting.
    if (!e.integrity && session.body.sessionId) await owner.call('DELETE', `/v1/download-sessions/${session.body.sessionId}`).catch(() => {});
    throw e;
  }
}
async function browseOne() {
  const { entries } = await idOf();
  if (entries.length !== files.size) throw new Error(`listing has ${entries.length} entries`);
}
let uploadCounter = 0;
async function uploadOne() {
  const { rootId, inbox } = await dropInbox();
  const bytes = randomBytes(40_000 + (uploadCounter % 5) * 30_000);
  const name = `up-${Date.now()}-${uploadCounter++}.bin`;
  const begun = await owner.post(`/v1/pcs/${pcId}/uploads`, { collision: 'reject', rootId, folderEntryId: inbox, name, size: String(bytes.length) });
  if (begun.status !== 201) throw new Error(`start ${begun.status}`);
  try {
    const put = await fetch(`${proxy.url}/v1/pcs/${pcId}/uploads/${begun.body.uploadId}/chunk?offset=0`, { method: 'PUT', headers: owner.headers({ 'x-chunk-sha256': sha(bytes), 'content-type': 'application/octet-stream' }), body: bytes });
    if (put.status !== 200) throw new Error(`chunk ${put.status}`);
    const done = await owner.post(`/v1/pcs/${pcId}/upload-finish`, { uploadId: begun.body.uploadId });
    if (done.status !== 200) throw new Error(`finish ${done.status}`);
  } catch (e) {
    // A person who gives up cancels the upload. (An upload that is merely left is kept for 45 minutes so it can be resumed, and the PC allows 3 at once.)
    await owner.post(`/v1/pcs/${pcId}/upload-cancel`, { uploadId: begun.body.uploadId }).catch(() => {});
    throw e;
  }
  const onDisk = join(drop, 'Inbox', name);
  if (!existsSync(onDisk) || sha(readFileSync(onDisk)) !== sha(bytes)) { const e = new Error('an upload reported success but the file on the PC is missing or different'); e.integrity = true; throw e; }
}
const worker = (kind, work, pause) => (async () => { while (!stopping) { await guard(kind, work); await sleep(pause); } })();

// ---- memory sampling
const samples = [];
const sampler = setInterval(() => samples.push({ at: since(), relayMiB: rssBytes(gateway.pid) / 1048576, agentMiB: rssBytes(agent.pid) / 1048576 }), 2000);

// ---- disturbances
const recoveries = [];
async function disturb(kind, run) {
  const t0 = since();
  note('disturbance', { kind });
  await run();
  const healed = since();
  const recovered = {};
  for (const k of Object.keys(okTimes)) {
    const deadline = Date.now() + RECOVERY_CEILING_MS;
    let first;
    while (!(first = okTimes[k].find((t) => t > healed)) && Date.now() < deadline) await sleep(200);
    recovered[k] = first === undefined ? null : first - healed;                  // time from "the disturbance ended" to the first success of this kind
  }
  recoveries.push({ kind, at: t0, durationMs: healed - t0, firstSuccessAfterHealMs: recovered });
  for (const [k, v] of Object.entries(recovered)) if (v === null) violations.push(`no ${k} succeeded within ${RECOVERY_CEILING_MS} ms after "${kind}"`);
}
const disturbances = {
  'relay crash and restart': async () => { await kill(gateway); await sleep(1500); gateway = await boot(); },
  'dropped connections': async () => { proxy.cut(); await sleep(500); },
  'silent network loss (6 s)': async () => { proxy.blackhole(); await sleep(6000); proxy.heal(); },
  'slow link (250 KB/s for 8 s)': async () => { proxy.throttle(250_000); await sleep(8000); proxy.throttle(0); },
};

const workers = [
  worker('download', () => downloadOne(0), 100), worker('download', () => downloadOne(0), 150), worker('download', () => downloadOne(15), 100),
  worker('browse', browseOne, 200), worker('upload', uploadOne, 300),
];

const plan = Object.keys(disturbances);
const endAt = Date.now() + SECONDS * 1000;
let round = 0;
await sleep(5000);                                                                    // a quiet start gives the baseline
while (Date.now() + TAIL_QUIET_MS + 12_000 < endAt) { await disturb(plan[round % plan.length], disturbances[plan[round % plan.length]]); round++; await sleep(5000); }
quietFrom.t = since();
await sleep(Math.max(0, endAt - Date.now()));
stopping = true; await Promise.all(workers); clearInterval(sampler);

// ---- judgement
const tailFailures = events.filter((e) => e.type === 'failure' && e.at >= quietFrom.t + 3000);          // 3 s grace after the last heal
if (tailFailures.length) violations.push(`${tailFailures.length} failures in the quiet tail after the last disturbance (first: ${tailFailures[0].kind}: ${tailFailures[0].why})`);
const quarter = (list, f, which) => { const n = Math.max(1, Math.floor(list.length / 4)); const part = which === 'first' ? list.slice(0, n) : list.slice(-n); return Math.max(0, ...part.map(f)); };
const growth = { relayMiB: quarter(samples, (s) => s.relayMiB, 'last') - quarter(samples, (s) => s.relayMiB, 'first'), agentMiB: quarter(samples, (s) => s.agentMiB, 'last') - quarter(samples, (s) => s.agentMiB, 'first') };
// Warm-up (runtime heaps, pools) legitimately grows for the first minute or two, so growth is judged only on runs long enough to be past it.
if (SECONDS >= 240) for (const [who, value] of Object.entries(growth)) if (value > GROWTH_MARGIN_MIB) violations.push(`${who} grew ${value.toFixed(1)} MiB between the first and last quarter (margin ${GROWTH_MARGIN_MIB})`);
if (stats.download.ok + stats.upload.ok < 20) violations.push('too little work was completed for the run to mean anything');

const peak = (f) => Math.max(0, ...samples.map(f));
const report = {
  kind: 'bounded soak: NOT a Windows-service or real-device soak; one machine, loopback network, fault-injecting proxy',
  platform: process.platform, node: process.version, cores: (await import('node:os')).cpus().length,
  seconds: Math.round(since() / 1000), disturbances: recoveries.length,
  work: stats, relayPeakMiB: +peak((s) => s.relayMiB).toFixed(1), agentPeakMiB: +peak((s) => s.agentMiB).toFixed(1),
  memoryGrowthFirstToLastQuarterMiB: { relay: +growth.relayMiB.toFixed(1), agent: +growth.agentMiB.toFixed(1) },
  memoryTimelineMiB: samples.filter((_, i) => i % 5 === 0).map((x) => ({ s: Math.round(x.at / 1000), relay: Math.round(x.relayMiB), agent: Math.round(x.agentMiB) })),
  failureReasons: Object.entries(events.filter((e) => e.type === 'failure').reduce((m, e) => { const k = `${e.kind}: ${e.why}`.replace(/\d{4,}/g, 'N'); m[k] = (m[k] ?? 0) + 1; return m; }, {})).sort((a, b) => b[1] - a[1]).slice(0, 8),
  recoveries, tailFailures: tailFailures.length, violations,
  verdict: violations.length === 0 ? 'PASS' : 'FAIL',
};
mkdirSync(join(repoRoot, 'artifacts'), { recursive: true });
writeFileSync(join(repoRoot, 'artifacts', `soak-report-${process.platform}.json`), JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ...report, recoveries: recoveries.map((r) => ({ kind: r.kind, firstSuccessAfterHealMs: r.firstSuccessAfterHealMs })) }, null, 2));

proxy.stop(); await agent.stop(); await gateway.stop();
process.exit(violations.length === 0 ? 0 : 1);
