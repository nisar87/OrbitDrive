// The release-signing tool: what it signs verifies with the matching public key and with OpenSSL (which is what the relay host uses), and nothing else does.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { generateKeys, signFile, verifyFile } from '../../scripts/relay-sign.mjs';

const dir = mkdtempSync(join(tmpdir(), 'orbit-sign-'));
const archive = join(dir, 'gateway-1.0.0.tar.gz');
writeFileSync(archive, 'pretend release bytes');
const keys = generateKeys(join(dir, 'a')), other = generateKeys(join(dir, 'b'));

test('a signature verifies with its key and with OpenSSL, as the relay host checks it', () => {
  const sig = signFile(archive, keys.priv);
  assert.equal(verifyFile(archive, keys.pub), true);
  if (process.platform !== 'win32') {
    const openssl = spawnSync('openssl', ['dgst', '-sha256', '-verify', keys.pub, '-signature', sig, archive], { encoding: 'utf8' });
    assert.equal(openssl.status, 0, openssl.stderr + openssl.stdout);
  }
});

test('another key, a changed archive and a damaged signature all fail', () => {
  const sig = signFile(archive, keys.priv);
  assert.equal(verifyFile(archive, other.pub), false);
  const changed = join(dir, 'changed.tar.gz'); writeFileSync(changed, 'pretend release bytes!');
  assert.equal(verifyFile(changed, keys.pub, sig), false);
  const bytes = readFileSync(sig); bytes[10] ^= 1; writeFileSync(sig + '.bad', bytes);
  assert.equal(verifyFile(archive, keys.pub, sig + '.bad'), false);
});

test('the private key is owner-only and key generation never overwrites an existing key', () => {
  if (process.platform !== 'win32') assert.equal(statSync(keys.priv).mode & 0o077, 0);
  assert.throws(() => generateKeys(join(dir, 'a')), /refusing to overwrite/);
});
