// R47/D11: seeded, repeatable fuzzing of every request-accepting route of the real gateway (real agent behind it). A reproducible corpus of valid-looking bodies is
// mutated (missing/extra/wrong-typed fields, huge and hostile strings, truncated or duplicate-key JSON, invalid UTF-8, wrong content types, junk path ids, junk
// headers) and sent as the signed-in owner, as an anonymous caller and with forged credentials. The invariants: NEVER a 5xx, never a hang, errors are the typed
// JSON error with no stack trace/file path/secret in it, anonymous and forged callers never get through, and the service is still healthy and correct afterwards.
//   ORBIT_FUZZ_SEED=<n> to replay a failure, ORBIT_FUZZ_ROUNDS=<n> for more (default 6 per route and caller).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, tempDir, makeFixtureRoot, agentCli, Browser, pair, repoRoot } from './harness.mjs';

const SEED = Number(process.env.ORBIT_FUZZ_SEED ?? 20261119), ROUNDS = Number(process.env.ORBIT_FUZZ_ROUNDS ?? 6);
let gateway, agent, owner, other, pcId, rootId, entryId;
const rng = (() => { let a = SEED >>> 0; return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; })();
const pick = (list) => list[Math.floor(rng() * list.length)];
const int = (n) => Math.floor(rng() * n);

