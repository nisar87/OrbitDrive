// The audit-fix round, end to end with real processes: the operator second factor (F-005), reasons and audit-before-change (F-006), account export and
// delete (F-012, F-013), ordering closed until the start date (decision b), and the relay keeping no file names (F-003, decision a).
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, connectIpc, agentCli, tempDir, makeFixtureRoot, Browser, pair, waitFor } from './harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OPERATOR = 'ops@example.test', OWNER = 'person@example.test';
const signIn = async (gateway, email, query = '') => {
  const browser = new Browser(gateway, email);
  const response = await fetch(`${gateway.url}/v1/dev/sign-in${query}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email }) });
  ({ idToken: browser.token, uid: browser.uid } = await response.json());
  return browser;
};
const auditText = (dir) => (existsSync(join(dir, 'admin-audit.jsonl')) ? readFileSync(join(dir, 'admin-audit.jsonl'), 'utf8') : '');

describe('operator second factor (ready but off by default)', () => {
  let off, on, offDir, onDir;
  before(async () => {
    offDir = tempDir('mfa-off'); onDir = tempDir('mfa-on');
    off = await startGateway({ OrbitDrive__DataDirectory: offDir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR) });
    on = await startGateway({ OrbitDrive__DataDirectory: onDir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR), OrbitDrive__RequireOperatorSecondFactor: 'true' });
  });
  after(async () => { await off?.stop(); await on?.stop(); });

  test('switched off: a plain sign-in reaches the console, so the owner cannot be locked out by default', async () => {
    const op = await signIn(off, OPERATOR);
    const reply = await op.call('GET', '/v1/admin/overview');
    assert.equal(reply.status, 200);
    assert.equal(reply.body.secondFactorRequired, false);
  });

  test('switched on: a sign-in without a second factor is refused; one with it is let in; a stranger is still refused', async () => {
    const plain = await signIn(on, OPERATOR);
    const refused = await plain.call('GET', '/v1/admin/overview');
    assert.equal(refused.status, 403);
    assert.match(JSON.stringify(refused.body), /second factor/i);
    const refusedWrite = await plain.call('PUT', '/v1/admin/settings', { minAgentVersion: '2.0.0', reason: 'should not happen' });
    assert.equal(refusedWrite.status, 403);
    assert.equal(auditText(onDir).includes('should not happen'), false, 'a refused call leaves no change record');
    const strong = await signIn(on, OPERATOR, '?secondFactor=true');
    const ok = await strong.call('GET', '/v1/admin/overview');
    assert.equal(ok.status, 200);
    assert.equal(ok.body.secondFactorRequired, true);
    assert.equal(ok.body.secondFactorPresent, true);
    const stranger = await signIn(on, 'stranger@example.test', '?secondFactor=true');
    assert.equal((await stranger.call('GET', '/v1/admin/overview')).status, 403);
  });
});

describe('operator changes need a reason, and the record comes first', () => {
  let gateway, dir, op;
  before(async () => {
    dir = tempDir('reason-data');
    gateway = await startGateway({ OrbitDrive__DataDirectory: dir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR) });
    op = await signIn(gateway, OPERATOR);
  });
  after(async () => { await gateway?.stop(); });

  test('every change route refuses a missing, empty, too short or too long reason, and nothing changes', async () => {
    const routes = [['PUT', '/v1/admin/settings', { minAgentVersion: '3.0.0' }], ['PUT', '/v1/admin/billing/rate', { pkrPerUsd: 300 }],
      ['PUT', '/v1/admin/billing/limits', { enabled: true }], ['POST', `/v1/admin/users/${uidOf('someone@example.test')}/suspend`, {}]];
    for (const [method, path, body] of routes) {
      for (const reason of [undefined, '', 'ab', 'x'.repeat(201)]) {
        const reply = await op.call(method, path, { ...body, ...(reason === undefined ? {} : { reason }) });
        assert.equal(reply.status, 400, `${method} ${path} with reason ${JSON.stringify(reason)?.slice(0, 12)}`);
      }
    }
    assert.notEqual((await op.call('GET', '/v1/admin/settings')).body.minAgentVersion, '3.0.0');
    assert.equal((await op.call('GET', '/v1/admin/overview')).body.limitsEnabled ?? false, false);
  });

  test('a good reason is written to the record with the change', async () => {
    assert.equal((await op.call('PUT', '/v1/admin/settings', { minAgentVersion: '1.0.0', reason: 'raise the floor for a fix' })).status, 200);
    assert.match(auditText(dir), /raise the floor for a fix/);
    const shown = await op.call('GET', '/v1/admin/audit');
    assert.ok(JSON.stringify(shown.body).includes('raise the floor for a fix'));
  });

  test('when the record cannot be written the change does not happen', async () => {
    // Make the audit file unwritable by replacing it with a directory of the same name.
    const { rmSync, mkdirSync } = await import('node:fs');
    const file = join(dir, 'admin-audit.jsonl');
    rmSync(file, { force: true }); mkdirSync(file);
    const reply = await op.call('PUT', '/v1/admin/settings', { minAgentVersion: '4.0.0', reason: 'must not apply' });
    assert.ok([503, 500].includes(reply.status), `status ${reply.status}`);
    assert.notEqual((await op.call('GET', '/v1/admin/settings')).body.minAgentVersion, '4.0.0');
  });
});

describe('ordering stays closed until the start date, with a single switch', () => {
  let gateway, dir, op, person;
  before(async () => {
    dir = tempDir('order-data');
    gateway = await startGateway({ OrbitDrive__DataDirectory: dir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR) });
    op = await signIn(gateway, OPERATOR);
    person = await signIn(gateway, OWNER);
  });
  after(async () => { await gateway?.stop(); });

  test('quotes, orders and proofs are refused while closed, and work after the operator opens ordering', async () => {
    const plans = await person.call('GET', '/v1/plans');
    assert.equal(plans.status, 200);
    const planId = plans.body.plans.find((p) => p.purchasable && p.prices.length > 0)?.id;
    assert.ok(planId, 'a paid plan exists');
    const closed = await person.call('POST', '/v1/account/orders', { planId, months: 1, method: 'bank' });
    assert.equal(closed.status, 409);
    assert.match(JSON.stringify(closed.body), /not open yet|start date/i);
    assert.equal((await person.call('POST', '/v1/account/quote', { planId, months: 1 })).status, 409);
    assert.equal((await op.call('PUT', '/v1/admin/billing/limits', { enabled: true, reason: 'start date reached' })).status, 200);
    const open = await person.call('POST', '/v1/account/orders', { planId, months: 1, method: 'bank' });
    assert.equal(open.status, 201, JSON.stringify(open.body));
  });
});

describe('account export and delete', () => {
  let gateway, dir, person, agent, state, root;
  before(async () => {
    dir = tempDir('acct-data');
    root = makeFixtureRoot(); state = tempDir('acct-state');
    agentCli(state, 'roots', 'add', 'Share', root);
    gateway = await startGateway({ OrbitDrive__DataDirectory: dir, OrbitDrive__OperatorUids__0: uidOf(OPERATOR) });
    agent = await startAgent(gateway, state, 'Export PC');
    person = await new Browser(gateway, OWNER).signIn();
    await pair(agent, person, 'Export phone');
  });
  after(async () => { await agent?.stop(); await gateway?.stop(); });

  test('export needs a recent sign-in, works with no paired browser, and holds no secrets or file names', async () => {
    const stale = await signIn(gateway, OWNER, '?signedInAgoSeconds=86400');
    assert.equal((await stale.call('GET', '/v1/account/export')).status, 401);
    const fresh = await signIn(gateway, OWNER);
    const reply = await fresh.call('GET', '/v1/account/export');
    assert.equal(reply.status, 200);
    const text = JSON.stringify(reply.body);
    assert.ok(text.includes('Export PC'));
    assert.ok(!text.includes(person.device.secret) && !text.includes(person.token), 'no secrets');
    assert.ok(!text.includes('data.bin'), 'no file names');
    assert.ok(reply.body.billing, 'billing records are part of the export');
  });

  test('delete needs the typed phrase and a recent sign-in, removes the PC and browsers, and the account is empty afterwards', async () => {
    const stale = await signIn(gateway, OWNER, '?signedInAgoSeconds=86400');
    assert.equal((await stale.call('DELETE', '/v1/account', { confirm: 'delete my account' })).status, 401);
    const fresh = await signIn(gateway, OWNER);
    assert.equal((await fresh.call('DELETE', '/v1/account', { confirm: 'yes' })).status, 400);
    assert.equal((await fresh.call('DELETE', '/v1/account', { confirm: 'delete my account' })).status, 200);
    assert.ok([401, 403].includes((await person.get('/v1/pcs')).status), 'the old browser no longer works');
    const after = await signIn(gateway, OWNER);
    const exported = await after.call('GET', '/v1/account/export');
    assert.equal(exported.status, 200);
    assert.ok(!JSON.stringify(exported.body).includes('Export PC'), 'the PC is released');
  });
});

describe('the relay keeps no file names (F-003, decision a)', () => {
  let gateway, dir, agent, ipc, phone, state, outside;
  before(async () => {
    dir = tempDir('names-data');
    outside = tempDir('names-outside'); state = tempDir('names-state');
    const root = makeFixtureRoot(); agentCli(state, 'roots', 'add', 'Share', root);
    gateway = await startGateway({ OrbitDrive__DataDirectory: dir });
    agent = await startAgent(gateway, state, 'Names PC');
    ipc = await connectIpc(agent.ready.pipe);
    phone = await new Browser(gateway, OWNER).signIn();
    await pair(agent, phone, 'Phone');
  });
  after(async () => { ipc?.close(); await agent?.stop(); await gateway?.stop(); });

  test('an offered file shows its name in the Inbox, yet the name is in no file the relay writes', async () => {
    const name = 'quarterly-secret-plan-zebra.txt';
    writeFileSync(join(outside, name), 'hello');
    const reply = await ipc.send('send_files', { receiverId: phone.device.id, paths: [join(outside, name)] });
    assert.ok(reply.ok, JSON.stringify(reply));
    await waitFor(async () => (await phone.get('/v1/invitations')).body.invitations.length === 1, 15000, 'the offer to reach the Inbox');
    const shown = (await phone.get('/v1/invitations')).body.invitations[0];
    assert.equal(shown.name, name, 'the Inbox still shows the real name (the PC supplies it)');
    // Give the relay time to persist, then look at every file it wrote.
    await new Promise((r) => setTimeout(r, 1500));
    for (const file of readdirSync(dir).filter((f) => f !== 'relay.lock')) {     // the lock file is held open by the relay (and holds no data)
      const bytes = readFileSync(join(dir, file));
      assert.ok(!bytes.includes(Buffer.from(name)), `${file} must not hold the file name`);
    }
  });
});

describe('the relay enforces its own upload piece cap (F-022)', () => {
  let gateway, agent, phone, state, drop, dropId, pcId;
  before(async () => {
    drop = tempDir('cap-drop'); state = tempDir('cap-state'); mkdirSync(join(drop, 'Inbox'));
    dropId = agentCli(state, 'roots', 'add', 'Drop', drop).match(/\(([A-Za-z0-9_-]+)\)/)[1];
    agentCli(state, 'roots', 'uploads', dropId, 'on', '50', '10');
    // The relay's cap (1 KiB here) is far below the PC's own cap (8 MiB), so only the relay's check can refuse a 2 KiB piece.
    gateway = await startGateway({ OrbitDrive__MaxUploadChunkBytes: '1024' });
    agent = await startAgent(gateway, state, 'Cap PC');
    pcId = agent.identity.pcId;
    phone = await new Browser(gateway, OWNER).signIn();
    await pair(agent, phone, 'Phone');
  });
  after(async () => { await agent?.stop(); await gateway?.stop(); });

  test('a piece over the relay cap is refused as too large and nothing reaches the PC; a piece within it is accepted', async () => {
    const page = await phone.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: dropId, pageSize: 50 });
    const inbox = page.body.entries.find((e) => e.name === 'Inbox').entryId;
    const started = await phone.post(`/v1/pcs/${pcId}/uploads`, { rootId: dropId, folderEntryId: inbox, name: 'cap.bin', size: '4096', collision: 'reject' });
    assert.equal(started.status, 201, JSON.stringify(started.body));
    const put = async (bytes) => {
      const response = await fetch(`${gateway.url}/v1/pcs/${pcId}/uploads/${started.body.uploadId}/chunk?offset=0`, {
        method: 'PUT', headers: phone.headers({ 'x-chunk-sha256': createHash('sha256').update(bytes).digest('hex'), 'content-type': 'application/octet-stream' }), body: bytes });
      return response.status;
    };
    assert.equal(await put(Buffer.alloc(2048, 7)), 413);
    const status = await phone.post(`/v1/pcs/${pcId}/upload-status`, { uploadId: started.body.uploadId });
    assert.equal(status.body.committedBytes, '0', 'nothing of the refused piece was stored');
    assert.equal(await put(Buffer.alloc(1024, 7)), 200, 'a piece within the cap still works');
    await phone.post(`/v1/pcs/${pcId}/upload-cancel`, { uploadId: started.body.uploadId });
  });
});
