// Relay host automation, tested with the REAL updater/monitor/backup scripts and REAL gateway builds.
// GitHub's releases API is replaced by a local server and systemd by a small restart hook; everything else is real.
// This does not prove the scripts on the Oracle host itself (systemd units, Caddy, apt) — see docs/operations/relay.md.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { execFileSync, spawnSync, spawn } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync, truncateSync, cpSync, chmodSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { generateKeys, signFile, verifyFile } from '../../scripts/relay-sign.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const bin = (name) => join(repo, 'infra/relay/bin', name);
const skip = process.platform === 'win32' ? 'relay host scripts are Linux-only' : false;
const work = mkdtempSync(join(tmpdir(), 'orbit-relay-'));
const root = join(work, 'opt'), assets = join(work, 'assets'), log = join(work, 'deploy.log');
const port = 5300 + Math.floor(Math.random() * 400);
let api, releases = [];

function build(version) {
  const out = join(work, `build-${version}`);
  execFileSync('dotnet', ['publish', join(repo, 'services/gateway/OrbitDrive.Gateway'), '-c', 'Release', '-o', out, `-p:Version=${version}`, '-v', 'q', '--nologo'], { stdio: 'pipe' });
  return out;
}
function publish(version, dir, { channel = 'dev', badChecksum = false, tag, signWith, badSignature = false } = {}) {
  const name = `gateway-${version}.tar.gz`;
  execFileSync('tar', ['-czf', join(assets, name), '-C', dir, '.']);
  const sum = createHash('sha256').update(readFileSync(join(assets, name))).digest('hex');
  writeFileSync(join(assets, `${name}.sha256`), `${badChecksum ? '0'.repeat(64) : sum}  ${name}\n`);
  const url = (n) => `http://127.0.0.1:${api.address().port}/assets/${n}`;
  const files = [{ name, browser_download_url: url(name) }, { name: `${name}.sha256`, browser_download_url: url(`${name}.sha256`) }];
  if (signWith) {
    signFile(join(assets, name), signWith, join(assets, `${name}.sig`));
    if (badSignature) { const bytes = readFileSync(join(assets, `${name}.sig`)); bytes[bytes.length - 1] ^= 0xff; writeFileSync(join(assets, `${name}.sig`), bytes); }
    files.push({ name: `${name}.sig`, browser_download_url: url(`${name}.sig`) });
  }
  releases.unshift({ tag_name: tag ?? `relay-${channel}-${version}`, draft: false, assets: files });
}
const env = (extra = {}) => ({ ...process.env, ORBIT_ROOT: root, ORBIT_API: `http://127.0.0.1:${api.address().port}`, ORBIT_HEALTH_URL: `http://127.0.0.1:${port}`,
  ORBIT_RESTART_CMD: join(work, 'restart.sh'), ORBIT_DEPLOY_LOG: log, ORBIT_HEALTH_TRIES: '10', ...extra });
// Asynchronous on purpose: the stand-in releases API lives in this process and must be able to answer.
const update = (extra) => new Promise((resolveRun) => {
  const child = spawn('bash', [bin('orbitdrive-update')], { env: env(extra) });
  let stderr = '';
  child.stderr.on('data', (d) => { stderr += d; });
  child.on('close', (status) => resolveRun({ status, stderr }));
});
const audit = () => (existsSync(log) ? readFileSync(log, 'utf8').trim().split('\n').map((l) => JSON.parse(l)) : []);
const serving = async () => { try { return (await (await fetch(`http://127.0.0.1:${port}/v1/meta`)).json()).version; } catch { return null; } };

