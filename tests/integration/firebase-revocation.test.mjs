// W1/W3: Firebase account status at the gateway. A real gateway verifies real RS256 tokens AND asks a stand-in for Firebase's accounts:lookup whether the
// account is disabled/deleted or its sessions were revoked. The stand-in is NOT Firebase: this is evidence about OrbitDrive's decisions, not the live project.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { generateKeyPairSync, createSign } from 'node:crypto';
import { once } from 'node:events';
import { startGateway, sleep } from './harness.mjs';

const PROJECT = 'orbitdrive-test';
const b64 = (value) => Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64url');
const key = { kid: 'k1', ...generateKeyPairSync('rsa', { modulusLength: 2048 }) };
const LOOKUP_TOKEN = 'test-service-token';
let keyServer, lookupServer, gateway;
const accounts = new Map();        // uid -> { disabled, validSince } ; absent = deleted
let lookupDown = false; let lookups = []; let lastAuth = '';

const now = () => Math.floor(Date.now() / 1000);
function token(uid, authTime = now() - 30) {
  const claims = { iss: `https://securetoken.google.com/${PROJECT}`, aud: PROJECT, sub: uid, email: `${uid}@example.test`, email_verified: true, iat: now() - 5, auth_time: authTime, exp: now() + 3600 };
  const input = `${b64({ alg: 'RS256', kid: key.kid, typ: 'JWT' })}.${b64(claims)}`;
  return `${input}.${createSign('RSA-SHA256').update(input).sign(key.privateKey).toString('base64url')}`;
}
const status = async (bearer) => (await fetch(`${gateway.url}/v1/pcs`, { headers: { authorization: `Bearer ${bearer}` } })).status;   // 403 = signed in but no approved device yet

before(async () => {
  keyServer = createServer((_, response) => { response.setHeader('content-type', 'application/json'); response.end(JSON.stringify({ keys: [{ ...key.publicKey.export({ format: 'jwk' }), kid: key.kid, alg: 'RS256', use: 'sig' }] })); }).listen(0, '127.0.0.1');
  lookupServer = createServer((request, response) => {
    let body = ''; request.on('data', (c) => { body += c; });
    request.on('end', () => {
      lastAuth = request.headers.authorization ?? '';
      if (lookupDown) { response.statusCode = 503; response.end('{}'); return; }
      const uid = JSON.parse(body).localId[0]; lookups.push(uid);
      response.setHeader('content-type', 'application/json');
      const account = accounts.get(uid);
      response.end(JSON.stringify(account ? { users: [{ localId: uid, disabled: account.disabled, validSince: String(account.validSince) }] } : {}));
    });
  }).listen(0, '127.0.0.1');
  await Promise.all([once(keyServer, 'listening'), once(lookupServer, 'listening')]);
  gateway = await startGateway({
    OrbitDrive__DevIdentity__Enabled: 'false', OrbitDrive__DevIdentity__Secret: '',
    OrbitDrive__Firebase__ProjectId: PROJECT, OrbitDrive__Firebase__KeysUrl: `http://127.0.0.1:${keyServer.address().port}/keys`,
    OrbitDrive__Firebase__CheckRevocation: 'true', OrbitDrive__Firebase__RevocationCacheSeconds: '1',
    OrbitDrive__Firebase__AccountLookupBase: `http://127.0.0.1:${lookupServer.address().port}`, OrbitDrive__Firebase__AccountLookupTestToken: LOOKUP_TOKEN,
  });
  accounts.set('uid-ready', { disabled: false, validSince: 0 });
  for (let i = 0; i < 50 && (await status(token('uid-ready'))) === 503; i++) await sleep(100);
});
after(async () => { await gateway?.stop(); keyServer?.close(); lookupServer?.close(); });
const expire = () => sleep(1300);   // longer than the one-second cache

test('a healthy account is accepted, and Firebase is asked with the service credential for exactly that account', async () => {
  accounts.set('uid-alice', { disabled: false, validSince: 0 });
  lookups = [];
  assert.equal(await status(token('uid-alice')), 403);
  assert.equal(await status(token('uid-alice')), 403);
  assert.deepEqual(lookups, ['uid-alice'], 'one lookup, then the cached answer');
  assert.equal(lastAuth, `Bearer ${LOOKUP_TOKEN}`);
});

test('a disabled account is refused as soon as the cached answer expires, and works again when re-enabled', async () => {
  accounts.set('uid-bob', { disabled: false, validSince: 0 });
  assert.equal(await status(token('uid-bob')), 403);
  accounts.set('uid-bob', { disabled: true, validSince: 0 });
  await expire();
  assert.equal(await status(token('uid-bob')), 401);
  accounts.set('uid-bob', { disabled: false, validSince: 0 });
  await expire();
  assert.equal(await status(token('uid-bob')), 403);
});

test('a deleted account is refused', async () => {
  assert.equal(await status(token('uid-ghost')), 401);
});

test('"sign out everywhere": a token from before the revocation is refused, one from a later sign-in is accepted', async () => {
  accounts.set('uid-carol', { disabled: false, validSince: 0 });
  const old = token('uid-carol', now() - 600);
  assert.equal(await status(old), 403);
  accounts.set('uid-carol', { disabled: false, validSince: now() - 300 });
  await expire();
  assert.equal(await status(old), 401, 'the old session is cut');
  assert.equal(await status(token('uid-carol', now() - 30)), 403, 'a fresh sign-in works');
});

test('when Firebase cannot be asked: a recent answer is used, no answer means unavailable (503), never silently allowed', async () => {
  accounts.set('uid-dave', { disabled: false, validSince: 0 });
  assert.equal(await status(token('uid-dave')), 403);
  lookupDown = true;
  try {
    await expire();
    assert.equal(await status(token('uid-dave')), 403, 'the answer from moments ago is reused during the outage');
    assert.equal(await status(token('uid-newcomer')), 503, 'an account never checked is not let in blind');
  } finally { lookupDown = false; }
});
