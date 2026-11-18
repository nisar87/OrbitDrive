import { memo, useSyncExternalStore, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent } from 'react';
import { createPortal } from 'react-dom';
import { api, ApiError, type Session } from '../../api';
import type { Breadcrumb, FileEntry, PcSummary, RootSummary } from '../../protocol';
import { transfers } from '../transfers/transfers';
import { formatBytes, formatTime } from '../../format';
import type { Prefs, SortKey, ViewMode } from '../../prefs';
import { opensOnActivate, previewKindOf } from '../preview/kinds';
import { previewStore, thumbnails } from '../preview/preview';
import { uploads } from '../upload/upload';
import { archives } from '../archive/archive';
import { ArchivePanel } from '../archive/ArchivePanel';
import { UploadConfirm, UploadJobs } from '../upload/UploadPanel';
import { SearchPanel, type OpenHit } from '../search/SearchPanel';
import { addRecent, clearPlaces, groupByPc, isFavorite, loadPlaces, removeFavorite, removeRecent, savePlaces, toggleFavorite, type Availability, type Place, type Places } from './places';
import { emptySelection, filterEntries, pruneSelection, select, sortEntries, totalSize, typeLabel, type Selection } from './model';

interface Location { pc: PcSummary; root: RootSummary; entryId: string | null }
type Transfer = { name: string; phase: 'preparing' } | { name: string; phase: 'handed'; size: string } | { name: string; phase: 'saving' } | { name: string; phase: 'failed'; message: string };
interface Menu { x: number; y: number; entryId: string }
const PAGE = 200, LOAD_ALL_LIMIT = 20_000, RENDER_STEP = 600;
const SORTS: { key: SortKey; label: string }[] = [{ key: 'name', label: 'Name' }, { key: 'modified', label: 'Date modified' }, { key: 'type', label: 'Type' }, { key: 'size', label: 'Size' }];

/** Two-step button for actions that end access; avoids blocking browser dialogs. */
export function ConfirmButton({ label, confirmLabel, onConfirm }: { label: string; confirmLabel: string; onConfirm: () => void | Promise<void> }) {
  const [armed, setArmed] = useState(false);
  if (!armed) return <button type="button" className="ghost danger" onClick={() => setArmed(true)}>{label}</button>;
  return (
    <span className="confirm">
      <button type="button" className="danger" onClick={() => { setArmed(false); void onConfirm(); }}>{confirmLabel}</button>
      <button type="button" className="ghost" onClick={() => setArmed(false)}>Cancel</button>
    </span>
  );
}

const Icon = ({ name }: { name: 'back' | 'forward' | 'up' | 'refresh' | 'tree' | 'search' | 'upload' }) => (
  <svg className="tool-icon" viewBox="0 0 16 16" aria-hidden="true">
    {name === 'back' && <path d="M9.5 3L4.5 8l5 5M4.5 8h8" />}
    {name === 'forward' && <path d="M6.5 3l5 5-5 5M11.5 8h-8" />}
    {name === 'up' && <path d="M3 7.5l5-5 5 5M8 2.5v11" />}
    {name === 'upload' && <path d="M2.5 10.5v3h11v-3M8 10V2.5M4.8 5.2L8 2l3.2 3.2" />}
    {name === 'refresh' && <path d="M13 8a5 5 0 1 1-1.5-3.6M13 2.5v3h-3" />}
    {name === 'tree' && <path d="M2.5 3.5h11M2.5 8h11M2.5 12.5h11" />}
    {name === 'search' && <path d="M6.8 11.6a4.8 4.8 0 1 1 0-9.6 4.8 4.8 0 0 1 0 9.6zM10.3 10.3l3.7 3.7" />}
  </svg>
);

interface Props { session: Session; prefs: Prefs; onPrefs: (next: Prefs) => void; onAuthLost: (error: unknown) => void; dataVersion: number; onOpenApp: (id: string) => void }

