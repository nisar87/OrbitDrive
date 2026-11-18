// W8: favorites/recents, filename search across PCs and ZIP preparation — the pure and store logic, with the network mocked.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ArchiveStatus, PcSummary, SearchResult } from './protocol';
import { addRecent, forgetPc, groupByPc, isFavorite, MAX_FAVORITES, MAX_RECENTS, parsePlaces, placeKey, removeFavorite, toggleFavorite, type Place, type Places } from './features/explorer/places';

const place = (name: string, patch: Partial<Place> = {}): Place => ({ pcId: 'pc1', rootId: 'r1', entryId: 'e-' + name, name, kind: 'file', path: ['Docs'], at: 1, ...patch });
const none: Places = { favorites: [], recents: [] };

describe('favorites and recents', () => {
  it('toggle a favorite on and off by place, not by the (re-issued) entry id', () => {
    const a = place('report.txt');
    const on = toggleFavorite(none, a);
    const relisted: Place = { ...a, entryId: 'a-different-listing' };
    expect(isFavorite(on, relisted)).toBe(true);
    expect(toggleFavorite(on, relisted).favorites).toEqual([]);
  });
  it('the same name on two PCs, or in two folders, are different places', () => {
    const a = place('report.txt'), b = place('report.txt', { pcId: 'pc2' }), c = place('report.txt', { path: ['Other'] }), d = place('report.txt', { rootId: 'r2' });
    expect(new Set([a, b, c, d].map(placeKey)).size).toBe(4);
    expect(toggleFavorite(toggleFavorite(none, a), b).favorites).toHaveLength(2);
  });
  it('recents are newest first, de-duplicated and bounded', () => {
    let places = none;
    for (let i = 0; i < MAX_RECENTS + 5; i++) places = addRecent(places, place(`f${i}`));
    expect(places.recents).toHaveLength(MAX_RECENTS);
    expect(places.recents[0].name).toBe(`f${MAX_RECENTS + 4}`);
    places = addRecent(places, place(`f${MAX_RECENTS + 2}`, { entryId: 'fresh-id' }));
    expect(places.recents.filter((p) => p.name === `f${MAX_RECENTS + 2}`)).toHaveLength(1);
    expect(places.recents[0].entryId).toBe('fresh-id');
  });
  it('favorites are bounded', () => {
    let places = none;
    for (let i = 0; i < MAX_FAVORITES + 10; i++) places = toggleFavorite(places, place(`f${i}`));
    expect(places.favorites).toHaveLength(MAX_FAVORITES);
  });
  it('removing and forgetting a PC only touch that PC', () => {
    const base: Places = { favorites: [place('a'), place('b', { pcId: 'pc2' })], recents: [place('c'), place('d', { pcId: 'pc2' })] };
    expect(forgetPc(base, 'pc1')).toEqual({ favorites: [base.favorites[1]], recents: [base.recents[1]] });
    expect(removeFavorite(base, base.favorites[0]).favorites).toEqual([base.favorites[1]]);
  });
  it('stored data is read defensively: junk, wrong types, oversize and extra fields are dropped', () => {
    expect(parsePlaces(null)).toEqual(none);
    expect(parsePlaces('not json')).toEqual(none);
    expect(parsePlaces('[]')).toEqual(none);
    const raw = JSON.stringify({
      favorites: [place('ok'), { pcId: 1 }, { ...place('x'), kind: 'socket' }, { ...place('y'), name: 'n'.repeat(300) }, { ...place('z'), path: ['a', 5, 'b'] }, null, 'str'],
      recents: 'nope',
    });
    const parsed = parsePlaces(raw);
    expect(parsed.favorites.map((p) => p.name)).toEqual(['ok', 'z']);
    expect(parsed.favorites[1].path).toEqual(['a', 'b']);
    expect(parsed.recents).toEqual([]);
  });
  it('groups places per PC so ids are only ever sent to the PC they belong to', () => {
    const groups = groupByPc([place('a'), place('b', { pcId: 'pc2' }), place('c')]);
    expect([...groups.keys()]).toEqual(['pc1', 'pc2']);
    expect(groups.get('pc1')!.map((p) => p.name)).toEqual(['a', 'c']);
  });
});

const pc = (id: string, online = true): PcSummary => ({ pcId: id, name: `PC ${id}`, online, lastSeenUnixMs: '1', agentVersion: '1' });
const result = (patch: Partial<SearchResult> = {}): SearchResult => ({ hits: [], complete: true, reason: null, scanned: 5, elapsedMs: 3, rootsSearched: 1, rootsUnavailable: [], ...patch });
const hit = (name: string, extra = {}) => ({ entry: { entryId: 'e-' + name, name, kind: 'file' as const, size: '1', modifiedUnixMs: '1', hidden: false }, rootId: 'r', rootName: 'Docs', path: [], parentEntryId: null, ...extra });

