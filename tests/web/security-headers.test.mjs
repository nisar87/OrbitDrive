// W11: the web security headers are strict, cannot be loosened by a bad relay origin, and firebase.json cannot drift from the builder.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildCsp, pageHeaders, checkGatewayOrigin, PLACEHOLDER_GATEWAY } from '../../scripts/web-security-headers.mjs';

const directives = (csp) => Object.fromEntries(csp.split('; ').map((d) => { const [name, ...values] = d.split(' '); return [name, values]; }));

test('the policy allows nothing by default and never allows inline or eval scripts, styles, wildcards, framing or plugins', () => {
  const csp = buildCsp('https://relay.example.org');
  const d = directives(csp);
  assert.deepEqual(d['default-src'], ["'none'"]);
  assert.deepEqual(d['script-src'], ["'self'"]);
  assert.deepEqual(d['style-src'], ["'self'"]);
  assert.deepEqual(d['frame-ancestors'], ["'none'"]);
  assert.deepEqual(d['frame-src'], ["'none'"]);
  assert.deepEqual(d['object-src'], ["'none'"]);
  assert.deepEqual(d['base-uri'], ["'none'"]);
  assert.ok('upgrade-insecure-requests' in d);
  assert.doesNotMatch(csp, /unsafe-inline|unsafe-eval|wasm-unsafe-eval|\*|data:|http:(?!\/\/)|;;/);
});

test('only the page itself, the relay and Firebase sign-in can be talked to; media only from the relay', () => {
  const d = directives(buildCsp('https://relay.example.org'));
  assert.deepEqual(d['connect-src'], ["'self'", 'https://relay.example.org', 'https://identitytoolkit.googleapis.com', 'https://securetoken.googleapis.com']);
  assert.deepEqual(d['media-src'], ['https://relay.example.org']);
  assert.deepEqual(d['img-src'], ["'self'", 'https://relay.example.org']);
});

test('a relay origin that could change the policy is refused', () => {
  for (const bad of ['', 'relay.example.org', 'http://relay.example.org', 'https://relay.example.org/path', 'https://relay.example.org/', "https://x.example; script-src *", 'https://a.example https://b.example', "https://relay.example.org'", 'https://*.example.org', 'javascript:alert(1)', 'https://user@relay.example.org'])
    assert.throws(() => buildCsp(bad), undefined, `should refuse ${JSON.stringify(bad)}`);
});

test('plain http is accepted only for loopback test servers, and only when the caller asks', () => {
  assert.throws(() => buildCsp('http://127.0.0.1:5199'));
  assert.doesNotThrow(() => buildCsp('http://127.0.0.1:5199', { allowLoopbackHttp: true }));
  assert.throws(() => buildCsp('http://relay.example.org', { allowLoopbackHttp: true }));
  assert.equal(checkGatewayOrigin('https://relay.example.org:8443'), 'https://relay.example.org:8443');
});

test('page headers carry the full set', () => {
  const names = pageHeaders('https://relay.example.org').map((h) => h.key);
  for (const required of ['Content-Security-Policy', 'X-Content-Type-Options', 'Referrer-Policy', 'X-Frame-Options', 'Cross-Origin-Opener-Policy', 'Permissions-Policy', 'Strict-Transport-Security', 'Cache-Control'])
    assert.ok(names.includes(required), `missing ${required}`);
  const permissions = pageHeaders('https://relay.example.org').find((h) => h.key === 'Permissions-Policy').value;
  assert.match(permissions, /camera=\(self\)/);      // the QR scanner
  assert.match(permissions, /microphone=\(\)/);
});

test('the committed firebase.json uses the builder and a fail-closed placeholder relay, never a loose or local policy', () => {
  const config = JSON.parse(readFileSync(new URL('../../infra/firebase/firebase.json', import.meta.url), 'utf8'));
  const rule = config.hosting.headers.find((r) => r.source === '**');
  assert.deepEqual(rule.headers, pageHeaders(PLACEHOLDER_GATEWAY));
  assert.doesNotMatch(JSON.stringify(config), /localhost|127\.0\.0\.1|unsafe-/);
  assert.ok(config.hosting.headers.find((r) => r.source === '/assets/**').headers.some((h) => /immutable/.test(h.value)));
});

test('hashed assets are cached for a year: Firebase lets the LATER matching rule win, so the catch-all no-cache rule must come first', () => {
  const rules = JSON.parse(readFileSync(new URL('../../infra/firebase/firebase.json', import.meta.url), 'utf8')).hosting.headers;
  const cache = (source) => rules.findIndex((r) => r.source === source && r.headers.some((h) => h.key === 'Cache-Control'));
  assert.ok(cache('**') >= 0 && cache('/assets/**') >= 0, 'both cache rules exist');
  assert.ok(cache('/assets/**') > cache('**'), 'the more specific rule comes after the catch-all (observed live: a catch-all placed after it overrode immutable caching)');
  assert.match(rules[cache('/assets/**')].headers.find((h) => h.key === 'Cache-Control').value, /immutable/);
});
