import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { parseInt64, validate } from './protocol';
import { formatBytes } from './format';
import { parseManualCode, takePairingLinkFromLocation } from './features/pairing/pairingLink';
import { assertProductionSafe } from '../vite.config';

const fixture = (name: string): unknown => JSON.parse(readFileSync(fileURLToPath(new URL(`../../../tests/contracts/fixtures/${name}.json`, import.meta.url)), 'utf8'));

describe('shared protocol contract (same fixtures as the C# tests)', () => {
  it.each(['DirectoryPage', 'Envelope', 'ErrorResponse', 'DownloadSession'] as const)('accepts the %s fixture', (name) => {
    expect(() => validate(name, fixture(name))).not.toThrow();
  });

  it('keeps sizes above 2^53 exact', () => {
    const page = validate('DirectoryPage', fixture('DirectoryPage'));
    expect(parseInt64(page.entries[0].size)).toBe(9007199254740993n);
    expect(String(Number(page.entries[0].size))).not.toBe(page.entries[0].size); // why a JS number is never used
    expect(parseInt64('9223372036854775807')).toBe(9223372036854775807n);
    expect(parseInt64('-9223372036854775808')).toBe(-9223372036854775808n);
  });

  it.each(['9223372036854775808', '-9223372036854775809', '01', '+1', '1.0', '1e3', '', ' 1', '0x10'])('rejects non-canonical int64 %j', (text) => {
    expect(() => parseInt64(text)).toThrow();
  });

  it('rejects numeric sizes, unknown enum values and missing fields from an untrusted peer', () => {
    const session = fixture('DownloadSession') as Record<string, unknown>;
    expect(() => validate('DownloadSession', { ...session, size: 5368709120 })).toThrow(/size/);
    expect(() => validate('ErrorResponse', { error: { code: 'execute_command', message: 'x' } })).toThrow(/ErrorCode/);
    expect(() => validate('DirectoryPage', { entries: [], breadcrumbs: [] })).toThrow(/skippedLinks/);
    expect(() => validate('DirectoryPage', { entries: [{}], breadcrumbs: [], skippedLinks: 0 })).toThrow(/entries\[0\]/);
    expect(() => validate('PcListResponse', { pcs: 'nope' })).toThrow();
  });
});

describe('formatting', () => {
  it('formats exact sizes including values beyond Number precision', () => {
    expect(formatBytes('0')).toBe('0 bytes');
    expect(formatBytes('1023')).toBe('1023 bytes');
    expect(formatBytes('1536')).toBe('1.5 KB');
    expect(formatBytes('1073741824')).toBe('1 GB');
    expect(formatBytes('5368709120')).toBe('5 GB');
    expect(formatBytes('9007199254740993')).toBe('8 PB');
  });
});

describe('pairing links', () => {
  it('parses manual codes and rejects malformed ones', () => {
    expect(parseManualCode(' abcdef-ghjkmnpqrs ')).toEqual({ id: 'ABCDEF', secret: 'GHJKMNPQRS' });
    for (const bad of ['', 'ABCDEF', 'ABCDEF-SHORT', 'ABCDE1-GHJKMNPQRS', '../../x-GHJKMNPQRS', 'ABCDEF-GHJKMNPQRS-EXTRA']) expect(parseManualCode(bad)).toBeNull();
  });

  it('takes the secret from the fragment and scrubs it from history', () => {
    const calls: unknown[][] = [];
    const history = { replaceState: (...args: unknown[]) => { calls.push(args); } } as unknown as History;
    const location = { pathname: '/pair', hash: '#id=ABCDEF&s=GHJKMNPQRS' } as Location;
    expect(takePairingLinkFromLocation(location, history)).toEqual({ id: 'ABCDEF', secret: 'GHJKMNPQRS' });
    expect(calls).toEqual([[null, '', '/pair']]);
    expect(takePairingLinkFromLocation({ pathname: '/', hash: '#id=ABCDEF&s=GHJKMNPQRS' } as Location, history)).toBeNull();
  });
});

describe('production build guard', () => {
  const good = { VITE_AUTH_MODE: 'firebase', VITE_GATEWAY_URL: 'https://relay.orbitdrive.example', VITE_FIREBASE_API_KEY: 'public-web-key', VITE_FIREBASE_AUTH_DOMAIN: 'orbitdrive.firebaseapp.com', VITE_FIREBASE_PROJECT_ID: 'orbitdrive', VITE_FIREBASE_APP_ID: '1:1:web:1' };
  it('accepts a real configuration and ignores non-production modes', () => {
    expect(() => assertProductionSafe('production', good)).not.toThrow();
    expect(() => assertProductionSafe('e2e', { VITE_AUTH_MODE: 'dev' })).not.toThrow();
  });
  it.each([
    [{ ...good, VITE_AUTH_MODE: 'dev' }, /development identities/],
    [{ ...good, VITE_AUTH_MODE: undefined }, /development identities/],
    [{ ...good, VITE_GATEWAY_URL: 'http://relay.example' }, /https origin/],
    [{ ...good, VITE_GATEWAY_URL: 'https://localhost' }, /local or emulator/],
    [{ ...good, VITE_GATEWAY_URL: 'https://x.web.app/api' }, /https origin/],
    [{ ...good, VITE_USE_EMULATORS: 'true' }, /emulator settings/],
    [{ ...good, VITE_FIREBASE_AUTH_EMULATOR_URL: 'http://127.0.0.1:9099' }, /emulator settings/],
    [{ ...good, VITE_FIREBASE_PROJECT_ID: 'demo-orbitdrive' }, /real project/],
    [{ ...good, VITE_FIREBASE_API_KEY: undefined }, /VITE_FIREBASE_API_KEY is required/],
  ])('rejects %j', (env, message) => {
    expect(() => assertProductionSafe('production', env)).toThrow(message);
  });
});
