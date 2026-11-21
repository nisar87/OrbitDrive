// Dependency licence review for the npm lock files (the NuGet packages that ship are covered by the release notices). Fails on any licence that is not on
// the reviewed allowlist, so a new dependency with an unexpected licence is a decision, not an accident.   node scripts/licence-check.mjs [--summary]
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const LOCKS = ['apps/web/package-lock.json', 'tests/integration/package-lock.json', 'tests/emulators/package-lock.json'];
/** Reviewed. MPL-2.0 is file-level copyleft: acceptable because the packages are used unmodified as build tooling and are not patched or redistributed by us. */
const ALLOWED = new Set(['MIT', 'ISC', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'BSD', '0BSD', 'MPL-2.0', 'BlueOak-1.0.0', 'CC0-1.0', 'Python-2.0', 'public domain', 'Unlicense']);

/** A licence expression is acceptable if every AND term has at least one allowed OR alternative. */
export function acceptable(expression) {
  const text = String(expression).replace(/[()]/g, ' ');
  return text.split(/\s+AND\s+/i).every((term) => term.split(/\s+OR\s+/i).some((name) => ALLOWED.has(name.trim())));
}

export function review(locks = LOCKS) {
  const counts = new Map(), problems = [], unlabelled = [];
  for (const file of locks) {
    const path = join(root, file);
    if (!existsSync(path)) { problems.push(`${file}: lock file not found`); continue; }
    for (const [key, entry] of Object.entries(JSON.parse(readFileSync(path, 'utf8')).packages ?? {})) {
      if (!key) continue;
      const name = key.slice(key.lastIndexOf('node_modules/') + 'node_modules/'.length);
      const licence = typeof entry.license === 'string' ? entry.license : entry.license?.type;
      if (!licence) { unlabelled.push(`${file}: ${name}`); continue; }
      counts.set(licence, (counts.get(licence) ?? 0) + 1);
      if (!acceptable(licence)) problems.push(`${file}: ${name} is ${licence}`);
    }
  }
  return { counts, problems, unlabelled };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { counts, problems, unlabelled } = review();
  if (process.argv.includes('--summary')) for (const [licence, n] of [...counts].sort((a, b) => b[1] - a[1])) console.log(`${String(n).padStart(5)}  ${licence}`);
  for (const line of unlabelled) console.warn(`no licence field (check by hand): ${line}`);
  if (problems.length) { for (const line of problems) console.error(`NOT ALLOWED: ${line}`); process.exit(1); }
  console.log(`Licence review: ${[...counts.values()].reduce((a, b) => a + b, 0)} packages, all on the reviewed allowlist${unlabelled.length ? `; ${unlabelled.length} without a licence field` : ''}.`);
}
