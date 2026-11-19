#!/usr/bin/env node
// Verifies a PUBLISHED update feed the way the app will: fetches update.json, checks the ECDSA signature against the public keys built into the app
// (apps/desktop/OrbitDrive.Core/Update/update-keys.json), then downloads the zip and checks size and SHA-256. Exit code 1 on any problem.
//   node verify-feed.mjs --feed https://github.com/<owner>/<repo>/releases/download/desktop-dev/update.json [--expect-version 0.1.9] [--keys file]
import { readFileSync } from 'node:fs';
import { createPublicKey, verify } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256 } from './release-lib.mjs';

const arg = (name) => { const i = process.argv.indexOf(`--${name}`); return i < 0 ? undefined : process.argv[i + 1]; };
const feed = arg('feed');
if (!feed) { console.error('usage: verify-feed.mjs --feed <url> [--expect-version X.Y.Z]'); process.exit(2); }
const keys = JSON.parse(readFileSync(arg('keys') ?? join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'apps', 'desktop', 'OrbitDrive.Core', 'Update', 'update-keys.json'), 'utf8'));
const fail = (m) => { console.error('FEED PROBLEM: ' + m); process.exit(1); };

const response = await fetch(feed, { redirect: 'follow' });
if (!response.ok) fail(`feed answered ${response.status}`);
const envelope = JSON.parse(await response.text());
const manifestBytes = Buffer.from(envelope.manifest, 'base64');
const trusted = envelope.signatures.find((s) => keys[s.keyId]);
if (!trusted) fail('no signature by a key the app trusts');
const ok = verify('sha256', manifestBytes, { key: createPublicKey({ key: Buffer.from(keys[trusted.keyId], 'base64'), format: 'der', type: 'spki' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(trusted.sig, 'base64'));
if (!ok) fail('signature does not verify');
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (arg('expect-version') && manifest.version !== arg('expect-version')) fail(`feed says ${manifest.version}, expected ${arg('expect-version')}`);
if (manifest.expires * 1000 <= Date.now()) fail('manifest already expired');
const file = manifest.files.find((f) => f.arch === 'x64') ?? fail('no x64 file');
const download = await fetch(file.url, { redirect: 'follow' });
if (!download.ok) fail(`zip answered ${download.status}`);
const zip = Buffer.from(await download.arrayBuffer());
if (zip.length !== file.size) fail(`zip size ${zip.length} differs from signed size ${file.size}`);
if (sha256(zip) !== file.sha256) fail('zip hash differs from the signed hash');
console.log(`feed ok: version ${manifest.version}, ${file.name}, ${zip.length} bytes, sha256 ${file.sha256}, signed by ${trusted.keyId}`);
