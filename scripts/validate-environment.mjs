#!/usr/bin/env node
// Validates an environment definition in infra/environments (W12, R40/R57/R58).
//   node scripts/validate-environment.mjs infra/environments/production.json            template mode: placeholders allowed, everything else checked
//   node scripts/validate-environment.mjs infra/environments/production.json --deploy   deploy mode: every placeholder must be gone
// Deploy mode is what any deployment command must run first, so a template can never be deployed by accident.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { checkGatewayOrigin } from './web-security-headers.mjs';

const DEV_PROJECT = 'orbitdrive-dev';
const placeholder = (v) => typeof v === 'string' && (/REPLACE_/.test(v) || /\.invalid$/.test(v) || /example\.(com|org|invalid)$/.test(v));

/** Returns a list of problems (empty = valid). `others` are the other environments, to prove separation. */
export function validateEnvironment(env, { deploy = false, others = [] } = {}) {
  const problems = [];
  const add = (m) => problems.push(m);
  for (const k of ['environment', 'firebaseProjectId', 'hostingSite', 'authDomain', 'webOrigin', 'relayOrigin', 'authMode']) if (typeof env[k] !== 'string' || !env[k]) add(`${k} is missing`);
  if (problems.length) return problems;
  if (!['development', 'staging', 'production'].includes(env.environment)) add('environment must be development, staging or production');
  if (env.authMode !== 'firebase') add('authMode must be "firebase" (the dev sign-in is for local test servers only)');
  if (env.paidResources !== false) add('paidResources must be explicitly false: spending needs the owner\'s approval');
  if (env.environment !== 'development' && env.firebaseProjectId === DEV_PROJECT) add('staging/production must not use the development project');
  for (const o of others) if (o.environment !== env.environment && o.firebaseProjectId === env.firebaseProjectId) add(`shares Firebase project ${env.firebaseProjectId} with ${o.environment}`);

  const origins = { webOrigin: env.webOrigin, relayOrigin: env.relayOrigin, ...Object.fromEntries((env.previewOrigins ?? []).map((o, i) => [`previewOrigins[${i}]`, o])) };
  for (const [name, origin] of Object.entries(origins)) {
    if (placeholder(origin) && !deploy) continue;
    if (placeholder(origin)) { add(`${name} is still a placeholder`); continue; }
    try { checkGatewayOrigin(origin); } catch (e) { add(`${name}: ${e.message}`); }
    if (/\*/.test(origin)) add(`${name}: wildcards are not allowed`);
  }
  // Addresses earlier releases shipped as defaults; a PC still holding exactly one of them is moved to the current ones. Plain origins only.
  if (env.supersededAddresses !== undefined) {
    const list = env.supersededAddresses;
    if (!Array.isArray(list) || list.length > 16) add('supersededAddresses must be a list of at most 16 origins');
    else for (const a of list) {
      let ok = false;
      try { const u = new URL(a); ok = u.protocol === 'https:' && u.pathname === '/' && !u.search && !u.hash && !u.username && u.origin === a; } catch { /* not a URL */ }
      if (!ok) add(`supersededAddresses: ${String(a)} is not a plain https origin`);
      if (a === env.relayOrigin || a === env.webOrigin) add(`supersededAddresses must not contain the current address ${a}`);
    }
  }
  if (env.webOrigin === env.relayOrigin) add('the relay must be its own origin: large downloads never go through Hosting');
  if (/\.(web\.app|firebaseapp\.com)$/.test(new URL(placeholder(env.relayOrigin) ? 'https://x.invalid' : env.relayOrigin).hostname)) add('the relay must not be a Firebase Hosting address');
  if (env.environment === 'production' && (env.previewOrigins ?? []).length) add('production must not list preview origins');
  if (env.environment === 'production' && deploy && /\.(web\.app|firebaseapp\.com)$/.test(new URL(env.webOrigin).hostname)) add('production needs the owner\'s own domain, not a Firebase default address');

  const allowed = env.gateway?.AllowedWebOrigins ?? [];
  const expected = new Set([env.webOrigin, ...(env.previewOrigins ?? [])]);
  if (allowed.length === 0) add('gateway AllowedWebOrigins is empty');
  for (const o of allowed) { if (!expected.has(o)) add(`gateway allows ${o}, which is neither the web origin nor a listed preview origin`); if (o.includes('*')) add('gateway origins must be exact'); }
  for (const o of expected) if (!allowed.includes(o)) add(`gateway AllowedWebOrigins is missing ${o}`);
  if (env.gateway?.Firebase__ProjectId !== env.firebaseProjectId) add('gateway Firebase project differs from the environment\'s project');

  // Public (VITE_) values may never carry secrets; nothing in an environment file may look like one.
  const text = JSON.stringify(env);
  if (/PRIVATE KEY|"type":\s*"service_account"|client_secret|password/i.test(text)) add('the file contains secret-looking content');
  if (/AIza[0-9A-Za-z_-]{35}/.test(text)) add('the file contains an API key; web config belongs in the web env file');
  if (env.status === 'template' && deploy) add('status is "template": fill in and set status to "ready" first');
  return problems;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const file = process.argv[2];
  if (!file) { console.error('usage: validate-environment.mjs <file> [--deploy]'); process.exit(2); }
  const env = JSON.parse(readFileSync(file, 'utf8'));
  const problems = validateEnvironment(env, { deploy: process.argv.includes('--deploy') });
  if (problems.length) { console.error(`${file}: ${problems.length} problem(s)\n - ${problems.join('\n - ')}`); process.exit(1); }
  console.log(`${file}: valid${process.argv.includes('--deploy') ? ' for deployment' : ' as a template'}`);
}