const api = {
  search: vi.fn(), cancelSearch: vi.fn(async () => ({ ok: true })),
  startArchive: vi.fn(), archiveStatus: vi.fn(), cancelArchive: vi.fn(async () => ({ ok: true })),
};
class FakeApiError extends Error { constructor(public code: string, message: string, public status = 0) { super(message); } }
vi.mock('./api', () => ({ api, ApiError: FakeApiError, ARCHIVE_PREFIX: 'archive:' }));
const session = { idToken: 't', email: 'a@b.c', device: { id: 'd', secret: 's' } };

beforeEach(() => { vi.useFakeTimers(); for (const fn of Object.values(api)) fn.mockReset(); api.cancelSearch.mockResolvedValue({ ok: true }); api.cancelArchive.mockResolvedValue({ ok: true }); vi.stubGlobal('crypto', { getRandomValues: (a: Uint8Array) => a.fill(7) }); });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('search across PCs', () => {
  it('asks only online PCs, shows an offline PC as offline and never as "no matches"', async () => {
    const { startSearch, describeOutcome, isComplete } = await import('./features/search/search');
    api.search.mockResolvedValue(result({ hits: [hit('report.txt')] }));
    const updates: unknown[] = [];
    const run = startSearch(session, 'report', [{ pc: pc('A') }, { pc: pc('B', false) }], (r) => updates.push(r as never));
    await run.done;
    expect(api.search).toHaveBeenCalledTimes(1);
    expect(api.search.mock.calls[0][1]).toBe('A');
    const last = updates[updates.length - 1] as unknown as { outcomes: Array<{ state: string; hits: unknown[] }>; finished: boolean };
    expect(last.finished).toBe(true);
    expect(last.outcomes.map((o) => o.state)).toEqual(['done', 'offline']);
    expect(describeOutcome(last.outcomes[1] as never)).toMatch(/offline/);
    expect(isComplete(last as never)).toBe(false);                     // an unsearched PC means the answer is not complete
  });
  it('keeps identical names on two PCs apart and reports incomplete answers', async () => {
    const { startSearch, describeOutcome } = await import('./features/search/search');
    api.search.mockImplementation(async (_s: unknown, id: string) => (id === 'A' ? result({ hits: [hit('same.txt')], complete: false, reason: 'limit' }) : result({ hits: [hit('same.txt')], rootsUnavailable: ['USB'], complete: false, reason: 'unavailable' })));
    let final: { outcomes: Array<{ pcId: string; hits: Array<{ entry: { entryId: string } }>; reason: string | null; unavailableFolders: string[] }> } | null = null;
    await startSearch(session, 'same', [{ pc: pc('A') }, { pc: pc('B') }], (r) => { final = r as never; }).done;
    expect(final!.outcomes.map((o) => o.pcId)).toEqual(['A', 'B']);
    expect(final!.outcomes.every((o) => o.hits.length === 1)).toBe(true);
    expect(describeOutcome(final!.outcomes[0] as never)).toMatch(/only the first 1 matches on PC A/);
    expect(describeOutcome(final!.outcomes[1] as never)).toMatch(/USB .*unavailable on PC B/);
  });
  it('one PC failing does not hide the others; an offline error from the service is shown as offline', async () => {
    const { startSearch } = await import('./features/search/search');
    api.search.mockImplementation(async (_s: unknown, id: string) => { if (id === 'A') throw new FakeApiError('internal', 'boom'); if (id === 'B') throw new FakeApiError('pc_offline', 'off'); return result({ hits: [hit('x')] }); });
    let final: { outcomes: Array<{ state: string; message: string | null }> } | null = null;
    await startSearch(session, 'x', [{ pc: pc('A') }, { pc: pc('B') }, { pc: pc('C') }], (r) => { final = r as never; }).done;
    expect(final!.outcomes.map((o) => o.state)).toEqual(['error', 'offline', 'done']);
    expect(final!.outcomes[0].message).toBe('boom');
  });
  it('cancel tells the PCs to stop, marks the unfinished as stopped and ignores late answers', async () => {
    const { startSearch } = await import('./features/search/search');
    let resolve!: (r: SearchResult) => void;
    api.search.mockImplementation(() => new Promise<SearchResult>((r) => { resolve = r; }));
    let final: { outcomes: Array<{ state: string; hits: unknown[] }>; finished: boolean } | null = null;
    const run = startSearch(session, 'x', [{ pc: pc('A') }], (r) => { final = r as never; });
    run.cancel();
    expect(api.cancelSearch).toHaveBeenCalledTimes(1);
    expect(final!.outcomes[0].state).toBe('cancelled');
    resolve(result({ hits: [hit('late')] }));
    await run.done;
    expect(final!.outcomes[0].hits).toEqual([]);
    expect(final!.outcomes[0].state).toBe('cancelled');
    run.cancel();                                                      // cancelling twice is harmless
    expect(api.cancelSearch).toHaveBeenCalledTimes(1);
  });
});

const status = (patch: Partial<ArchiveStatus> = {}): ArchiveStatus => ({ state: 'building', name: 'Docs.zip', filesDone: 1, filesTotal: 4, bytesDone: '10', bytesTotal: '40', omittedCount: 0, omitted: [], size: null, error: null, expiresUnixMs: null, resumable: true, ...patch });

