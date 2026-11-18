import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { validateEnvironment } from '../../scripts/validate-environment.mjs';

const load = (n) => JSON.parse(readFileSync(new URL(`../../infra/environments/${n}.json`, import.meta.url), 'utf8'));
const dev = load('development'), staging = load('staging'), production = load('production');
const ready = (env, over = {}) => ({ ...structuredClone(env), status: 'ready', ...over });
const filled = {
  staging: ready(staging, { firebaseProjectId: 'orbit-stg-1', hostingSite: 'orbit-stg-1', authDomain: 'orbit-stg-1.firebaseapp.com', webOrigin: 'https://orbit-stg-1.web.app', relayOrigin: 'https://relay-stg.orbit.test.example.net', gateway: { AllowedWebOrigins: ['https://orbit-stg-1.web.app'], Firebase__ProjectId: 'orbit-stg-1' } }),
  production: ready(production, { firebaseProjectId: 'orbit-prod-1', hostingSite: 'orbit-prod-1', authDomain: 'drive.orbit.test.example.net', webOrigin: 'https://drive.orbit.test.example.net', relayOrigin: 'https://relay.orbit.test.example.net', gateway: { AllowedWebOrigins: ['https://drive.orbit.test.example.net'], Firebase__ProjectId: 'orbit-prod-1' } }),
};
const problems = (env, o) => validateEnvironment(env, o).join(' | ');

test('the three committed files are valid templates and development is deployable as it is', () => {
  for (const e of [dev, staging, production]) assert.deepEqual(validateEnvironment(e, { others: [dev, staging, production] }), []);
  assert.deepEqual(validateEnvironment(dev, { deploy: true }), []);
});

test('staging and production templates can never be deployed until filled in', () => {
  for (const e of [staging, production]) { const p = problems(e, { deploy: true }); assert.match(p, /placeholder/); assert.match(p, /template/); }
  assert.deepEqual(validateEnvironment(filled.staging, { deploy: true }), []);
  assert.deepEqual(validateEnvironment(filled.production, { deploy: true }), []);
});

test('separation: staging/production never share a project with another environment or use the development project', () => {
  assert.match(problems({ ...filled.staging, firebaseProjectId: 'orbitdrive-dev', gateway: { ...filled.staging.gateway, Firebase__ProjectId: 'orbitdrive-dev' } }, { deploy: true }), /development project/);
  assert.match(problems(filled.production, { deploy: true, others: [{ ...filled.staging, firebaseProjectId: 'orbit-prod-1' }] }), /shares Firebase project/);
});

test('spending and dev sign-in are refused', () => {
  assert.match(problems({ ...filled.production, paidResources: true }, { deploy: true }), /paidResources/);
  assert.match(problems({ ...filled.production, authMode: 'dev' }, { deploy: true }), /authMode/);
});

test('origins: exact, https, no wildcards, relay separate from Hosting, gateway list equals web + preview origins', () => {
  const bad = (over) => problems({ ...filled.production, ...over }, { deploy: true });
  assert.match(bad({ relayOrigin: 'http://relay.orbit.test.example.net' }), /https/);
  assert.match(bad({ relayOrigin: 'https://relay.orbit.test.example.net/path' }), /bare origin/);
  assert.match(bad({ relayOrigin: 'https://drive.orbit.test.example.net' }), /own origin/);
  assert.match(bad({ relayOrigin: 'https://orbit-prod-1.web.app' }), /Hosting address/);
  assert.match(bad({ previewOrigins: ['https://*.web.app'] }), /preview|wildcard|not allowed/i);
  assert.match(bad({ gateway: { AllowedWebOrigins: ['https://drive.orbit.test.example.net', 'https://evil.example.net'], Firebase__ProjectId: 'orbit-prod-1' } }), /neither the web origin/);
  assert.match(bad({ gateway: { AllowedWebOrigins: [], Firebase__ProjectId: 'orbit-prod-1' } }), /empty|missing/);
  assert.match(bad({ gateway: { AllowedWebOrigins: ['https://drive.orbit.test.example.net'], Firebase__ProjectId: 'other' } }), /project differs/);
  assert.match(bad({ previewOrigins: ['https://pr-1.orbit.test.example.net'], gateway: { AllowedWebOrigins: ['https://drive.orbit.test.example.net', 'https://pr-1.orbit.test.example.net'], Firebase__ProjectId: 'orbit-prod-1' } }), /production must not list preview/);
});

test('production needs the owner\'s own domain; staging may use Firebase default addresses', () => {
  const onHosting = { ...filled.production, webOrigin: 'https://orbit-prod-1.web.app', gateway: { AllowedWebOrigins: ['https://orbit-prod-1.web.app'], Firebase__ProjectId: 'orbit-prod-1' } };
  assert.match(problems(onHosting, { deploy: true }), /own domain/);
});

test('secrets never live in an environment file', () => {
  assert.match(problems({ ...filled.production, note: ['-----BEGIN', 'PRIVATE', 'KEY-----'].join(' ') }, { deploy: true }), /secret/);
  assert.match(problems({ ...filled.production, note: 'AIza' + 'a'.repeat(35) }, { deploy: true }), /API key/);
});

test('command line: deploy mode exits non-zero for templates, zero for development', () => {
  const run = (f, ...a) => spawnSync('node', ['scripts/validate-environment.mjs', `infra/environments/${f}.json`, ...a], { encoding: 'utf8' });
  assert.equal(run('production').status, 0);
  assert.equal(run('production', '--deploy').status, 1);
  assert.equal(run('development', '--deploy').status, 0);
});
