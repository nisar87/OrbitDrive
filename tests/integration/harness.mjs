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

export const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const config = process.env.ORBIT_CONFIG || 'Release';
const gatewayDll = join(repoRoot, `services/gateway/OrbitDrive.Gateway/bin/${config}/net10.0/OrbitDrive.Gateway.dll`);
const agentDll = join(repoRoot, `apps/desktop/OrbitDrive.Agent/bin/${config}/net10.0/orbitdrive-agent.dll`);
export const WEB_ORIGIN = 'http://localhost:5173';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

async function freePort() {
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

async function stop(child) {
  if (child.exitCode !== null) return;
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

export async function startAgent(gateway, stateDir, name = 'Test PC') {
  const connection = gateway ? ['--gateway', gateway.url, '--web-origin', WEB_ORIGIN] : [];
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
  if (gateway) agent.identity = await agent.next('connected');
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
  browser.device = { id: claim.body.browserDeviceId, secret: claim.body.deviceSecret };
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
