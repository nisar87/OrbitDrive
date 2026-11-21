import { describe, expect, it } from 'vitest';
import { fileKindOf } from './features/explorer/fileKind';

describe('fileKindOf', () => {
  it.each([
    ['photo.JPG', 'image'], ['a.pdf', 'pdf'], ['song.mp3', 'audio'], ['clip.MKV', 'video'], ['notes.md', 'text'], ['main.rs', 'code'],
    ['backup.tar', 'archive'], ['cv.docx', 'document'], ['budget.xlsx', 'sheet'], ['deck.pptx', 'slides'], ['setup.exe', 'app'],
  ])('%s is %s', (name, kind) => expect(fileKindOf(name)).toBe(kind));

  it.each(['README', 'trailingdot.', '.gitignore-ish', 'weird.unknownext', ''])('%j falls back to a plain icon', (name) => expect(fileKindOf(name)).toBe('other'));

  it('uses the last extension and never throws on odd names', () => {
    expect(fileKindOf('archive.tar.gz')).toBe('archive');
    expect(fileKindOf('evil.pdf.exe')).toBe('app');
    expect(fileKindOf('a‮gnp.exe')).toBe('app');
    expect(fileKindOf('x'.repeat(5000) + '.png')).toBe('image');
  });
});
