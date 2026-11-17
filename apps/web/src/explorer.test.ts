import { describe, expect, it } from 'vitest';
import type { FileEntry } from './protocol';
import { emptySelection, filterEntries, pruneSelection, select, sortEntries, totalSize, typeLabel } from './features/explorer/model';
import { defaultPrefs, parsePrefs } from './prefs';

const entry = (name: string, kind: 'file' | 'directory' = 'file', size = '0', modified = '0', hidden = false): FileEntry =>
  ({ entryId: 'id-' + name, name, kind, size, modifiedUnixMs: modified, hidden });
const names = (list: FileEntry[]) => list.map((e) => e.name);

describe('explorer sorting', () => {
  const list = [entry('b.txt', 'file', '20', '300'), entry('Zeta', 'directory', '0', '100'), entry('a10.txt', 'file', '9007199254740993', '200'),
    entry('a2.txt', 'file', '9007199254740992', '400'), entry('alpha', 'directory', '0', '500'), entry('README', 'file', '5', '50')];

  it('keeps folders first and sorts names the way people read numbers', () => {
    expect(names(sortEntries(list, 'name', false))).toEqual(['alpha', 'Zeta', 'a2.txt', 'a10.txt', 'b.txt', 'README']);
    expect(names(sortEntries(list, 'name', true))).toEqual(['Zeta', 'alpha', 'README', 'b.txt', 'a10.txt', 'a2.txt']);
  });

  it('compares sizes exactly, beyond what a JavaScript number can hold', () => {
    expect(names(sortEntries(list, 'size', true)).slice(2)).toEqual(['a10.txt', 'a2.txt', 'b.txt', 'README']);
    expect(names(sortEntries(list, 'size', false)).slice(0, 2)).toEqual(['alpha', 'Zeta']); // folders have no size: by name
  });

  it('sorts by modified time and by type, without changing the input', () => {
    const copy = [...list];
    expect(names(sortEntries(list, 'modified', false))).toEqual(['Zeta', 'alpha', 'README', 'a10.txt', 'b.txt', 'a2.txt']);
    expect(names(sortEntries(list, 'type', false))).toEqual(['alpha', 'Zeta', 'README', 'a2.txt', 'a10.txt', 'b.txt']);
    expect(list).toEqual(copy);
  });
});

describe('explorer labels, filter and totals', () => {
  it('derives the type only from the real name', () => {
    expect(typeLabel(entry('Photos', 'directory'))).toBe('Folder');
    expect(typeLabel(entry('report.PDF'))).toBe('PDF file');
    expect(typeLabel(entry('archive.tar.gz'))).toBe('GZ file');
    expect(typeLabel(entry('.gitignore'))).toBe('File');
    expect(typeLabel(entry('README'))).toBe('File');
    expect(typeLabel(entry('trailing.'))).toBe('File');
    expect(typeLabel(entry('notes.my long ext'))).toBe('File');
  });

  it('filters by name, case-insensitively, in any script, and hides hidden files unless asked', () => {
    const list = [entry('اردو فائل.txt'), entry('Emoji 😀 Notes.txt'), entry('desktop.ini', 'file', '0', '0', true)];
    expect(names(filterEntries(list, 'فائل', false))).toEqual(['اردو فائل.txt']);
    expect(names(filterEntries(list, '  NOTES ', false))).toEqual(['Emoji 😀 Notes.txt']);
    expect(names(filterEntries(list, '', false))).toHaveLength(2);
    expect(names(filterEntries(list, '', true))).toHaveLength(3);
    expect(filterEntries(list, 'nothing-matches', true)).toEqual([]);
  });

  it('adds file sizes exactly and ignores folders', () => {
    expect(totalSize([entry('a', 'file', '9007199254740993'), entry('b', 'file', '7'), entry('d', 'directory', '999')])).toBe(9007199254741000n);
  });
});

describe('explorer selection', () => {
  const order = ['a', 'b', 'c', 'd', 'e'];
  it('selects one, toggles and extends a range from the anchor', () => {
    let s = select(emptySelection, order, 'b', 'single');
    expect([...s.ids]).toEqual(['b']);
    s = select(s, order, 'd', 'range');
    expect([...s.ids]).toEqual(['b', 'c', 'd']);
    s = select(s, order, 'a', 'range');                       // the anchor stays where the range started
    expect([...s.ids]).toEqual(['a', 'b']);
    s = select(s, order, 'e', 'toggle');
    expect([...s.ids].sort()).toEqual(['a', 'b', 'e']);
    s = select(s, order, 'a', 'toggle');
    expect([...s.ids].sort()).toEqual(['b', 'e']);
    s = select(s, order, 'c', 'single');
    expect([...s.ids]).toEqual(['c']);
  });

  it('a range with no usable anchor selects just the item, and vanished items are dropped', () => {
    expect([...select(emptySelection, order, 'c', 'range').ids]).toEqual(['c']);
    expect([...select({ ids: new Set(['zz']), anchor: 'zz' }, order, 'c', 'range').ids]).toEqual(['c']);
    const pruned = pruneSelection({ ids: new Set(['a', 'x']), anchor: 'x' }, order);
    expect([...pruned.ids]).toEqual(['a']);
    expect(pruned.anchor).toBeNull();
    const same = { ids: new Set(['a']), anchor: 'a' };
    expect(pruneSelection(same, order)).toBe(same);
  });
});

describe('stored preferences', () => {
  it('falls back to defaults for anything missing, malformed or hostile', () => {
    expect(parsePrefs(null)).toEqual(defaultPrefs);
    expect(parsePrefs('not json')).toEqual(defaultPrefs);
    expect(parsePrefs('[1,2]')).toEqual(defaultPrefs);
    const odd = parsePrefs(JSON.stringify({ theme: 'neon', view: '<script>', sortKey: 'size', sortDescending: 'yes', treeOpen: false,
      windows: { explorer: { x: 10, y: '5', w: 1e999, h: 300, maximized: 1 }, '__proto__': { x: 1 }, 'Bad Id!': { x: 1 } } }));
    expect(odd.theme).toBe('system');
    expect(odd.view).toBe('details');
    expect(odd.sortKey).toBe('size');
    expect(odd.sortDescending).toBe(false);
    expect(odd.treeOpen).toBe(false);
    expect(Object.keys(odd.windows)).toEqual(['explorer']);
    expect(odd.windows.explorer).toEqual({ x: 10, y: 0, w: 0, h: 300, maximized: false });
  });
});