export function Explorer({ session, prefs, onPrefs, onAuthLost, dataVersion, onOpenApp }: Props) {
  const [pcs, setPcs] = useState<PcSummary[] | null>(null);
  const [roots, setRoots] = useState<Record<string, RootSummary[] | string>>({});
  const [location, setLocation] = useState<Location | null>(null);
  const [pendingUpload, setPendingUpload] = useState<File[] | null>(null);
  const uploadInput = useRef<HTMLInputElement>(null);
  const savedUploads = useSyncExternalStore(uploads.subscribe, uploads.list).filter((j) => j.phase === 'done' && j.pcId === location?.pc.pcId).length;
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [breadcrumbs, setBreadcrumbs] = useState<Breadcrumb[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [skipped, setSkipped] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingCount, setLoadingCount] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [transfer, setTransfer] = useState<Transfer | null>(null);
  const [filter, setFilter] = useState('');
  const [selection, setSelection] = useState<Selection>(emptySelection);
  const [selectMode, setSelectMode] = useState(false);
  const [current, setCurrent] = useState<string | null>(null);
  const [menu, setMenu] = useState<Menu | null>(null);
  const [properties, setProperties] = useState<FileEntry[] | null>(null);
  const [treeDrawer, setTreeDrawer] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [places, setPlacesState] = useState<Places>(() => loadPlaces(session.email));
  const [availability, setAvailability] = useState<Record<string, Availability>>({});
  const setPlaces = useCallback((update: (p: Places) => Places) => setPlacesState((previous) => { const next = update(previous); savePlaces(session.email, next); return next; }), [session.email]);
  // Visit history for Back/Forward. Browser Back is wired to the same list through history.state.
  const history = useRef<{ list: (Location | null)[]; index: number }>({ list: [null], index: 0 });
  const [, bump] = useState(0);
  const body = useRef<HTMLDivElement>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const requestId = useRef(0);

  const fail = useCallback((e: unknown) => {
    onAuthLost(e);
    setError(e instanceof ApiError ? e.message : 'Something went wrong.');
  }, [onAuthLost]);

  const loadPcs = useCallback(async () => {
    try {
      const list = (await api.pcs(session)).pcs;
      setPcs(list);
      for (const pc of list) {
        if (!pc.online) { setRoots((r) => ({ ...r, [pc.pcId]: 'This PC is offline. It must be on, awake and running OrbitDrive.' })); continue; }
        api.roots(session, pc.pcId)
          .then((result) => setRoots((r) => ({ ...r, [pc.pcId]: result.roots })))
          .catch((e: unknown) => setRoots((r) => ({ ...r, [pc.pcId]: e instanceof ApiError ? e.message : 'Unavailable.' })));
      }
    } catch (e) { fail(e); }
  }, [session, fail]);

  /** Shows a location (null = the list of PCs). `more` appends the next page of the same folder. */
  const show = useCallback(async (target: Location | null, more?: string): Promise<boolean> => {
    const id = ++requestId.current;
    setError(null); setMenu(null);
    if (target === null) {
      setLocation(null); setEntries([]); setBreadcrumbs([]); setCursor(null); setSkipped(0); setSelection(emptySelection); setFilter(''); setCurrent(null);
      await loadPcs();
      return true;
    }
    setLoading(true);
    try {
      const page = await api.directory(session, target.pc.pcId, { rootId: target.root.rootId, entryId: target.entryId, cursor: more ?? null, pageSize: PAGE });
      if (id !== requestId.current) return false;              // a newer navigation won
      setLocation(target);
      setEntries((previous) => (more ? [...previous, ...page.entries] : page.entries));
      setBreadcrumbs(page.breadcrumbs);
      setCursor(page.nextCursor ?? null);
      setSkipped(page.skippedLinks);
      if (!more) { setSelection(emptySelection); setFilter(''); setCurrent(null); }
      return true;
    } catch (e) { if (id === requestId.current) fail(e); return false; }
    finally { if (id === requestId.current) setLoading(false); }
  }, [session, fail, loadPcs]);

  /** A user navigation: shows the place and records it for Back/Forward (app buttons and the browser's Back). */
  const navigate = useCallback(async (target: Location | null) => {
    if (!(await show(target))) return;
    const h = history.current;
    h.list = [...h.list.slice(0, h.index + 1), target];
    h.index = h.list.length - 1;
    window.history.pushState({ orbitExplorer: h.index }, '');
    bump((n) => n + 1);
    body.current?.scrollTo({ top: 0 });
    // Opening a folder removes the item that had focus. Keep keyboard users inside the list instead of dropping
    // focus to the page (where Backspace, arrows and Enter would do nothing).
    requestAnimationFrame(() => {
      const content = body.current;
      if (!content || (document.activeElement !== document.body && !content.contains(document.activeElement))) return;
      (content.querySelector<HTMLElement>('.row-button[tabindex="0"], .tile:not(:disabled)') ?? content).focus({ preventScroll: true });
    });
  }, [show]);

  const go = useCallback(async (index: number) => {
    const h = history.current;
    if (index < 0 || index >= h.list.length) return;
    h.index = index;
    bump((n) => n + 1);
    await show(h.list[index]);
  }, [show]);

  useEffect(() => {
    window.history.replaceState({ orbitExplorer: 0 }, '');
    const onPop = (event: PopStateEvent) => {
      const index = (event.state as { orbitExplorer?: unknown } | null)?.orbitExplorer;
      if (typeof index === 'number') void go(index);
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, [go]);

  // First load, and whenever another app changed what this browser can reach (PC disconnected, device removed).
  useEffect(() => { void show(history.current.list[history.current.index]); }, [show, dataVersion]);
  // A file that was just saved on the PC appears in the listing without a manual refresh.
  useEffect(() => { if (savedUploads > 0) void show(history.current.list[history.current.index]); }, [savedUploads]); // eslint-disable-line react-hooks/exhaustive-deps

  // Sort once per folder/sort change; filtering afterwards keeps the order, so typing in the filter never re-sorts.
  const sorted = useMemo(() => sortEntries(entries, prefs.sortKey, prefs.sortDescending), [entries, prefs.sortKey, prefs.sortDescending]);
  const visible = useMemo(() => filterEntries(sorted, filter, prefs.showHidden), [sorted, filter, prefs.showHidden]);
  // Only part of a very large folder is put on screen at a time; more rows are added as the reader scrolls or jumps
  // (End, Ctrl+A and the status bar always work on the whole folder).
  const [range, setRange] = useState({ start: 0, end: RENDER_STEP });
  useEffect(() => { setRange({ start: 0, end: RENDER_STEP }); }, [location, filter, prefs.sortKey, prefs.sortDescending, prefs.view]);
  const rendered = useMemo(() => (range.start > 0 || visible.length > range.end ? visible.slice(range.start, range.end) : visible), [visible, range]);
  const sentinel = useRef<HTMLDivElement>(null);
  const topSentinel = useRef<HTMLDivElement>(null);
  const heightBeforePrepend = useRef<number | null>(null);
  useEffect(() => {
    const scroller = body.current;
    if (!scroller) return;
    const observer = new IntersectionObserver((hits) => {
      for (const hit of hits) {
        if (!hit.isIntersecting) continue;
        if (hit.target === sentinel.current) setRange((r) => ({ start: r.start, end: r.end + RENDER_STEP }));
        else { heightBeforePrepend.current = scroller.scrollHeight; setRange((r) => ({ start: Math.max(0, r.start - RENDER_STEP), end: r.end })); }
      }
    }, { root: scroller, rootMargin: '600px' });
    if (sentinel.current) observer.observe(sentinel.current);
    if (topSentinel.current) observer.observe(topSentinel.current);
    return () => observer.disconnect();
  }, [range, visible.length]);
  // Rows added above the view must not make the list jump (not every browser anchors scrolling by itself).
  useLayoutEffect(() => {
    const scroller = body.current;
    if (scroller && heightBeforePrepend.current !== null) { scroller.scrollTop += scroller.scrollHeight - heightBeforePrepend.current; heightBeforePrepend.current = null; }
  }, [range.start]);
  const thumbContext = useMemo(() => (prefs.view === 'icons' && location ? { session, pcId: location.pc.pcId } : null), [prefs.view, session, location]);
  const order = useMemo(() => visible.map((entry) => entry.entryId), [visible]);
  useEffect(() => { setSelection((s) => pruneSelection(s, order)); }, [order]);
  const selected = useMemo(() => visible.filter((entry) => selection.ids.has(entry.entryId)), [visible, selection]);
  const hiddenCount = useMemo(() => (prefs.showHidden ? 0 : entries.filter((entry) => entry.hidden).length), [entries, prefs.showHidden]);
  const currentId = current !== null && order.includes(current) ? current : order[0] ?? null;

  // Ask each PC whether the favorites and recents it owns are still shared. Ids are only ever sent to the PC they came from.
  useEffect(() => {
    if (!pcs) return;
    const everything = [...places.favorites, ...places.recents];
    let cancelled = false;
    const mark = (patch: Record<string, Availability>) => { if (!cancelled) setAvailability((previous) => ({ ...previous, ...patch })); };
    for (const [pcId, list] of groupByPc(everything)) {
      const pc = pcs.find((candidate) => candidate.pcId === pcId);
      const ids = [...new Set(list.map((place) => place.entryId))];
      if (!pc) { mark(Object.fromEntries(ids.map((id) => [id, { state: 'gone', reason: 'This PC is no longer connected to your account.' } as Availability]))); continue; }
      if (!pc.online) { mark(Object.fromEntries(ids.map((id) => [id, { state: 'offline' } as Availability]))); continue; }
      for (let i = 0; i < ids.length; i += 50) {
        const batch = ids.slice(i, i + 50);
        api.checkEntries(session, pcId, batch).then((result) => {
          mark(Object.fromEntries(result.entries.map((check) => [check.entryId, check.available && check.entry ? { state: 'available', name: check.entry.name, size: check.entry.size } as Availability : { state: 'gone', reason: check.reason ?? 'No longer shared.' } as Availability])));
        }).catch((e: unknown) => {
          onAuthLost(e);
          mark(Object.fromEntries(batch.map((id) => [id, { state: e instanceof ApiError && e.code === 'pc_offline' ? 'offline' : 'unknown' } as Availability])));
        });
      }
    }
    return () => { cancelled = true; };
  }, [places, pcs, session, onAuthLost, dataVersion]);

  const crumbPath = useMemo(() => breadcrumbs.slice(1).map((crumb) => crumb.name), [breadcrumbs]);
  const placeOf = useCallback((entry: FileEntry): Place | null => (location ? { pcId: location.pc.pcId, rootId: location.root.rootId, entryId: entry.entryId, name: entry.name, kind: entry.kind, path: crumbPath, at: Date.now() } : null), [location, crumbPath]);
  const remember = useCallback((entry: FileEntry) => { const place = placeOf(entry); if (place) setPlaces((p) => addRecent(p, place)); }, [placeOf, setPlaces]);

  async function archiveSelection(entries: FileEntry[]) {
    if (!location || entries.length === 0) return;
    archives.start(session, location.pc, entries.map((entry) => ({ entryId: entry.entryId, name: entry.name })));
    setSelection(emptySelection); setSelectMode(false);
  }

  async function download(entry: FileEntry) {
    remember(entry);
    if (!location) return;
    setTransfer({ name: entry.name, phase: 'preparing' });
    try {
      // Native browser download: the relay streams with Content-Disposition; nothing is buffered in page memory.
      const started = await transfers.toBrowser(session, location.pc.pcId, entry);
      setTransfer({ name: entry.name, phase: 'handed', size: (started.size ?? 0n).toString() });
    } catch (e) {
      onAuthLost(e);
      setTransfer({ name: entry.name, phase: 'failed', message: e instanceof ApiError ? e.message : 'Download could not start.' });
    }
  }

  /** The reader chooses where to save and OrbitDrive writes the file itself (real progress, resumable). */
  async function saveToFile(entry: FileEntry) {
    if (!location) return;
    const started = await transfers.toFile(session, location.pc.pcId, entry);
    if (started) { setTransfer({ name: entry.name, phase: 'saving' }); onOpenApp('transfers'); }
  }

  /** Opens the Preview window on this file; the other previewable files of the folder become its previous/next. */
  const preview = (entry: FileEntry) => {
    if (!location || entry.kind !== 'file') return;
    remember(entry);
    // Previous/next stay within the kind that was opened (pictures with pictures, text with text, audio and video together).
    const group = (name: string) => { const k = previewKindOf(name); return k === 'audio' || k === 'video' ? 'media' : k; };
    const items = visible.filter((e) => e.kind === 'file' && group(e.name) !== null && group(e.name) === group(entry.name)).map((e) => ({ entryId: e.entryId, name: e.name, size: Number(e.size ?? 0n) }));
    previewStore.open(location.pc.pcId, items, Math.max(0, items.findIndex((i) => i.entryId === entry.entryId)));
    onOpenApp('preview');
  };

  const activate = (entry: FileEntry) => {
    if (!location) return;
    if (entry.kind === 'directory') { remember(entry); void navigate({ ...location, entryId: entry.entryId }); }
    else if (opensOnActivate(previewKindOf(entry.name))) preview(entry);
    else void download(entry);
  };

  async function loadAll() {
    if (!location) return;
    // Pages are collected first and shown together: updating a 10,000-row list fifty times would be far slower.
    let next = cursor;
    const collected: FileEntry[] = [];
    const id = ++requestId.current;
    setLoading(true);
    try {
      while (next && entries.length + collected.length < LOAD_ALL_LIMIT) {
        const page = await api.directory(session, location.pc.pcId, { rootId: location.root.rootId, entryId: location.entryId, cursor: next, pageSize: PAGE });
        if (id !== requestId.current) return;                    // the reader went somewhere else meanwhile
        collected.push(...page.entries);
        next = page.nextCursor ?? null;
        setLoadingCount(entries.length + collected.length);
      }
    } catch (e) { if (id === requestId.current) fail(e); }
    finally {
      if (id === requestId.current) {
        if (collected.length > 0) { setEntries((previous) => [...previous, ...collected]); setCursor(next); }
        setLoading(false); setLoadingCount(0);
      }
    }
  }

  // The row may not be on screen yet (large folders render in steps): make sure it is, then focus it once drawn.
  const focusRow = (id: string) => {
    const index = order.indexOf(id);
    if (index >= 0 && (index < range.start || index >= range.end)) setRange({ start: Math.max(0, index - RENDER_STEP / 2), end: index + RENDER_STEP / 2 });
    let tries = 0;
    const attempt = () => {
      const button = body.current?.querySelector<HTMLElement>(`[data-entry="${CSS.escape(id)}"] .row-button`);
      if (button) button.focus(); else if (tries++ < 20) requestAnimationFrame(attempt);
    };
    requestAnimationFrame(attempt);
  };

  function onListKeyDown(event: ReactKeyboardEvent) {
    if ((event.target as HTMLElement).closest('input, select, dialog')) return;
    const index = currentId === null ? -1 : order.indexOf(currentId);
    const columns = prefs.view === 'details' ? 1 : Math.max(1, Math.floor((body.current?.querySelector('tbody')?.clientWidth ?? 1) / ((body.current?.querySelector('tbody tr') as HTMLElement | null)?.offsetWidth || 1)));
    const move = (to: number, extend: boolean) => {
      if (order.length === 0) return;
      const id = order[Math.min(Math.max(to, 0), order.length - 1)];
      event.preventDefault();
      setCurrent(id);
      if (extend) setSelection((s) => select(s.anchor === null ? { ids: s.ids, anchor: currentId } : s, order, id, 'range'));
      focusRow(id);
    };
    if (event.key === 'ArrowDown') move(index + columns, event.shiftKey);
    else if (event.key === 'ArrowUp') move(index - columns, event.shiftKey);
    else if (event.key === 'ArrowRight' && prefs.view !== 'details') move(index + 1, event.shiftKey);
    else if (event.key === 'ArrowLeft' && prefs.view !== 'details' && !event.altKey) move(index - 1, event.shiftKey);
    else if (event.key === 'Home') move(0, event.shiftKey);
    else if (event.key === 'End') move(order.length - 1, event.shiftKey);
    else if (event.key === ' ' && currentId !== null && (event.target as HTMLElement).classList.contains('row-button')) { event.preventDefault(); setSelection((s) => select(s, order, currentId, 'toggle')); }
    else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') { event.preventDefault(); setSelection({ ids: new Set(order), anchor: order[0] ?? null }); }
    else if (event.key === 'Escape' && selection.ids.size > 0) { setSelection(emptySelection); setSelectMode(false); }
    else if (event.key === 'Backspace' || (event.altKey && event.key === 'ArrowUp')) { event.preventDefault(); goUp(); }
    else if (event.altKey && event.key === 'ArrowLeft') { event.preventDefault(); void go(history.current.index - 1); }
    else if (event.altKey && event.key === 'ArrowRight') { event.preventDefault(); void go(history.current.index + 1); }
    else if ((event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) && currentId !== null) {
      event.preventDefault();
      const rect = body.current?.querySelector(`[data-entry="${CSS.escape(currentId)}"]`)?.getBoundingClientRect();
      openMenu(currentId, (rect?.left ?? 0) + 40, (rect?.top ?? 0) + 24);
    }
  }

  function goUp() {
    if (!location) return;
    if (breadcrumbs.length <= 1) void navigate(null);
    else void navigate({ ...location, entryId: breadcrumbs[breadcrumbs.length - 2].entryId ?? null });
  }

  function openMenu(entryId: string, x: number, y: number) {
    if (!selection.ids.has(entryId)) setSelection({ ids: new Set([entryId]), anchor: entryId });
    setCurrent(entryId);
    setMenu({ entryId, x: Math.min(x, window.innerWidth - 230), y: Math.min(y, window.innerHeight - 190) });
  }

  function onRowClick(event: ReactMouseEvent, entry: FileEntry) {
    if ((event.target as HTMLElement).closest('button, input')) return;       // the name button and the checkbox act for themselves
    setCurrent(entry.entryId);
    setSelection((s) => select(s, order, entry.entryId, event.shiftKey ? 'range' : event.ctrlKey || event.metaKey || selectMode ? 'toggle' : 'single'));
  }

  // Long-press on touch opens the actions menu (a tap opens the item).
  const press = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const suppressClick = useRef(false);
  const startPress = (entryId: string) => (event: React.PointerEvent) => {
    if (event.pointerType === 'mouse') return;
    const { clientX, clientY } = event;
    press.current = setTimeout(() => { suppressClick.current = true; openMenu(entryId, clientX, clientY); }, 550);
  };
  const cancelPress = () => clearTimeout(press.current);

  useEffect(() => { if (properties) dialog.current?.showModal(); }, [properties]);
  // Keep the end of a long path in view: the current folder matters more than the first crumb.
  const crumbs = useRef<HTMLElement>(null);
  useEffect(() => { const bar = crumbs.current; if (bar) bar.scrollLeft = bar.scrollWidth; }, [breadcrumbs, location]);
  const setPref = (patch: Partial<Prefs>) => onPrefs({ ...prefs, ...patch });
  const showCheckboxes = selectMode || selection.ids.size > 1;
  const h = history.current;

  const openFromSearch = {
    folder: ({ pc, hit }: OpenHit) => {
      const root = (Array.isArray(roots[pc.pcId]) ? (roots[pc.pcId] as RootSummary[]) : []).find((r) => r.rootId === hit.rootId);
      if (!root) { setError('That shared folder is not available right now.'); return; }
      setSearchOpen(false);
      void navigate({ pc, root, entryId: hit.entry.entryId || null });
    },
    file: ({ pc, hit }: OpenHit) => {
      const entry = hit.entry;
      setPlaces((p) => addRecent(p, { pcId: pc.pcId, rootId: hit.rootId, entryId: entry.entryId, name: entry.name, kind: 'file', path: hit.path, at: Date.now() }));
      if (opensOnActivate(previewKindOf(entry.name))) { previewStore.open(pc.pcId, [{ entryId: entry.entryId, name: entry.name, size: Number(entry.size ?? 0n) }], 0); onOpenApp('preview'); }
      else { setTransfer({ name: entry.name, phase: 'preparing' }); void transfers.toBrowser(session, pc.pcId, entry).then((started) => setTransfer({ name: entry.name, phase: 'handed', size: (started.size ?? 0n).toString() })).catch((e: unknown) => { onAuthLost(e); setTransfer({ name: entry.name, phase: 'failed', message: e instanceof ApiError ? e.message : 'Download could not start.' }); }); }
    },
  };

  /** A favorite or recent item. It opens only if the PC just confirmed it is still shared (the check runs whenever the lists or PCs change). */
  const openPlace = (place: Place) => {
    const pc = pcs?.find((candidate) => candidate.pcId === place.pcId);
    if (!pc) return;
    if (place.kind === 'directory') {
      const root = (Array.isArray(roots[pc.pcId]) ? (roots[pc.pcId] as RootSummary[]) : []).find((r) => r.rootId === place.rootId);
      if (root) { setTreeDrawer(false); void navigate({ pc, root, entryId: place.entryId }); }
      return;
    }
    const known = availability[place.entryId];
    openFromSearch.file({ pc, hit: { entry: { entryId: place.entryId, name: place.name, kind: 'file', size: known?.state === 'available' ? known.size : '0', modifiedUnixMs: '0', hidden: false }, rootId: place.rootId, rootName: '', path: place.path, parentEntryId: null } });
  };
  const placeItem = (place: Place, list: 'favorite' | 'recent') => {
    const state = availability[place.entryId] ?? { state: 'unknown' as const };
    const pcName = pcs?.find((candidate) => candidate.pcId === place.pcId)?.name ?? 'a PC that is no longer connected';
    const usable = state.state === 'available' || state.state === 'unknown';
    const note = state.state === 'offline' ? 'PC offline' : state.state === 'gone' ? 'Unavailable' : null;
    return (
      <li key={place.pcId + place.entryId} className="place" data-state={state.state}>
        <button type="button" className="tree-item place-open" disabled={!usable} onClick={() => openPlace(place)}>
          {/* No separate aria-label: the accessible name is exactly the visible text (name, PC, state), so speech control by what is shown works. */}
          <span className={`icon ${place.kind === 'directory' ? 'folder' : 'file'}`} aria-hidden="true" /><span className="place-text"><bdi>{place.name}</bdi>{' '}<small><bdi>{pcName}</bdi>{note ? ` · ${note}` : ''}</small></span>
        </button>
        <button type="button" className="ghost icon-button place-remove" aria-label={`Remove ${place.name} from ${list === 'favorite' ? 'favorites' : 'recent items'}`} onClick={() => setPlaces((p) => (list === 'favorite' ? removeFavorite(p, place) : removeRecent(p, place)))}>×</button>
      </li>
    );
  };

  const tree = (
    <nav className={`tree${treeDrawer ? ' drawer-open' : ''}`} aria-label="PCs and shared folders">
      <button type="button" className={`tree-item home${location === null ? ' current' : ''}`} aria-current={location === null ? 'true' : undefined} onClick={() => { setTreeDrawer(false); void navigate(null); }}>
        <span className="icon pc" aria-hidden="true" />This account&apos;s PCs
      </button>
      {pcs?.map((pc) => {
        const state = roots[pc.pcId];
        return (
          <div key={pc.pcId} className="tree-group">
            <p className="tree-pc"><span className={pc.online ? 'dot online' : 'dot'} aria-hidden="true" /><bdi>{pc.name}</bdi>{!pc.online && <small> (offline)</small>}</p>
            {Array.isArray(state) && state.map((root) => {
              const here = location?.pc.pcId === pc.pcId && location.root.rootId === root.rootId;
              return (
                <button key={root.rootId} type="button" className={`tree-item${here ? ' current' : ''}`} aria-current={here ? 'true' : undefined} disabled={!root.available}
                  onClick={() => { setTreeDrawer(false); void navigate({ pc, root, entryId: null }); }}>
                  <span className="icon folder" aria-hidden="true" /><bdi>{root.name}</bdi>
                </button>
              );
            })}
          </div>
        );
      })}
      {places.favorites.length > 0 && (
        <div className="tree-group" role="group" aria-label="Favorites">
          <p className="tree-pc">Favorites</p>
          <ul className="places">{places.favorites.map((place) => placeItem(place, 'favorite'))}</ul>
        </div>
      )}
      {places.recents.length > 0 && (
        <div className="tree-group" role="group" aria-label="Recent">
          <p className="tree-pc">Recent <button type="button" className="ghost linklike" onClick={() => { clearPlaces(session.email); setPlacesState((p) => ({ ...p, recents: [] })); }}>Clear</button></p>
          <ul className="places">{places.recents.map((place) => placeItem(place, 'recent'))}</ul>
        </div>
      )}
    </nav>
  );

  // Rows are memoised and call back through this stable object, so selecting or moving in a 10,000-item folder
  // re-renders only the rows that changed.
  const latest = { onRowClick, activate, openMenu, startPress, cancelPress, setCurrent, toggle: (id: string) => setSelection((sel) => select(sel, order, id, 'toggle')), suppressClick };
  const latestRef = useRef(latest);
  latestRef.current = latest;
  const rowActions = useMemo<RowActions>(() => ({
    click: (event, entry) => latestRef.current.onRowClick(event, entry),
    activate: (entry) => latestRef.current.activate(entry),
    menu: (id, x, y) => latestRef.current.openMenu(id, x, y),
    pressStart: (id, event) => latestRef.current.startPress(id)(event),
    pressCancel: () => latestRef.current.cancelPress(),
    focus: (id) => latestRef.current.setCurrent(id),
    toggle: (id) => latestRef.current.toggle(id),
    consumeSuppressedClick: () => { const was = latestRef.current.suppressClick.current; latestRef.current.suppressClick.current = false; return was; },
  }), []);

  const menuEntry = menu ? visible.find((entry) => entry.entryId === menu.entryId) ?? null : null;

  return (
    <div className="explorer" data-tree={prefs.treeOpen ? 'open' : 'closed'}>
      <div className="toolbar" role="toolbar" aria-label="Navigation">
        <button type="button" className="ghost icon-button tree-toggle" aria-label="Folders" aria-expanded={treeDrawer} onClick={() => { setTreeDrawer((open) => !open); setPref({ treeOpen: !prefs.treeOpen }); }}><Icon name="tree" /></button>
        <button type="button" className="ghost icon-button" aria-label="Back" disabled={h.index === 0} onClick={() => void go(h.index - 1)}><Icon name="back" /></button>
        <button type="button" className="ghost icon-button" aria-label="Forward" disabled={h.index >= h.list.length - 1} onClick={() => void go(h.index + 1)}><Icon name="forward" /></button>
        <button type="button" className="ghost icon-button" aria-label="Up one level" disabled={location === null} onClick={goUp}><Icon name="up" /></button>
        <button type="button" className="ghost icon-button" aria-label="Refresh" onClick={() => void show(location)}><Icon name="refresh" /></button>
        <button type="button" className="ghost icon-button" aria-label="Search" aria-pressed={searchOpen} onClick={() => setSearchOpen((open) => !open)}><Icon name="search" /></button>
        {location && location.pc.online && location.root.canUpload && (
          <>
            <button type="button" className="ghost icon-button" aria-label="Upload files here" onClick={() => uploadInput.current?.click()}><Icon name="upload" /></button>
            <input ref={uploadInput} type="file" multiple hidden data-testid="upload-input" onChange={(e) => { const picked = [...(e.target.files ?? [])]; e.target.value = ''; if (picked.length) setPendingUpload(picked); }} />
          </>
        )}
        <nav className="breadcrumbs" aria-label="Breadcrumb" ref={crumbs}>
          <button type="button" className="ghost" onClick={() => void navigate(null)} aria-current={location === null ? 'page' : undefined}>{location ? location.pc.name : 'This account’s PCs'}</button>
          {location && breadcrumbs.map((crumb, index) => (
            <span key={index} className="crumb">
              <span aria-hidden="true">›</span>
              <button type="button" className="ghost" aria-current={index === breadcrumbs.length - 1 ? 'page' : undefined}
                onClick={() => void navigate({ ...location, entryId: crumb.entryId ?? null })}><bdi>{crumb.name}</bdi></button>
            </span>
          ))}
        </nav>
      </div>

      {location && (
        <div className="toolbar second" role="toolbar" aria-label="View">
          <input type="search" className="filter" aria-label="Filter this folder" placeholder="Filter this folder" value={filter} onChange={(event) => setFilter(event.target.value)} />
          <label className="inline"><span className="label-text">Sort by</span>
            <select value={prefs.sortKey} onChange={(event) => setPref({ sortKey: event.target.value as SortKey })}>
              {SORTS.map((sort) => <option key={sort.key} value={sort.key}>{sort.label}</option>)}
            </select>
          </label>
          <button type="button" className="ghost" aria-label={prefs.sortDescending ? 'Descending. Switch to ascending' : 'Ascending. Switch to descending'} onClick={() => setPref({ sortDescending: !prefs.sortDescending })}>{prefs.sortDescending ? '↓' : '↑'}</button>
          <div className="segmented" role="group" aria-label="View">
            {(['details', 'list', 'icons'] as ViewMode[]).map((view) => (
              <button key={view} type="button" className="ghost" aria-pressed={prefs.view === view} onClick={() => setPref({ view })}>{view === 'details' ? 'Details' : view === 'list' ? 'List' : 'Icons'}</button>
            ))}
          </div>
          <button type="button" className="ghost" aria-pressed={selectMode} onClick={() => { setSelectMode((on) => !on); if (selectMode) setSelection(emptySelection); }}>Select</button>
        </div>
      )}

      <div className="explorer-main">
        {tree}
        {treeDrawer && <button type="button" className="scrim" aria-label="Close folders" onClick={() => setTreeDrawer(false)} />}
        <div className="content" ref={body} tabIndex={-1} onKeyDown={onListKeyDown}>
          {error && <p role="alert" className="notice error">{error}</p>}
          {searchOpen && pcs && (
            <SearchPanel session={session} pcs={pcs} onClose={() => setSearchOpen(false)} onOpenFolder={openFromSearch.folder} onOpenFile={openFromSearch.file}
              here={location ? { pc: location.pc, root: location.root, entryId: location.entryId, folderName: breadcrumbs[breadcrumbs.length - 1]?.name ?? location.root.name } : null} />
          )}
          {location && pendingUpload && location.root.canUpload && (
            <UploadConfirm session={session} files={pendingUpload} root={location.root} onDone={() => setPendingUpload(null)}
              target={{ pcId: location.pc.pcId, pcName: location.pc.name, rootId: location.root.rootId, folderEntryId: location.entryId, path: breadcrumbs.length ? breadcrumbs.map((c) => c.name) : [location.root.name] }} />
          )}
          {location && <UploadJobs pcId={location.pc.pcId} />}
          {location && <ArchivePanel session={session} pcId={location.pc.pcId} onOpenTransfers={() => onOpenApp('transfers')} />}
          {transfer && (
            <p role="status" className={transfer.phase === 'failed' ? 'notice error' : 'notice'} data-testid="transfer-status">
              {transfer.phase === 'preparing' && <>Checking <strong><bdi>{transfer.name}</bdi></strong> on the PC…</>}
              {transfer.phase === 'saving' && <>Saving <strong><bdi>{transfer.name}</bdi></strong> to the place you chose. Progress is shown in Transfers.</>}
              {transfer.phase === 'handed' && <><strong><bdi>{transfer.name}</bdi></strong> ({formatBytes(transfer.size)}) was handed to your browser. Check your browser&apos;s downloads — OrbitDrive cannot see when the file has finished saving.</>}
              {transfer.phase === 'failed' && <><strong><bdi>{transfer.name}</bdi></strong>: {transfer.message}</>}
            </p>
          )}

          {location === null ? (
            <section aria-label="This account's PCs">
              {pcs === null && !error && <p className="empty" role="status">Loading your PCs…</p>}
              {pcs?.length === 0 && <p className="empty">No PCs are paired with this browser yet.</p>}
              {pcs?.map((pc) => {
                const state = roots[pc.pcId];
                return (
                  <article key={pc.pcId} className="pc">
                    <h2><span className={pc.online ? 'dot online' : 'dot'} aria-hidden="true" /><bdi>{pc.name}</bdi> <small>{pc.online ? 'Online' : `Offline · last seen ${formatTime(pc.lastSeenUnixMs)}`}</small></h2>
                    {typeof state === 'string' && <p className="notice">{state}</p>}
                    {Array.isArray(state) && state.length === 0 && <p className="empty">No folders are shared from this PC. Choose folders to share in OrbitDrive on the PC.</p>}
                    {Array.isArray(state) && (
                      <ul className="tiles">
                        {state.map((root) => (
                          <li key={root.rootId}>
                            <button type="button" className="tile" disabled={!root.available} onClick={() => void navigate({ pc, root, entryId: null })}>
                              <span className="icon folder large" aria-hidden="true" />
                              <span><bdi>{root.name}</bdi><small>{root.available ? 'Read-only' : 'Unavailable on the PC'}</small></span>
                            </button>
                          </li>
                        ))}
                      </ul>
                    )}
                  </article>
                );
              })}
            </section>
          ) : (
            <>
              {range.start > 0 && <div ref={topSentinel} className="more-rows" aria-hidden="true" />}
              <table className="entries" role="grid" data-view={prefs.view} aria-label="Files and folders" aria-multiselectable="true">
                <thead>
                  <tr>
                    {showCheckboxes && <th scope="col" className="check"><span className="sr-only">Selected</span></th>}
                    {SORTS.map((sort) => (
                      <th key={sort.key} scope="col" className={sort.key} aria-sort={prefs.sortKey === sort.key ? (prefs.sortDescending ? 'descending' : 'ascending') : undefined}>
                        <button type="button" className="ghost th-button" onClick={() => setPref(prefs.sortKey === sort.key ? { sortDescending: !prefs.sortDescending } : { sortKey: sort.key, sortDescending: false })}>
                          {sort.label}{prefs.sortKey === sort.key && <span aria-hidden="true"> {prefs.sortDescending ? '↓' : '↑'}</span>}
                        </button>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {rendered.map((entry) => (
                    <Row key={entry.entryId} entry={entry} selected={selection.ids.has(entry.entryId)} current={entry.entryId === currentId} checkbox={showCheckboxes} actions={rowActions} thumbs={thumbContext} />
                  ))}
                </tbody>
              </table>
              {range.end < visible.length && <div ref={sentinel} className="more-rows" aria-hidden="true" />}
              {!loading && !error && entries.length === 0 && <p className="empty">This folder is empty.</p>}
              {!loading && entries.length > 0 && visible.length === 0 && <p className="empty">{filter.trim() ? <>Nothing in this folder matches “<bdi>{filter.trim()}</bdi>”.</> : 'Only hidden items are here. Turn on “Show hidden items” in Settings to see them.'}</p>}
              {skipped > 0 && <p className="notice">{skipped} link{skipped === 1 ? '' : 's'} or shortcut target{skipped === 1 ? '' : 's'} not shown. OrbitDrive does not follow links out of a shared folder.</p>}
              {cursor && (
                <p className="more">
                  <button type="button" onClick={() => void show(location, cursor)} disabled={loading}>Load more</button>
                  <button type="button" className="ghost" onClick={() => void loadAll()} disabled={loading}>Load all</button>
                  <span className="hint">Sorting and filtering apply to the {entries.length.toLocaleString()} items loaded so far.</span>
                </p>
              )}
              {loading && <p className="empty" role="status">{loadingCount > 0 ? `Loading… ${loadingCount.toLocaleString()} items so far` : 'Loading…'}</p>}
            </>
          )}
        </div>
      </div>

      {location && (
        <p className="statusbar" role="status" aria-live="off">
          <span>{visible.length.toLocaleString()} item{visible.length === 1 ? '' : 's'}{cursor ? ' loaded (more available)' : ''}{hiddenCount > 0 ? ` · ${hiddenCount} hidden` : ''}</span>
          {selected.length > 0 && <span>{selected.length.toLocaleString()} selected{selected.some((entry) => entry.kind === 'file') ? ` · ${formatBytes(totalSize(selected).toString())}` : ''}</span>}
          {selected.length === 1 && selected[0].kind === 'file' && previewKindOf(selected[0].name) !== null && <button type="button" className="ghost" onClick={() => preview(selected[0])}>Preview</button>}
          {selected.length === 1 && (() => { const place = placeOf(selected[0]); return place && <button type="button" className="ghost" aria-pressed={isFavorite(places, place)} onClick={() => setPlaces((p) => toggleFavorite(p, place))}>{isFavorite(places, place) ? 'Remove from favorites' : 'Add to favorites'}</button>; })()}
          {selected.length > 0 && (selected.length > 1 || selected[0].kind === 'directory') && <button type="button" className="ghost" onClick={() => void archiveSelection(selected)}>Download as ZIP</button>}
          {selected.length > 0 && <button type="button" className="ghost" onClick={() => setProperties(selected)}>Properties</button>}
        </p>
      )}

      {menu && menuEntry && (
        <ContextMenu x={menu.x} y={menu.y} onClose={() => { setMenu(null); focusRow(menu.entryId); }}>
          {selected.length <= 1 && menuEntry.kind === 'file' && previewKindOf(menuEntry.name) !== null && <button type="button" role="menuitem" onClick={() => { setMenu(null); preview(menuEntry); }}>Preview</button>}
          {selected.length <= 1 && <button type="button" role="menuitem" onClick={() => { setMenu(null); if (menuEntry.kind === 'directory') activate(menuEntry); else void download(menuEntry); }}>{menuEntry.kind === 'directory' ? 'Open' : 'Download'}</button>}
          {selected.length <= 1 && menuEntry.kind === 'file' && transfers.canSaveToFile() && <button type="button" role="menuitem" onClick={() => { setMenu(null); void saveToFile(menuEntry); }}>Save to a file…</button>}
          {(selected.length > 1 || menuEntry.kind === 'directory') && <button type="button" role="menuitem" onClick={() => { setMenu(null); void archiveSelection(selected.length > 1 ? selected : [menuEntry]); }}>{selected.length > 1 ? `Download ${selected.length} items as ZIP` : 'Download as ZIP'}</button>}
          {selected.length <= 1 && (() => { const place = placeOf(menuEntry); return place && <button type="button" role="menuitem" onClick={() => { setMenu(null); setPlaces((p) => toggleFavorite(p, place)); }}>{isFavorite(places, place) ? 'Remove from favorites' : 'Add to favorites'}</button>; })()}
          <button type="button" role="menuitem" onClick={() => { setMenu(null); setProperties(selected.length > 1 ? selected : [menuEntry]); }}>{selected.length > 1 ? `Properties of ${selected.length} items` : 'Properties'}</button>
        </ContextMenu>
      )}

      {properties && location && (
        <dialog ref={dialog} className="properties" aria-labelledby="properties-title" onClose={() => setProperties(null)}>
          <h2 id="properties-title">{properties.length === 1 ? <bdi>{properties[0].name}</bdi> : `${properties.length} items`}</h2>
          <dl>
            {properties.length === 1 ? (<>
              <dt>Type</dt><dd>{typeLabel(properties[0])}</dd>
              {properties[0].kind === 'file' && (<><dt>Size</dt><dd>{formatBytes(properties[0].size)} ({BigInt(properties[0].size).toLocaleString()} bytes)</dd></>)}
              <dt>Modified</dt><dd>{formatTime(properties[0].modifiedUnixMs)}</dd>
              <dt>Attributes</dt><dd>{properties[0].hidden ? 'Hidden, read-only access' : 'Read-only access'}</dd>
            </>) : (<>
              <dt>Contains</dt><dd>{properties.filter((entry) => entry.kind === 'file').length} files, {properties.filter((entry) => entry.kind === 'directory').length} folders</dd>
              <dt>Size of files</dt><dd>{formatBytes(totalSize(properties).toString())} ({totalSize(properties).toLocaleString()} bytes; folder contents not counted)</dd>
            </>)}
            <dt>Location</dt><dd><bdi>{[location.pc.name, ...breadcrumbs.map((crumb) => crumb.name)].join(' › ')}</bdi></dd>
          </dl>
          <form method="dialog"><button type="submit">Close</button></form>
        </dialog>
      )}
    </div>
  );
}

interface RowActions {
  click: (event: ReactMouseEvent, entry: FileEntry) => void;
  activate: (entry: FileEntry) => void;
  menu: (entryId: string, x: number, y: number) => void;
  pressStart: (entryId: string, event: React.PointerEvent) => void;
  pressCancel: () => void;
  focus: (entryId: string) => void;
  toggle: (entryId: string) => void;
  consumeSuppressedClick: () => boolean;
}

/** A small picture in the Icons view: requested only when scrolled into view, and an ordinary icon whenever the PC cannot make one. */
function Thumb({ session, pcId, entryId }: { session: Session; pcId: string; entryId: string }) {
  const holder = useRef<HTMLSpanElement>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const node = holder.current;
    if (!node || typeof IntersectionObserver === 'undefined') { setVisible(true); return; }
    const observer = new IntersectionObserver((hits) => { if (hits.some((h) => h.isIntersecting)) { setVisible(true); observer.disconnect(); } }, { rootMargin: '200px' });
    observer.observe(node);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    if (!visible) return;
    let cancelled = false;
    void thumbnails.url(session, pcId, entryId).then((u) => { if (!cancelled) setUrl(u); });
    return () => { cancelled = true; };
  }, [visible, session, pcId, entryId]);
  return (
    <span ref={holder} className={`icon file${url ? ' has-thumb' : ''}`} aria-hidden="true">
      {url && <img src={url} alt="" loading="lazy" referrerPolicy="no-referrer" draggable={false} onError={() => setUrl(null)} />}
    </span>
  );
}

const Row = memo(function Row({ entry, selected, current, checkbox, actions, thumbs }: { entry: FileEntry; selected: boolean; current: boolean; checkbox: boolean; actions: RowActions; thumbs: { session: Session; pcId: string } | null }) {
  return (
    <tr data-entry={entry.entryId} aria-selected={selected} className={`${selected ? 'selected' : ''}${entry.hidden ? ' hidden-item' : ''}` || undefined}
      onClick={(event) => actions.click(event, entry)} onDoubleClick={(event) => { if (!(event.target as HTMLElement).closest('button, input')) actions.activate(entry); }}
      onContextMenu={(event) => { event.preventDefault(); actions.menu(entry.entryId, event.clientX, event.clientY); }}
      onPointerDown={(event) => actions.pressStart(entry.entryId, event)} onPointerUp={actions.pressCancel} onPointerMove={actions.pressCancel} onPointerCancel={actions.pressCancel}>
      {checkbox && (
        <td className="check"><input type="checkbox" aria-label={`Select ${entry.name}`} checked={selected} onChange={() => actions.toggle(entry.entryId)} /></td>
      )}
      <td className="name">
        <button type="button" className="row-button" tabIndex={current ? 0 : -1} onFocus={() => actions.focus(entry.entryId)}
          onClick={() => { if (!actions.consumeSuppressedClick()) actions.activate(entry); }}
          title={entry.name} aria-label={`${entry.kind === 'directory' ? `Open folder ${entry.name}` : opensOnActivate(previewKindOf(entry.name)) ? `Preview ${entry.name}` : `Download ${entry.name}`}${entry.hidden ? ' (hidden item)' : ''}`}>
          {thumbs && entry.kind === 'file' && previewKindOf(entry.name) === 'image' ? <Thumb session={thumbs.session} pcId={thumbs.pcId} entryId={entry.entryId} /> : <span className={`icon ${entry.kind === 'directory' ? 'folder' : 'file'}`} aria-hidden="true" />}<bdi>{entry.name}</bdi>
        </button>
      </td>
      <td className="modified">{formatTime(entry.modifiedUnixMs)}</td>
      <td className="type">{typeLabel(entry)}</td>
      <td className="size">{entry.kind === 'file' ? formatBytes(entry.size) : ''}</td>
    </tr>
  );
});

function ContextMenu({ x, y, onClose, children }: { x: number; y: number; onClose: () => void; children: React.ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    ref.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    const outside = (event: PointerEvent) => { if (!ref.current?.contains(event.target as Node)) close.current(); };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, []);
  const onKeyDown = (event: ReactKeyboardEvent) => {
    const items = [...(ref.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
    const index = items.indexOf(document.activeElement as HTMLElement);
    event.stopPropagation();
    if (event.key === 'Escape' || event.key === 'Tab') { event.preventDefault(); onClose(); }
    else if (event.key === 'ArrowDown') { event.preventDefault(); items[(index + 1) % items.length]?.focus(); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); items[(index - 1 + items.length) % items.length]?.focus(); }
    else if (event.key === 'Home') { event.preventDefault(); items[0]?.focus(); }
    else if (event.key === 'End') { event.preventDefault(); items[items.length - 1]?.focus(); }
  };
  // Rendered at the page root: inside a window it would be clipped by the window's rounded, blurred frame.
  return createPortal(<div ref={ref} className="flyout context" role="menu" aria-label="Actions" style={{ left: Math.max(8, x), top: Math.max(8, y) }} onKeyDown={onKeyDown}>{children}</div>, document.body);
}