describe('ZIP preparation', () => {
  it('walks starting → building → ready and hands the archive to the normal download path', async () => {
    const { archives } = await import('./features/archive/archive');
    api.startArchive.mockResolvedValue({ archiveId: 'AR1' });
    api.archiveStatus.mockResolvedValueOnce(status()).mockResolvedValueOnce(status({ state: 'ready', size: '99', filesDone: 4 }));
    const id = archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e1', name: 'Docs' }]);
    expect(archives.list()[0].phase).toBe('starting');
    await vi.advanceTimersByTimeAsync(0);
    expect(archives.list()[0].phase).toBe('building');
    await vi.advanceTimersByTimeAsync(600);
    expect(archives.list()[0].status?.filesDone).toBe(1);
    await vi.advanceTimersByTimeAsync(1500);
    const job = archives.list()[0];
    expect(job.phase).toBe('ready');
    expect(job.downloadId).toBe('archive:AR1');
    expect(api.startArchive).toHaveBeenCalledWith(session, 'pc1', ['e1'], false);
    archives.dismiss(id);                                                // discarding a ready archive removes it from the PC
    expect(api.cancelArchive).toHaveBeenCalledWith(session, 'pc1', 'AR1');
    expect(archives.list()).toEqual([]);
  });
  it('shows omissions on a ready archive and failures with the PC\'s own words', async () => {
    const { archives } = await import('./features/archive/archive');
    api.startArchive.mockResolvedValueOnce({ archiveId: 'AR1' }).mockResolvedValueOnce({ archiveId: 'AR2' });
    api.archiveStatus.mockResolvedValueOnce(status({ state: 'ready', size: '5', omittedCount: 2, omitted: [{ path: 'Docs/a.txt', reason: 'in use' }] })).mockResolvedValueOnce(status({ state: 'failed', error: 'There is not enough free space on this PC.' }));
    archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e1', name: 'Docs' }]);
    archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e2', name: 'More' }, { entryId: 'e3', name: 'Other' }]);
    await vi.advanceTimersByTimeAsync(1500);
    const [second, first] = archives.list();
    expect(first.status?.omittedCount).toBe(2);
    expect(first.phase).toBe('ready');                                   // ready, but the panel labels it incomplete from omittedCount
    expect(second.phase).toBe('failed');
    expect(second.message).toBe('There is not enough free space on this PC.');
    expect(second.label).toBe('2 items');
    archives.clearAll();
  });
  it('a start the PC refuses is shown as failed; an expired archive is "gone", not an error page', async () => {
    const { archives } = await import('./features/archive/archive');
    api.startArchive.mockRejectedValueOnce(new FakeApiError('limit_exceeded', 'Too many archives are waiting on this PC.')).mockResolvedValueOnce({ archiveId: 'AR9' });
    archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e1', name: 'Docs' }]);
    await vi.advanceTimersByTimeAsync(0);
    expect(archives.list()[0]).toMatchObject({ phase: 'failed', message: 'Too many archives are waiting on this PC.' });
    api.archiveStatus.mockRejectedValue(new FakeApiError('not_found', 'gone'));
    archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e2', name: 'More' }]);
    await vi.advanceTimersByTimeAsync(1000);
    expect(archives.list()[0].phase).toBe('gone');
    archives.clearAll();
  });
  it('cancelling stops polling and tells the PC; a cancel before the PC answered still cancels once it did', async () => {
    const { archives } = await import('./features/archive/archive');
    let finishStart!: (v: { archiveId: string }) => void;
    api.startArchive.mockReturnValue(new Promise((r) => { finishStart = r; }));
    const id = archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e1', name: 'Docs' }]);
    archives.cancel(id);
    expect(archives.list()[0].phase).toBe('cancelled');
    finishStart({ archiveId: 'LATE' });
    await vi.advanceTimersByTimeAsync(10);
    expect(api.cancelArchive).toHaveBeenCalledWith(session, 'pc1', 'LATE');
    expect(api.archiveStatus).not.toHaveBeenCalled();
    archives.clearAll();
  });
  it('gives up politely after repeated network failures and refuses a selection that mixes PCs', async () => {
    const { archives, assertOnePc } = await import('./features/archive/archive');
    api.startArchive.mockResolvedValue({ archiveId: 'AR1' });
    api.archiveStatus.mockRejectedValue(new FakeApiError('network', 'no network'));
    archives.start(session, { pcId: 'pc1', name: 'Tower' }, [{ entryId: 'e1', name: 'Docs' }]);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(archives.list()[0]).toMatchObject({ phase: 'failed' });
    expect(archives.list()[0].message).toMatch(/Lost contact/);
    expect(() => assertOnePc('pc1', [{ pcId: 'pc1' }, { pcId: 'pc2' }])).toThrow(/different PCs/);
    expect(() => assertOnePc('pc1', [{ pcId: 'pc1' }])).not.toThrow();
    archives.clearAll();
  });
});
