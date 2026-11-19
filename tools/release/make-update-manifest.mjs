#!/usr/bin/env node
// Builds the (unsigned) update manifest the app's UpdateVerifier expects, from the release zip. Signing is a separate step (sign-update-manifest.mjs).
//   node make-update-manifest.mjs --zip OrbitDrive-0.1.9-win-x64.zip --version 0.1.9 --url-base https://github.com/<owner>/<repo>/releases/download/desktop-dev --out manifest.json [--notes "text"] [--days 60]
import { readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256 } from './release-lib.mjs';

const arg = (name, fallback) => { const i = process.argv.indexOf(`--${name}`); return i < 0 ? fallback : process.argv[i + 1]; };
const zipPath = arg('zip'), version = arg('version'), base = arg('url-base'), out = arg('out');
if (!zipPath || !version || !base || !out) { console.error('usage: make-update-manifest.mjs --zip f --version X.Y.Z --url-base https://... --out manifest.json [--notes text] [--days 60]'); process.exit(2); }
if (!/^\d+\.\d+\.\d+$/.test(version)) { console.error('version must be X.Y.Z (the app compares numeric versions)'); process.exit(2); }
if (!/^https:\/\/[^/?#]+(\/[^?#]*)?$/.test(base)) { console.error('url-base must be an https URL without query or fragment'); process.exit(2); }
const protocol = Number(/const\s+int\s+Version\s*=\s*(\d+)/.exec(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'packages', 'protocol', 'generated', 'csharp', 'Protocol.g.cs'), 'utf8'))?.[1]);
if (!Number.isInteger(protocol)) { console.error('could not read the protocol version'); process.exit(2); }
const zip = readFileSync(zipPath);
const name = basename(zipPath);
const days = Number(arg('days', '60'));
if (!(days > 0 && days <= 90)) { console.error('days must be 1..90 (the app refuses anything valid for longer)'); process.exit(2); }
const now = Math.floor(Date.now() / 1000);
const manifest = {
  format: 1, channel: 'dev', version, minProtocol: protocol, maxProtocol: 0, published: now - 60, expires: now + days * 86400,
  files: [{ name, arch: 'x64', sha256: sha256(zip), size: zip.length, url: `${base.replace(/\/$/, '')}/${name}` }],
  notes: (arg('notes', '') ?? '').slice(0, 900),
};
writeFileSync(out, JSON.stringify(manifest, null, 2));
console.log(`manifest for ${version}: ${name} ${zip.length} bytes, protocol >= ${protocol}, valid ${days} days`);