let v1, v2;
before(async () => {
  if (skip) return;
  mkdirSync(assets, { recursive: true }); mkdirSync(root, { recursive: true });
  api = createServer((request, response) => {
    if (request.url.startsWith('/releases')) { response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(releases)); }
    else if (request.url.startsWith('/assets/')) { try { response.end(readFileSync(join(assets, request.url.slice(8)))); } catch { response.statusCode = 404; response.end(); } }
    else { response.statusCode = 404; response.end(); }
  }).listen(0, '127.0.0.1');
  await once(api, 'listening');
  // Stand-in for "systemctl restart orbitdrive-gateway": stop the old process, start whatever "current" points to.
  writeFileSync(join(work, 'restart.sh'), `#!/usr/bin/env bash
[ -f "${work}/pid" ] && kill "$(cat "${work}/pid")" 2>/dev/null; sleep 0.5
cd "${root}/current" || exit 0
ASPNETCORE_ENVIRONMENT=Development OrbitDrive__DevIdentity__Enabled=true OrbitDrive__DevIdentity__Secret=${'s'.repeat(40)} \\
  nohup dotnet OrbitDrive.Gateway.dll --urls http://127.0.0.1:${port} >/dev/null 2>&1 &
echo $! > "${work}/pid"
`);
  chmodSync(join(work, 'restart.sh'), 0o755);
  v1 = build('1.0.0'); v2 = build('1.4.0');
});
after(() => { api?.close(); try { process.kill(Number(readFileSync(join(work, 'pid'), 'utf8'))); } catch { /* not running */ } });

test('no release for this channel: nothing happens', { skip }, async () => {
  publish('9.9.9', v1, { channel: 'prod' });                       // another channel must be ignored
  assert.equal((await update()).status, 0);
  assert.deepEqual([audit().length, await serving(), existsSync(join(root, 'current'))], [0, null, false]);
});

test('first release is installed and verified by the version the gateway reports', { skip }, async () => {
  publish('1.0.0', v1);
  const result = await update();
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await serving(), '1.0.0');
  assert.deepEqual(audit().map((a) => [a.action, a.version]), [['deployed', '1.0.0']]);
  assert.equal((await update()).status, 0);
  assert.equal(audit().length, 1, 'running again with nothing new changes nothing');
});

test('a release whose checksum does not match is refused and never started', { skip }, async () => {
  publish('1.1.0', v2, { badChecksum: true });
  assert.equal((await update()).status, 1);
  assert.equal(await serving(), '1.0.0');
  assert.equal(existsSync(join(root, 'releases', '1.1.0')), false);
  assert.deepEqual([audit().at(-1).action, audit().at(-1).detail], ['refused', 'checksum mismatch']);
  releases.shift();
});

test('a release that cannot start is rolled back automatically and not retried', { skip }, async () => {
  const broken = join(work, 'broken'); cpSync(v2, broken, { recursive: true });
  truncateSync(join(broken, 'OrbitDrive.Gateway.dll'), 100);
  publish('1.2.0', broken);
  assert.equal((await update()).status, 2);
  assert.equal(await serving(), '1.0.0', 'the previous release is serving again');
  const last = audit().at(-1);
  assert.deepEqual([last.action, last.version, last.previous], ['rolled_back', '1.2.0', '1.0.0']);
  assert.equal(readFileSync(join(root, 'bad-versions'), 'utf8').trim(), '1.2.0');
  const count = audit().length;
  assert.equal((await update()).status, 0);
  assert.equal(audit().length, count, 'a known-bad release is not installed again');
});

test('a release that runs but reports a different version is treated as failed', { skip }, async () => {
  publish('1.3.0', v1);                                             // really the 1.0.0 build under a new label
  assert.equal((await update()).status, 2);
  assert.equal(await serving(), '1.0.0');
  assert.equal(audit().at(-1).action, 'rolled_back');
});

