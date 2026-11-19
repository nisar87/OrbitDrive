#!/usr/bin/env node
// Prints the key id when the private key file matches a public key the app trusts (update-keys.json); exits 1 otherwise, so a release is never
// signed with a key the installed apps would reject. The private key is only read, never printed.
//   node check-signing-key.mjs --key private.pem [--keys update-keys.json]
import { readFileSync } from 'node:fs';
import { createPrivateKey, createPublicKey } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const arg = (name) => { const i = process.argv.indexOf(`--${name}`); return i < 0 ? undefined : process.argv[i + 1]; };
if (!arg('key')) { console.error('usage: check-signing-key.mjs --key private.pem [--keys update-keys.json]'); process.exit(2); }
const keys = JSON.parse(readFileSync(arg('keys') ?? join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'apps', 'desktop', 'OrbitDrive.Core', 'Update', 'update-keys.json'), 'utf8'));
if (Object.keys(keys).length === 0) { console.error('The app has no trusted update key yet (update-keys.json is empty).'); process.exit(1); }
let spki;
try { spki = createPublicKey(createPrivateKey(readFileSync(arg('key')))).export({ type: 'spki', format: 'der' }).toString('base64'); }
catch { console.error('The signing secret is not a valid PKCS#8 PEM private key.'); process.exit(1); }
const id = Object.keys(keys).find((k) => keys[k] === spki);
if (!id) { console.error('The signing secret does not match any key the app trusts; installed copies would reject the update.'); process.exit(1); }
console.log(id);
