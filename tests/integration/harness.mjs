import assert from 'node:assert/strict';
// Real-process integration harness: starts the built gateway and agent binaries and talks to them over HTTP.
// Nothing here is mocked; fixtures are generated into a temp directory at run time.
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, createWriteStream } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { inflateRawSync } from 'node:zlib';

export const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const config = process.env.ORBIT_CONFIG || 'Release';
const gatewayDll = join(repoRoot, `services/gateway/OrbitDrive.Gateway/bin/${config}/net10.0/OrbitDrive.Gateway.dll`);
const agentDll = join(repoRoot, `apps/desktop/OrbitDrive.Agent/bin/${config}/net10.0/orbitdrive-agent.dll`);
export const WEB_ORIGIN = 'http://localhost:5173';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

/** A port the operating system reports as free right now (never a hard-coded range: Windows reserves some). */
export async function freePort() {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address();
  server.close();
  await once(server, 'close');
  return port;
}

export function rssBytes(pid) {
  try {
    if (process.platform === 'win32') {
      const out = execFileSync('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
      const match = out.match(/"([\d.,\s]+) K"/);
      return match ? Number(match[1].replace(/[^\d]/g, '')) * 1024 : 0;
    }
    const status = readFileSync(`/proc/${pid}/status`, 'utf8');
    return Number(status.match(/VmRSS:\s+(\d+) kB/)[1]) * 1024;
  } catch { return 0; }
}

export async function startGateway(overrides = {}, fixedPort = 0) {
  for (const dll of [gatewayDll, agentDll]) if (!existsSync(dll)) throw new Error(`Build first: missing ${dll}`);
  const port = fixedPort || await freePort();
  const url = `http://127.0.0.1:${port}`;
  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',
    OrbitDrive__DevIdentity__Enabled: 'true',
    OrbitDrive__DevIdentity__Secret: randomBytes(32).toString('hex'),
    OrbitDrive__AllowedWebOrigins__0: WEB_ORIGIN,
    // Every suite shares one client address and a few accounts; the real limits are exercised in pairing-limits.test.mjs.
    OrbitDrive__PairingClaimsPerMinute: '600',
    OrbitDrive__PairingPollsPerMinute: '6000',
    ...overrides,
  };
  const child = spawn('dotnet', [gatewayDll, '--urls', url], { cwd: dirname(gatewayDll), env, stdio: ['ignore', 'pipe', 'pipe'] });
  const gateway = { url, child, log: '', pid: child.pid, stop: () => stop(child) };
  child.stdout.on('data', (d) => { gateway.log += d; });
  child.stderr.on('data', (d) => { gateway.log += d; });
  for (let i = 0; i < 150; i++) {
    if (child.exitCode !== null) throw new Error(`gateway exited early:\n${gateway.log}`);
    try { if ((await fetch(`${url}/healthz`)).ok) return gateway; } catch { /* not up yet */ }
    await sleep(100);
  }
  throw new Error(`gateway did not start:\n${gateway.log}`);
}

/** Hard kill (no graceful shutdown), as a crash or power loss would look. */
export async function kill(processLike) {
  const child = processLike.child;
  if (child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGKILL');
  await Promise.race([once(child, 'exit'), sleep(5000)]);
}

/**
 * Fault-injecting TCP proxy in front of a gateway. `blackhole()` silently stops forwarding in both directions without
 * closing anything (a pulled cable / dead NAT mapping / half-open connection); `heal()` drops the dead connections and
 * forwards again.
 */
export async function startProxy(gateway, listenPort = 0) {
  const { connect } = await import('node:net');
  const target = Number(new URL(gateway.url).port);
  const pairs = new Set();
  let dark = false;
  let downstreamRate = 0;                                         // bytes per second towards the client; 0 = unlimited
  let upstreamRate = 0;                                           // bytes per second from the client (uploads); 0 = unlimited
  const server = createServer((client) => {
    const upstream = connect(target, '127.0.0.1');
    const pair = { client, upstream };
    pairs.add(pair);
    const forward = (from, to, rateOf) => from.on('data', (chunk) => {
      if (dark) return;
      const flushed = to.write(chunk);
      // Like a real network path, the proxy must not soak up data: when the receiving side is slow, stop reading
      // from the sender until it has drained (otherwise the proxy itself would hide missing backpressure).
      if (!flushed) { from.pause(); to.once('drain', () => from.resume()); }
      const rate = rateOf();
      if (rate > 0) {                                             // a slow link: hold the sender back in proportion to what was sent
        from.pause();
        setTimeout(() => { if (to.writableNeedDrain) to.once('drain', () => from.resume()); else from.resume(); }, Math.ceil((chunk.length / rate) * 1000));
      }
    });
    forward(client, upstream, () => upstreamRate); forward(upstream, client, () => downstreamRate);
    const close = () => { pairs.delete(pair); client.destroy(); upstream.destroy(); };
    for (const socket of [client, upstream]) { socket.on('error', close); socket.on('close', () => { if (!dark) close(); }); }
  });
  server.listen(listenPort, '127.0.0.1');
  await once(server, 'listening');
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    /** Limits download speed towards the client (bytes per second; 0 removes the limit). */
    throttle(bytesPerSecond) { downstreamRate = bytesPerSecond; },
    /** Limits upload speed from the client (bytes per second; 0 removes the limit). */
    throttleUploads(bytesPerSecond) { upstreamRate = bytesPerSecond; },
    /** Drops every open connection at once (a network drop); new connections work. */
    cut() { for (const pair of pairs) { pair.client.destroy(); pair.upstream.destroy(); } pairs.clear(); },
    blackhole() { dark = true; },
    heal() { for (const pair of pairs) { pair.client.destroy(); pair.upstream.destroy(); } pairs.clear(); dark = false; },
    stop() { this.heal(); server.close(); },
  };
}

