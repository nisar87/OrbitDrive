// W1: Firebase ID-token verification at the gateway. Tokens here are real RS256 JWTs signed with a test key whose
// public half is served as a JWK set (the gateway is pointed at it; Production refuses any key source but Google's).
// This exercises the real signature and claim checks. It is NOT evidence about the live Firebase project.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { generateKeyPairSync, createSign } from 'node:crypto';
import { once } from 'node:events';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair, sleep } from './harness.mjs';

const PROJECT = 'orbitdrive-test';
const b64 = (value) => Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64url');
const makeKey = (kid) => ({ kid, ...generateKeyPairSync('rsa', { modulusLength: 2048 }) });
let keys = [makeKey('key-1')];
const attacker = makeKey('key-1'); // same key id, different private key
let keyServer, gateway, agent;

function token(overrides = {}, { key = keys[0], header = {} } = {}) {
  const now = Math.floor(Date.now() / 1000);
  const claims = { iss: `https://securetoken.google.com/${PROJECT}`, aud: PROJECT, sub: 'uid-alice', email: 'alice@example.test', email_verified: true, iat: now - 5, auth_time: now - 5, exp: now + 3600, ...overrides };
  for (const name of Object.keys(claims)) if (claims[name] === undefined) delete claims[name];
  const head = { alg: 'RS256', kid: key.kid, typ: 'JWT', ...header };
  const signingInput = `${b64(head)}.${b64(claims)}`;
  return `${signingInput}.${createSign('RSA-SHA256').update(signingInput).sign(key.privateKey).toString('base64url')}`;
}
const status = async (bearer) => (await fetch(`${gateway.url}/v1/pcs`, { headers: bearer === undefined ? {} : { authorization: `Bearer ${bearer}` } })).status;

before(async () => {
  keyServer = createServer((request, response) => {
    response.setHeader('content-type', 'application/json');
    response.end(JSON.stringify({ keys: keys.map((k) => ({ ...k.publicKey.export({ format: 'jwk' }), kid: k.kid, alg: 'RS256', use: 'sig' })) }));
  }).listen(0, '127.0.0.1');
  await once(keyServer, 'listening');
  gateway = await startGateway({
    OrbitDrive__DevIdentity__Enabled: 'false', OrbitDrive__DevIdentity__Secret: '',
    OrbitDrive__Firebase__ProjectId: PROJECT, OrbitDrive__Firebase__KeysUrl: `http://127.0.0.1:${keyServer.address().port}/keys`,
  });
  for (let i = 0; i < 50 && (await status(token())) === 503; i++) await sleep(100);
  const state = tempDir('fb-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  agent = await startAgent(gateway, state, 'FB PC');
});
after(async () => { await agent?.stop(); await gateway?.stop(); keyServer?.close(); });

test('a correctly signed token for this project is accepted (then needs a device approval like any account)', async () => {
  assert.equal(await status(token()), 403, 'authenticated, but this browser is not approved yet');
  assert.equal(await status(undefined), 401);
});

test('development sign-in does not exist when Firebase is the verifier', async () => {
  const response = await fetch(`${gateway.url}/v1/dev/sign-in`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"email":"a@b.c"}' });
  assert.equal(response.status, 404);
  assert.equal(await status('dev.e30.AAAA'), 401);
});

const now = () => Math.floor(Date.now() / 1000);
const rejected = {
  'expired token': () => token({ exp: now() - 1 }),
  'token for another Firebase project (audience)': () => token({ aud: 'some-other-project' }),
  'token from another issuer': () => token({ iss: 'https://securetoken.google.com/some-other-project' }),
  'issuer that merely starts with the right prefix': () => token({ iss: `https://securetoken.google.com/${PROJECT}-evil` }),
  'issued in the future': () => token({ iat: now() + 3600 }),
  'authenticated in the future': () => token({ auth_time: now() + 3600 }),
  'empty subject': () => token({ sub: '' }),
  'missing subject': () => token({ sub: undefined }),
  'missing expiry': () => token({ exp: undefined }),
  'signed by a different private key with the same key id': () => token({}, { key: attacker }),
  'unknown key id': () => token({}, { key: { ...keys[0], kid: 'nope' } }),
  'alg none with empty signature': () => `${b64({ alg: 'none', kid: 'key-1' })}.${token().split('.')[1]}.`,
  'HS256 token signed with the public key as secret': () => token({}, { header: { alg: 'HS256' } }),
  'payload altered after signing (uid swapped)': () => { const parts = token().split('.'); parts[1] = b64({ ...JSON.parse(Buffer.from(parts[1], 'base64url')), sub: 'uid-victim' }); return parts.join('.'); },
  'truncated token': () => token().slice(0, -10),
  'garbage': () => 'not.a.jwt',
  'oversized token': () => token({ junk: 'x'.repeat(9000) }),
};
for (const [name, make] of Object.entries(rejected)) {
  test(`rejected: ${name}`, async () => { assert.equal(await status(make()), 401); });
}

test('an unverified email address is refused with a clear message, not treated as signed in', async () => {
  const response = await fetch(`${gateway.url}/v1/pcs`, { headers: { authorization: `Bearer ${token({ email_verified: false })}` } });
  assert.equal(response.status, 403);
  assert.match((await response.json()).error.message, /Verify your email/);
  assert.equal(await status(token({ email: undefined })), 403);
});

test('the whole flow works on verified identities: pair, browse, and the account is the token subject', async () => {
  const alice = new Browser(gateway, 'alice@example.test'); alice.token = token();
  const mallory = new Browser(gateway, 'mallory@example.test'); mallory.token = token({ sub: 'uid-mallory', email: 'mallory@example.test' });
  await pair(agent, alice, 'Alice phone');
  const pcId = agent.identity.pcId;
  assert.equal((await alice.get(`/v1/pcs/${pcId}/roots`)).body.roots[0].name, 'Share');
  // A different verified account presenting Alice's device credential is still refused.
  mallory.device = alice.device;
  assert.equal((await mallory.get(`/v1/pcs/${pcId}/roots`)).status, 403);
  // A token that claims Alice's email but another uid is a different account.
  const impostor = new Browser(gateway, 'x'); impostor.token = token({ sub: 'uid-other' }); impostor.device = alice.device;
  assert.equal((await impostor.get('/v1/pcs')).status, 403);
  // A fresh token for the same uid (after hourly refresh) keeps working with the same device approval.
  alice.token = token({ iat: now() - 1 });
  assert.equal((await alice.get('/v1/pcs')).status, 200);
});

test('signing-key rotation is picked up without a restart', async () => {
  const next = makeKey('key-2');
  keys = [next];
  let accepted = 401;
  for (let i = 0; i < 60 && accepted === 401; i++) { accepted = await status(token({}, { key: next })); if (accepted === 401) await sleep(500); }
  assert.equal(accepted, 403, 'token signed by the rotated key is accepted after a refresh');
});
