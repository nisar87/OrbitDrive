import { describe, expect, test } from 'vitest';
import { displayName } from './features/explorer/FileName';
import { needsFreshSignIn, DELETE_PHRASE } from './features/account/DataControls';
import { ApiError } from './api';

describe('file names (F-025)', () => {
  test('ordinary names are drawn unchanged, right-to-left names too', () => {
    for (const name of ['report.pdf', 'اردو فائل.txt', 'archive.tar.gz', '.gitignore', 'a b.c d', '文件.docx', '']) expect(displayName(name)).toBe(name);
  });
  test('characters that reorder text are not drawn, so a name cannot be made to look like another type', () => {
    expect(displayName('photo\u202Egpj.exe')).toBe('photogpj.exe');
    expect(displayName('a\u2066b\u2069c\u202Ad\u202Ce')).toBe('abcde');
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
