// Deploys the web app to Firebase Hosting for the development project. Run by the OWNER (it needs their Firebase login):
//   npx firebase-tools login      (once)
//   node scripts/deploy-web.mjs
// It reads the public web configuration from apps/web/.env.local (git-ignored; template: apps/web/.env.example), builds, assembles a small
// self-contained deploy folder (.deploy-web/: the built site + a firebase.json carrying the page security headers for that relay origin),
// and deploys HOSTING ONLY from it. Firebase refuses a public folder outside the folder that holds firebase.json, hence the staging folder.
// The committed infra/firebase/firebase.json is never modified. Nothing here touches Firestore, Auth or billing.
import { readFileSync, existsSync, rmSync, mkdirSync, cpSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const web = join(root, 'apps', 'web');
const staging = join(root, '.deploy-web');
const env = { ...process.env };
// Precedence: real environment, then apps/web/.env.local (the owner's own), then the committed public development values (used by CI).
for (const envFile of [join(web, '.env.local'), join(root, 'infra', 'firebase', 'web-dev.env')]) {
  if (!existsSync(envFile)) continue;
  for (const line of readFileSync(envFile, 'utf8').split(/\r?\n/)) {
    const m = line.match(/^\s*(VITE_[A-Z_]+)\s*=\s*(.*?)\s*$/);
    if (m && !(m[1] in env)) env[m[1]] = m[2];
  }
}
const need = ['VITE_GATEWAY_URL', 'VITE_FIREBASE_API_KEY', 'VITE_FIREBASE_AUTH_DOMAIN', 'VITE_FIREBASE_PROJECT_ID', 'VITE_FIREBASE_APP_ID'];
const missing = need.filter((k) => !env[k]);
if (missing.length) { console.error(`Missing ${missing.join(', ')}. Put them in apps/web/.env.local.`); process.exit(2); }
if (env.VITE_AUTH_MODE && env.VITE_AUTH_MODE !== 'firebase') { console.error('VITE_AUTH_MODE must be "firebase" for a deployed site.'); process.exit(2); }
if (/\.invalid$/.test(new URL(env.VITE_GATEWAY_URL).hostname)) { console.error('VITE_GATEWAY_URL is still a placeholder.'); process.exit(2); }
env.VITE_AUTH_MODE = 'firebase';
const project = env.VITE_FIREBASE_PROJECT_ID;

const { firebaseConfigFor, checkGatewayOrigin } = await import(pathToFileURL(join(root, 'scripts', 'web-security-headers.mjs')).href);
const relayOrigin = checkGatewayOrigin(new URL(env.VITE_GATEWAY_URL).origin);
const committed = JSON.parse(readFileSync(join(root, 'infra', 'firebase', 'firebase.json'), 'utf8'));
const config = firebaseConfigFor(committed, relayOrigin);
config.hosting.public = 'dist';                                    // relative to the staging folder
config.hosting.ignore = ['firebase.json', '**/.*', '**/node_modules/**'];

const run = (command, args, options = {}) => execFileSync(command, args, { stdio: 'inherit', shell: process.platform === 'win32', ...options });
run('npm', ['ci'], { cwd: web });
run('npm', ['run', 'build'], { cwd: web, env });

rmSync(staging, { recursive: true, force: true });                 // only our own staging folder
mkdirSync(staging);
cpSync(join(web, 'dist'), join(staging, 'dist'), { recursive: true });
// The marketing site (apps/site, static, no build) is its own Firebase Hosting site in the same project (myorbitdrive-site, served at the
// apex domain myorbitdrive.app). Only the public files are copied; tests, capture tools and node_modules never leave the repo.
const siteDir = join(staging, 'site');
mkdirSync(siteDir);
for (const f of ['index.html', 'styles.css', 'site.js', 'robots.txt', 'sitemap.xml']) cpSync(join(root, 'apps', 'site', f), join(siteDir, f));
cpSync(join(root, 'apps', 'site', 'assets'), join(siteDir, 'assets'), { recursive: true });
const siteHeaders = JSON.parse(readFileSync(join(root, 'apps', 'site', 'headers.hosting.json'), 'utf8')).headers;
const appSite = process.env.APP_HOSTING_SITE || project;            // the web app keeps its existing site (orbitdrive-dev)
const marketingSite = process.env.MARKETING_HOSTING_SITE || 'myorbitdrive-site';
const app = { ...config.hosting, site: appSite, redirects: [{ source: '/welcome{,/**}', destination: 'https://myorbitdrive.app/', type: 302 }] };
const marketing = { site: marketingSite, public: 'site', ignore: ['firebase.json', '**/.*', '**/node_modules/**'], cleanUrls: true, headers: siteHeaders };
config.hosting = [app, marketing];
writeFileSync(join(staging, 'firebase.json'), JSON.stringify(config, null, 2) + '\n');
writeFileSync(join(staging, '.firebaserc'), JSON.stringify({ projects: { default: project } }) + '\n');

run('npx', ['--yes', 'firebase-tools', 'deploy', '--only', 'hosting', '--project', project], { cwd: staging });
console.log(`\nDeployed: https://${appSite}.web.app (app) and https://${marketingSite}.web.app (marketing site)`);
