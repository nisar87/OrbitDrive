// Billing over HTTP with a real gateway process and a real operator identity (F-008): plans, orders, payment references, coupons, passes and organisations,
// and the null / missing / wrong-type bodies that used to return server errors or act without a record (F-006). Logic is also covered by the C# unit tests;
// this file covers the wiring between the two: who may call, status codes, and what is written down.
import { test, before, after, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, tempDir } from './harness.mjs';

const uidOf = (email) => 'dev-' + createHash('sha256').update(email.toLowerCase()).digest('hex').slice(0, 20);
const OP = 'billing-op@example.test', ALICE = 'alice@example.test', BOB = 'bob@example.test', CAROL = 'carol@example.test';
let gateway, dir, op, alice, bob, carol;
const signIn = async (email) => {
  const response = await fetch(`${gateway.url}/v1/dev/sign-in`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email }) });
  const body = await response.json();
  return { email, token: body.idToken, uid: body.uid, headers: (extra = {}) => ({ authorization: `Bearer ${body.idToken}`, ...extra }),
    call: (method, path, data) => fetch(gateway.url + path, { method, headers: { authorization: `Bearer ${body.idToken}`, ...(data === undefined ? {} : { 'content-type': 'application/json' }) }, body: data === undefined ? undefined : JSON.stringify(data) })
      .then(async (r) => { const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ } return { status: r.status, body: json, text }; }) };
};
const audit = () => (existsSync(join(dir, 'admin-audit.jsonl')) ? readFileSync(join(dir, 'admin-audit.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []);

before(async () => {
  dir = tempDir('billing-http');
  gateway = await startGateway({ OrbitDrive__DataDirectory: dir, OrbitDrive__OperatorUids__0: uidOf(OP) });
  [op, alice, bob, carol] = await Promise.all([signIn(OP), signIn(ALICE), signIn(BOB), signIn(CAROL)]);
});
after(async () => { await gateway?.stop(); });

describe('who may call', () => {
  test('plans are public; an account\'s plan, orders and usage need a sign-in; the operator routes need an operator', async () => {
    assert.equal((await fetch(`${gateway.url}/v1/plans`)).status, 200);
    for (const path of ['/v1/account/plan', '/v1/account/orders', '/v1/account/usage', '/v1/account/org']) assert.equal((await fetch(gateway.url + path)).status, 401, path);
    for (const [method, path] of [['GET', '/v1/admin/billing'], ['GET', '/v1/admin/orders'], ['GET', '/v1/admin/coupons'], ['PUT', '/v1/admin/billing/limits'], ['POST', '/v1/admin/orders/x/verify']]) {
      assert.equal((await alice.call(method, path, method === 'GET' ? undefined : {})).status, 403, `${method} ${path} as an ordinary account`);
    }
  });
});

describe('ordering opens with the start date, then orders, references and verification work', () => {
  let order;
  test('before the start date a customer cannot order; the operator opens ordering with a reason and the change is recorded', async () => {
    const plans = (await alice.call('GET', '/v1/plans')).body;
    assert.equal(plans.limitsEnabled, false);
    assert.equal((await alice.call('POST', '/v1/account/orders', { planId: 'plus', months: 1, method: 'bank' })).status, 409);
    assert.equal((await op.call('PUT', '/v1/admin/billing/limits', { enabled: true })).status, 400, 'no reason, no change');
    assert.equal((await op.call('PUT', '/v1/admin/billing/limits', { enabled: true, reason: 'start date reached' })).status, 200);
    assert.ok(audit().some((e) => e.action === 'limits-on' && /start date reached/.test(e.detail)));
  });

  test('an order is private to its owner; the payment reference is checked; the same reference cannot pay two orders', async () => {
    const created = await alice.call('POST', '/v1/account/orders', { planId: 'plus', months: 1, method: 'bank' });
    assert.equal(created.status, 201, created.text);
    order = created.body;
    assert.equal((await bob.call('POST', `/v1/account/orders/${order.id}/proof`, { method: 'bank', reference: 'TXN-778899' })).status, 404, 'another account cannot touch it');
    assert.equal((await alice.call('POST', `/v1/account/orders/${order.id}/proof`, { method: 'cheque', reference: 'TXN-778899' })).status, 400);
    assert.equal((await alice.call('POST', `/v1/account/orders/${order.id}/proof`, { method: 'bank', reference: 'ab' })).status, 400);
    assert.equal((await alice.call('POST', `/v1/account/orders/${order.id}/proof`, { method: 'bank', reference: 'TXN-778899' })).status, 200);
    const bobs = (await bob.call('POST', '/v1/account/orders', { planId: 'plus', months: 1, method: 'bank' })).body;
    const reuse = await bob.call('POST', `/v1/account/orders/${bobs.id}/proof`, { method: 'bank', reference: ' txn 778899 ' });
    assert.equal(reuse.status, 409, 'the same reference, spelled differently, is still the same payment');
        assert.ok((await bob.call('GET', '/v1/account/orders')).body.every((o) => !o.sameReferenceAs), 'customers never see the owner-only flag');
  });

  test('the operator sees the order, verifies it with a reason, and the account is on the plan; a second verify is refused', async () => {
    const listed = (await op.call('GET', '/v1/admin/orders')).body.find((o) => o.id === order.id);
    assert.equal(listed.status, 'proof_submitted');
    assert.equal((await op.call('POST', `/v1/admin/orders/${order.id}/verify`, {})).status, 400, 'a decision without a reason is refused');
    assert.equal((await op.call('POST', `/v1/admin/orders/${order.id}/verify`, { reason: 'money received' })).status, 200);
    assert.equal((await alice.call('GET', '/v1/account/plan')).body.planId, 'plus');
    assert.ok([400, 409].includes((await op.call('POST', `/v1/admin/orders/${order.id}/verify`, { reason: 'again' })).status));
    assert.ok(audit().some((e) => e.action === 'verify-order' && /money received/.test(e.detail)));
  });
});

describe('coupons: one use per account, counted when an order is verified', () => {
  test('one account placing two unpaid orders no longer uses up a two-use code; a verified use is not offered to the same account again', async () => {
    const saved = await op.call('PUT', '/v1/admin/coupons/save10', { kind: 'percent', value: 10, planIds: ['plus'], maxRedemptions: 2, active: true, note: 'test', reason: 'launch offer' });
    assert.equal(saved.status, 200, saved.text);
    const first = await carol.call('POST', '/v1/account/orders', { planId: 'plus', months: 1, coupon: 'SAVE10', method: 'bank' });
    assert.equal(first.status, 201, first.text);
    const second = await carol.call('POST', '/v1/account/orders', { planId: 'plus', months: 3, coupon: 'SAVE10', method: 'bank' });
    assert.ok(second.status >= 400 && second.status < 500, `the same account cannot hold the code twice (${second.status})`);
    const other = await bob.call('POST', '/v1/account/quote', { planId: 'plus', months: 3, coupon: 'SAVE10' });
    assert.equal(other.status, 200, 'the second use is still there for someone else: ' + other.text);
    assert.equal((await carol.call('POST', `/v1/account/orders/${first.body.id}/proof`, { method: 'jazzcash', reference: 'JC-5550001' })).status, 200);
    assert.equal((await op.call('POST', `/v1/admin/orders/${first.body.id}/verify`, { reason: 'paid by wallet' })).status, 200);
    const again = await carol.call('POST', '/v1/account/quote', { planId: 'plus', months: 3, coupon: 'SAVE10' });
    assert.ok(again.status >= 400 && again.status < 500, `a verified use is not offered to the same account again (${again.status})`);
    const coupon = (await op.call('GET', '/v1/admin/coupons')).body.find((c) => c.code === 'SAVE10');
    assert.equal(coupon.redeemed, 1, 'the use is counted at verification');
    assert.equal((await op.call('PUT', '/v1/admin/coupons/save10', { kind: 'percent', value: 10, active: true })).status, 400, 'no reason, no change');
  });
});

describe('hostile bodies on operator and billing routes: refused cleanly, nothing happens without a record (F-006)', () => {
  test('null, missing and wrong-type fields never produce a server error and never change anything', async () => {
    const before = audit().length;
    const bodies = [{ reason: null }, { reason: 5 }, { reason: [] }, {}, { reason: '' }, { reason: 'x'.repeat(500) }, null, 'text', 7];
    const routes = [['PUT', '/v1/admin/billing/plans'], ['PUT', '/v1/admin/billing/rate'], ['PUT', '/v1/admin/billing/limits'], ['PUT', '/v1/admin/billing/instructions'],
      ['PUT', '/v1/admin/coupons/hostile'], ['POST', '/v1/admin/orders/none/verify'], ['POST', '/v1/admin/orders/none/reject'], ['POST', `/v1/admin/users/${uidOf(BOB)}/passes`],
      ['POST', `/v1/admin/users/${uidOf(BOB)}/suspend`], ['PUT', '/v1/admin/settings']];
    for (const [method, path] of routes) {
      for (const body of bodies) {
        const reply = await op.call(method, path, body);
        assert.ok(reply.status >= 400 && reply.status < 500, `${method} ${path} with ${JSON.stringify(body)?.slice(0, 30)} gave ${reply.status}`);
      }
    }
    assert.equal(audit().length, before, 'refused calls leave no change record');
    assert.equal((await op.call('GET', `/v1/admin/users/${uidOf(BOB)}/billing`)).status, 200);
    assert.equal((await fetch(`${gateway.url}/healthz`)).status, 200, 'the relay is still up');
  });

  test('customer routes refuse null and wrong-type bodies the same way', async () => {
    for (const [path, body] of [['/v1/account/orders', null], ['/v1/account/orders', { planId: 5, months: 'x' }], ['/v1/account/orders', { planId: 'plus', months: -3 }], ['/v1/account/quote', {}], ['/v1/account/org', { name: null }], ['/v1/account/org/join', { code: 5 }]]) {
      const reply = await alice.call('POST', path, body);
      assert.ok(reply.status >= 400 && reply.status < 500, `${path} ${JSON.stringify(body)} gave ${reply.status}`);
    }
  });
});

describe('organisations over HTTP', () => {
  test('an Office owner invites; a member joins; a member cannot act as an admin; an outsider sees nothing', async () => {
    assert.equal((await op.call('POST', `/v1/admin/users/${uidOf(ALICE)}/passes`, { planId: 'office', months: 1, reason: 'test office pass', source: 'grant' })).status, 201);
    const created = await alice.call('POST', '/v1/account/org', { name: 'Test Office' });
    assert.equal(created.status, 201, created.text);
    const invite = (await alice.call('POST', '/v1/account/org/invite', {})).body;
    assert.ok(invite.code);
    assert.equal((await bob.call('POST', '/v1/account/org/join', { code: invite.code, label: 'Bob' })).status, 200);
    assert.equal((await bob.call('POST', '/v1/account/org/join', { code: invite.code, label: 'Bob again' })).status >= 400, true, 'an invitation works once');
    assert.equal((await bob.call('POST', '/v1/account/org/invite', {})).status, 403, 'a plain member cannot invite');
    assert.equal((await bob.call('POST', `/v1/account/org/members/${alice.uid}/remove`, {})).status, 403);
    assert.equal((await carol.call('GET', '/v1/account/org')).body.org, null, 'an outsider is in no organisation');
    assert.equal((await carol.call('GET', `/v1/account/org/members/${alice.uid}/usage`)).status >= 400, true);
    const view = (await alice.call('GET', '/v1/account/org')).body;
    assert.ok(!JSON.stringify(view).includes('@example.test'), 'members are shown by label, not email address');
  });
});
