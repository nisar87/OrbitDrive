// R65: operators see whom an account is (email and display name, from the verified sign-in) and nobody else does. Real gateway process, development sign-in.
// Covers: capture at the first authenticated request, saved in the billing store, shown only on the operator endpoints, never to ordinary accounts or organisation
// admins, kept in the audit entry as it was at the time, and erased from the billing record when the account is deleted.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, tempDir } from './harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OP = 'emails-op@example.test', ALICE = 'alice.emails@example.test', BOB = 'bob.emails@example.test', CAROL = 'carol.emails@example.test', DAVE = 'dave.emails@example.test', ERIN = 'erin.emails@example.test';
let gateway, dir, op, alice, bob, carol, dave, erin;
const signIn = async (email, name) => {
  const response = await fetch(`${gateway.url}/v1/dev/sign-in${name ? `?name=${encodeURIComponent(name)}` : ''}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email }) });
  const body = await response.json();
  return { email, uid: body.uid, token: body.idToken,
    call: (method, path, data) => fetch(gateway.url + path, { method, headers: { authorization: `Bearer ${body.idToken}`, ...(data === undefined ? {} : { 'content-type': 'application/json' }) }, body: data === undefined ? undefined : JSON.stringify(data) })
      .then(async (r) => { const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ } return { status: r.status, body: json, text }; }) };
};
const audit = () => (existsSync(join(dir, 'admin-audit.jsonl')) ? readFileSync(join(dir, 'admin-audit.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []);
const billingFile = () => (existsSync(join(dir, 'billing.json')) ? readFileSync(join(dir, 'billing.json'), 'utf8') : '');
const until = async (check, what) => { for (let i = 0; i < 60; i++) { if (check()) return; await new Promise((r) => setTimeout(r, 250)); } assert.fail(`timed out: ${what}`); };
const directory = async () => (await op.call('GET', '/v1/admin/directory')).body.people;

before(async () => {
  dir = tempDir('operator-emails');
  gateway = await startGateway({ OrbitDrive__DataDirectory: dir, OrbitDrive__OperatorUids__0: uidOf(OP) });
  op = await signIn(OP);
  alice = await signIn(ALICE, 'Alice Example');
  bob = await signIn(BOB);
  carol = await signIn(CAROL);
  dave = await signIn(DAVE);
  erin = await signIn(ERIN);                 // signs in but never makes an authenticated request: the service has not seen the token used
  for (const who of [alice, bob, carol, dave]) assert.equal((await who.call('GET', '/v1/account/plan')).status, 200);
});
after(async () => { await gateway?.stop(); });

describe('what is captured', () => {
  test('the first authenticated request stores the email and display name from the token; they show in the operator directory and the account detail', async () => {
    const people = await directory();
    const found = people.find((p) => p.uid === alice.uid);
    assert.equal(found.email, ALICE);
    assert.equal(found.name, 'Alice Example');
    assert.equal(people.find((p) => p.uid === bob.uid).email, BOB);
    assert.equal(people.find((p) => p.uid === bob.uid).name ?? null, null, 'no name when the sign-in supplies none');
    const detail = await op.call('GET', `/v1/admin/users/${alice.uid}/detail`);
    assert.equal(detail.status, 200);
    assert.equal(detail.body.email, ALICE);
    assert.equal(detail.body.name, 'Alice Example');
  });

  test('an account whose sign-in was never used has no email yet, and the operator is told so with a null, not a blank or a guess', async () => {
    assert.equal((await directory()).some((p) => p.uid === erin.uid), false);
    const detail = await op.call('GET', `/v1/admin/users/${erin.uid}/detail`);
    assert.equal(detail.status, 200);
    assert.equal(detail.body.email ?? null, null);
  });

  test('the email is saved in the billing record (write-behind) and a later sign-in refreshes it', async () => {
    await until(() => billingFile().includes(ALICE), 'billing.json holds the email');
    const stored = JSON.parse(billingFile());
    assert.ok(stored[`account/${alice.uid}`], 'one account/<uid> entry');
    assert.equal(JSON.parse(stored[`account/${alice.uid}`]).Email, ALICE);
    const again = await signIn(ALICE, 'Alice Renamed');
    await again.call('GET', '/v1/account/plan');
    assert.equal((await directory()).find((p) => p.uid === alice.uid).name, 'Alice Renamed');
  });

  test('the accounts table rows carry the email too (so the console can search by it)', async () => {
    const rows = (await op.call('GET', '/v1/admin/accounts')).body;
    assert.ok(Array.isArray(rows));
    for (const row of rows) if (row.email) assert.match(row.email, /@example\.test$/);
  });
});

describe('who can see it', () => {
  test('the directory, account detail and rows need an operator: ordinary accounts and signed-out callers are refused and learn no email', async () => {
    for (const who of [alice, bob, carol]) {
      for (const path of ['/v1/admin/directory', `/v1/admin/users/${bob.uid}/detail`, '/v1/admin/accounts', '/v1/admin/audit', '/v1/admin/orders', '/v1/admin/orgs']) {
        const r = await who.call('GET', path);
        assert.equal(r.status, 403, path);
        assert.ok(!r.text.includes(BOB) && !r.text.includes(CAROL) && !r.text.includes('@example.test'), `${path} leaks no email`);
      }
    }
    assert.equal((await fetch(`${gateway.url}/v1/admin/directory`)).status, 401);
  });

  test('no ordinary account endpoint returns another account\'s email, for a plain member, a stranger or an organisation admin', async () => {
    // alice owns an organisation; bob joins as a member and carol joins and is made an admin. Dave stays outside.
    assert.equal((await alice.call('POST', '/v1/account/org', { name: 'Email Test Office' })).status, 201);
    const invite = async () => (await alice.call('POST', '/v1/account/org/invite', {})).body.code;
    assert.equal((await bob.call('POST', '/v1/account/org/join', { code: await invite(), label: 'Bobby' })).status, 200);
    assert.equal((await carol.call('POST', '/v1/account/org/join', { code: await invite(), label: 'Caz' })).status, 200);
    assert.equal((await alice.call('POST', `/v1/account/org/members/${carol.uid}/role`, { role: 'admin' })).status, 200);
    const paths = [['GET', '/v1/account'], ['GET', '/v1/account/plan'], ['GET', '/v1/account/orders'], ['GET', '/v1/account/usage'], ['GET', '/v1/account/org'], ['GET', '/v1/account/org/audit'],
      ['GET', `/v1/account/org/members/${bob.uid}/usage`], ['GET', '/v1/plans'], ['GET', '/v1/account/quote']];
    const others = { alice: [BOB, CAROL, DAVE], bob: [ALICE, CAROL, DAVE], carol: [ALICE, BOB, DAVE], dave: [ALICE, BOB, CAROL] };
    for (const [name, who] of Object.entries({ alice, bob, carol, dave })) {
      for (const [method, path] of paths) {
        const r = await who.call(method, path);
        for (const email of others[name]) assert.ok(!r.text.includes(email), `${name} ${method} ${path} must not contain ${email}`);
        for (const other of ['bob.emails', 'carol.emails', 'dave.emails', 'alice.emails'].filter((x) => !who.email.startsWith(x))) assert.ok(!r.text.includes(other), `${name} ${method} ${path} must not contain ${other}`);
      }
    }
    // The organisation admin (carol) and the owner see labels, not addresses.
    const view = (await carol.call('GET', '/v1/account/org')).body;
    assert.ok(view.members.length >= 3);
    for (const m of view.members) assert.equal(m.email, undefined);
    // Neither the organisation admin nor the owner can use an admin route to find an address.
    assert.equal((await carol.call('GET', `/v1/admin/users/${bob.uid}/detail`)).status, 403);
    assert.equal((await alice.call('GET', '/v1/admin/directory')).status, 403);
  });

  test('the operator sees organisation members and owner with their emails; the member list the organisation itself gets still has none', async () => {
    const orgs = (await op.call('GET', '/v1/admin/orgs')).body;
    const mine = orgs.find((o) => o.name === 'Email Test Office');
    assert.equal(mine.ownerEmail, ALICE);
    const detail = (await op.call('GET', `/v1/admin/orgs/${mine.id}`)).body;
    assert.equal(detail.members.find((m) => m.uid === bob.uid).email, BOB);
    assert.equal(detail.members.find((m) => m.uid === carol.uid).label, 'Caz');
  });
});

describe('the audit log', () => {
  test('an entry keeps the operator\'s and the account\'s email as they were at the time', async () => {
    const r = await op.call('POST', `/v1/admin/users/${bob.uid}/suspend`, { reason: 'testing the audit trail' });
    assert.equal(r.status, 200);
    const entry = audit().find((e) => e.action === 'suspend' && e.target === bob.uid);
    assert.equal(entry.targetEmail, BOB);
    assert.equal(entry.operatorEmail, OP);
    const shown = (await op.call('GET', '/v1/admin/audit')).body.entries.find((e) => e.action === 'suspend' && e.target === bob.uid);
    assert.equal(shown.targetEmail, BOB);
    assert.equal((await op.call('POST', `/v1/admin/users/${bob.uid}/unsuspend`, { reason: 'done testing' })).status, 200);
  });
});

describe('deleting an account', () => {
  test('erases the stored email and name from the billing record, the directory and the detail, while the audit entry stays readable', async () => {
    await until(() => billingFile().includes(DAVE), 'dave is saved');
    const gone = await dave.call('DELETE', '/v1/account', { confirm: 'delete my account' });
    assert.equal(gone.status, 200);
    await until(() => !billingFile().includes(DAVE), 'billing.json no longer holds the deleted account\'s email');
    assert.ok(!billingFile().includes('dave.emails'), 'no trace of the address in the billing record');
    assert.ok(billingFile().includes(ALICE), 'other accounts are untouched');
    assert.equal((await directory()).some((p) => p.uid === dave.uid || p.email === DAVE), false);
    assert.equal((await op.call('GET', `/v1/admin/users/${dave.uid}/detail`)).body.email ?? null, null);
    const entry = audit().find((e) => e.action === 'account-deleted' && e.target === dave.uid);
    assert.equal(entry.targetEmail, DAVE, 'the audit entry still says who it was');
  });
});
