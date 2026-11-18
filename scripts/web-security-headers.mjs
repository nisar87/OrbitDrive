// The one definition of the web app's security headers (W11). firebase.json is generated from it and the browser tests serve the
// built app with exactly the same headers, so what is tested is what ships.
//
//   node scripts/web-security-headers.mjs --check                       committed firebase.json matches the builder (fail-closed placeholder origin)
//   node scripts/web-security-headers.mjs --write --gateway https://relay.example.org   rewrites the headers for a real relay (deploy time)
//
// The relay origin is deployment-specific, so the committed file uses https://relay.example.invalid: if someone deploys it without
// running --write, the app cannot talk to any server (it fails closed instead of running with a loose policy).
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

export const PLACEHOLDER_GATEWAY = 'https://relay.example.invalid';
const FIREBASE_AUTH_HOSTS = ['https://identitytoolkit.googleapis.com', 'https://securetoken.googleapis.com'];

/** Validates the relay origin. Production needs https; plain http is only for loopback test servers. */
export function checkGatewayOrigin(origin, { allowLoopbackHttp = false } = {}) {
  let url;
  try { url = new URL(origin); } catch { throw new Error(`Relay origin is not a URL: ${origin}`); }
  const loopback = ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname);
  if (url.protocol !== 'https:' && !(allowLoopbackHttp && loopback && url.protocol === 'http:')) throw new Error('Relay origin must be https.');
  if (url.origin !== origin) throw new Error(`Relay origin must be a bare origin (scheme, host, optional port), got ${origin}`);
  if (/[\s;,'"*]/.test(origin)) throw new Error('Relay origin contains characters that are not allowed in a policy.');
  return url.origin;
}

/**
 * The Content-Security-Policy. No inline or eval'd script, no inline style sheets, no framing, no plugins, no base-tag tricks;
 * the page may talk only to itself, the relay and Firebase sign-in, and pictures/video come only from itself or the relay.
 */
export function buildCsp(gatewayOrigin, options = {}) {
  const relay = checkGatewayOrigin(gatewayOrigin, options);
  const authHosts = options.firebaseAuthEmulator ? [...FIREBASE_AUTH_HOSTS, options.firebaseAuthEmulator] : FIREBASE_AUTH_HOSTS;
  return [
    "default-src 'none'",
    "script-src 'self'",
    "style-src 'self'",
    `img-src 'self' ${relay}`,
    `media-src ${relay}`,
    `connect-src 'self' ${relay} ${authHosts.join(' ')}`,
    "font-src 'self'",
    "manifest-src 'self'",
    "worker-src 'self'",
    "frame-src 'none'",
    "frame-ancestors 'none'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'",
    ...(options.upgradeInsecure === false ? [] : ['upgrade-insecure-requests']),
  ].join('; ');
}

/** All headers for pages. (The immutable-asset cache rule is separate and unchanged.) */
export function pageHeaders(gatewayOrigin, options = {}) {
  return [
    { key: 'Content-Security-Policy', value: buildCsp(gatewayOrigin, options) },
    { key: 'Cache-Control', value: 'no-cache' },
    { key: 'X-Content-Type-Options', value: 'nosniff' },
    { key: 'Referrer-Policy', value: 'no-referrer' },
    { key: 'X-Frame-Options', value: 'DENY' },
    { key: 'Cross-Origin-Opener-Policy', value: 'same-origin' },
    { key: 'Permissions-Policy', value: 'camera=(self), microphone=(), geolocation=(), payment=(), usb=(), serial=(), bluetooth=(), interest-cohort=()' },
    { key: 'Strict-Transport-Security', value: 'max-age=31536000; includeSubDomains' },
  ];
}

/** firebase.json with the page headers for the given relay origin. */
export function firebaseConfigFor(current, gatewayOrigin) {
  const next = structuredClone(current);
  const rule = next.hosting.headers.find((r) => r.source === '**');
  if (!rule) throw new Error('firebase.json has no "**" header rule.');
  rule.headers = pageHeaders(gatewayOrigin);
  return next;
}

const here = dirname(fileURLToPath(import.meta.url));
const firebasePath = join(here, '..', 'infra', 'firebase', 'firebase.json');

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const current = JSON.parse(readFileSync(firebasePath, 'utf8'));
  if (args.includes('--check')) {
    const expected = JSON.stringify(firebaseConfigFor(current, PLACEHOLDER_GATEWAY).hosting.headers);
    if (JSON.stringify(current.hosting.headers) !== expected) { console.error('infra/firebase/firebase.json headers differ from scripts/web-security-headers.mjs. Run: node scripts/web-security-headers.mjs --write --gateway ' + PLACEHOLDER_GATEWAY); process.exit(1); }
    console.log('firebase.json headers match the builder (placeholder relay origin: fails closed until deploy).');
  } else if (args.includes('--write')) {
    const gateway = args[args.indexOf('--gateway') + 1];
    if (!gateway) { console.error('--gateway <https origin> is required'); process.exit(2); }
    writeFileSync(firebasePath, JSON.stringify(firebaseConfigFor(current, checkGatewayOrigin(gateway)), null, 2) + '\n');
    console.log(`Wrote headers for ${gateway}`);
  } else { console.error('Use --check or --write --gateway <origin>'); process.exit(2); }
}
