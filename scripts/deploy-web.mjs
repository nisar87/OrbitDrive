// Deploys the web app to Firebase Hosting for the development project. Run by the OWNER (it needs their Firebase login):
//   firebase login            (once)
//   node scripts/deploy-web.mjs
// It reads the public web configuration from apps/web/.env.local (git-ignored; template: apps/web/.env.example), writes the page security
// headers for that relay origin, builds, deploys to hosting only, and ALWAYS puts the committed fail-closed firebase.json back.
// Nothing here creates cloud resources beyond the Hosting site of the named project, and it never touches Firestore, Auth or billing.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const web = join(root, 'apps', 'web');
const envFile = join(web, '.env.local');
const env = { ...process.env };
if (existsSync(envFile)) for (const line of readFileSync(envFile, 'utf8').split(/\r?\n/)) {
  const m = line.match(/^\s*(VITE_[A-Z_]+)\s*=\s*(.*?)\s*$/);
  if (m && !(m[1] in env)) env[m[1]] = m[2];
}
const need = ['VITE_GATEWAY_URL', 'VITE_FIREBASE_API_KEY', 'VITE_FIREBASE_AUTH_DOMAIN', 'VITE_FIREBASE_PROJECT_ID', 'VITE_FIREBASE_APP_ID'];
const missing = need.filter((k) => !env[k]);
if (missing.length) { console.error(`Missing ${missing.join(', ')}. Put them in apps/web/.env.local.`); process.exit(2); }
if (env.VITE_AUTH_MODE && env.VITE_AUTH_MODE !== 'firebase') { console.error('VITE_AUTH_MODE must be "firebase" for a deployed site.'); process.exit(2); }
if (/\.invalid$/.test(new URL(env.VITE_GATEWAY_URL).hostname)) { console.error('VITE_GATEWAY_URL is still a placeholder.'); process.exit(2); }
env.VITE_AUTH_MODE = 'firebase';
const project = env.VITE_FIREBASE_PROJECT_ID;

const run = (command, args, options = {}) => execFileSync(command, args, { stdio: 'inherit', shell: process.platform === 'win32', ...options });
const firebaseJson = join(root, 'infra', 'firebase', 'firebase.json');
const original = readFileSync(firebaseJson, 'utf8');
try {
  run('node', [join(root, 'scripts', 'web-security-headers.mjs'), '--write', '--gateway', new URL(env.VITE_GATEWAY_URL).origin]);
  run('npm', ['ci'], { cwd: web });
  run('npm', ['run', 'build'], { cwd: web, env });
  run('npx', ['--yes', 'firebase-tools', 'deploy', '--only', 'hosting', '--project', project], { cwd: join(root, 'infra', 'firebase') });
  console.log(`\nDeployed: https://${project}.web.app`);
} finally {
  writeFileSync(firebaseJson, original);                          // the committed file stays fail-closed whatever happened
}
