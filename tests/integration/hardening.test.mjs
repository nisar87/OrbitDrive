// W11/D11: adversarial checks against the real gateway and agent. Every route is read from the source so a new endpoint cannot
// silently skip authentication; headers, CORS, malformed and hostile input, and what the service log may contain are all checked.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { join } from 'node:path';
import { startGateway, startAgent, tempDir, makeFixtureRoot, agentCli, Browser, pair, repoRoot, WEB_ORIGIN } from './harness.mjs';

let gateway, agent, root, state, pcId, owner;

before(async () => {
  root = makeFixtureRoot(); state = tempDir('hardening-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Hardening PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, owner, 'Owner phone');
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

const raw = (path, init = {}) => fetch(gateway.url + path, { redirect: 'manual', ...init });

// ---------------------------------------------------------------- routes, read from the source
const source = readFileSync(join(repoRoot, 'services/gateway/OrbitDrive.Gateway/Program.cs'), 'utf8');
const routes = [
  ...[...source.matchAll(/app\.Map(Get|Post|Put|Delete|Patch)\("([^"]+)"/g)].map((m) => ({ method: m[1].toUpperCase(), path: m[2] })),
  ...[...source.matchAll(/app\.MapMethods\("([^"]+)", \["GET", "HEAD"\]/g)].map((m) => ({ method: 'GET', path: m[1] })),
];
/** Endpoints that are reachable without a user sign-in on purpose, and what protects each instead. */
const ANONYMOUS = new Map([
  ['/healthz', 'liveness probe, reveals nothing'],
  ['/v1/meta', 'public service facts (version, limits)'],
  ['/v1/dev/sign-in', 'development sign-in; absent in production (checked below)'],
  ['/v1/downloads/{capability}', 'unguessable short-lived capability link'],
  ['/v1/agent-downlinks/{streamId}', 'PC-side stream, needs the PC credential'],
  ['/v1/agent-streams/{streamId}', 'PC-side stream, needs the PC credential'],
  ['/v1/agent-control', 'PC control channel, needs the PC credential'],
]);
const fill = (path) => path.replace(/\{[^}]+\}/g, 'x1');

describe('every endpoint', () => {
  test('the route list was read and is not trivially small (so the sweep below really covers the service)', () => {
    assert.ok(routes.length >= 40, `only ${routes.length} routes found`);
    assert.ok(routes.some((r) => r.path.startsWith('/v1/admin/')) && routes.some((r) => r.path.startsWith('/v1/account')));
  });

  test('requires sign-in unless it is on the short, reviewed anonymous list: no credentials and garbage credentials both get 401', async () => {
    const garbage = ['Bearer', 'Bearer ', 'Bearer not-a-token', 'Bearer ' + 'A'.repeat(5000), 'Basic YWxpY2U6c2VjcmV0', 'bearer ' + owner.token.split('').reverse().join(''), owner.token];
    for (const { method, path } of routes) {
      if (ANONYMOUS.has(path)) continue;
      const url = fill(path);
      const body = method === 'GET' || method === 'DELETE' ? undefined : '{}';
      const headers = body ? { 'content-type': 'application/json' } : {};
      const none = await raw(url, { method, headers, body });
      assert.equal(none.status, 401, `${method} ${path} without credentials`);
      for (const value of garbage.slice(0, -1)) {
        const response = await raw(url, { method, headers: { ...headers, authorization: value }, body });
        assert.equal(response.status, 401, `${method} ${path} with ${value.slice(0, 20)}…`);
      }
    }
  });

  test('a valid token is not enough where more is needed: PC data needs the paired browser device, admin needs an operator', async () => {
    const stranger = await new Browser(gateway, 'stranger@example.test').signIn();
    for (const [method, path, body] of [['GET', `/v1/pcs/${pcId}/roots`], ['POST', `/v1/pcs/${pcId}/directory-queries`, { rootId: 'x' }], ['POST', '/v1/download-sessions', { pcId, entryId: 'x' }], ['GET', '/v1/admin/overview']]) {
      const response = await stranger.call(method, path, body);
      assert.ok([401, 403, 404].includes(response.status), `${method} ${path} → ${response.status}`);
      assert.notEqual(response.status, 200);
    }
  });

  test('the anonymous endpoints that serve PC streams or capabilities refuse an unknown caller and never answer 2xx', async () => {
    for (const path of ['/v1/agent-downlinks/x1', '/v1/agent-streams/x1', '/v1/downloads/x1', '/v1/downloads/' + 'a'.repeat(200), '/v1/downloads/..%2f..%2fetc%2fpasswd', '/v1/downloads/%00']) {
      for (const method of ['GET', 'POST', 'HEAD']) {
        const response = await raw(path, { method });
        assert.ok(response.status >= 400 && response.status < 500, `${method} ${path} → ${response.status}`);
      }
    }
  });
});

// ---------------------------------------------------------------- the PC control channel
describe('the PC control channel (anonymous until it proves who it is)', () => {
  const wsUrl = (g) => g.url.replace('http', 'ws') + '/v1/agent-control';
  /** Opens a control connection (no Origin header, like a real agent) and records what happens to it. */
  const connect = (g = gateway) => new Promise((resolve) => {
    const socket = new WebSocket(wsUrl(g));
    const run = { socket, messages: [], opened: false, closedAt: null, started: Date.now() };
    socket.addEventListener('open', () => { run.opened = true; });
    socket.addEventListener('message', (e) => run.messages.push(String(e.data)));
    socket.addEventListener('close', () => { run.closedAt = Date.now(); });
    socket.addEventListener('error', () => { run.closedAt ??= Date.now(); });
    run.closed = new Promise((done) => { socket.addEventListener('close', done); socket.addEventListener('error', done); });
    resolve(run);
  });
  const pcCount = async () => (await owner.get('/v1/pcs')).body.pcs.length;

  test('it is challenged first, and one that never answers is dropped within the deadline', async () => {
    const before = await pcCount();
    const silent = await connect();
    await silent.closed;
    assert.ok(silent.opened && silent.messages.length === 1 && JSON.parse(silent.messages[0]).action === 'challenge');
    assert.ok(silent.closedAt - silent.started < 14_000, `kept open ${silent.closedAt - silent.started} ms`);
    assert.equal(await pcCount(), before);
  });

  test('garbage, the wrong kind of message, an unsigned or forged hello and an oversized first message all end the connection and register nothing', async () => {
    const before = await pcCount();
    const hello = (payload) => JSON.stringify({ v: 1, id: '1', kind: 'req', action: 'hello', payload });
    for (const first of ['not json', '[]', '{}', JSON.stringify({ v: 1, id: '1', kind: 'req', action: 'list_roots', payload: {} }), hello({}), hello({ pcId: pcId, publicKey: 'AAAA', signature: 'AAAA', protocolVersion: 1 }), hello({ pcId: 'x'.repeat(100000) }), 'x'.repeat(300_000), '\u0000']) {
      const run = await connect();
      await new Promise((r) => run.socket.addEventListener('open', r, { once: true }));
      run.socket.send(first);
      await Promise.race([run.closed, new Promise((r) => setTimeout(r, 5000))]);
      assert.ok(run.closedAt !== null, `a connection stayed open after ${first.slice(0, 40)}`);
      assert.ok(!run.messages.slice(1).some((m) => /"action":"hello"[^}]*"ok":true/.test(m)), 'must not be welcomed');
    }
    assert.equal(await pcCount(), before, 'nothing was registered');
    assert.ok(gateway.child.exitCode === null);
  });

  test('a request that carries an Origin header (a web page, not a PC) is refused before it is accepted', async () => {
    const status = await new Promise((resolve) => {
      const request = httpRequest(gateway.url + '/v1/agent-control', { headers: { Upgrade: 'websocket', Connection: 'Upgrade', Origin: 'https://evil.example', 'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==', 'Sec-WebSocket-Version': '13' } }, (response) => { response.resume(); resolve(response.statusCode); });
      request.on('upgrade', () => resolve(101)); request.on('error', () => resolve(0)); request.end();
    });
    assert.ok(status >= 400 || status === 0, `answered ${status}`);
  });

  test('only a limited number may wait to identify themselves at once; the rest are refused, and the door reopens when they time out', async () => {
    const small = await startGateway({ OrbitDrive__MaxPendingHandshakes: '4' });
    try {
      const attempts = await Promise.all(Array.from({ length: 10 }, () => connect(small)));
      await new Promise((r) => setTimeout(r, 1500));
      const accepted = attempts.filter((a) => a.opened).length;
      assert.ok(accepted >= 1 && accepted <= 4, `${accepted} accepted with a limit of 4`);
      assert.ok(attempts.filter((a) => !a.opened).length >= 6, 'the extra attempts were refused');
      assert.equal((await fetch(`${small.url}/healthz`)).status, 200, 'the service is still answering');
      await Promise.all(attempts.map((a) => Promise.race([a.closed, new Promise((r) => setTimeout(r, 13_000))])));
      const later = await connect(small);
      await new Promise((r) => setTimeout(r, 1000));
      assert.ok(later.opened, 'a new connection is accepted again once the waiting ones are gone');
      later.socket.close();
    } finally { await small.stop(); }
  });
});

// ---------------------------------------------------------------- headers
describe('response headers', () => {
  const samples = [['GET', '/healthz'], ['GET', '/v1/meta'], ['GET', '/v1/pcs'], ['GET', '/v1/does-not-exist'], ['POST', '/v1/pcs/x1/directory-queries'], ['GET', '/v1/downloads/x1']];
  test('success, rejection and not-found answers all carry the full set', async () => {
    for (const [method, path] of samples) {
      const h = (await raw(path, { method, headers: { 'content-type': 'application/json' }, body: method === 'POST' ? '{' : undefined })).headers;
      assert.equal(h.get('cache-control'), 'no-store', `${path} cache-control`);
      assert.equal(h.get('x-content-type-options'), 'nosniff', `${path} nosniff`);
      assert.equal(h.get('referrer-policy'), 'no-referrer', `${path} referrer`);
      assert.equal(h.get('x-frame-options'), 'DENY', `${path} frame`);
      assert.equal(h.get('cross-origin-resource-policy'), 'same-origin', `${path} corp`);
      assert.match(h.get('content-security-policy') ?? '', /default-src 'none'.*frame-ancestors 'none'.*sandbox/, `${path} csp`);
      assert.match(h.get('permissions-policy') ?? '', /camera=\(\)/, `${path} permissions`);
      assert.equal(h.get('server'), null, `${path} must not announce the server software`);
    }
  });

  test('an inline preview is the one place that is shown by the app: still sandboxed, and only that relaxes resource policy', async () => {
    const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
    const page = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
    const file = page.body.entries.find((e) => e.name === 'hello.txt');
    const session = await owner.post('/v1/download-sessions', { pcId, entryId: file.entryId, disposition: 'inline' });
    if (session.status !== 201) return;                                  // inline sessions are covered by previews.test.mjs; this only checks headers when available
    const response = await fetch(gateway.url + session.body.urlPath);
    await response.arrayBuffer();
    assert.match(response.headers.get('content-security-policy') ?? '', /sandbox/);
    assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  });
});

// ---------------------------------------------------------------- CORS
describe('cross-origin access', () => {
  test('only the configured web origin is answered, with that exact origin, never * and never credentials', async () => {
    for (const method of ['GET', 'POST', 'DELETE', 'PUT']) {
      const preflight = await raw('/v1/pcs', { method: 'OPTIONS', headers: { origin: WEB_ORIGIN, 'access-control-request-method': method, 'access-control-request-headers': 'authorization,content-type' } });
      assert.equal(preflight.headers.get('access-control-allow-origin'), WEB_ORIGIN);
      assert.notEqual(preflight.headers.get('access-control-allow-origin'), '*');
      assert.equal(preflight.headers.get('access-control-allow-credentials'), null, 'no cookies or ambient credentials are ever allowed');
      assert.match(preflight.headers.get('vary') ?? '', /origin/i);
    }
    const real = await raw('/v1/pcs', { headers: { origin: WEB_ORIGIN, authorization: `Bearer ${owner.token}` } });
    assert.equal(real.headers.get('access-control-allow-origin'), WEB_ORIGIN);
  });

  test('look-alike, null, scheme-changed and other origins get no permission at all', async () => {
    const evil = ['https://evil.example', 'null', 'http://localhost:5173.evil.example', 'http://localhost:5173@evil.example', 'http://localhost', 'http://localhost:5174', 'https://localhost:5173', 'http://LOCALHOST:5173.', 'file://', 'http://127.0.0.1:5173', '*'];
    for (const origin of evil) {
      const preflight = await raw('/v1/pcs', { method: 'OPTIONS', headers: { origin, 'access-control-request-method': 'GET', 'access-control-request-headers': 'authorization' } });
      assert.equal(preflight.headers.get('access-control-allow-origin'), null, `preflight for ${origin}`);
      const real = await raw('/v1/pcs', { headers: { origin, authorization: `Bearer ${owner.token}` } });
      assert.equal(real.headers.get('access-control-allow-origin'), null, `response for ${origin}`);
    }
  });

  test('headers and methods outside the allowed list are not granted', async () => {
    const preflight = await raw('/v1/pcs', { method: 'OPTIONS', headers: { origin: WEB_ORIGIN, 'access-control-request-method': 'GET', 'access-control-request-headers': 'x-evil-header,cookie' } });
    const allowed = (preflight.headers.get('access-control-allow-headers') ?? '').toLowerCase();
    assert.ok(!allowed.includes('x-evil-header') && !allowed.includes('cookie'), allowed);
    const methods = await raw('/v1/pcs', { method: 'OPTIONS', headers: { origin: WEB_ORIGIN, 'access-control-request-method': 'TRACE' } });
    assert.ok(!(methods.headers.get('access-control-allow-methods') ?? '').includes('TRACE'));
  });
});

// ---------------------------------------------------------------- malformed and hostile input
describe('hostile input', () => {
  const leaks = /\bat [A-Z][\w.]+\(|System\.|Microsoft\.|Exception|\\Users\\|\/home\/|\/tmp\/|stack ?trace|Program\.cs|\.cs:line/i;
  const typed = async (response, where) => {
    const text = await response.text();
    assert.doesNotMatch(text, leaks, `${where} leaked internals: ${text.slice(0, 200)}`);
    return text;
  };

  test('invalid, empty, truncated, wrongly-typed and absurdly nested JSON is a clean 4xx with a typed error, never a 5xx or a stack trace', async () => {
    const bodies = ['', '{', '{"rootId":', 'null', '[]', '"text"', '123', '{"rootId":{"a":1}}', '{"pageSize":"many"}', '{"pageSize":-1}', '{"pageSize":99999999999999999999}', '\u0000\u0000', '{"rootId":"\u0000"}', '[' .repeat(20000), '{"a":'.repeat(5000), 'x'.repeat(1000)];
    for (const body of bodies) {
      for (const path of [`/v1/pcs/${pcId}/directory-queries`, `/v1/pcs/${pcId}/searches`, '/v1/download-sessions', '/v1/account/preferences', `/v1/pcs/${pcId}/uploads`]) {
        const method = path.endsWith('preferences') ? 'PUT' : 'POST';
        const response = await raw(path, { method, headers: owner.headers({ 'content-type': 'application/json', 'x-orbitdrive-device': `${owner.device.id}.${owner.device.secret}` }), body });
        assert.ok(response.status >= 400 && response.status < 500, `${path} ← ${JSON.stringify(body).slice(0, 30)} → ${response.status}`);
        await typed(response, path);
      }
    }
  });

  test('a body over the JSON limit is refused before it is read in full', async () => {
    const big = JSON.stringify({ rootId: 'x', filler: 'a'.repeat(200 * 1024) });
    const response = await raw(`/v1/pcs/${pcId}/directory-queries`, { method: 'POST', headers: owner.headers({ 'content-type': 'application/json' }), body: big });
    assert.ok([400, 413].includes(response.status), `status ${response.status}`);
    await typed(response, 'oversized');
  });

  test('odd paths, ids, methods and very long inputs never cause a server error', async () => {
    const ids = ['%00', '%2e%2e', '..%2f..%2f', '%c0%af', '‮', 'a'.repeat(4000), '%', '%zz', '😀', 'x;DROP TABLE', "x'--", '${jndi:ldap://x}', '{{7*7}}'];
    for (const id of ids) {
      for (const path of [`/v1/pcs/${id}/roots`, `/v1/browser-devices/${id}`, `/v1/invitations/${id}/accept`, `/v1/download-sessions/${id}`]) {
        for (const method of ['GET', 'POST', 'DELETE']) {
          let response;
          try { response = await raw(path, { method, headers: owner.headers() }); } catch { continue; }       // the client itself refusing a malformed URL is fine
          assert.ok(response.status < 500, `${method} ${path.slice(0, 60)} → ${response.status}`);
          await typed(response, path.slice(0, 60));
        }
      }
    }
    for (const method of ['TRACE', 'CONNECT', 'PROPFIND', 'PATCH']) {
      let response; try { response = await raw('/v1/pcs', { method, headers: owner.headers() }); } catch { continue; }
      assert.ok(response.status >= 400 && response.status < 500, `${method} → ${response.status}`);
      assert.notEqual(response.headers.get('content-type')?.includes('message/http'), true, 'TRACE must not echo the request');
    }
  });

  test('absurd headers are refused cleanly and the service keeps answering', async () => {
    for (const headers of [{ authorization: 'Bearer ' + 'x'.repeat(20000) }, { 'x-orbitdrive-device': 'a.'.repeat(5000) }, { range: 'bytes=' + '0-1,'.repeat(5000) }, { 'content-length': '-1' }]) {
      try { const response = await raw('/v1/pcs', { headers }); assert.ok(response.status >= 400 && response.status < 500, `status ${response.status}`); } catch { /* connection refused by the server for an impossible header is acceptable */ }
    }
    assert.equal((await raw('/healthz')).status, 200);
    assert.equal(gateway.child.exitCode, null, 'the gateway is still running');
  });

  test('the development sign-in exists only where it was explicitly enabled', async () => {
    const production = await startGateway({ ASPNETCORE_ENVIRONMENT: 'Production', OrbitDrive__DevIdentity__Enabled: 'false', OrbitDrive__DevIdentity__Secret: '', OrbitDrive__Firebase__ProjectId: 'demo-orbitdrive', OrbitDrive__Firebase__AcceptEmulatorTokens: 'true', OrbitDrive__Firebase__KeysUrl: 'http://127.0.0.1:1/none' }).catch((e) => e);
    try {
      if (production instanceof Error) return;                          // refusing to start with an unsafe configuration is also correct
      const response = await fetch(`${production.url}/v1/dev/sign-in`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: 'a@example.test' }) });
      assert.ok(response.status === 404 || response.status === 401 || response.status === 403, `status ${response.status}`);
    } finally { if (!(production instanceof Error)) await production.stop(); }
  });
});

// ---------------------------------------------------------------- what the service log may contain
describe('the service log', () => {
  test('contains none of the secrets, capability links, pairing material or file names that passed through it', async () => {
    const roots = await owner.get(`/v1/pcs/${pcId}/roots`);
    const page = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
    const file = page.body.entries.find((e) => e.name === 'hello.txt');
    const session = await owner.post('/v1/download-sessions', { pcId, entryId: file.entryId });
    assert.equal(session.status, 201);
    const bytes = Buffer.from(await (await fetch(gateway.url + session.body.urlPath)).arrayBuffer());
    assert.equal(bytes.toString(), 'Hello from OrbitDrive\n');
    await raw('/v1/pcs', { headers: { authorization: 'Bearer secret-token-value-123' } });
    const capability = session.body.urlPath.split('/').pop();
    const log = gateway.log;
    const mustNotAppear = { 'the sign-in token': owner.token, 'the browser device secret': owner.device.secret, 'a rejected token value': 'secret-token-value-123', 'the download capability': capability, 'a file name': 'hello.txt', 'a private file name': 'اردو', 'the folder path': root, 'file contents': 'Hello from OrbitDrive' };
    for (const [what, value] of Object.entries(mustNotAppear)) assert.ok(!log.includes(value), `the log contains ${what}`);
    assert.ok(!/authorization:|bearer /i.test(log), 'authorization headers are not logged');
  });
});
