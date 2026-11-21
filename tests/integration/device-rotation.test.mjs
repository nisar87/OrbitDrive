// A browser's approval over a real gateway: its secret is replaced on a schedule and handed back once in a response header (the old one briefly still works),
// and an approval nobody uses lapses. Real timings, scaled down through the options (a couple of seconds stand in for weeks).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair, sleep } from './harness.mjs';

let gateway, agent;
before(async () => {
  gateway = await startGateway({
    OrbitDrive__DeviceRotateDays: String(2 / 86400),        // 2 s
    OrbitDrive__DeviceIdleDays: String(6 / 86400),           // 6 s
    OrbitDrive__DeviceRotationGraceMinutes: String(3 / 60),  // 3 s
  });
  const state = tempDir('rotation-state');
  agentCli(state, 'roots', 'add', 'Share', makeFixtureRoot());
  agent = await startAgent(gateway, state, 'Rotation PC');
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

const call = (browser, secret) => fetch(`${gateway.url}/v1/pcs`, { headers: { authorization: `Bearer ${browser.token}`, 'x-orbitdrive-device': `${browser.device.id}.${secret}` } });

test('a secret past its age is replaced once, the new one works, the old one only for the grace window', { timeout: 60_000 }, async () => {
  const phone = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, phone, 'Phone');
  const first = phone.device.secret;
  assert.equal((await call(phone, first)).headers.get('x-orbitdrive-device-rotated'), null, 'not due yet');
  await sleep(2300);
  const due = await call(phone, first);
  assert.equal(due.status, 200);
  const rotated = due.headers.get('x-orbitdrive-device-rotated');
  assert.match(rotated, new RegExp(`^${phone.device.id}\\.[A-Za-z0-9_-]{16,}$`));
  const second = rotated.slice(phone.device.id.length + 1);
  assert.notEqual(second, first);
  const withNew = await call(phone, second);
  assert.equal(withNew.status, 200);
  assert.equal(withNew.headers.get('x-orbitdrive-device-rotated'), null, 'a new secret is not rotated again at once');
  assert.equal((await call(phone, first)).status, 200, 'the old secret still works inside the grace window');
  await sleep(3300);
  assert.equal((await call(phone, first)).status, 403, 'and not after it');
  assert.equal((await call(phone, second)).status, 200);
});

test('an approval that is not used lapses and the browser must pair again; use keeps it alive', { timeout: 60_000 }, async () => {
  const idle = await new Browser(gateway, 'owner@example.test').signIn();
  const busy = await new Browser(gateway, 'owner@example.test').signIn();
  await pair(agent, idle, 'Idle phone');
  await pair(agent, busy, 'Busy phone');
  let busySecret = busy.device.secret;
  for (let i = 0; i < 4; i++) {                 // 8 s of regular use, longer than the 6 s idle limit
    await sleep(2000);
    const response = await call(busy, busySecret);
    assert.equal(response.status, 200, `use ${i}`);
    const rotated = response.headers.get('x-orbitdrive-device-rotated');
    if (rotated) busySecret = rotated.slice(busy.device.id.length + 1);
  }
  const lapsed = await call(idle, idle.device.secret);
  assert.equal(lapsed.status, 403);
  assert.match((await lapsed.json()).error.message, /not been used for a long time|not approved/);
  const account = await fetch(`${gateway.url}/v1/account`, { headers: { authorization: `Bearer ${busy.token}`, 'x-orbitdrive-device': `${busy.device.id}.${busySecret}` } });
  assert.equal(account.status, 200);
  const names = (await account.json()).devices.map((d) => d.name);
  assert.ok(names.includes('Busy phone'), 'the used approval is listed');
  assert.ok(!names.includes('Idle phone'), 'the lapsed approval is no longer listed');
});
