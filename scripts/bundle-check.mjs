#!/usr/bin/env node
// Proves what the production web bundle is made of: builds it (placeholder settings, nothing is deployed or kept)
// and fails if any package that is not an approved runtime dependency ended up in it. Build and test tooling
// (vite, vitest, picomatch, tinyglobby, ...) must never ship.
// Usage: node scripts/bundle-check.mjs   (run from the repository root after `npm ci` in apps/web)
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'apps', 'web');
// Runtime packages reviewed for shipping: the declared dependencies and what they are built from.
const allowed = new Set(['@firebase/app', '@firebase/auth', '@firebase/component', '@firebase/logger', '@firebase/util', 'idb', 'jsqr', 'react', 'react-dom', 'scheduler', 'tslib']);
const declared = Object.keys(JSON.parse(readFileSync(join(web, 'package.json'), 'utf8')).dependencies);

Object.assign(process.env, {
  VITE_AUTH_MODE: 'firebase', VITE_GATEWAY_URL: 'https://relay.bundle-check.invalid', VITE_FIREBASE_API_KEY: 'bundle-check-placeholder',
  VITE_FIREBASE_AUTH_DOMAIN: 'bundle-check.invalid', VITE_FIREBASE_PROJECT_ID: 'bundle-check', VITE_FIREBASE_APP_ID: 'bundle-check-placeholder',
});
process.chdir(web);
const { build } = await import(pathToFileURL(createRequire(join(web, 'package.json')).resolve('vite')).href);
const packages = new Set();
let text = '';
await build({
  logLevel: 'error', build: { write: false },
  plugins: [{
    name: 'collect-bundle-modules',
    generateBundle(_options, bundle) {
      for (const chunk of Object.values(bundle)) {
        text += chunk.code ?? (typeof chunk.source === 'string' ? chunk.source : '');
        for (const id of Object.keys(chunk.modules ?? {})) {
          const match = /node_modules[\\/]((?:@[^\\/]+[\\/])?[^\\/]+)/.exec(id);
          if (match) packages.add(match[1].replace('\\', '/'));
        }
      }
    },
  }],
});
const unexpected = [...packages].filter((name) => !allowed.has(name)).sort();
const missing = declared.filter((name) => !packages.has(name));
const markers = ['picomatch', 'tinyglobby', 'isExtglob', 'node:fs', 'vitest'].filter((marker) => text.includes(marker));
console.log(`production bundle is built from: ${[...packages].sort().join(', ')}`);
if (unexpected.length) console.log(`::error::packages in the production bundle that were not reviewed for shipping: ${unexpected.join(', ')}`);
if (markers.length) console.log(`::error::build-tooling code found in the production bundle: ${markers.join(', ')}`);
if (missing.length) console.log(`::error::declared runtime dependencies missing from the bundle (the check may be looking at the wrong build): ${missing.join(', ')}`);
if (unexpected.length || markers.length || missing.length) process.exit(1);
console.log('production bundle contains only reviewed runtime packages; no build or test tooling');