test('malicious or malformed releases are refused', { skip }, async () => {
  publish('1.3.5', v2, { tag: 'relay-dev-1.3.5; touch /tmp/pwned' });
  assert.equal((await update()).status, 1);
  assert.equal(audit().at(-1).detail, 'invalid version in tag');
  releases.shift();
  const evil = join(work, 'evil'); mkdirSync(join(evil, 'sub'), { recursive: true });
  writeFileSync(join(evil, 'OrbitDrive.Gateway.dll'), 'x'); writeFileSync(join(work, 'escaped.txt'), 'outside');
  execFileSync('tar', ['-czf', join(assets, 'gateway-1.3.6.tar.gz'), '-C', join(evil, 'sub'), '-P', '../../escaped.txt', '../OrbitDrive.Gateway.dll']);
  const sum = createHash('sha256').update(readFileSync(join(assets, 'gateway-1.3.6.tar.gz'))).digest('hex');
  writeFileSync(join(assets, 'gateway-1.3.6.tar.gz.sha256'), `${sum}  gateway-1.3.6.tar.gz\n`);
  const url = (n) => `http://127.0.0.1:${api.address().port}/assets/${n}`;
  releases.unshift({ tag_name: 'relay-dev-1.3.6', draft: false, assets: [{ name: 'gateway-1.3.6.tar.gz', browser_download_url: url('gateway-1.3.6.tar.gz') }, { name: 'gateway-1.3.6.tar.gz.sha256', browser_download_url: url('gateway-1.3.6.tar.gz.sha256') }] });
  assert.notEqual((await update()).status, 0);
  assert.equal(existsSync(join(root, 'escaped.txt')), false, 'archive entries cannot escape the release directory');
  assert.equal(existsSync(join(root, 'releases', 'escaped.txt')), false);
  assert.equal(await serving(), '1.0.0');
  releases.shift();
});

test('a nearly full disk blocks installation', { skip }, async () => {
  publish('1.3.9', v2);
  assert.equal((await update({ ORBIT_MAX_DISK_PCT: '0' })).status, 1);
  assert.match(audit().at(-1).detail, /disk \d+% used/);
  assert.equal(await serving(), '1.0.0');
  releases.shift();
});

test('a good new release replaces the old one and old releases are pruned', { skip }, async () => {
  publish('1.4.0', v2);
  const result = await update();
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await serving(), '1.4.0');
  assert.deepEqual([audit().at(-1).action, audit().at(-1).previous], ['deployed', '1.0.0']);
  assert.ok(readdirSync(join(root, 'releases')).length <= 4);
  for (const entry of audit()) assert.ok(entry.time && entry.action, 'every step is in the audit trail');
});

test('monitor reports health, resources and alerts without leaking anything private', { skip }, async () => {
  const state = join(work, 'state');
  const run = (extra) => spawnSync('bash', [bin('orbitdrive-monitor')], { env: { ...process.env, ORBIT_STATE: state, ORBIT_HEALTH_URL: `http://127.0.0.1:${port}`, ...extra }, encoding: 'utf8' });
  const first = run();
  assert.equal(first.status, 0, first.stderr);
  const status = JSON.parse(readFileSync(join(state, 'public/status.json'), 'utf8'));
  assert.equal(status.version, '1.4.0');
  assert.deepEqual(Object.keys(status).sort(), ['alerts', 'diskUsedPct', 'egressGbThisMonth', 'last24h', 'logs', 'memAvailableMb', 'swapFreeMb', 'time', 'version']);
  assert.ok(status.memAvailableMb > 0 && status.diskUsedPct >= 0);
  run({ ORBIT_HEALTH_URL: 'http://127.0.0.1:1', ORBIT_WARN_DISK_PCT: '0', ORBIT_MIN_MEM_MB: '99999999' });
  const bad = JSON.parse(readFileSync(join(state, 'public/status.json'), 'utf8'));
  assert.deepEqual(bad.alerts.filter((a) => ['gateway_down', 'low_memory', 'disk_filling'].includes(a)).sort(), ['disk_filling', 'gateway_down', 'low_memory']);
});

