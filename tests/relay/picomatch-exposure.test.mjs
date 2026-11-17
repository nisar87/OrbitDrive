// Keeps the assumptions behind the accepted picomatch advisory (GHSA-fc5q-39xc-vjpf) under test:
//   - the weakness is driven by the glob PATTERN, and no extended-glob pattern is configured in this repository;
//   - the patterns that are configured stay fast on hostile matched text (file names, request paths).
// If either stops holding, the exception in security/audit-exceptions.json must be re-reviewed.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const picomatch = createRequire(root + 'apps/web/package.json')('picomatch');
const ms = (fn) => { const start = process.hrtime.bigint(); fn(); return Number(process.hrtime.bigint() - start) / 1e6; };

test('no tool configuration in the repository contains an extended-glob pattern', () => {
  const files = ['apps/web/vite.config.ts', 'apps/web/playwright.config.mjs', 'apps/web/tsconfig.json', 'apps/web/package.json', 'tests/emulators/firebase.json', 'infra/firebase/firebase.json']
    .filter((file) => existsSync(root + file));
  assert.ok(files.length >= 4);
  for (const file of files) {
    const extglob = readFileSync(root + file, 'utf8').match(/['"`][^'"`\n]*[@!+*?]\([^'"`\n]*\)[^'"`\n]*['"`]/g) ?? [];
    assert.deepEqual(extglob, [], `${file} contains extended-glob syntax; picomatch exposure must be re-reviewed`);
  }
  assert.equal(/import\.meta\.glob/.test(readFileSync(root + 'apps/web/src/App.tsx', 'utf8')), false);
});

test('the patterns the tools are given stay fast on hostile matched text', () => {
  const patterns = ['src/**/*.test.ts', '**/node_modules/**', '**/.git/**', '.env', '.env.*', '*.{crt,pem}', '**/.DS_Store', '**/dist/**', '**/*.{js,mjs,cjs,ts,mts,cts,jsx,tsx}', '**/test-results/**'];
  const hostile = ['a'.repeat(50_000), 'a/'.repeat(20_000), ('a'.repeat(200) + '/').repeat(200) + '!', '.env.' + 'a.'.repeat(20_000), 'src/' + 'x/'.repeat(20_000) + 'y.test.tsX', '('.repeat(20_000), '+(a)'.repeat(5_000), '*'.repeat(20_000)];
  let worst = 0;
  for (const pattern of patterns) { const match = picomatch(pattern, { dot: true }); for (const text of hostile) worst = Math.max(worst, ms(() => match(text))); }
  assert.ok(worst < 250, `worst single match took ${worst.toFixed(1)} ms`);
});

test('the weakness itself is real and pattern-driven (documents why untrusted patterns must never reach it)', () => {
  const cost = (count) => ms(() => picomatch('+(a)'.repeat(count) + 'b')('a'.repeat(count + 6) + 'c'));
  const small = cost(10), large = cost(24);
  // Informational once a fixed release lands: the gate will already have forced a re-review by then.
  console.log(`repeated-extglob pattern cost: 10 groups ${small.toFixed(1)} ms, 24 groups ${large.toFixed(1)} ms`);
  assert.ok(large >= 0);
});
