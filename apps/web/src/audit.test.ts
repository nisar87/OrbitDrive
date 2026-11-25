import { describe, expect, test } from 'vitest';
import { splitName } from './features/explorer/FileName';
import { needsFreshSignIn, DELETE_PHRASE } from './features/account/DataControls';
import { ApiError } from './api';

describe('file names (F-025)', () => {
  test('the extension is split from the stem, also for right-to-left names', () => {
    expect(splitName('report.pdf')).toEqual({ stem: 'report', extension: '.pdf' });
    expect(splitName('اردو فائل.txt')).toEqual({ stem: 'اردو فائل', extension: '.txt' });
    expect(splitName('archive.tar.gz')).toEqual({ stem: 'archive.tar', extension: '.gz' });
  });
  test('a leading dot, a trailing dot and no dot are not extensions', () => {
    expect(splitName('.gitignore')).toEqual({ stem: '.gitignore', extension: '' });
    expect(splitName('notes.')).toEqual({ stem: 'notes.', extension: '' });
    expect(splitName('README')).toEqual({ stem: 'README', extension: '' });
    expect(splitName('')).toEqual({ stem: '', extension: '' });
  });
  test('nothing is lost or reordered: stem plus extension is always the original name', () => {
    for (const name of ['a.b.c', '.x.y', 'x..', 'مستند.pdf', 'a b.c d', '...']) {
      const { stem, extension } = splitName(name);
      expect(stem + extension).toBe(name);
    }
  });
});

describe('export and delete need a recent sign-in (F-012)', () => {
  test('only the relay\'s "sign in again" answer is explained on the spot; other failures are not mistaken for it', () => {
    expect(needsFreshSignIn(new ApiError('unauthenticated', 'Sign out and sign in again before deleting your account.', 401))).toBe(true);
    expect(needsFreshSignIn(new ApiError('unauthenticated', 'Sign in required.', 401))).toBe(false);
    expect(needsFreshSignIn(new ApiError('forbidden', 'Sign out and sign in again', 403))).toBe(false);
    expect(needsFreshSignIn(new Error('Sign out and sign in again'))).toBe(false);
  });
  test('the confirmation phrase is the one the relay expects', () => {
    expect(DELETE_PHRASE).toBe('delete my account');
  });
});