test('monitor digests the gateway log into counts only, alerts on spikes, and leaks no message text', { skip }, () => {
  const state = join(work, 'state-logs'), all = join(work, 'log-all.txt'), warn = join(work, 'log-warn.txt');
  const secret = 'alice@example.test token=SECRET123 C:\\Users\\alice\\tax.pdf';
  const lines = [];
  for (let i = 0; i < 6; i++) lines.push(`      GET /v1/pcs/{pcId}/roots -> 500 in ${40 + i} ms [abcdefgh${i}]`);
  for (let i = 0; i < 4; i++) lines.push(`      POST /v1/pairing-requests/{id}/claim -> 429 in 2 ms [zzzzzzzz${i}]`);
  lines.push('      GET /healthz -> 200 in 1 ms [okokokok1]', '      GET /v1/pcs -> 401 in 3 ms [authauth1]', '      GET /v1/agent-control -> 101 in 158899 ms [longlong1]', '      GET /v1/downloads/{capability} -> 200 in 90000 ms [streamst1]');   // long-lived by design
  writeFileSync(all, lines.join('\n') + '\n');
  writeFileSync(warn, [`fail: Microsoft.AspNetCore.Server.Kestrel[13]`, `      Unhandled exception: System.InvalidOperationException ${secret}`, `fail: x`, `      System.InvalidOperationException ${secret}`, `fail: y`, `      System.IO.IOException ${secret}`, ''].join('\n'));
  const run = spawnSync('bash', [bin('orbitdrive-monitor')], { env: { ...process.env, ORBIT_STATE: state, ORBIT_HEALTH_URL: `http://127.0.0.1:${port}`, ORBIT_LOG_ALL_FILE: all, ORBIT_LOG_WARN_FILE: warn }, encoding: 'utf8' });
  assert.equal(run.status, 0, run.stderr);
  const raw = readFileSync(join(state, 'public/status.json'), 'utf8');
  const status = JSON.parse(raw);
  assert.equal(status.logs.requests, 14);
  assert.equal(status.logs.status5xx, 6);
  assert.equal(status.logs.status429, 4);
  assert.equal(status.logs.status401403, 1);
  assert.equal(status.logs.slowestMs, 45, 'permanent connections are not "slow requests"');
  assert.deepEqual(status.logs.topFailingRoutes, [{ route: 'GET /v1/pcs/{pcId}/roots', count: 6 }]);
  assert.deepEqual(status.logs.topExceptions.map((e) => e.type), ['System.InvalidOperationException', 'System.IO.IOException']);
  assert.ok(status.alerts.includes('http_5xx_spike') && status.alerts.includes('gateway_errors'));
  for (const private_ of ['alice', 'SECRET123', 'tax.pdf', 'abcdefgh']) assert.ok(!raw.includes(private_), `the public status must not contain ${private_}`);
  // Quiet logs raise nothing.
  writeFileSync(all, '      GET /healthz -> 200 in 1 ms [okokokok1]\n'); writeFileSync(warn, '');
  const calm = spawnSync('bash', [bin('orbitdrive-monitor')], { env: { ...process.env, ORBIT_STATE: state, ORBIT_HEALTH_URL: `http://127.0.0.1:${port}`, ORBIT_LOG_ALL_FILE: all, ORBIT_LOG_WARN_FILE: warn }, encoding: 'utf8' });
  assert.equal(calm.status, 0, calm.stderr);
  const quiet = JSON.parse(readFileSync(join(state, 'public/status.json'), 'utf8'));
  assert.ok(!quiet.alerts.some((a) => ['http_5xx_spike', 'gateway_errors', 'rate_limit_flood', 'auth_failures_spike'].includes(a)));
  // The host remembers the last day itself: the earlier spike is still visible although the current window is calm.
  assert.equal(quiet.last24h.samples, 2);
  assert.equal(quiet.last24h.status5xx, 6);
  assert.equal(quiet.last24h.requests, 15);
  assert.ok(quiet.last24h.recentAlerts.includes('http_5xx_spike') && quiet.last24h.recentAlerts.includes('gateway_errors'));
  assert.equal(quiet.last24h.slowestMs, 45);
  assert.ok(!JSON.stringify(quiet).includes('alice'), 'the rolling summary holds counts only');
});

