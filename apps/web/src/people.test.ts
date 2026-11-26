import { describe, expect, it } from 'vitest';
import { accountLabel, buildPeople, lookup, NOT_KNOWN, searchText, shortUid, uidOfSubject } from './features/console/people';

describe('who an account is in the operator console', () => {
  const people = buildPeople([{ uid: 'dev-0123456789abcdef0123', email: 'Sam@Example.test', name: 'Sam Example' }, { uid: 'u-2', email: 'two@example.test', name: null }]);

  it('shortens ids but keeps short ones whole', () => {
    expect(shortUid('dev-0123456789abcdef0123')).toBe('dev-01…0123');
    expect(shortUid('abc')).toBe('abc');
  });
  it('names an account by name, email and short id, and never leaves it blank when the email is unknown', () => {
    expect(accountLabel(people, 'dev-0123456789abcdef0123')).toBe('Sam Example, Sam@Example.test (dev-01…0123)');
    expect(accountLabel(people, 'u-2')).toBe('two@example.test (u-2)');
    expect(accountLabel(people, 'nobody-known-here-xyz')).toBe(`${shortUid('nobody-known-here-xyz')} (email not known yet)`);
    expect(lookup(people, 'nobody').email).toBeNull();
    expect(NOT_KNOWN).toBe('Email not known yet (shows after their next sign-in)');
  });
  it('search text holds the email, the name and the full id, so a search by any of them finds the account', () => {
    const text = searchText(people, 'dev-0123456789abcdef0123').toLowerCase();
    for (const part of ['sam@example.test', 'sam example', 'dev-0123456789abcdef0123']) expect(text).toContain(part);
  });
  it('rows that carry an email override the directory, and rows without one do not erase it', () => {
    const merged = buildPeople([{ uid: 'u-2', email: 'old@example.test', name: null }], [{ uid: 'u-2', email: 'new@example.test', name: 'Two' }, { uid: 'u-3', email: null }]);
    expect(lookup(merged, 'u-2').email).toBe('new@example.test');
    expect(lookup(merged, 'u-3').email).toBeNull();
  });
  it('reads an account id from a billing subject and leaves other targets alone', () => {
    expect(uidOfSubject('acct:abc')).toBe('abc');
    expect(uidOfSubject('service')).toBe('service');
  });
});