async function stop(child) {
  if (child.exitCode !== null || child.signalCode !== null) return;
  child.kill();
  await Promise.race([once(child, 'exit'), sleep(5000)]);
}

export function agentCliRaw(stateDir, ...args) {
  return spawnSync('dotnet', [agentDll, ...args, '--state', stateDir], { encoding: 'utf8' });
}

/** Connects to the agent's same-user local pipe, like the owner console does. */
export async function connectIpc(pipeName) {
  const { connect } = await import('node:net');
  const socket = connect(process.platform === 'win32' ? `\\\\.\\pipe\\${pipeName}` : `/tmp/CoreFxPipe_${pipeName}`);
  await once(socket, 'connect');
  const pending = new Map(), events = [];
  let buffer = '', next = 0, closed = false;
  socket.on('data', (chunk) => {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const message = JSON.parse(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
      if (message.event) events.push(message);
      else pending.get(message.id)?.(message);
    }
  });
  socket.on('error', () => {});
  socket.on('close', () => { closed = true; for (const resolve of pending.values()) resolve({ ok: false, error: 'closed' }); });
  return {
    events, socket,
    get closed() { return closed; },
    raw: (text) => new Promise((resolve) => { pending.set(null, resolve); pending.set(undefined, resolve); socket.write(text); }),
    send: (cmd, args) => new Promise((resolve) => { const id = String(++next); pending.set(id, resolve); socket.write(JSON.stringify({ id, cmd, args }) + '\n'); }),
    close: () => socket.destroy(),
  };
}

export function agentCli(stateDir, ...args) {
  const result = spawnSync('dotnet', [agentDll, ...args, '--state', stateDir], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`agent ${args.join(' ')} failed: ${result.stderr}`);
  return result.stdout;
}

/** `refused: true` returns the agent even if the service never accepts it (to test refusals); `agent.identity` is then undefined. */
export async function startAgent(gateway, stateDir, name = 'Test PC', webOrigin = WEB_ORIGIN, { refused = false } = {}) {
  const connection = gateway ? ['--gateway', gateway.url, '--web-origin', webOrigin] : [];
  const child = spawn('dotnet', [agentDll, 'run', ...connection, '--state', stateDir, '--name', name], { stdio: ['pipe', 'pipe', 'pipe'] });
  const events = [];
  const waiters = [];
  let buffer = '';
  const agent = {
    child, pid: child.pid, events, log: '',
    send: (line) => child.stdin.write(line + '\n'),
    stop: () => stop(child),
    drain: () => events.forEach((e) => { e.consumed = true; }),
    // Resolves with the next event of that name that has not been consumed yet.
    next(name, timeoutMs = 20000) {
      const index = events.findIndex((e) => e.event === name && !e.consumed);
      if (index >= 0) { events[index].consumed = true; return Promise.resolve(events[index].data); }
      return new Promise((resolveNext, reject) => {
        const timer = setTimeout(() => reject(new Error(`timed out waiting for agent event '${name}'\n${agent.log}`)), timeoutMs);
        waiters.push({ name, resolve: (e) => { clearTimeout(timer); e.consumed = true; resolveNext(e.data); } });
      });
    },
  };
  child.stdout.on('data', (chunk) => {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      agent.log += line + '\n';
      if (!line.startsWith('@@ ')) continue;
      const event = JSON.parse(line.slice(3));
      events.push(event);
      const waiter = waiters.findIndex((w) => w.name === event.event);
      if (waiter >= 0) waiters.splice(waiter, 1)[0].resolve(event);
    }
  });
  child.stderr.on('data', (d) => { agent.log += d; });
  agent.ready = await agent.next('ready');
  if (gateway) agent.identity = refused ? await agent.next('connected', 6000).catch(() => undefined) : await agent.next('connected');
  return agent;
}

