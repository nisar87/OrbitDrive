import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { crc32, sha256, writeZip, checksumFile, parseChecksums, buildManifest, checkEntryName } from '../../tools/release/release-lib.mjs';
import { readZip } from '../integration/harness.mjs';

const files = [
  { name: 'orbitdrive-agent.exe', data: Buffer.alloc(300_000, 7) },
  { name: 'docs/INSTALL.md', data: Buffer.from('install — ünïcode ✓\n') },
  { name: 'A.txt', data: Buffer.from('') },
];

test('crc32 matches the standard check value', () => { assert.equal(crc32(Buffer.from('123456789')), 0xcbf43926); });

test('the zip is deterministic, sorted, UTF-8 flagged, and round-trips byte for byte', () => {
  const a = writeZip(files), b = writeZip([...files].reverse());
  assert.equal(sha256(a), sha256(b), 'same inputs, same bytes, any input order');
  const entries = readZip(a);
  assert.deepEqual(entries.map((e) => e.name), ['A.txt', 'docs/INSTALL.md', 'orbitdrive-agent.exe']);
  for (const e of entries) {
    const original = files.find((f) => f.name === e.name).data;
    assert.equal(sha256(e.data()), sha256(original), e.name);   // data() also verifies size and CRC
  }
});

test('an independent reader (python zipfile) accepts the archive and agrees on contents', (t) => {
  const py = spawnSync('python3', ['--version']);
  if (py.status !== 0) return t.skip('python3 not available');
  const dir = mkdtempSync(join(tmpdir(), 'orbit-zip-')), file = join(dir, 'a.zip');
  writeFileSync(file, writeZip(files));
  const script = 'import zipfile,hashlib,sys\nz=zipfile.ZipFile(sys.argv[1])\nassert z.testzip() is None\nfor i in z.infolist(): print(i.filename, hashlib.sha256(z.read(i)).hexdigest())\n';
  const out = spawnSync('python3', ['-I', '-c', script, file], { encoding: 'utf8' });
  assert.equal(out.status, 0, out.stderr);
  for (const f of files) assert.ok(out.stdout.includes(`${f.name} ${sha256(f.data)}`), f.name);
});

test('unsafe or duplicate entry names are refused (security)', () => {
  for (const bad of ['../x', '/abs', 'a/../b', 'C:\\x', 'a\\b', '', 'x\0y']) assert.throws(() => checkEntryName(bad), /Unsafe/, JSON.stringify(bad));
  assert.throws(() => writeZip([{ name: 'a', data: Buffer.alloc(1) }, { name: 'a', data: Buffer.alloc(1) }]), /Duplicate/);
});

test('checksum file round-trips and detects a changed byte', () => {
  const text = checksumFile(files);
  const parsed = parseChecksums(text);
  for (const f of files) assert.equal(parsed.get(f.name), sha256(f.data));
  const tampered = Buffer.from(files[0].data); tampered[10] ^= 1;
  assert.notEqual(parsed.get(files[0].name), sha256(tampered));
  assert.throws(() => parseChecksums('not a checksum line\n'), /Bad checksum/);
});

test('manifest is honest: unpublished, unsigned, lists what is still awaited; refuses bad version or short commit', () => {
  const m = buildManifest({ version: '0.1.0', commit: 'a'.repeat(40), builder: { os: 'windows-2025' }, artifacts: [{ name: 'x.zip', size: 3, sha256: 'b'.repeat(64) }] });
  assert.equal(m.published, false);
  assert.equal(m.signing.state, 'unsigned');
  assert.match(m.status, /awaiting/);
  assert.ok(m.awaiting.some((a) => /Windows 11/.test(a)) && m.awaiting.some((a) => /Android and iOS/.test(a)) && m.awaiting.some((a) => /signing/.test(a)));
  assert.throws(() => buildManifest({ version: 'v1', commit: 'a'.repeat(40), builder: {}, artifacts: [] }), /version/);
  assert.throws(() => buildManifest({ version: '1.0.0', commit: 'abc', builder: {}, artifacts: [] }), /commit/);
});
