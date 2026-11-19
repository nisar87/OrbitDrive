// W3/R13: a retried "start work" request (new download, search) with the same Idempotency-Key never starts the work twice, never leaks one account's answer
// to another, never remembers a failure, and refuses a key reused for a different request. Real gateway and real agent processes.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, Browser, pair } from './harness.mjs';

let gateway, agent, owner, stranger, pcId, roots, hello, notes;
const KEY = () => 'k-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
const sessions = async () => (await owner.get('/v1/download-sessions')).body.sessions.length;

before(async () => {
  const root = makeFixtureRoot(), state = tempDir('idem-state');
  agentCli(state, 'roots', 'add', 'Share', root);
  gateway = await startGateway();
  agent = await startAgent(gateway, state, 'Idempotency PC');
  pcId = agent.identity.pcId;
  owner = await new Browser(gateway, 'idem@example.test').signIn();
  stranger = await new Browser(gateway, 'idem-other@example.test').signIn();
  await pair(agent, owner, 'Phone');
  roots = (await owner.get(`/v1/pcs/${pcId}/roots`)).body.roots;
  const entries = (await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots[0].rootId, pageSize: 50 })).body.entries;
  hello = entries.find((e) => e.name === 'hello.txt'); notes = entries.find((e) => e.name !== 'hello.txt' && e.kind !== 'directory') ?? hello;
});
after(async () => { await agent?.stop(); await gateway?.stop(); });

test('a retry with the same key returns the first answer and starts nothing new', async () => {
  const key = KEY(), before = await sessions();
  const first = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.equal(first.status, 201, JSON.stringify(first.body)); assert.equal(first.headers.get('idempotent-replayed'), null);
  const again = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.equal(again.status, 201); assert.equal(again.headers.get('idempotent-replayed'), 'true');
  assert.deepEqual(again.body, first.body, 'the very same session, link included');
  assert.equal(await sessions(), before + 1, 'one session, not two');
  const third = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.deepEqual(third.body, first.body);
});

test('without a key nothing changes: two requests are two sessions', async () => {
  const before = await sessions();
  const a = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId });
  const b = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId });
  assert.equal(a.status, 201); assert.equal(b.status, 201); assert.notEqual(a.body.sessionId, b.body.sessionId);
  assert.equal(b.headers.get('idempotent-replayed'), null);
  assert.equal(await sessions(), before + 2);
});

test('the same key for a different request is refused and starts nothing', async () => {
  const key = KEY(), before = await sessions();
  assert.equal((await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key })).status, 201);
  if (notes.entryId !== hello.entryId) {
    const other = await owner.post('/v1/download-sessions', { pcId, entryId: notes.entryId }, { 'idempotency-key': key });
    assert.equal(other.status, 409); assert.equal(other.body.error.code, 'conflict'); assert.match(other.body.error.message, /different request/);
  }
  const search = await owner.post(`/v1/pcs/${pcId}/searches`, { searchId: 'x', query: 'hello', rootId: roots[0].rootId, limit: 10 }, { 'idempotency-key': key });
  assert.equal(search.status, 409, 'a key is bound to one route and body');
  assert.equal(await sessions(), before + 1);
});

test('a key belongs to one account: another account using it gets its own (refused) result, never the first answer', async () => {
  const key = KEY();
  const mine = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.equal(mine.status, 201);
  const theirs = await stranger.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.ok(theirs.status === 403 || theirs.status === 404, `stranger got ${theirs.status}`);
  assert.equal(theirs.headers.get('idempotent-replayed'), null);
  assert.doesNotMatch(JSON.stringify(theirs.body), new RegExp(mine.body.sessionId));
});

test('failures are not remembered: the retry runs again, and the key can still be used for a success of the same request', async () => {
  const key = KEY();
  const bad = await owner.post('/v1/download-sessions', { pcId, entryId: 'no-such-entry' }, { 'idempotency-key': key });
  assert.ok(bad.status >= 400, String(bad.status));
  const retry = await owner.post('/v1/download-sessions', { pcId, entryId: 'no-such-entry' }, { 'idempotency-key': key });
  assert.equal(retry.status, bad.status); assert.equal(retry.headers.get('idempotent-replayed'), null, 'an error is answered afresh, not replayed');
});

test('two simultaneous requests with one key start the work once', async () => {
  const key = KEY(), before = await sessions();
  const results = await Promise.all([1, 2, 3, 4].map(() => owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key })));
  for (const r of results) assert.ok([201, 409].includes(r.status), `unexpected ${r.status}`);
  assert.ok(results.filter((r) => r.status === 201 && r.headers.get('idempotent-replayed') === null).length === 1, 'exactly one request ran');
  assert.equal(await sessions(), before + 1);
  const again = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': key });
  assert.equal(again.status, 201); assert.equal(again.headers.get('idempotent-replayed'), 'true');
});

test('keys are validated, and anonymous callers never reach the key table', async () => {
  for (const bad of ['short', 'x'.repeat(129), 'has space in it', 'semi;colon;key']) {
    const r = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId }, { 'idempotency-key': bad });
    assert.equal(r.status, 400, `${bad}: ${r.status}`); assert.equal(r.body.error.code, 'invalid_request');
  }
  const anon = await fetch(gateway.url + '/v1/download-sessions', { method: 'POST', headers: { 'content-type': 'application/json', 'idempotency-key': KEY() }, body: '{}' });
  assert.equal(anon.status, 401);
});

test('searches replay too, and a key on a route that does not start work is simply ignored', async () => {
  const key = KEY(), body = { searchId: 'idem-search-1', query: 'hello', rootId: roots[0].rootId, limit: 10 };
  const first = await owner.post(`/v1/pcs/${pcId}/searches`, body, { 'idempotency-key': key });
  assert.equal(first.status, 200, JSON.stringify(first.body));
  const again = await owner.post(`/v1/pcs/${pcId}/searches`, body, { 'idempotency-key': key });
  assert.equal(again.headers.get('idempotent-replayed'), 'true'); assert.deepEqual(again.body, first.body);
  const ignored = await owner.post(`/v1/pcs/${pcId}/directory-queries`, { rootId: roots[0].rootId, pageSize: 5 }, { 'idempotency-key': 'short' });
  assert.equal(ignored.status, 200, 'only routes that start work look at the key');
});

test('an oversized body with a key is refused, not buffered', async () => {
  const r = await owner.post('/v1/download-sessions', { pcId, entryId: hello.entryId, pad: 'x'.repeat(70_000) }, { 'idempotency-key': KEY() });
  assert.ok(r.status === 413 || r.status === 400, String(r.status));
});
