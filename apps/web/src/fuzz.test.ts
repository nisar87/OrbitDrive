import { describe, expect, it } from 'vitest';
import { SCHEMA, validate } from './protocol';

// Seeded fuzzing of the validator that every answer from the service passes through. Replay with ORBIT_FUZZ_SEED.
const SEED = Number(process.env.ORBIT_FUZZ_SEED ?? 20261119);
const ROUNDS = Number(process.env.ORBIT_FUZZ_ROUNDS ?? 40);
let state = SEED >>> 0;
const rng = () => { state = (state + 0x6d2b79f5) >>> 0; let t = state; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
const pick = <T,>(list: readonly T[]): T => list[Math.floor(rng() * list.length)];

const enums = SCHEMA.enums as Record<string, readonly string[]>;
const types = SCHEMA.types as Record<string, Record<string, string>>;

/** A minimal valid value for a schema spec (optional fields omitted, recursion bounded). */
function sample(spec: string, depth = 0): unknown {
  const base = spec.replace(/\?$/, '');
  if (base.endsWith('[]')) return depth > 3 ? [] : [sample(base.slice(0, -2), depth + 1)];
  switch (base) { case 'string': return 'x'; case 'bool': return true; case 'int32': return 1; case 'int64': return '1'; case 'json': return {}; }
  if (enums[base]) return enums[base][0];
  const out: Record<string, unknown> = {};
  for (const [field, fieldSpec] of Object.entries(types[base] ?? {})) if (!fieldSpec.endsWith('?')) out[field] = sample(fieldSpec, depth + 1);
  return out;
}
const HOSTILE: unknown[] = [null, undefined, true, 0, -1, 1.5, NaN, Infinity, 2 ** 53, '', 'x'.repeat(100_000), '9223372036854775808', '-0', '01', [], [null], {}, { __proto__: { admin: true } }, JSON.parse('{"__proto__":{"admin":true},"constructor":{"prototype":{"p":1}}}'), Symbol.iterator.toString(), new Date(0), () => 1];

function corrupt(value: unknown, depth = 0): unknown {
  if (value === null || typeof value !== 'object') return pick(HOSTILE);
  const copy: Record<string, unknown> | unknown[] = Array.isArray(value) ? [...value] : { ...(value as object) };
  const keys = Object.keys(copy);
  if (keys.length === 0) return pick(HOSTILE);
  const key = pick(keys);
  const roll = rng();
  if (roll < 0.3) delete (copy as Record<string, unknown>)[key];
  else if (roll < 0.7 || depth > 3) (copy as Record<string, unknown>)[key] = pick(HOSTILE);
  else (copy as Record<string, unknown>)[key] = corrupt((copy as Record<string, unknown>)[key], depth + 1);
  return copy;
}

const names = Object.keys(types);
describe('validator fuzzing (seeded)', () => {
  it('accepts a minimal valid sample of every protocol type', () => {
    for (const name of names) expect(() => validate(name as never, sample(name)), name).not.toThrow();
  });

  it('only ever refuses with a TypeError (never another exception, never a hang) and never changes the input or Object.prototype', () => {
    const before = Object.getOwnPropertyNames(Object.prototype).sort().join();
    for (const name of names) {
      for (let i = 0; i < ROUNDS; i++) {
        const input = corrupt(sample(name));
        const snapshot = JSON.stringify(input, (_k, v) => (typeof v === 'bigint' ? String(v) : v));
        try { validate(name as never, input); } catch (error) { expect(error, `${name} seed ${SEED}`).toBeInstanceOf(TypeError); }
        expect(JSON.stringify(input, (_k, v) => (typeof v === 'bigint' ? String(v) : v))).toBe(snapshot);
      }
    }
    expect(Object.getOwnPropertyNames(Object.prototype).sort().join()).toBe(before);
    expect(({} as Record<string, unknown>).admin).toBeUndefined();
  });

  it('refuses every top-level value that is not an object for object types, and every missing required field', () => {
    for (const name of names) {
      for (const bad of [null, undefined, 0, 'x', true, [], [{}]]) expect(() => validate(name as never, bad), name).toThrow(TypeError);
      for (const [field, spec] of Object.entries(types[name])) {
        if (spec.endsWith('?')) continue;
        const input = sample(name) as Record<string, unknown>; delete input[field];
        expect(() => validate(name as never, input), `${name}.${field}`).toThrow(TypeError);
      }
    }
  });

  it('survives absurdly deep or wide untrusted JSON in tolerated places without a stack overflow', () => {
    const deep = JSON.parse('['.repeat(5000) + ']'.repeat(5000));
    for (const name of names) { try { validate(name as never, { ...(sample(name) as object), extra: deep }); } catch (error) { expect(error).toBeInstanceOf(TypeError); } }
    const wide = Object.fromEntries(Array.from({ length: 50_000 }, (_, i) => [`k${i}`, i]));
    expect(() => validate('Ack', { ok: true, ...wide })).not.toThrow();
  });
});
