#!/usr/bin/env node
// Signs and verifies relay release archives with an ECDSA P-256 key (SHA-256, DER signature: what `openssl dgst -sha256 -verify` checks on the relay host).
//
//   node scripts/relay-sign.mjs generate <directory>          make release-signing.key (keep it OFFLINE) and release-signing.pub (copy to the host)
//   node scripts/relay-sign.mjs sign <archive> <private-key>  write <archive>.sig next to the archive
//   node scripts/relay-sign.mjs verify <archive> <public-key> [<signature>]
//
// The relay verifies only when /etc/orbitdrive/release-signing.pub exists on the host (see infra/relay/bin/orbitdrive-update and docs/operations/relay.md).
import { generateKeyPairSync, createPrivateKey, createPublicKey, sign, verify } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function generateKeys(directory) {
  mkdirSync(directory, { recursive: true });
  const priv = join(directory, 'release-signing.key'), pub = join(directory, 'release-signing.pub');
  if (existsSync(priv) || existsSync(pub)) throw new Error(`${directory} already holds a signing key; refusing to overwrite it`);
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  writeFileSync(priv, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
  writeFileSync(pub, publicKey.export({ type: 'spki', format: 'pem' }));
  return { priv, pub };
}

export function signFile(archive, privateKeyFile, out = archive + '.sig') {
  const key = createPrivateKey(readFileSync(privateKeyFile));
  if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') throw new Error('The signing key must be an ECDSA P-256 key.');
  writeFileSync(out, sign('sha256', readFileSync(archive), { key, dsaEncoding: 'der' }));
  return out;
}

export function verifyFile(archive, publicKeyFile, signatureFile = archive + '.sig') {
  const key = createPublicKey(readFileSync(publicKeyFile));
  return verify('sha256', readFileSync(archive), { key, dsaEncoding: 'der' }, readFileSync(signatureFile));
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  const [command, a, b, c] = process.argv.slice(2);
  try {
    if (command === 'generate' && a) { const r = generateKeys(a); console.log(`Wrote ${r.priv} (keep it offline, never commit it) and ${r.pub} (install on the relay host as /etc/orbitdrive/release-signing.pub).`); }
    else if (command === 'sign' && a && b) console.log('Wrote ' + signFile(a, b));
    else if (command === 'verify' && a && b) { const ok = verifyFile(a, b, c); console.log(ok ? 'signature ok' : 'SIGNATURE DOES NOT VERIFY'); process.exit(ok ? 0 : 1); }
    else { console.error('usage: relay-sign.mjs generate <dir> | sign <archive> <private-key> | verify <archive> <public-key> [<sig>]'); process.exit(2); }
  } catch (e) { console.error(e.message); process.exit(1); }
}
