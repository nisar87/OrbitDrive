// The pairing QR drawn by the Windows console must be a standards-conformant code. Two independent checks:
// (1) module-for-module equality with the reference "qrcode" encoder, (2) a real decoder ("jsQR") reads the text back.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import QRCode from 'qrcode';
import jsQR from 'jsqr';
import { agentCliRaw, tempDir } from './harness.mjs';

const state = tempDir('qr');
function encode(text, mask) {
  const result = agentCliRaw(state, 'qr', text, ...(mask === undefined ? [] : [String(mask)]));
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}
function decode(qr) {
  const scale = 4, quiet = 4, side = (qr.size + quiet * 2) * scale;
  const pixels = new Uint8ClampedArray(side * side * 4).fill(255);
  for (let y = 0; y < qr.size; y++) for (let x = 0; x < qr.size; x++) {
    if (qr.rows[y][x] !== '1') continue;
    for (let dy = 0; dy < scale; dy++) for (let dx = 0; dx < scale; dx++) {
      const offset = (((y + quiet) * scale + dy) * side + (x + quiet) * scale + dx) * 4;
      pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
    }
  }
  return jsQR(pixels, side, side)?.data;
}

const samples = [
  'A',
  'http://localhost:5173/pair#id=ABCDEF&s=GHJKMNPQRS',
  'https://orbitdrive-app.web.app/pair#id=Z2X3C4&s=Q9W8E7R6T5',
  'https://files.example-company-name.co.uk/pair#id=ABCDEF&s=GHJKMNPQRS&utm=' + 'x'.repeat(40),
  'اردو and emoji 😀 survive as UTF-8 bytes',
  'v'.repeat(120), 'w'.repeat(150), 'x'.repeat(180), 'y'.repeat(213),
];

for (const text of samples) {
  test(`QR for ${text.length}-char text matches the reference encoder and decodes`, () => {
    const mine = encode(text);
    assert.equal(mine.size, mine.version * 4 + 17);
    const reference = QRCode.create([{ data: Buffer.from(text, 'utf8'), mode: 'byte' }], { errorCorrectionLevel: 'M', version: mine.version, maskPattern: mine.mask });
    assert.equal(reference.modules.size, mine.size);
    const expected = [];
    for (let y = 0; y < mine.size; y++) expected.push(Array.from(reference.modules.data.subarray(y * mine.size, (y + 1) * mine.size)).join(''));
    assert.deepEqual(mine.rows, expected, 'every module equals the reference encoder');
    assert.equal(decode(mine), text, 'a real decoder reads the exact text back');
  });
}

test('every mask pattern is drawn correctly and decodes', () => {
  const text = 'https://orbitdrive.example/pair#id=ABCDEF&s=GHJKMNPQRS';
  for (let mask = 0; mask < 8; mask++) {
    const mine = encode(text, mask);
    assert.equal(mine.mask, mask);
    assert.equal(decode(mine), text, `mask ${mask}`);
  }
});

test('the smallest version that fits is used, and oversized text is refused rather than truncated', () => {
  assert.equal(encode('x'.repeat(14)).version, 1);
  assert.equal(encode('x'.repeat(15)).version, 2);
  assert.equal(encode('x'.repeat(213)).version, 10);
  assert.notEqual(agentCliRaw(state, 'qr', 'x'.repeat(214)).status, 0);
});
