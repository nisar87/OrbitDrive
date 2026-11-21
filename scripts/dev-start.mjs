// One command for a local development stack: a Development gateway (dev sign-in, local only) and the web app against it. The PC agent is started separately so
// you can choose the folder to share and type its commands:   node scripts/dev-start.mjs        (Ctrl+C stops everything)
// Nothing here touches a real service: the identity is the local development one and the relay listens on 127.0.0.1 only.
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const GATEWAY = 'http://127.0.0.1:5199', WEB = 'http://localhost:5173';
const secret = randomBytes(32).toString('hex');
const shell = process.platform === 'win32';
const children = [];

const start = (label, command, args, cwd, env) => {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'], shell });
  const tag = (data) => String(data).split(/\r?\n/).filter(Boolean).forEach((line) => console.log(`[${label}] ${line}`));
  child.stdout.on('data', tag); child.stderr.on('data', tag);
  child.on('exit', (code) => { console.log(`[${label}] stopped (${code})`); stop(code ?? 1); });
  children.push(child);
};
let stopping = false;
function stop(code = 0) { if (stopping) return; stopping = true; for (const child of children) child.kill(); setTimeout(() => process.exit(code), 500); }
process.on('SIGINT', () => stop(0)); process.on('SIGTERM', () => stop(0));

start('gateway', 'dotnet', ['run', '-c', 'Release', '--', '--urls', GATEWAY], join(root, 'services/gateway/OrbitDrive.Gateway'), {
  ASPNETCORE_ENVIRONMENT: 'Development', OrbitDrive__DevIdentity__Enabled: 'true', OrbitDrive__DevIdentity__Secret: secret, OrbitDrive__AllowedWebOrigins__0: WEB,
});
start('web', 'npx', ['vite', '--mode', 'e2e'], join(root, 'apps/web'), { VITE_GATEWAY_URL: GATEWAY, VITE_AUTH_MODE: 'dev' });

console.log(`\nWeb app:  ${WEB}\nGateway:  ${GATEWAY}\n\nIn another terminal, share a folder and start the PC agent:\n` +
  `  dotnet run -c Release --project apps/desktop/OrbitDrive.Agent -- roots add "My Share" <folder>\n` +
  `  dotnet run -c Release --project apps/desktop/OrbitDrive.Agent -- run --gateway ${GATEWAY} --web-origin ${WEB}\n` +
  `then type  pair  in the agent window for a pairing link and code.\n`);
