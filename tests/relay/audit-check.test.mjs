// The dependency-audit gate must never become a rubber stamp. Every way an exception could quietly widen is refused.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { judge, rootAdvisories } from '../../scripts/audit-check.mjs';

const report = {
  '@vitejs/plugin-react': { severity: 'high', via: ['vite'] },
  vite: { severity: 'high', via: ['picomatch', 'tinyglobby'] },
  vitest: { severity: 'high', via: ['picomatch', 'tinyglobby', 'vite'] },
  tinyglobby: { severity: 'high', via: ['fdir', 'picomatch'] },
  fdir: { severity: 'high', via: ['picomatch'] },
  picomatch: { severity: 'high', via: [{ url: 'https://github.com/advisories/GHSA-fc5q-39xc-vjpf', title: 'ReDoS', severity: 'high' }] },
  chalk: { severity: 'moderate', via: [{ url: 'https://github.com/advisories/GHSA-mmmm-mmmm-mmmm', title: 'minor', severity: 'moderate' }] },
};
const exception = {
  advisory: 'GHSA-fc5q-39xc-vjpf', package: 'picomatch', directory: 'apps/web', allowedVia: ['vite', 'vitest', 'tinyglobby', 'fdir', '@vitejs/plugin-react'],
  reviewedLatest: '4.0.7', authorisedBy: 'owner', reason: 'build tooling only', added: '2026-11-18', expires: '2026-12-17',
};
const latest = (version) => () => version;
const run = (overrides = {}, { rep = report, dir = 'apps/web', today = '2026-11-18', version = '4.0.7' } = {}) =>
  judge(rootAdvisories(rep), [{ ...exception, ...overrides }], dir, today, latest(version));

test('finds the root advisory once, with every package that pulls it in, and ignores lower severities', () => {
  const findings = rootAdvisories(report);
  assert.deepEqual(findings.map((f) => [f.id, f.package]), [['GHSA-fc5q-39xc-vjpf', 'picomatch']]);
  assert.deepEqual(findings[0].via, ['@vitejs/plugin-react', 'fdir', 'tinyglobby', 'vite', 'vitest']);
});

test('the reviewed exception is accepted exactly as reviewed', () => {
  const result = run();
  assert.deepEqual([result.accepted.length, result.blocking.length], [1, 0]);
  assert.equal(run({}, { today: '2026-12-17' }).accepted.length, 1, 'valid on its last day');
});

test('anything outside the reviewed situation blocks again', () => {
  const blocked = (result, pattern) => { assert.equal(result.accepted.length, 0); assert.match(result.blocking[0].why, pattern); };
  blocked(run({}, { today: '2026-12-18' }), /expired/);
  blocked(run({ expires: '2027-06-01' }), /longer than 30 days/);
  blocked(run({ expires: 'never' }), /real added\/expires dates/);
  blocked(run({ added: '2026-12-01', expires: '2026-12-17' }), /future/);
  blocked(run({ reason: '' }), /no reason/);
  blocked(run({ authorisedBy: '' }), /no owner authorisation/);
  blocked(run({}, { dir: 'tests/integration' }), /apps\/web only/);
  blocked(run({ package: 'minimatch' }), /package minimatch/);
  blocked(run({}, { version: '4.0.8' }), /4\.0\.8 has been released/);          // a fix may exist: re-review
  blocked(run({ allowedVia: ['vite', 'vitest', 'tinyglobby', 'fdir'] }), /@vitejs\/plugin-react, which was not reviewed/);
  const withNewDependent = { ...report, express: { severity: 'high', via: ['picomatch'] } };  // e.g. a runtime library starts using it
  blocked(run({}, { rep: withNewDependent }), /express, which was not reviewed/);
});

test('an advisory nobody reviewed always blocks, whatever else is accepted', () => {
  const other = { ...report, lodash: { severity: 'critical', via: [{ url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc', title: 'RCE', severity: 'critical' }] } };
  const result = run({}, { rep: other });
  assert.deepEqual(result.accepted.map((f) => f.id), ['GHSA-fc5q-39xc-vjpf']);
  assert.deepEqual(result.blocking.map((f) => [f.id, f.why]), [['GHSA-aaaa-bbbb-cccc', 'not on the exception list']]);
  assert.equal(judge(rootAdvisories(report), [], 'apps/web', '2026-11-18', latest('4.0.7')).blocking.length, 1);
});

test('the committed exception file holds exactly one entry, for this advisory, within the 30-day limit', () => {
  const file = JSON.parse(readFileSync(new URL('../../security/audit-exceptions.json', import.meta.url), 'utf8'));
  assert.deepEqual(file.exceptions.map((e) => [e.advisory, e.package, e.directory]), [['GHSA-fc5q-39xc-vjpf', 'picomatch', 'apps/web']]);
  const [entry] = file.exceptions;
  assert.ok((Date.parse(entry.expires) - Date.parse(entry.added)) / 86_400_000 <= 30);
  assert.ok(entry.reason.length > 80 && entry.authorisedBy.includes('owner') && entry.removeWhen.length > 20);
});
