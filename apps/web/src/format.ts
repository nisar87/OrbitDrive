import { parseInt64 } from './protocol';

const UNITS = ['bytes', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB'];

/** Human size from an exact 64-bit string (binary multiples, Explorer-style labels). */
export function formatBytes(size: string): string {
  let value = parseInt64(size);
  if (value < 1024n) return `${value} bytes`;
  let unit = 0;
  let remainder = 0n;
  while (value >= 1024n && unit < UNITS.length - 1) { remainder = value % 1024n; value /= 1024n; unit++; }
  const tenths = (remainder * 10n) / 1024n;
  return value >= 100n || tenths === 0n ? `${value} ${UNITS[unit]}` : `${value}.${tenths} ${UNITS[unit]}`;
}

export function formatTime(unixMs: string): string {
  return new Date(Number(parseInt64(unixMs))).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
