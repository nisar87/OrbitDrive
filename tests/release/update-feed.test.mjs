import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync, spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtempSync, writeFileSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { generateKeyPairSync, createPublicKey, verify } from 'node:crypto';
import { sha256, writeZip } from '../../tools/release/release-lib.mjs';

const tools = new URL('../../tools/release/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const node = (script, ...args) => spawnSync('node', [join(tools, script), ...args], { encoding: 'utf8' });
const dir = mkdtempSync(join(tmpdir(), 'orbit-feed-'));
const zip = writeZip([{ name: 'OrbitDrive/OrbitDrive.exe', data: Buffer.alloc(50_000, 3) }]);
const zipPath = join(dir, 'OrbitDrive-0.1.9-win-x64.zip'); writeFileSync(zipPath, zip);
const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const keyFile = join(dir, 'k.pem'); writeFileSync(keyFile, privateKey.export({ type: 'pkcs8', format: 'pem' }));
const spki = publicKey.export({ type: 'spki', format: 'der' });
const keysFile = join(dir, 'keys.json'); writeFileSync(keysFile, JSON.stringify({ 'test-key': spki.toString('base64') }));
let server, base, served = {};

before(async () => {
  server = createServer((req, res) => { const body = served[req.url]; if (!body) { res.writeHead(404).end(); return; } res.writeHead(200).end(body); });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  base = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

const verifyFeed = (...args) => new Promise((resolve) => {
  const child = spawn('node', [join(tools, 'verify-feed.mjs'), ...args], { encoding: 'utf8' });
  let stdout = '', stderr = '';
  child.stdout.on('data', (d) => { stdout += d; }); child.stderr.on('data', (d) => { stderr += d; });
  child.on('close', (status) => resolve({ status, stdout, stderr }));
});
const build = (version, urlBase, extra = []) => node('make-update-manifest.mjs', '--zip', zipPath, '--version', version, '--url-base', urlBase, '--out', join(dir, 'm.json'), ...extra);
const sign = (out = join(dir, 'update.json')) => node('sign-update-manifest.mjs', '--manifest', join(dir, 'm.json'), '--key', keyFile, '--key-id', 'test-key', '--out', out);

test('manifest: names, size, hash, https url, protocol and validity window are exactly what the app checks', () => {
  assert.equal(build('0.1.9', 'https://github.com/o/r/releases/download/desktop-dev', ['--notes', 'x'.repeat(2000)]).status, 0);
  const m = JSON.parse(readFileSync(join(dir, 'm.json'), 'utf8'));
  assert.equal(m.format, 1); assert.equal(m.channel, 'dev'); assert.equal(m.version, '0.1.9');
  assert.deepEqual(m.files[0], { name: 'OrbitDrive-0.1.9-win-x64.zip', arch: 'x64', sha256: sha256(zip), size: zip.length, url: 'https://github.com/o/r/releases/download/desktop-dev/OrbitDrive-0.1.9-win-x64.zip' });
  assert.ok(Number.isInteger(m.minProtocol) && m.minProtocol >= 1 && m.maxProtocol === 0);
  assert.ok(m.expires - m.published <= 90 * 86400 && m.expires > Date.now() / 1000);
  assert.ok(m.notes.length <= 900);
});

test('manifest builder refuses what the app would refuse', () => {
  assert.notEqual(build('v1.0', 'https://x.test').status, 0);
  assert.notEqual(build('0.1', 'https://x.test').status, 0);
  assert.notEqual(build('0.1.9', 'http://x.test').status, 0);
  assert.notEqual(build('0.1.9', 'https://x.test?token=1').status, 0);
  assert.notEqual(build('0.1.9', 'https://x.test', ['--days', '91']).status, 0);
});

test('signed envelope verifies with the public key (same ECDSA P-256 / P1363 form the C# verifier reads) and fails for any other key or a changed byte', () => {
  build('0.1.9', 'https://github.com/o/r/releases/download/desktop-dev'); assert.equal(sign().status, 0);
  const envelope = JSON.parse(readFileSync(join(dir, 'update.json'), 'utf8'));
  const manifest = Buffer.from(envelope.manifest, 'base64');
  const check = (key, bytes) => verify('sha256', bytes, { key: createPublicKey({ key, format: 'der', type: 'spki' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(envelope.signatures[0].sig, 'base64'));
  assert.equal(check(spki, manifest), true);
  assert.equal(check(generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ type: 'spki', format: 'der' }), manifest), false);
  const tampered = Buffer.from(manifest); tampered[20] ^= 1;
  assert.equal(check(spki, tampered), false);
  assert.doesNotMatch(readFileSync(join(dir, 'update.json'), 'utf8'), /PRIVATE KEY/);
});

test('verify-feed accepts a correct published feed and rejects a swapped zip, a wrong key and an unsigned manifest', async () => {
  // The builder only emits https URLs, so the local-server variant is derived from its output.
  build('0.1.9', 'https://github.com/o/r/releases/download/desktop-dev');
  const local = JSON.parse(readFileSync(join(dir, 'm.json'), 'utf8')); local.files[0].url = `${base}/${local.files[0].name}`;
  writeFileSync(join(dir, 'm.json'), JSON.stringify(local)); sign();
  served = { '/update.json': readFileSync(join(dir, 'update.json')), '/OrbitDrive-0.1.9-win-x64.zip': zip };
  const good = await verifyFeed('--feed', `${base}/update.json`, '--keys', keysFile, '--expect-version', '0.1.9');
  assert.equal(good.status, 0, good.stderr); assert.match(good.stdout, /feed ok: version 0.1.9/);

  served['/OrbitDrive-0.1.9-win-x64.zip'] = Buffer.concat([zip.subarray(0, zip.length - 1), Buffer.from([9])]);
  const swapped = await verifyFeed('--feed', `${base}/update.json`, '--keys', keysFile);
  assert.equal(swapped.status, 1); assert.match(swapped.stderr, /differs from the signed hash/);

  served['/OrbitDrive-0.1.9-win-x64.zip'] = zip;
  const otherKeys = join(dir, 'other.json'); writeFileSync(otherKeys, JSON.stringify({ 'test-key': generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ type: 'spki', format: 'der' }).toString('base64') }));
  assert.equal((await verifyFeed('--feed', `${base}/update.json`, '--keys', otherKeys)).status, 1);
  const unsigned = JSON.parse(readFileSync(join(dir, 'update.json'), 'utf8')); unsigned.signatures = [];
  served['/update.json'] = Buffer.from(JSON.stringify(unsigned));
  assert.match((await verifyFeed('--feed', `${base}/update.json`, '--keys', keysFile)).stderr, /no signature/);
  assert.equal((await verifyFeed('--feed', `${base}/missing.json`, '--keys', keysFile)).status, 1);
});

test('the key helper never prints the private key, refuses to overwrite, and prints a public line', () => {
  const home = mkdtempSync(join(tmpdir(), 'orbit-home-'));
  const run = () => spawnSync('node', [join(tools, 'make-update-key.mjs')], { encoding: 'utf8', env: { ...process.env, HOME: home, USERPROFILE: home } });
  const first = run();
  assert.equal(first.status, 0, first.stderr);
  assert.doesNotMatch(first.stdout + first.stderr, /PRIVATE KEY|BEGIN/);
  const line = /orbitdrive-update-1\s+(\S+)/.exec(first.stdout);
  assert.ok(line && Buffer.from(line[1], 'base64').length > 60);
  assert.match(readFileSync(join(home, '.orbitdrive', 'update-signing-key.pem'), 'utf8'), /BEGIN PRIVATE KEY/);
  const second = run();
  assert.equal(second.status, 1); assert.match(second.stderr, /Not overwriting/);
});

test('check-signing-key accepts only the key the app trusts and never prints the private key', () => {
  const check = (keys) => node('check-signing-key.mjs', '--key', keyFile, '--keys', keys);
  const ok = check(keysFile);
  assert.equal(ok.status, 0); assert.equal(ok.stdout.trim(), 'test-key');
  const empty = join(dir, 'empty.json'); writeFileSync(empty, '{}');
  assert.equal(check(empty).status, 1);
  const other = join(dir, 'other2.json'); writeFileSync(other, JSON.stringify({ x: generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ type: 'spki', format: 'der' }).toString('base64') }));
  const bad = check(other); assert.equal(bad.status, 1); assert.doesNotMatch(bad.stdout + bad.stderr, /BEGIN|PRIVATE/);
  const junk = join(dir, 'junk.pem'); writeFileSync(junk, 'not a key');
  assert.equal(node('check-signing-key.mjs', '--key', junk, '--keys', keysFile).status, 1);
});