before(async () => {
  const root = makeFixtureRoot(), state = tempDir('fuzz-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Fuzz PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'fuzz-owner@example.test').signIn();
  other = await new Browser(gateway, 'fuzz-other@example.test').signIn();
  await pair(agent, owner, 'Fuzz phone');
  const roots = (await owner.get(`/v1/pcs/${pcId}/roots`)).body.roots; rootId = roots[0].rootId;
  entryId = (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, pageSize: 50 })).body.entries.find((e) => e.name === 'hello.txt').entryId;
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

// ---------------------------------------------------------------- what is fuzzed
const source = readFileSync(join(repoRoot, 'services/gateway/OrbitDrive.Gateway/Program.cs'), 'utf8');
const NOT_FUZZED = new Set(['/healthz', '/readyz', '/v1/dev/sign-in', '/v1/downloads/{capability}', '/v1/agent-control', '/v1/agent-downlinks/{streamId}', '/v1/agent-streams/{streamId}']);
const PUBLIC = new Set(['/v1/meta']);   // anonymous on purpose (public service facts); everything else must refuse anonymous callers
const routes = [...source.matchAll(/app\.Map(Get|Post|Put|Delete|Patch)\("([^"]+)"/g)].map((m) => ({ method: m[1].toUpperCase(), path: m[2] })).filter((r) => !NOT_FUZZED.has(r.path));
/** Valid-looking bodies for the routes that read one; routes not listed get `{}` mutated. */
const templates = () => ({
  '/v1/download-sessions': { pcId, entryId },
  '/v1/pairing-requests/{id}/claim': { secret: 'x'.repeat(43), browserName: 'Phone' },
  '/v1/pcs/{pcId}/archives': { entryIds: [entryId], strict: false },
  '/v1/pcs/{pcId}/archive-status': { archiveId: 'a1' }, '/v1/pcs/{pcId}/archive-cancel': { archiveId: 'a1' },
  '/v1/pcs/{pcId}/directory-queries': { rootId, pageSize: 10 },
  '/v1/pcs/{pcId}/entry-checks': { entryIds: [entryId] },
  '/v1/pcs/{pcId}/searches': { searchId: 's1', query: 'hello', rootId, limit: 10 }, '/v1/pcs/{pcId}/search-cancel': { searchId: 's1' },
  '/v1/pcs/{pcId}/uploads': { rootId, folderEntryId: null, name: 'a.txt', size: '5', collision: 'rename' },
  '/v1/pcs/{pcId}/upload-status': { uploadId: 'u1' }, '/v1/pcs/{pcId}/upload-cancel': { uploadId: 'u1' }, '/v1/pcs/{pcId}/upload-finish': { uploadId: 'u1' },
  '/v1/account/preferences': { theme: 'dark' }, '/v1/admin/settings': { minimumPcVersion: '0.1.0' },
  '/v1/browser-devices/{deviceId}': { name: 'Renamed' },
});
const NASTY = ['', ' ', '\0', '\u0000\u0001', '../../../etc/passwd', '..\\..\\windows', 'C:\\Windows\\win.ini', '\\\\server\\share', '%00', '%2e%2e%2f', "'; DROP TABLE x;--", '<script>alert(1)</script>', '${jndi:ldap://x}', '{{7*7}}',
  '\u202e\u0645\u0631\u062d\u0628\u0627', '🙂'.repeat(50), 'A'.repeat(300), 'A'.repeat(70_000), '\ud800', '\uffff', 'NUL', 'con.txt', '__proto__', 'constructor', 'toString', '\n\r\n', '%', '%zz', '*', '?', '|', 'null', 'undefined', 'NaN', '-1', '9'.repeat(40)];
const WEIRD_VALUES = () => [null, true, false, 0, -1, 1.5, 2 ** 53, -(2 ** 63), 1e308, '', [], {}, [[]], { a: { b: { c: {} } } }, [null], ['x'.repeat(1000)], pick(NASTY), pick(NASTY), '9223372036854775808', Array.from({ length: 300 }, (_, i) => i)];
function mutate(value, depth = 0) {
  const roll = rng();
  if (value === null || typeof value !== 'object') return roll < 0.5 ? pick(WEIRD_VALUES()) : value;
  const copy = Array.isArray(value) ? Array.prototype.slice.call(value) : { ...value };
  const keys = Object.keys(copy);
  for (let n = 1 + int(3); n > 0; n--) {
    const action = int(6), key = keys.length ? pick(keys) : 'x';
    if (action === 0) delete copy[key];
    else if (action === 1) copy[key] = pick(WEIRD_VALUES());
    else if (action === 2 && depth < 3 && copy[key] && typeof copy[key] === 'object') copy[key] = mutate(copy[key], depth + 1);
    else if (action === 3) copy[pick(NASTY)] = pick(WEIRD_VALUES());
    else if (action === 4 && typeof copy[key] === 'string') copy[key] = pick(NASTY);
    else copy['__proto__'] = { admin: true };
  }
  return copy;
}
/** The request body as raw bytes, sometimes deliberately not valid JSON. */
function bodyBytes(template) {
  const json = JSON.stringify(mutate(template ?? {}));
  switch (int(10)) {
    case 0: return Buffer.from(json.slice(0, Math.max(1, int(json.length))));                   // truncated
    case 1: return Buffer.from(json.replace('{', '{"dup":1,"dup":2,'));                           // duplicate keys
    case 2: return Buffer.concat([Buffer.from(json), Buffer.from([0xff, 0xfe, 0xc3])]);          // invalid UTF-8 tail
    case 3: return Buffer.from(Array.from({ length: int(2000) }, () => int(256)));               // random bytes
    case 4: return Buffer.from('[' .repeat(2000));                                                // deep nesting
    case 5: return Buffer.alloc(0);
    case 6: return Buffer.from('\ufeff' + json);                                                  // BOM
    default: return Buffer.from(json);
  }
}
const CONTENT_TYPES = ['application/json', 'application/json', 'application/json', 'text/plain', 'application/x-www-form-urlencoded', 'multipart/form-data; boundary=x', 'application/json; charset=utf-16', '', 'application/octet-stream'];
const PATH_JUNK = () => { try { return encodeURIComponent(pick(NASTY).slice(0, 200)) || 'x'; } catch { return 'x'; } };   // a lone surrogate cannot be URL-encoded by the client
/** Requests that would legitimately destroy the account or PC the later checks rely on; the owner only sends them with junk ids/malformed bodies, the disposable account sends them for real. */
const SELF_DESTRUCTIVE = new Set(['DELETE /v1/account', 'POST /v1/account/revoke-all', 'DELETE /v1/pcs/{pcId}']);
const fill = (path, keepReal) => path.replace(/\{[^}]+\}/g, (m) => (m === '{pcId}' && keepReal && rng() < 0.5 ? pcId : PATH_JUNK()));

const SECRETS = () => [owner.token, owner.device?.secret, gateway.dataDir].filter((s) => typeof s === 'string' && s.length > 12);
async function send(caller, route, intoBody) {
  const method = route.method, destructive = SELF_DESTRUCTIVE.has(`${method} ${route.path}`);
  if (destructive && caller === 'owner') return { status: 0, caller, route };   // exercised below by the disposable account
  const path = fill(route.path, caller === 'owner');
  const headers = {};
  if (caller === 'owner') Object.assign(headers, owner.headers());
  else if (caller === 'other') Object.assign(headers, other.headers());
  else if (caller === 'forged') { headers.authorization = pick(['Bearer ', 'Bearer x', 'Bearer ' + 'a'.repeat(3000), 'Basic Zm9vOmJhcg==', 'Bearer ' + owner.token.slice(0, -2) + 'zz', 'bearer\t' + owner.token.slice(0, 20)]); headers['x-orbitdrive-device'] = pick(['x', 'a.b', '.', '..', 'id.' + 'z'.repeat(60), '%00']); }
  if (rng() < 0.15) headers['x-orbitdrive-device'] = pick(NASTY.filter((n) => /^[\x21-\x7e]+$/.test(n)));
  if (rng() < 0.1) headers['idempotency-key'] = pick(['short', 'k'.repeat(200), 'good-key-' + int(1e9)]);
  let body;
  if (!['GET', 'DELETE'].includes(method) || rng() < 0.1) { body = bodyBytes(templates()[route.path]); const type = pick(CONTENT_TYPES); if (type) headers['content-type'] = type; }
  const controller = new AbortController(); const timer = setTimeout(() => controller.abort(), 8000);
  const started = Date.now();
  let response, text;
  try {
    response = await fetch(gateway.url + path, { method, headers, body: body && method !== 'GET' ? body : undefined, signal: controller.signal, redirect: 'manual' });
    text = await response.text();
  } catch (error) { assert.fail(`${caller} ${method} ${path}: no answer (${error.name}) after ${Date.now() - started} ms; replay with ORBIT_FUZZ_SEED=${SEED}`); }
  finally { clearTimeout(timer); }
  const context = `${caller} ${method} ${route.path} (${path.slice(0, 80)}) seed=${SEED}`;
  assert.ok(response.status < 500, `${context}: ${response.status} ${text.slice(0, 200)}`);
  if (response.status >= 400) {
    if (text) { let parsed; try { parsed = JSON.parse(text); } catch { assert.fail(`${context}: error body is not JSON: ${text.slice(0, 120)}`); } assert.ok(parsed.error?.code && typeof parsed.error.message === 'string', `${context}: not the typed error: ${text.slice(0, 160)}`); }
    else assert.ok([400, 401, 404, 405, 413, 415, 431].includes(response.status), `${context}: empty ${response.status}`);
  }
  assert.doesNotMatch(text, /\bat [A-Z][\w.]+\(.*\)|\.cs:line|System\.|Microsoft\.AspNetCore|Exception|[A-Z]:\\\\?(Users|home|Windows)|\/home\/|\/tmp\//, `${context}: internals leaked: ${text.slice(0, 200)}`);
  for (const secret of SECRETS()) assert.ok(!text.includes(secret), `${context}: a secret was echoed`);
  return { status: response.status, caller, route };
}

test('every route survives mutated bodies, junk ids and junk headers as the owner: never a 5xx, always the typed error', async () => {
  const seen = new Map();
  for (const route of routes) for (let i = 0; i < ROUNDS; i++) { const r = await send('owner', route); seen.set(r.status, (seen.get(r.status) ?? 0) + 1); }
  seen.delete(0);
  assert.ok(seen.size > 1, 'the corpus produced both accepted and refused requests');
  assert.ok([...seen.keys()].every((s) => s < 500));
});

test('the destructive account routes survive hostile input as a disposable account (the real owner is untouched)', async () => {
  const victim = await new Browser(gateway, 'fuzz-victim@example.test').signIn();
  for (const route of routes.filter((r) => SELF_DESTRUCTIVE.has(`${r.method} ${r.path}`))) for (let i = 0; i < ROUNDS; i++) {
    const response = await fetch(gateway.url + fill(route.path, false), { method: route.method, headers: { ...victim.headers(), 'content-type': 'application/json' }, body: route.method === 'DELETE' ? undefined : bodyBytes({}) });
    assert.ok(response.status < 500, `${route.method} ${route.path} -> ${response.status}`); await response.text();
  }
  assert.equal((await owner.get('/v1/pcs')).status, 200);
});

test('another account, anonymous and forged credentials never get through any route', async () => {
  for (const route of routes) for (const caller of ['anonymous', 'forged', 'other']) for (let i = 0; i < Math.max(2, ROUNDS >> 1); i++) {
    const r = await send(caller, route);
    if (caller === 'other' && /pcs\/\{pcId\}|download-sessions|admin/.test(route.path)) assert.ok(r.status >= 400 || route.method === 'GET', `other account was served ${route.method} ${route.path}: ${r.status}`);
    if (caller !== 'other' && !PUBLIC.has(route.path)) assert.ok(r.status >= 400, `${caller} got ${r.status} on ${route.method} ${route.path}`);
    if (caller !== 'other' && !PUBLIC.has(route.path)) assert.ok(r.status !== 200 && r.status !== 201 && r.status !== 204, `${caller} was accepted on ${route.method} ${route.path}`);
  }
});

test('hostile paths, methods and a flood of junk do not wedge the service', async () => {
  const paths = ['/', '//', '/v1', '/v1/', '/v1/%00', '/v1/%2e%2e/%2e%2e/etc/passwd', '/v1/pcs/' + 'a'.repeat(5000), '/v1/pcs/%ff%fe/roots', '/V1/PCS', '/v1/pcs/..%2f..%2froots', '/v1/download-sessions/%00', '/%', '/v1/?' + 'q=1&'.repeat(2000)];
  for (const p of paths) for (const m of ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS', 'TRACE', 'HEAD']) {
    let response; try { response = await fetch(gateway.url + p, { method: m, headers: owner.headers(), redirect: 'manual' }); await response.text(); } catch { continue; }
    assert.ok(response.status < 500, `${m} ${p.slice(0, 60)} -> ${response.status}`);
    if (m === 'TRACE') assert.notEqual(response.status, 200);
  }
  const flood = await Promise.all(Array.from({ length: 150 }, () => send(pick(['owner', 'anonymous', 'forged']), pick(routes))));
  assert.ok(flood.every((r) => r.status < 500));
});

test('afterwards the service is healthy and still answers correctly', async () => {
  assert.equal((await fetch(gateway.url + '/healthz')).status, 200);
  assert.equal((await fetch(gateway.url + '/readyz')).status, 200);
  const page = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId, pageSize: 50 });
  assert.equal(page.status, 200, JSON.stringify(page.body)); assert.ok(page.body.entries.some((e) => e.name === 'hello.txt'), 'the fixture file is still listed: ' + JSON.stringify(page.body).slice(0, 300));
  const session = await owner.post('/v1/download-sessions', { pcId, entryId });
  assert.equal(session.status, 201);
  const file = await fetch(gateway.url + session.body.urlPath);
  assert.equal(file.status, 200); assert.ok((await file.text()).length > 0);
  assert.equal((await owner.get('/v1/pcs')).body.pcs.length, 1, 'fuzzing did not delete or alter the PC');
});