test('backup archives configuration, proves it restores, and keeps seven copies', { skip }, () => {
  const config = join(work, 'etc-orbitdrive'), dest = join(work, 'backups');
  mkdirSync(config); writeFileSync(join(config, 'gateway.env'), 'ASPNETCORE_ENVIRONMENT=Production\n');
  const run = () => spawnSync('bash', [bin('orbitdrive-backup')], { env: { ...process.env, ORBIT_BACKUP_DIR: dest, ORBIT_BACKUP_SOURCES: `${config} ${join(work, 'missing')}` }, encoding: 'utf8' });
  for (let i = 0; i < 9; i++) { const result = run(); assert.equal(result.status, 0, result.stderr); spawnSync('sleep', ['1.1']); }
  const files = readdirSync(dest);
  assert.equal(files.length, 7);
  const restored = join(work, 'restore'); mkdirSync(restored);
  execFileSync('tar', ['-xzf', join(dest, files.sort().at(-1)), '-C', restored, '--absolute-names', '--transform', 's,^/,,']);
  assert.equal(readFileSync(join(restored, config.slice(1), 'gateway.env'), 'utf8'), 'ASPNETCORE_ENVIRONMENT=Production\n');
});

// ---- F-010: off-host copy is encrypted, default off, and a restore drill proves a copy opens ----
test('off-host backup: off by default; refuses without a passphrase; sends only an encrypted file that the restore drill opens; leaves invitation records out', { skip }, () => {
  const config = join(work, 'etc-orbitdrive-2'), data = join(work, 'gateway-data'), dest = join(work, 'backups-2'), sent = join(work, 'sent');
  mkdirSync(config); mkdirSync(data); mkdirSync(sent);
  writeFileSync(join(config, 'gateway.env'), 'ASPNETCORE_ENVIRONMENT=Production\n');
  writeFileSync(join(data, 'relay.json'), '{"pcs":[]}');
  writeFileSync(join(data, 'invitations.json'), '{"secret":"invitation-record-marker"}');
  const pass = join(work, 'pass.txt'); writeFileSync(pass, 'correct horse battery staple\n', { mode: 0o600 });
  const base = { ...process.env, ORBIT_BACKUP_DIR: dest, ORBIT_BACKUP_SOURCES: `${config} ${data}` };
  const run = (extra) => spawnSync('bash', [bin('orbitdrive-backup')], { env: { ...base, ...extra }, encoding: 'utf8' });

  const plain = run({});
  assert.equal(plain.status, 0, plain.stderr);
  assert.match(plain.stdout, /off-host copy: off/);
  assert.equal(readdirSync(sent).length, 0, 'nothing leaves the host unless it is configured');

  const upload = `cp "$1" ${sent}/`;
  const noPass = run({ ORBIT_BACKUP_UPLOAD_CMD: upload });
  assert.equal(noPass.status, 3, 'an upload command with no passphrase file is refused, never sent unencrypted');
  assert.equal(readdirSync(sent).length, 0);

  spawnSync('sleep', ['1.1']);
  const sentOk = run({ ORBIT_BACKUP_UPLOAD_CMD: upload, ORBIT_BACKUP_PASSPHRASE_FILE: pass });
  assert.equal(sentOk.status, 0, sentOk.stderr);
  const [encrypted] = readdirSync(sent);
  assert.match(encrypted, /\.tar\.gz\.gpg$/);
  const raw = readFileSync(join(sent, encrypted));
  assert.ok(!raw.includes('ASPNETCORE_ENVIRONMENT'), 'the sent file is not readable text');
  assert.equal(raw.includes(Buffer.from('invitation-record-marker')), false);

  const drill = (file, passFile) => spawnSync('bash', [bin('orbitdrive-restore-verify'), file, ...(passFile ? [passFile] : [])], { encoding: 'utf8', env: { ...process.env, ORBIT_BACKUP_PASSPHRASE_FILE: '' } });
  const good = drill(join(sent, encrypted), pass);
  assert.equal(good.status, 0, good.stdout + good.stderr);
  assert.match(good.stdout, /restore drill ok/);
  const wrong = join(work, 'wrong.txt'); writeFileSync(wrong, 'not the passphrase\n', { mode: 0o600 });
  assert.notEqual(drill(join(sent, encrypted), wrong).status, 0, 'a wrong passphrase fails the drill');
  assert.equal(drill(join(sent, encrypted)).status, 2, 'an encrypted archive needs the passphrase file');
  const local = readdirSync(dest).filter((f) => f.endsWith('.tar.gz')).sort().at(-1);
  assert.equal(drill(join(dest, local)).status, 0, 'a local archive opens without a passphrase');
  const listing = execFileSync('tar', ['-tzf', join(dest, local), '--absolute-names']).toString();
  assert.ok(!listing.includes('invitations.json'), 'invitation records are never archived');
  // A damaged archive is reported, not accepted.
  const broken = join(work, 'broken.tar.gz'); writeFileSync(broken, readFileSync(join(dest, local)).subarray(0, 40));
  assert.notEqual(drill(broken).status, 0);
  // A failing upload keeps the local copy and says so.
  spawnSync('sleep', ['1.1']);
  const failed = run({ ORBIT_BACKUP_UPLOAD_CMD: 'exit 1', ORBIT_BACKUP_PASSPHRASE_FILE: pass });
  assert.equal(failed.status, 5); assert.match(failed.stderr, /upload FAILED/);
});

