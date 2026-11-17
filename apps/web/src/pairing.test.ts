import { describe, expect, it } from 'vitest';
import QRCode from 'qrcode';
import jsQR from 'jsqr';
import { parseScannedText } from './features/pairing/pairingLink';

const site = 'https://orbitdrive.example';
const link = { id: 'ABCDEF', secret: 'GHJKMNPQRS' };

describe('scanned QR text', () => {
  it('accepts this site\'s pairing link and the bare manual code', () => {
    expect(parseScannedText(`${site}/pair#id=ABCDEF&s=GHJKMNPQRS`, site)).toEqual({ link });
    expect(parseScannedText('  abcdef-ghjkmnpqrs \n', site)).toEqual({ link });
  });

  it('explains a code that belongs to a different site instead of using or opening it', () => {
    const result = parseScannedText('https://evil.example/pair#id=ABCDEF&s=GHJKMNPQRS', site);
    expect(result.link).toBeNull();
    expect(result.problem).toMatch(/evil\.example/);
    expect(result.problem).toMatch(/orbitdrive\.example/);
  });

  it.each([
    'https://orbitdrive.example/',                                  // not a pairing link
    'https://orbitdrive.example/pair',                              // no secret
    'https://orbitdrive.example/pair?id=ABCDEF&s=GHJKMNPQRS',       // secret in the query would reach servers and logs
    'https://orbitdrive.example/pair#id=ABCDEF&s=short',
    'https://orbitdrive.example/pair#id=ABCDE1&s=GHJKMNPQRS',       // characters outside the code alphabet
    'https://orbitdrive.example.evil.test/pair#id=ABCDEF&s=GHJKMNPQRS',
    'javascript:alert(1)//pair#id=ABCDEF&s=GHJKMNPQRS',
    'orbitdrive://pair#id=ABCDEF&s=GHJKMNPQRS',
    'WIFI:T:WPA;S:cafe;P:secret;;',
    '',
    'x'.repeat(5000),
  ])('refuses %j', (text) => {
    const result = parseScannedText(text, site);
    expect(result.link).toBeNull();
    expect(result.problem).toBeTruthy();
  });

  it('the bundled fallback decoder reads a real pairing QR image', () => {
    const text = `${site}/pair#id=ABCDEF&s=GHJKMNPQRS`;
    const modules = QRCode.create(text, { errorCorrectionLevel: 'M' }).modules;
    const scale = 5, quiet = 4, side = (modules.size + quiet * 2) * scale;
    const pixels = new Uint8ClampedArray(side * side * 4).fill(255);
    for (let y = 0; y < modules.size; y++) for (let x = 0; x < modules.size; x++) {
      if (!modules.get(y, x)) continue;
      for (let dy = 0; dy < scale; dy++) for (let dx = 0; dx < scale; dx++) {
        const offset = (((y + quiet) * scale + dy) * side + (x + quiet) * scale + dx) * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
      }
    }
    const decoded = jsQR(pixels, side, side)?.data ?? '';
    expect(parseScannedText(decoded, site)).toEqual({ link });
  });
});
