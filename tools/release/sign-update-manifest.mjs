#!/usr/bin/env node
// Builds a signed update envelope that the app's UpdateVerifier accepts (ECDSA P-256 / SHA-256, IEEE P1363 signature).
// Usage: node sign-update-manifest.mjs --manifest manifest.json --key private.pem --key-id release-2026 --out update.json
// The private key is read from a file path and is never printed. Production release keys must live only in the protected release
// environment; ordinary pull-request jobs never receive them (see the security workflow).
import { readFileSync, writeFileSync } from 'node:fs';
import { createPrivateKey, sign } from 'node:crypto';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, i, all) => (value.startsWith('--') ? [...pairs, [value.slice(2), all[i + 1]]] : pairs), []));
for (const required of ['manifest', 'key', 'key-id', 'out']) if (!args[required]) { console.error(`missing --${required}`); process.exit(2); }

const manifest = JSON.parse(readFileSync(args.manifest, 'utf8'));
const bytes = Buffer.from(JSON.stringify(manifest), 'utf8');
const signature = sign('sha256', bytes, { key: createPrivateKey(readFileSync(args.key)), dsaEncoding: 'ieee-p1363' });
const b64 = (buffer) => buffer.toString('base64');
writeFileSync(args.out, JSON.stringify({ manifest: b64(bytes), signatures: [{ keyId: args['key-id'], sig: b64(signature) }] }, null, 2) + '\n');
console.log(`signed ${manifest.version} with key ${args['key-id']} -> ${args.out}`);