export function tempDir(prefix) { return mkdtempSync(join(tmpdir(), `orbit-${prefix}-`)); }

/** Deterministic pseudo-random file written in 1 MiB blocks; returns its SHA-256. Never held in memory whole. */
export async function writeLargeFile(path, mib) {
  const hash = createHash('sha256');
  const out = createWriteStream(path);
  let block = createHash('sha256').update('orbitdrive-fixture').digest();
  for (let i = 0; i < mib; i++) {
    const chunk = Buffer.alloc(1 << 20);
    for (let offset = 0; offset < chunk.length; offset += 32) { block = createHash('sha256').update(block).digest(); block.copy(chunk, offset); }
    hash.update(chunk);
    if (!out.write(chunk)) await once(out, 'drain');
  }
  out.end();
  await once(out, 'finish');
  return hash.digest('hex');
}

/** A signed-in (dev identity) browser client; `device` is set once pairing is approved. */
export class Browser {
  constructor(gateway, email) { this.gateway = gateway; this.email = email; this.device = null; }
  async signIn() {
    const response = await fetch(`${this.gateway.url}/v1/dev/sign-in`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: this.email }) });
    if (!response.ok) throw new Error(`sign-in failed ${response.status}`);
    ({ idToken: this.token, uid: this.uid } = await response.json());
    return this;
  }
  headers(extra = {}) {
    const headers = { ...extra };
    if (this.token) headers.authorization = `Bearer ${this.token}`;
    if (this.device) headers['x-orbitdrive-device'] = `${this.device.id}.${this.device.secret}`;
    return headers;
  }
  async call(method, path, body) {
    const response = await fetch(this.gateway.url + path, {
      method, headers: this.headers(body === undefined ? {} : { 'content-type': 'application/json' }), body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    return { status: response.status, body: text ? JSON.parse(text) : null, headers: response.headers };
  }
  get(path) { return this.call('GET', path); }
  post(path, body) { return this.call('POST', path, body); }
}

/** Runs the whole real pairing flow: PC issues a code, browser claims it, the PC owner approves locally. */
export async function pair(agent, browser, browserName = 'Test phone') {
  agent.send('pair');
  const pairing = await agent.next('pairing');
  const fragment = new URLSearchParams(new URL(pairing.url).hash.slice(1));
  const claim = await browser.post(`/v1/pairing-requests/${fragment.get('id')}/claim`, { secret: fragment.get('s'), browserName });
  if (claim.status !== 200) throw new Error(`claim failed: ${claim.status} ${JSON.stringify(claim.body)}`);
  const shown = await agent.next('pairing_claimed');
  if (shown.confirmationCode !== claim.body.confirmationCode) throw new Error('confirmation codes differ');
  agent.send('approve');
  const result = await agent.next('pairing_result');
  if (result.state !== 'approved') throw new Error(`pairing not approved: ${result.state}`);
  // A browser that already has a device is paired with this PC as the same device (the secret is not issued again).
  if (claim.body.deviceSecret) browser.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
  else assert.equal(claim.body.browserDeviceId, browser.device.id);
  return { pairing, claim: claim.body, pairingId: fragment.get('id') };
}

export function makeFixtureRoot() {
  const root = tempDir('root');
  writeFileSync(join(root, 'hello.txt'), 'Hello from OrbitDrive\n');
  writeFileSync(join(root, 'empty.bin'), '');
  writeFileSync(join(root, 'اردو فائل.txt'), 'یہ ایک آزمائشی فائل ہے\n');
  writeFileSync(join(root, 'emoji 😀 notes.txt'), 'emoji\n');
  writeFileSync(join(root, 'data.bin'), randomBytes(300_000));
  mkdirSync(join(root, 'Documents', 'Nested', 'Deeper'), { recursive: true });
  writeFileSync(join(root, 'Documents', 'report.txt'), 'quarterly report\n');
  writeFileSync(join(root, 'Documents', 'Nested', 'Deeper', 'deep.txt'), 'deep\n');
  mkdirSync(join(root, 'Many'));
  for (let i = 0; i < 450; i++) writeFileSync(join(root, 'Many', `item-${String(i).padStart(4, '0')}.txt`), String(i));
  return root;
}

export const waitFor = async (check, ms, what) => {
  const start = Date.now();
  for (;;) { const value = await check(); if (value) return value; if (Date.now() - start > ms) assert.fail(`${what} did not happen in ${ms} ms`); await sleep(100); }
};

/**
 * Points the agent at the image decoder helper: the real Skia helper when it has been built (always in CI), otherwise the
 * stand-in script (Linux only, for machines without NuGet access). Returns true when the real decoder is in use.
 */
export function useThumbnailHelper() {
  const config = process.env.ORBIT_CONFIG || 'Release';
  const real = join(repoRoot, `apps/desktop/OrbitDrive.ThumbnailHelper/bin/${config}/net10.0`, process.platform === 'win32' ? 'OrbitDrive.ThumbnailHelper.exe' : 'OrbitDrive.ThumbnailHelper');
  const isReal = existsSync(real);
  if (!isReal && process.env.CI) throw new Error('CI must exercise the real image decoder; the helper was not built');
  process.env.ORBITDRIVE_THUMBNAIL_HELPER = isReal ? real : join(repoRoot, 'tests/integration/fake-thumbnail-helper.sh');
  return isReal;
}

// ----- An independent ZIP reader (not the product's code, not .NET's): checks structure, ZIP64 fields, CRCs and names. -----
const crcTable = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
export function crc32(buffer) { let c = 0xffffffff; for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }

/** Reads a whole ZIP held in memory. Returns entries { name, directory, size, data() } and throws on any inconsistency. */
export function readZip(buffer) {
  let eocd = -1;
  for (let i = buffer.length - 22; i >= Math.max(0, buffer.length - 22 - 65535); i--) if (buffer.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error('no end-of-central-directory record');
  let count = buffer.readUInt16LE(eocd + 10), cdSize = buffer.readUInt32LE(eocd + 12), cdOffset = buffer.readUInt32LE(eocd + 16);
  if (count === 0xffff || cdOffset === 0xffffffff || cdSize === 0xffffffff) {
    const locator = eocd - 20;
    if (buffer.readUInt32LE(locator) !== 0x07064b50) throw new Error('ZIP64 locator missing');
    const z = Number(buffer.readBigUInt64LE(locator + 8));
    if (buffer.readUInt32LE(z) !== 0x06064b50) throw new Error('ZIP64 end record missing');
    count = Number(buffer.readBigUInt64LE(z + 32)); cdSize = Number(buffer.readBigUInt64LE(z + 40)); cdOffset = Number(buffer.readBigUInt64LE(z + 48));
  }
  const entries = [];
  let p = cdOffset;
  for (let i = 0; i < count; i++) {
    if (buffer.readUInt32LE(p) !== 0x02014b50) throw new Error('bad central header');
    const flags = buffer.readUInt16LE(p + 8), method = buffer.readUInt16LE(p + 10), crc = buffer.readUInt32LE(p + 16);
    let compressed = buffer.readUInt32LE(p + 20), size = buffer.readUInt32LE(p + 24), offset = buffer.readUInt32LE(p + 42);
    const nameLength = buffer.readUInt16LE(p + 28), extraLength = buffer.readUInt16LE(p + 30), commentLength = buffer.readUInt16LE(p + 32);
    const name = buffer.subarray(p + 46, p + 46 + nameLength).toString('utf8');
    if (!(flags & 0x0800)) throw new Error('names are not flagged UTF-8');
    for (let e = p + 46 + nameLength, end = e + extraLength; e < end;) {
      const id = buffer.readUInt16LE(e), length = buffer.readUInt16LE(e + 2);
      if (id === 1) { let q = e + 4; if (size === 0xffffffff) { size = Number(buffer.readBigUInt64LE(q)); q += 8; } if (compressed === 0xffffffff) { compressed = Number(buffer.readBigUInt64LE(q)); q += 8; } if (offset === 0xffffffff) offset = Number(buffer.readBigUInt64LE(q)); }
      e += 4 + length;
    }
    entries.push({
      name, directory: name.endsWith('/'), size, compressed, method, crc, offset,
      data() {
        if (buffer.readUInt32LE(offset) !== 0x04034b50) throw new Error('bad local header for ' + name);
        const start = offset + 30 + buffer.readUInt16LE(offset + 26) + buffer.readUInt16LE(offset + 28);
        const raw = buffer.subarray(start, start + compressed);
        const out = method === 0 ? raw : method === 8 ? inflateRawSync(raw) : (() => { throw new Error('method ' + method); })();
        if (out.length !== size) throw new Error(`size mismatch for ${name}`);
        if (crc32(out) !== crc) throw new Error(`CRC mismatch for ${name}`);
        return out;
      },
    });
    p += 46 + nameLength + extraLength + commentLength;
  }
  const names = entries.map((e) => e.name);
  if (new Set(names).size !== names.length) throw new Error('duplicate names');
  for (const n of names) if (n.startsWith('/') || n.includes('..') || n.includes('\\') || n.includes('//')) throw new Error('unsafe name ' + n);
  return entries;
}
