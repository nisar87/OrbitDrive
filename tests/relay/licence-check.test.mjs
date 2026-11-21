import { test } from 'node:test';
import assert from 'node:assert/strict';
import { acceptable, review } from '../../scripts/licence-check.mjs';

test('permissive licences and OR choices with an allowed option are accepted', () => {
  for (const ok of ['MIT', 'Apache-2.0', '(MIT OR CC0-1.0)', '(BSD-2-Clause OR MIT OR Apache-2.0)', 'MIT AND ISC']) assert.equal(acceptable(ok), true, ok);
});
test('strong copyleft, network copyleft and unknown licences are refused', () => {
  for (const bad of ['GPL-3.0', 'AGPL-3.0-only', 'SSPL-1.0', 'LGPL-2.1', 'UNLICENSED', 'SEE LICENSE IN LICENSE.md', '(GPL-2.0 OR GPL-3.0)', 'MIT AND GPL-3.0']) assert.equal(acceptable(bad), false, bad);
});
test('every committed lock file passes the review', () => {
  const { problems } = review();
  assert.deepEqual(problems, []);
});
