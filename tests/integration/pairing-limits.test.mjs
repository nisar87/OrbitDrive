// W2: pairing attempt limits (per client address and per account), forwarded-address trust, and renaming devices.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, freePort } from './harness.mjs';

const LIMIT = 10, POLLS = 90;
const dataDir = tempDir('limits-data');
const secret = randomBytes(32).toString('hex');
const port = await freePort();   // one address for the whole file: the gateway is restarted on it
const boot = (extra = {}, fixed = port) => startGateway({
  OrbitDrive__PairingClaimsPerMinute: String(LIMIT), OrbitDrive__PairingPollsPerMinute: String(POLLS),
  OrbitDrive__TrustedProxies__0: '127.0.0.1', OrbitDrive__DataDirectory: dataDir, OrbitDrive__DevIdentity__Secret: secret, ...extra,
}, fixed);
let gateway, agent, ipc, pcId;

before(async () => {
  const state = tempDir('limits-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  gateway = await boot();
  agent = await startAgent(gateway, state, 'Limits PC');
  ipc = await connectIpc(agent.ready.pipe);
  pcId = agent.identity.pcId;
});
after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

/** A request as the reverse proxy would deliver it: the proxy appends the real client address to X-Forwarded-For. */
async function via(browser, forwardedFor, method, path, body, target = gateway) {
  const headers = browser ? browser.headers() : {};
  if (forwardedFor) headers['x-forwarded-for'] = forwardedFor;
  if (body !== undefined) headers['content-type'] = 'application/json';
  const response = await fetch(target.url + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null, retryAfter: response.headers.get('retry-after') };
}
async function newCode(from = agent) {
  from.send('pair');
  const [id, code] = (await from.next('pairing')).code.split('-');
  return { id, code };
}
const user = (email) => new Browser(gateway, email).signIn();
const wrong = 'ZZZZZZZZZZ';

test('guessing a manual code: the code dies after 5 wrong tries, then the client address is locked out', async () => {
  const attacker = await user('attacker@example.test');
  const { id, code } = await newCode();
  const ip = '203.0.113.5';
  const results = [];
  for (let i = 0; i < LIMIT; i++) results.push(await via(attacker, ip, 'POST', `/v1/pairing-requests/${id}/claim`, { secret: wrong, browserName: 'x' }));
  assert.deepEqual(results.slice(0, 5).map((r) => r.status), [403, 403, 403, 403, 403]);
  assert.deepEqual(results.slice(5).map((r) => r.body.error.code), Array(LIMIT - 5).fill('expired'), 'the code no longer accepts anything, right or wrong');
  const locked = await via(attacker, ip, 'POST', `/v1/pairing-requests/${id}/claim`, { secret: code, browserName: 'x' });
  assert.deepEqual([locked.status, locked.body.error.code], [429, 'limit_exceeded']);
  assert.ok(Number(locked.retryAfter) >= 1 && Number(locked.retryAfter) <= 60, `Retry-After ${locked.retryAfter}`);
  assert.ok(!JSON.stringify(locked.body).includes(code));

  // Switching account does not help from the same address - the address limit is checked before anything else.
  const second = await user('attacker2@example.test');
  const fresh = await newCode();
  assert.equal((await via(second, ip, 'POST', `/v1/pairing-requests/${fresh.id}/claim`, { secret: wrong, browserName: 'x' })).status, 429);
  assert.equal((await via(null, ip, 'POST', `/v1/pairing-requests/${fresh.id}/claim`, { secret: wrong, browserName: 'x' })).status, 429, 'even unauthenticated noise is throttled');
  // Somebody else, somewhere else, is unaffected and can still pair with the right code.
  const honest = await user('owner@example.test');
  const ok = await via(honest, '198.51.100.7', 'POST', `/v1/pairing-requests/${fresh.id}/claim`, { secret: fresh.code, browserName: 'Honest phone' });
  assert.equal(ok.status, 200, JSON.stringify(ok.body));
  await agent.next('pairing_claimed');
  agent.send('deny'); await agent.next('pairing_result');
});

test('one account cannot spread its guesses over many addresses', async () => {
  const hopper = await user('hopper@example.test');
  const statuses = [];
  for (let i = 0; i <= LIMIT; i++) statuses.push((await via(hopper, `192.0.2.${i + 1}`, 'POST', '/v1/pairing-requests/AAAAAA/claim', { secret: wrong, browserName: 'x' })).status);
  assert.deepEqual(statuses, [...Array(LIMIT).fill(404), 429]);
});

test('a client cannot dodge the limit by forging X-Forwarded-For', async () => {
  // Through the trusted proxy: whatever the client put in the header first, the proxy's appended entry is what counts.
  const statuses = [];
  for (let i = 0; i <= LIMIT; i++) {
    const forger = await user(`forger${i}@example.test`);
    statuses.push((await via(forger, `10.9.8.${i}, 203.0.113.77`, 'POST', '/v1/pairing-requests/AAAAAA/claim', { secret: wrong, browserName: 'x' })).status);
  }
  assert.deepEqual(statuses, [...Array(LIMIT).fill(404), 429]);

  // No trusted proxy configured: the header is ignored entirely and the TCP peer address is used.
  const direct = await boot({ OrbitDrive__TrustedProxies__0: '10.255.255.1', OrbitDrive__DataDirectory: '' }, await freePort());
  try {
    const seen = [];
    for (let i = 0; i <= LIMIT; i++) {
      const forger = await new Browser(direct, `direct${i}@example.test`).signIn();
      seen.push((await via(forger, `198.18.0.${i}`, 'POST', '/v1/pairing-requests/AAAAAA/claim', { secret: wrong, browserName: 'x' }, direct)).status);
    }
    assert.deepEqual(seen, [...Array(LIMIT).fill(404), 429]);
  } finally { await direct.stop(); }
});

test('status polling is bounded per account and does not block other accounts', async () => {
  const poller = await user('poller@example.test');
  const statuses = new Set();
  for (let i = 0; i < POLLS; i++) statuses.add((await via(poller, '192.0.2.200', 'GET', '/v1/pairing-requests/AAAAAA')).status);
  assert.deepEqual([...statuses], [404]);
  const limited = await via(poller, '192.0.2.200', 'GET', '/v1/pairing-requests/AAAAAA');
  assert.deepEqual([limited.status, limited.body.error.code], [429, 'limit_exceeded']);
  assert.ok(Number(limited.retryAfter) >= 1);
  assert.equal((await via(await user('quiet@example.test'), '192.0.2.200', 'GET', '/v1/pairing-requests/AAAAAA')).status, 404);
});

test('device names: rename own browsers, visible on the PC, validated, isolated, durable', { timeout: 120_000 }, async () => {
  const phone = await user('renamer@example.test');
  const tablet = await user('renamer@example.test');
  const stranger = await user('other-renamer@example.test');
  await pair(agent, phone, 'Phone');
  await pair(agent, tablet, 'Tablet');
  const rename = (browser, id, name) => browser.call('PATCH', `/v1/browser-devices/${id}`, { name });
  const names = async (browser) => (await browser.get('/v1/browser-devices')).body.devices.map((d) => d.name).sort();

  assert.equal((await rename(phone, 'current', '  Nisar’s Pixel 📱  ')).status, 200);
  assert.equal((await rename(phone, tablet.device.id, 'Kitchen tablet')).status, 200, 'another browser of the same account');
  assert.deepEqual(await names(phone), ['Kitchen tablet', 'Nisar’s Pixel 📱'], 'trimmed, Unicode kept');
  assert.deepEqual((await ipc.send('devices')).data.map((d) => d.name).sort(), ['Kitchen tablet', 'Nisar’s Pixel 📱'], 'the PC owner sees the new names');

  for (const bad of ['', '   ', 'x'.repeat(65), 'line\nbreak', 'bell\u0007'])
    assert.equal((await rename(phone, 'current', bad)).status, 400, JSON.stringify(bad));
  assert.equal((await phone.call('PATCH', '/v1/browser-devices/current', { nombre: 'x' })).status, 400);
  assert.equal((await rename(stranger, phone.device.id, 'Hijacked')).status, 403, 'a signed-in account without an approved browser');
  // An approved browser of a DIFFERENT account must not learn the device exists.
  const otherState = tempDir('limits-state2');
  agentCli(otherState, 'roots', 'add', 'Other', makeFixtureRoot());
  const otherAgent = await startAgent(gateway, otherState, 'Other PC');
  try {
    await pair(otherAgent, stranger, 'Stranger phone');
    assert.equal((await rename(stranger, phone.device.id, 'Hijacked')).status, 404);
    assert.equal((await phone.call('DELETE', `/v1/browser-devices/${tablet.device.id}`)).status, 200);
    assert.equal((await rename(phone, tablet.device.id, 'Zombie')).status, 404, 'a removed browser cannot be renamed back into view');
    assert.equal((await rename(tablet, 'current', 'Zombie')).status, 403);
    assert.deepEqual(await names(phone), ['Nisar’s Pixel 📱']);
    // A control character in the name offered at pairing time never reaches the PC's approval prompt.
    const { id, code } = await newCode(otherAgent);
    const sneaky = await stranger.post(`/v1/pairing-requests/${id}/claim`, { secret: code, browserName: 'Phone‮\u0000evil' });
    assert.equal(sneaky.status, 400);
  } finally { await otherAgent.stop(); }

  await gateway.stop(); agent.drain(); gateway = await boot();
  await agent.next('connected', 60_000);
  assert.deepEqual(await names(phone), ['Nisar’s Pixel 📱'], 'the new name survives a relay restart');
});
