#!/usr/bin/env node
// Creates the OrbitDrive UPDATE signing key pair (ECDSA P-256) on the owner's own PC. This is NOT a Windows code-signing certificate and costs nothing.
//   node tools/release/make-update-key.mjs
// The PRIVATE key is written only to your own profile folder and copied to your clipboard so you can paste it as the GitHub secret UPDATE_SIGNING_KEY.
// It is never printed and never goes into the repository. The PUBLIC key is printed: that one is safe to share and is built into the app.
import { generateKeyPairSync } from 'node:crypto';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const dir = join(homedir(), '.orbitdrive');
const file = join(dir, 'update-signing-key.pem');
if (existsSync(file)) { console.error(`A key already exists at ${file}. Not overwriting it (that would break updates for installed copies). Delete it yourself first if you really want a new one.`); process.exit(1); }
const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const pem = privateKey.export({ type: 'pkcs8', format: 'pem' });
mkdirSync(dir, { recursive: true });
writeFileSync(file, pem, { mode: 0o600 });
const spki = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const keyId = 'orbitdrive-update-1';
let copied = false;
if (process.platform === 'win32') copied = spawnSync('clip', { input: pem, windowsHide: true }).status === 0;
else if (process.platform === 'darwin') copied = spawnSync('pbcopy', { input: pem }).status === 0;
console.log(`Private key saved to: ${file}   (keep a backup; losing it means installed copies can never update again)`);
console.log(copied ? 'The private key is now on your clipboard.' : `Open ${file} and copy its whole content.`);
console.log('');
console.log('1) Open https://github.com/nisar87/OrbitDrive/settings/secrets/actions/new');
console.log('   Name:   UPDATE_SIGNING_KEY');
console.log('   Secret: paste (Ctrl+V), then press "Add secret".');
console.log('');
console.log('2) Send this PUBLIC line to Claude (safe to share):');
console.log(`   ${keyId}  ${spki}`);
