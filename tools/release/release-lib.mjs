// Release-candidate helpers (D12/W12): a deterministic ZIP writer, checksums and the manifest. No dependencies, nothing is published.
import { createHash } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';

const crcTable = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
export function crc32(buffer) { let c = 0xffffffff; for (let i = 0; i < buffer.length; i++) c = crcTable[(c ^ buffer[i]) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
export const sha256 = (buffer) => createHash('sha256').update(buffer).digest('hex');

/** Entry names must be relative, forward-slashed, without "..", drive letters or duplicates (a hostile or buggy name must never reach an archive). */
export function checkEntryName(name) {
  if (!name || name.startsWith('/') || name.includes('\\') || /(^|\/)\.\.(\/|$)/.test(name) || /^[A-Za-z]:/.test(name) || name.includes('\0')) throw new Error(`Unsafe archive entry name: ${JSON.stringify(name)}`);
}

/**
 * Deterministic ZIP: entries sorted by name, fixed timestamp, UTF-8 names, deflate. The same inputs always give the same bytes, so a checksum
 * proves what was built. entries: [{ name, data: Buffer }]. Each entry < 4 GiB and the archive < 4 GiB (no ZIP64 needed for this product).
 */
export function writeZip(entries) {
  const seen = new Set();
  const sorted = [...entries].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const DOS_TIME = 0, DOS_DATE = (1 << 5) | 1 | ((2026 - 1980) << 9);   // 2026-01-01 00:00:00
  const parts = [], central = [];
  let offset = 0;
  for (const { name, data } of sorted) {
    checkEntryName(name);
    if (seen.has(name)) throw new Error(`Duplicate archive entry: ${name}`);
    seen.add(name);
    if (data.length >= 0xffffffff) throw new Error('Entry too large for a plain ZIP');
    const nameBytes = Buffer.from(name, 'utf8');
    const compressed = deflateRawSync(data, { level: 9 });
    const stored = compressed.length >= data.length;
    const body = stored ? data : compressed, method = stored ? 0 : 8, crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x0800, 6); local.writeUInt16LE(method, 8);
    local.writeUInt16LE(DOS_TIME, 10); local.writeUInt16LE(DOS_DATE, 12); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBytes.length, 26);
    parts.push(local, nameBytes, body);
    const header = Buffer.alloc(46);
    header.writeUInt32LE(0x02014b50, 0); header.writeUInt16LE(0x031e, 4); header.writeUInt16LE(20, 6); header.writeUInt16LE(0x0800, 8); header.writeUInt16LE(method, 10);
    header.writeUInt16LE(DOS_TIME, 12); header.writeUInt16LE(DOS_DATE, 14); header.writeUInt32LE(crc, 16);
    header.writeUInt32LE(body.length, 20); header.writeUInt32LE(data.length, 24); header.writeUInt16LE(nameBytes.length, 28);
    header.writeUInt32LE(0o100644 << 16 >>> 0, 38); header.writeUInt32LE(offset, 42);
    central.push(header, nameBytes);
    offset += 30 + nameBytes.length + body.length;
  }
  const centralBytes = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(sorted.length, 8); end.writeUInt16LE(sorted.length, 10);
  end.writeUInt32LE(centralBytes.length, 12); end.writeUInt32LE(offset, 16);
  if (offset + centralBytes.length >= 0xffffffff || sorted.length >= 0xffff) throw new Error('Archive too large for a plain ZIP');
  return Buffer.concat([...parts, centralBytes, end]);
}

/** "<hash>  <name>" lines, sorted, as `sha256sum -c` and PowerShell's Get-FileHash comparison expect. */
export function checksumFile(files) {
  return [...files].sort((a, b) => (a.name < b.name ? -1 : 1)).map((f) => `${sha256(f.data)}  ${f.name}`).join('\n') + '\n';
}
export function parseChecksums(text) {
  const map = new Map();
  for (const line of text.split('\n').filter(Boolean)) { const m = /^([0-9a-f]{64})  (.+)$/.exec(line); if (!m) throw new Error(`Bad checksum line: ${line}`); map.set(m[2], m[1]); }
  return map;
}

/**
 * The manifest says exactly what was built and what is NOT yet true. `evidence` statuses are never "passed" for things that need a person or a device.
 */
export function buildManifest({ version, commit, runId = null, builder, artifacts, signing = 'unsigned', generatedBy = 'tools/release/release-candidate.mjs' }) {
  if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) throw new Error(`Bad version: ${version}`);
  if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error('commit must be a full 40-character SHA');
  return {
    product: 'OrbitDrive', version, commit, ciRunId: runId, builder, generatedBy,
    status: 'release candidate awaiting owner-side evidence',
    published: false,
    signing: { state: signing, note: signing === 'unsigned' ? 'No production signing key exists yet: Windows SmartScreen will warn. Signing needs the owner\'s identity.' : 'signed' },
    awaiting: ['Windows 11 install/upgrade/uninstall on a standard-user machine', 'physical Android and iOS phone checks', 'production code-signing identity', 'owner-run acceptance script (docs/release/acceptance-checklist.md)'],
    artifacts: artifacts.map(({ name, size, sha256: h }) => ({ name, size, sha256: h })),
  };
}
