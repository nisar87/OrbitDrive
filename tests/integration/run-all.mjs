// Runs EVERY tests/integration/*.test.mjs file, so a new test file cannot be left out of CI by forgetting to add it to a list (F-008).
// Only files named in EXCLUDED run elsewhere or need something this runner cannot give; each has a reason, and a stale entry fails the run.
//   node tests/integration/run-all.mjs [--only-relay] [--list] [-- <extra node --test flags>]
// --only-relay drops the files that need a desktop-only surface (local IPC pipes, QR, key stores), for the Linux gateway-release job.
import { readdirSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const EXCLUDED = {
  'firestore-restart.test.mjs': 'needs the Firestore emulator; runs in the gateway workflow under `firebase emulators:exec`',
  'live-relay.test.mjs': 'needs ORBIT_LIVE_RELAY_URL; runs in the relay-release live-agent job',
};
// Not run by the relay-release gate (it tests the gateway on one Linux job); the integration workflow still runs them on Windows and Linux.
const NOT_IN_RELAY_GATE = new Set(['ipc.test.mjs', 'qr.test.mjs', 'firebase-revocation.test.mjs', 'defaults-migration.test.mjs', 'device-rotation.test.mjs', 'key-rotation.test.mjs', 'pc-rename.test.mjs']);

const args = process.argv.slice(2);
const dash = args.indexOf('--');
const extra = dash >= 0 ? args.slice(dash + 1) : [];
const own = dash >= 0 ? args.slice(0, dash) : args;
const onlyRelay = own.includes('--only-relay');

const all = readdirSync(here).filter((f) => f.endsWith('.test.mjs')).sort();
for (const name of Object.keys(EXCLUDED)) if (!existsSync(join(here, name))) { console.error(`run-all: EXCLUDED lists ${name}, which no longer exists`); process.exit(1); }
for (const name of NOT_IN_RELAY_GATE) if (!existsSync(join(here, name))) { console.error(`run-all: NOT_IN_RELAY_GATE lists ${name}, which no longer exists`); process.exit(1); }
const files = all.filter((f) => !(f in EXCLUDED) && !(onlyRelay && NOT_IN_RELAY_GATE.has(f)));
if (own.includes('--list')) { console.log(files.join('\n')); process.exit(0); }
if (files.length === 0) { console.error('run-all: no test files found'); process.exit(1); }
console.log(`run-all: ${files.length} files (${all.length - files.length} left out: ${all.filter((f) => !files.includes(f)).join(', ') || 'none'})`);
const result = spawnSync(process.execPath, ['--test', ...extra, ...files.map((f) => join(here, f))], { stdio: 'inherit' });
process.exit(result.status ?? 1);