// ---- F-011: no downgrades; signatures enforced only when the host has a public key ----
test('a release lower than the running one is refused, noted once, and never installed', { skip }, async () => {
  publish('1.2.5', v1);                                              // the newest release listed is older than the running 1.4.0
  assert.equal((await update()).status, 0);
  assert.equal(await serving(), '1.4.0');
  assert.equal(existsSync(join(root, 'releases', '1.2.5')), false);
  assert.deepEqual([audit().at(-1).action, audit().at(-1).detail], ['refused', 'version is lower than the running release']);
  const count = audit().length;
  assert.equal((await update()).status, 0);
  assert.equal(audit().length, count, 'the same refusal is not written every five minutes');
  releases.shift();
});

test('with no signing key on the host an unsigned release still installs (ready but off), and the log says so', { skip }, async () => {
  publish('1.5.0', build('1.5.0'));                                  // the gateway inside reports the version it is published as
  const result = await update({ ORBIT_SIGNING_KEY: join(work, 'no-such-key.pub') });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await serving(), '1.5.0');
  assert.match(audit().at(-1).detail, /checksum only: no signing key on this host/);
});

test('with a signing key on the host: a signed release installs; unsigned, wrongly signed and other-key releases are refused', { skip }, async () => {
  const keys = generateKeys(join(work, 'keys')), other = generateKeys(join(work, 'other-keys'));
  
  const strict = { ORBIT_SIGNING_KEY: keys.pub };

  publish('1.6.0', v2);                                              // no .sig at all
  assert.equal((await update(strict)).status, 1);
  assert.match(audit().at(-1).detail, /not signed but this host requires signed releases/);
  releases.shift();

  publish('1.6.1', v2, { signWith: keys.priv, badSignature: true }); // a damaged signature
  assert.equal((await update(strict)).status, 1);
  assert.equal(audit().at(-1).detail, 'signature does not verify');
  releases.shift();

  publish('1.6.2', v2, { signWith: other.priv });                    // signed by someone else's key
  assert.equal((await update(strict)).status, 1);
  assert.equal(audit().at(-1).detail, 'signature does not verify');
  assert.equal(await serving(), '1.5.0');
  releases.shift();

  publish('1.6.3', build('1.6.3'), { signWith: keys.priv });                     // the right key
  const result = await update(strict);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await serving(), '1.6.3');
  assert.match(audit().at(-1).detail, /signature verified/);
});
