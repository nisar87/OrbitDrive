export * from '../../../../packages/protocol/generated/ts/protocol.g';
import { SCHEMA, type ProtocolTypeMap } from '../../../../packages/protocol/generated/ts/protocol.g';

const INT64 = /^(0|-?[1-9][0-9]{0,18})$/;
const MAX = 9223372036854775807n;

/** Parses the wire encoding of a 64-bit integer without ever passing through a JS number. */
export function parseInt64(value: string): bigint {
  if (typeof value !== 'string' || !INT64.test(value)) throw new TypeError('Invalid 64-bit integer string');
  const parsed = BigInt(value);
  if (parsed > MAX || parsed < -MAX - 1n) throw new TypeError('64-bit integer out of range');
  return parsed;
}

type TypeName = keyof typeof SCHEMA.types;
const enums = SCHEMA.enums as Record<string, readonly string[]>;
const types = SCHEMA.types as Record<string, Record<string, string>>;

function check(spec: string, value: unknown, path: string): void {
  let base = spec;
  const optional = base.endsWith('?');
  if (optional) base = base.slice(0, -1);
  if (value === undefined || value === null) {
    if (optional) return;
    throw new TypeError(`${path}: required`);
  }
  if (base.endsWith('[]')) {
    if (!Array.isArray(value)) throw new TypeError(`${path}: expected array`);
    value.forEach((item, index) => check(base.slice(0, -2), item, `${path}[${index}]`));
    return;
  }
  switch (base) {
    case 'string': if (typeof value !== 'string') throw new TypeError(`${path}: expected string`); return;
    case 'bool': if (typeof value !== 'boolean') throw new TypeError(`${path}: expected boolean`); return;
    case 'int32': if (typeof value !== 'number' || !Number.isInteger(value) || Math.abs(value) > 2147483647) throw new TypeError(`${path}: expected int32`); return;
    case 'int64': try { parseInt64(value as string); } catch { throw new TypeError(`${path}: expected 64-bit integer string`); } return;
    case 'json': return;
  }
  if (enums[base]) {
    if (typeof value !== 'string' || !enums[base].includes(value)) throw new TypeError(`${path}: unknown ${base} value`);
    return;
  }
  const fields = types[base];
  if (!fields || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(`${path}: expected ${base}`);
  for (const [field, fieldSpec] of Object.entries(fields)) check(fieldSpec, (value as Record<string, unknown>)[field], `${path}.${field}`);
}

/** Validates an untrusted JSON value against the generated schema; unknown extra fields are tolerated. */
export function validate<K extends TypeName & keyof ProtocolTypeMap>(type: K, value: unknown): ProtocolTypeMap[K] {
  check(type, value, type);
  return value as ProtocolTypeMap[K];
}
