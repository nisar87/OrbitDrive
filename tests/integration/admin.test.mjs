// W10: operator administration and owner controls against the real gateway and agent.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, kill, tempDir, makeFixtureRoot, Browser, pair, waitFor } from './harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OPERATOR = 'operator@example.test', OWNER = 'owner@example.test', OTHER = 'other@example.test';
let gateway, agent, root, state, dataDir, pcId, owner, operator, other, ownerFile;

const signIn = async (email, signedInAgoSeconds) => {
  const browser = new Browser(gateway, email);
  const response = await fetch(`${gateway.url}/v1/dev/sign-in${signedInAgoSeconds ? `?signedInAgoSeconds=${signedInAgoSeconds}` : ''}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email }) });
  ({ idToken: browser.token, uid: browser.uid } = await response.json());
  return browser;
};
const admin = (browser, method, path, body) => browser.call(method, '/v1/admin' + path, body);
const downloadHello = async (browser) => {
  const roots = await browser.get(`/v1/pcs/${pcId}/roots`);
  const page = await browser.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots.body.roots[0].rootId, pageSize: 50 });
  const file = page.body.entries.find((e) => e.name === 'data.bin');
  const session = await browser.post('/v1/download-sessions', { pcId, entryId: file.entryId });
  if (session.status !== 201) return { status: session.status, body: session.body };
  const response = await fetch(gateway.url + session.body.urlPath);
  const bytes = Buffer.from(await response.arrayBuffer());
  return { status: response.status, bytes, size: file.size, body: response.status === 200 ? null : JSON.parse(bytes.toString('utf8')) };
};

async function boot(overrides = {}) {
  dataDir = tempDir('admin-data');
  gateway = await startGateway({ OrbitDrive__DataDirectory: dataDir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR), ...overrides });
  agent = await startAgent(gateway, state, 'Admin test PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, OWNER).signIn();
  await pair(agent, owner, 'Owner phone');
  operator = await signIn(OPERATOR);
  other = await signIn(OTHER);
}

before(async () => {
  root = makeFixtureRoot();
  ownerFile = join(root, 'hello.txt');
  state = tempDir('admin-state');
  const { agentCli } = await import('./harness.mjs');
  agentCli(state, 'roots', 'add', 'Share', root);
  await boot();
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

describe('who may administer', () => {
  test('ordinary users, strangers and signed-out callers cannot use any admin endpoint', async () => {
    const paths = [['GET', '/overview'], ['GET', '/users'], ['GET', '/audit'], ['GET', '/settings'], ['PUT', '/settings', { minAgentVersion: '1.0.0' }],
      ['POST', `/users/${uidOf(OTHER)}/suspend`, { reason: 'x' }], ['POST', `/users/${uidOf(OTHER)}/unsuspend`, { reason: 'x' }],
      ['POST', `/users/${uidOf(OTHER)}/revoke-all`, { reason: 'x' }], ['POST', `/users/${uidOf(OTHER)}/reset-relay-allowance`, { reason: 'x' }]];
    for (const [method, path, body] of paths) {
      assert.equal((await admin(owner, method, path, body)).status, 403, `owner ${method} ${path}`);
      assert.equal((await admin(other, method, path, body)).status, 403, `stranger ${method} ${path}`);
      assert.equal((await new Browser(gateway, 'x@example.test').call(method, '/v1/admin' + path, body)).status, 401, `anonymous ${method} ${path}`);
    }
    assert.equal(operator.device, null, 'the operator has no paired browser and needs none');
  });

  test('an operator whose sign-in is old must sign in again; a fresh sign-in works', async () => {
    const stale = await signIn(OPERATOR, 3600);
    const refused = await admin(stale, 'GET', '/overview');
    assert.equal(refused.status, 401);
    assert.match(refused.body.error.message, /recent sign-in/);
    assert.equal((await admin(operator, 'GET', '/overview')).status, 200);
  });

  test('nobody can promote themselves: with no operators configured the first account to register still has no admin rights', async () => {
    const bare = await startGateway();
    try {
      const first = await new Browser(bare, 'first@example.test').signIn();
      for (const path of ['/v1/admin/overview', '/v1/admin/users', '/v1/admin/audit']) assert.equal((await first.get(path)).status, 403, path);
      assert.equal((await first.call('PUT', '/v1/admin/settings', { minAgentVersion: '' })).status, 403);
    } finally { await bare.stop(); }
  });
});

describe('operator view', () => {
  test('the overview reports measured numbers and no private details', async () => {
    const before = (await admin(operator, 'GET', '/overview')).body;
    assert.equal(before.pcs, 1);
    assert.equal(before.pcsOnline, 1);
    assert.equal(before.users, 1);
    assert.equal(before.browserDevices, 1);
    const result = await downloadHello(owner);
    assert.equal(result.status, 200);
    await waitFor(async () => Number((await admin(operator, 'GET', '/overview')).body.downloadsCompleted) === Number(before.downloadsCompleted) + 1, 5000, 'the download to be counted');
    const after = (await admin(operator, 'GET', '/overview')).body;
    assert.equal(BigInt(after.relayBytesDown) - BigInt(before.relayBytesDown), BigInt(result.bytes.length), 'counted bytes equal bytes the browser received');
    const text = JSON.stringify(after);
    for (const secret of [OWNER, 'Admin test PC', 'data.bin', 'Share', root]) assert.ok(!text.includes(secret), `overview leaks ${secret}`);
  });

  test('the user list has ids and counts only', async () => {
    const list = await admin(operator, 'GET', '/users');
    assert.equal(list.status, 200);
    const mine = list.body.users.find((u) => u.uid === uidOf(OWNER));
    assert.deepEqual(Object.keys(mine).sort(), ['devices', 'pcs', 'relayBytesToday', 'suspended', 'uid']);
    assert.equal(mine.devices, 1);
    assert.equal(mine.pcs, 1);
    const text = JSON.stringify(list.body);
    for (const secret of [OWNER, 'Owner phone', 'Admin test PC']) assert.ok(!text.includes(secret), `user list leaks ${secret}`);
  });

  test('there is no operator endpoint that browses files or impersonates a user', async () => {
    for (const path of ['/pcs', '/files', '/browse', '/impersonate', '/users/' + uidOf(OWNER) + '/files', '/users/' + uidOf(OWNER) + '/token']) {
      const reply = await admin(operator, 'GET', path);
      assert.ok([404, 405].includes(reply.status), `${path} answered ${reply.status}`);
    }
    // An operator's own token does not open a user's PC.
    assert.notEqual((await operator.get(`/v1/pcs/${pcId}/roots`)).status, 200);
  });
});

describe('suspension, revocation and the audit trail', () => {
  test('suspending an account stops its browsers at once, is audited, survives a restart, and can be undone', async () => {
    assert.equal((await owner.get(`/v1/pcs/${pcId}/roots`)).status, 200);
    const reply = await admin(operator, 'POST', `/users/${uidOf(OWNER)}/suspend`, { reason: 'abuse report 42' });
    assert.equal(reply.status, 200);
    for (const path of [`/v1/pcs/${pcId}/roots`, '/v1/pcs', '/v1/account', '/v1/browser-devices']) {
      const blocked = await owner.get(path);
      assert.equal(blocked.status, 403, path);
      assert.match(blocked.body.error.message, /suspended/);
    }
    assert.equal((await admin(operator, 'GET', '/users')).body.users.find((u) => u.uid === uidOf(OWNER)).suspended, true);
    // A suspended account cannot pair another browser either.
    const entries = (await admin(operator, 'GET', '/audit')).body.entries;
    assert.deepEqual(entries.filter((e) => e.action === 'suspend').map((e) => [e.operator, e.target, e.detail]), [[uidOf(OPERATOR), uidOf(OWNER), 'abuse report 42']]);

    await agent.stop(); await gateway.stop();
    gateway = await startGateway({ OrbitDrive__DataDirectory: dataDir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR) }, Number(new URL(gateway.url).port));
    agent = await startAgent(gateway, state, 'Admin test PC');
    for (const person of [owner, other]) { person.gateway = gateway; await person.signIn(); }          // a restarted test gateway has a new signing secret
    operator = await signIn(OPERATOR);
    assert.equal((await owner.get('/v1/pcs')).status, 403, 'the suspension survived a restart');
    assert.equal((await admin(operator, 'GET', '/audit')).body.entries.some((e) => e.action === 'suspend'), true, 'so did the audit trail');

    assert.equal((await admin(operator, 'POST', `/users/${uidOf(OWNER)}/unsuspend`, { reason: 'resolved' })).status, 200);
    await waitFor(async () => (await owner.get('/v1/pcs')).status === 200, 5000, 'the account to work again');
  });

  test('an operator account cannot be suspended through the API', async () => {
    assert.equal((await admin(operator, 'POST', `/users/${uidOf(OPERATOR)}/suspend`, { reason: 'x' })).status, 409);
    assert.equal((await admin(operator, 'POST', '/users/../../etc/suspend', { reason: 'x' })).status, 404);
    assert.equal((await admin(operator, 'POST', '/users/' + encodeURIComponent('a b') + '/suspend', { reason: 'x' })).status, 400);
  });

  test('revoking all browsers of an account works from the operator console and is audited', async () => {
    const second = await new Browser(gateway, OWNER).signIn();
    await pair(agent, second, 'Second phone');
    const reply = await admin(operator, 'POST', `/users/${uidOf(OWNER)}/revoke-all`, { reason: 'lost phones' });
    assert.equal(reply.body.devicesRevoked, 2);
    assert.equal((await owner.get('/v1/pcs')).status, 403);
    assert.equal((await second.get('/v1/pcs')).status, 403);
    assert.equal((await admin(operator, 'GET', '/audit')).body.entries[0].action, 'revoke-all');
    await pair(agent, owner, 'Owner phone again');            // the person can pair again; revocation removes access, not the account
    assert.equal((await owner.get('/v1/pcs')).status, 200);
  });
});

describe('minimum PC version', () => {
  test('is validated, applied to PCs that connect, persisted and reversible', async () => {
    assert.equal((await admin(operator, 'PUT', '/settings', { minAgentVersion: 'banana' })).status, 400);
    assert.equal((await admin(operator, 'PUT', '/settings', { minAgentVersion: '99.0.0' })).status, 200);
    assert.equal((await admin(operator, 'GET', '/settings')).body.minAgentVersion, '99.0.0');
    assert.equal((await admin(operator, 'GET', '/overview')).body.minAgentVersion, '99.0.0');
    await agent.stop();
    const stale = await startAgent(gateway, state, 'Admin test PC', undefined, { refused: true });
    try {
      assert.equal(stale.identity, undefined, 'the service did not accept the PC');
      assert.match(stale.log, /update/i, 'the PC was told to update');
      assert.equal((await owner.get(`/v1/pcs/${pcId}/roots`)).status, 503, 'the PC is not served while below the minimum');
    } finally { await stale.stop(); }
    assert.equal((await admin(operator, 'PUT', '/settings', { minAgentVersion: '' })).status, 200);
    agent = await startAgent(gateway, state, 'Admin test PC');
    await waitFor(async () => (await owner.get(`/v1/pcs/${pcId}/roots`)).status === 200, 15000, 'the PC to be served again');
    assert.equal(JSON.parse(readFileSync(join(dataDir, 'admin-settings.json'), 'utf8')).minAgentVersion, '');
  });
});

describe('owner controls', () => {
  test('the account page lists this person\'s browsers and PCs, nobody else\'s', async () => {
    const mine = await owner.get('/v1/account');
    assert.equal(mine.status, 200);
    assert.equal(mine.body.pcs.length, 1);
    assert.equal(mine.body.pcs[0].name, 'Admin test PC');
    assert.equal(mine.body.pcs[0].online, true);
    assert.equal(mine.body.isOperator, false);
    assert.equal(mine.body.preferences.historyRetentionDays, 7);
    assert.equal((await other.get('/v1/account')).status, 403, 'an account with no approved browser has nothing to see');
    assert.equal((await fetch(`${gateway.url}/v1/account`)).status, 401);
  });

  test('preferences accept only the offered choices and are remembered', async () => {
    assert.equal((await owner.call('PUT', '/v1/account/preferences', { historyRetentionDays: 5, notifyInvitations: true, diagnosticsConsent: false })).status, 400);
    assert.equal((await owner.call('PUT', '/v1/account/preferences', { historyRetentionDays: 0, notifyInvitations: false, diagnosticsConsent: true })).status, 200);
    assert.deepEqual((await owner.get('/v1/account')).body.preferences, { historyRetentionDays: 0, notifyInvitations: false, diagnosticsConsent: true });
  });

  test('retention 0 hides a finished transfer from the list; a longer choice shows it', async () => {
    await owner.call('PUT', '/v1/account/preferences', { historyRetentionDays: 7, notifyInvitations: true, diagnosticsConsent: false });
    await downloadHello(owner);
    await waitFor(async () => (await owner.get('/v1/download-sessions')).body.sessions.some((s) => s.state === 'sent_to_browser'), 5000, 'a finished transfer to be listed');
    await owner.call('PUT', '/v1/account/preferences', { historyRetentionDays: 0, notifyInvitations: true, diagnosticsConsent: false });
    assert.equal((await owner.get('/v1/download-sessions')).body.sessions.some((s) => s.state === 'sent_to_browser'), false);
  });

  test('the export holds no secret, file name or path', async () => {
    const exported = await owner.get('/v1/account/export');
    assert.equal(exported.status, 200);
    assert.match(exported.headers.get('content-disposition'), /attachment/);
    const text = JSON.stringify(exported.body);
    for (const secret of [owner.device.secret, owner.token, 'hello.txt', 'data.bin', root]) assert.ok(!text.includes(secret), `export leaks ${secret}`);
  });

  test('revoke-all ends every browser of the account, including this one, and touches no PC file', async () => {
    const second = await new Browser(gateway, OWNER).signIn();
    await pair(agent, second, 'Tablet');
    const before = readFileSync(ownerFile);
    const reply = await owner.call('POST', '/v1/account/revoke-all');
    assert.equal(reply.status, 200);
    assert.equal(reply.body.devicesRevoked, 2);
    assert.equal((await owner.get('/v1/pcs')).status, 403);
    assert.equal((await second.get('/v1/pcs')).status, 403);
    assert.deepEqual(readFileSync(ownerFile), before);
    await pair(agent, owner, 'Phone, paired again');
  });
});

describe('account deletion', () => {
  test('needs a recent sign-in and the exact phrase', async () => {
    const stale = await signIn(OWNER, 7200);
    stale.device = owner.device;
    const refused = await stale.call('DELETE', '/v1/account', { confirm: 'delete my account' });
    assert.equal(refused.status, 401);
    assert.equal((await owner.call('DELETE', '/v1/account', { confirm: 'yes' })).status, 400);
    assert.equal((await owner.call('DELETE', '/v1/account', {})).status, 400);
    assert.equal((await owner.get('/v1/pcs')).status, 200, 'nothing was deleted by the refusals');
  });

  test('deleting revokes access, releases the PC and cleans the service\'s record, but never deletes the PC\'s files', async () => {
    const before = readFileSync(ownerFile);
    const reply = await owner.call('DELETE', '/v1/account', { confirm: 'delete my account' });
    assert.equal(reply.status, 200);
    assert.equal((await owner.get('/v1/pcs')).status, 403);
    assert.equal((await owner.get('/v1/account')).status, 403);
    assert.ok(existsSync(ownerFile) && existsSync(join(root, 'data.bin')), 'the PC\'s own files are untouched');
    assert.deepEqual(readFileSync(ownerFile), before);
    const users = (await admin(operator, 'GET', '/users')).body.users;
    assert.equal(users.some((u) => u.uid === uidOf(OWNER)), false, 'the service no longer lists the account');
    const stored = readFileSync(join(dataDir, 'registrations.json'), 'utf8');
    assert.ok(!stored.includes(uidOf(OWNER)), 'its record is gone from what the service keeps on disk');
    assert.equal((await admin(operator, 'GET', '/overview')).body.pcs, 0, 'the PC no longer counts as owned');
    const trail = (await admin(operator, 'GET', '/audit')).body.entries.find((e) => e.action === 'account-deleted');
    assert.ok(trail, 'the deletion is in the audit trail, which is the one retained record');
    assert.equal(trail.target, uidOf(OWNER));
    assert.ok(!JSON.stringify(trail).includes(OWNER), 'the trail has the account id, not the email address');
  });

  test('the PC can be paired again by a new account afterwards', async () => {
    const fresh = await new Browser(gateway, 'newowner@example.test').signIn();
    await pair(agent, fresh, 'New phone');
    assert.equal((await fresh.get(`/v1/pcs/${pcId}/roots`)).status, 200);
  });
});

describe('allowances recover', () => {
  test('a daily relay-byte cap refuses new transfers once used, an operator can restore it, and counts are per account', async () => {
    await agent.stop(); await gateway.stop();
    await boot({ OrbitDrive__MaxRelayBytesPerOwnerPerDay: '100000' });           // data.bin is 300 000 bytes: one download uses the whole day
    const first = await downloadHello(owner);
    assert.equal(first.status, 200);
    await waitFor(async () => Number((await admin(operator, 'GET', '/users')).body.users.find((u) => u.uid === uidOf(OWNER)).relayBytesToday) >= 300000, 5000, 'the bytes to be counted');
    const refused = await downloadHello(owner);
    assert.equal(refused.status, 429);
    assert.match(refused.body.error.message, /allowance/);
    assert.equal(Number((await admin(operator, 'GET', '/overview')).body.quotaRefusals) >= 1, true);
    const strangerPc = await owner.get('/v1/pcs');
    assert.equal(strangerPc.status, 200, 'listing is not relay traffic and still works');
    assert.equal((await admin(operator, 'POST', `/users/${uidOf(OWNER)}/reset-relay-allowance`, { reason: 'support ticket' })).status, 200);
    assert.equal((await downloadHello(owner)).status, 200, 'after the reset the account can transfer again');
  });
});
