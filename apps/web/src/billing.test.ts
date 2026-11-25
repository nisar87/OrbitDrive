import { describe, expect, it } from 'vitest';
import { formatPkr, formatSize, formatUsd, limitText, monthsLabel, percentUsed, priceLabel, GIB, MIB } from './billing';

describe('prices', () => {
  it('shows dollars first with rupees beside them, approximate only when derived', () => {
    expect(priceLabel({ usd: 3.5, pkr: 1000, pkrApprox: false })).toBe('$3.50 (PKR 1,000)');
    expect(priceLabel({ usd: 29, pkr: 8200, pkrApprox: true })).toBe('$29 (≈ PKR 8,200)');
    expect(formatUsd(11)).toBe('$11');
    expect(formatPkr(24000, false)).toBe('PKR 24,000');
  });
  it('names pass lengths', () => {
    expect([1, 3, 12].map(monthsLabel)).toEqual(['1 month', '3 months', '12 months']);
  });
});

describe('limits', () => {
  it('writes sizes and no-limit', () => {
    expect(formatSize(100 * MIB)).toBe('100 MB');
    expect(formatSize(1 * GIB)).toBe('1 GB');
    expect(formatSize(1.5 * GIB)).toBe('1.5 GB');
    expect(limitText(null, 'count')).toBe('No limit');
    expect(limitText(5, 'count')).toBe('5');
    expect(limitText(2 * GIB, 'bytes')).toBe('2 GB');
  });
  it('measures how full a limit is and never divides by nothing', () => {
    expect(percentUsed(3, 5)).toBe(60);
    expect(percentUsed(9, 5)).toBe(100);
    expect(percentUsed(3, null)).toBeNull();
    expect(percentUsed(3, 0)).toBeNull();
  });
});
