import { api, ApiError, GATEWAY_URL, type Session } from '../../api';
import { parseInt64 } from '../../protocol';

/**
 * Downloads started from this page. Two ways to receive a file:
 *  - "browser": the file is handed to the browser's own download manager. OrbitDrive cannot see whether or where it
 *    was saved; only what the relay sent can be shown.
 *  - "file": the reader picked a destination and this page writes the bytes into it. Progress is real, an
 *    interruption can be resumed, and "Saved" means the file was written and closed.
 * Nothing is buffered as a whole file in page memory in either mode.
 */
export type TransferState = 'preparing' | 'handed' | 'saving' | 'saved' | 'interrupted' | 'changed' | 'cancelled' | 'failed';
export interface Transfer {
  id: number;
  mode: 'browser' | 'file';
  pcId: string;
  entryId: string;
  name: string;
  size: bigint | null;
  revision: string | null;
  sessionId: string | null;
  state: TransferState;
  received: number;
  bytesPerSecond: number | null;
  message: string | null;
  startedAt: number;
}

interface Destination { createWritable(options?: { keepExistingData?: boolean }): Promise<WritableFile>; remove?: () => Promise<void> }
interface WritableFile { write(data: Uint8Array | { type: 'truncate'; size: number }): Promise<void>; seek(position: number): Promise<void>; close(): Promise<void>; abort?: () => Promise<void> }
type SavePicker = (options: { suggestedName: string }) => Promise<Destination>;

interface Internal extends Transfer { destination?: Destination; abort?: AbortController; autoRetries: number; session: Session }

const AUTO_RETRIES = 3;
/** A connection that delivers nothing for this long is treated as lost (networks can die without closing anything). */
const STALL_MS = 20_000;
class Stalled extends Error {}
function orStall<T>(work: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  return Promise.race([work, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Stalled()), STALL_MS); })]).finally(() => clearTimeout(timer));
}
let items: Internal[] = [];
let snapshot: Transfer[] = [];
let nextId = 1;
const listeners = new Set<() => void>();

function publish() {
  snapshot = items.map(({ destination: _d, abort: _a, autoRetries: _r, session: _s, ...visible }) => visible).reverse();
  for (const listener of listeners) listener();
}
function update(item: Internal, patch: Partial<Internal>) { Object.assign(item, patch); publish(); }

export const transfers = {
  subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  list: () => snapshot,
  /** True when this browser lets a page write a file the reader chose (not available on most phones). */
  canSaveToFile: () => typeof (window as unknown as { showSaveFilePicker?: SavePicker }).showSaveFilePicker === 'function',

  /** Hand the file to the browser's own download manager. */
  async toBrowser(session: Session, pcId: string, entry: { entryId: string; name: string }): Promise<Transfer> {
    const item = add(session, 'browser', pcId, entry);
    try {
      const prepared = await api.prepareDownload(session, pcId, entry.entryId);
      update(item, { state: 'handed', size: parseInt64(prepared.size), revision: prepared.revision, sessionId: prepared.sessionId ?? null });
      const anchor = document.createElement('a');
      anchor.href = GATEWAY_URL + prepared.urlPath;
      anchor.rel = 'noreferrer';
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } catch (e) {
      update(item, { state: 'failed', message: e instanceof ApiError ? e.message : 'Download could not start.' });
      throw e;
    }
    return item;
  },

  /** Ask where to save (must be called from a click), then stream the file into that place. */
  async toFile(session: Session, pcId: string, entry: { entryId: string; name: string }): Promise<Transfer | null> {
    const picker = (window as unknown as { showSaveFilePicker?: SavePicker }).showSaveFilePicker;
    if (!picker) return null;
    let destination: Destination;
    try { destination = await picker({ suggestedName: entry.name }); }
    catch { return null; }                                       // the reader closed the picker
    const item = add(session, 'file', pcId, entry);
    item.destination = destination;
    void run(item, 0);
    return item;
  },

  resume(id: number) { const item = find(id); if (item?.mode === 'file' && item.state === 'interrupted') { item.autoRetries = 0; void run(item, item.received); } },
  /** After the source changed: discard what was saved and fetch the new version from the start. */
  restart(id: number) { const item = find(id); if (item?.mode === 'file' && (item.state === 'changed' || item.state === 'interrupted' || item.state === 'failed')) { item.autoRetries = 0; void run(item, 0); } },

  async cancel(id: number) {
    const item = find(id);
    if (!item || !['preparing', 'saving', 'interrupted', 'handed', 'changed'].includes(item.state)) return;
    const wasHanded = item.state === 'handed';
    update(item, { state: 'cancelled', bytesPerSecond: null, message: wasHanded ? 'The link was cancelled. If your browser already has the whole file, it keeps it.' : null });
    item.abort?.abort();
    if (item.sessionId) await api.cancelDownload(item.session, item.sessionId).catch(() => undefined);
    if (item.mode === 'file' && item.destination) {
      // Do not leave a half-written file that looks complete.
      try {
        if (item.destination.remove) { await item.destination.remove(); update(item, { message: 'The partly saved file was removed.' }); }
        else { const writable = await item.destination.createWritable(); await writable.close(); update(item, { message: 'The partly saved file was emptied.' }); }
      } catch { update(item, { message: 'A partly saved file may remain where you chose to save it.' }); }
    }
  },

  remove(id: number) { items = items.filter((item) => item.id !== id || ['preparing', 'saving'].includes(item.state)); publish(); },
  /** Sign-out or unpairing: stop everything and forget the list. */
  reset() { for (const item of items) item.abort?.abort(); items = []; publish(); },
};

function add(session: Session, mode: 'browser' | 'file', pcId: string, entry: { entryId: string; name: string }): Internal {
  const item: Internal = { id: nextId++, mode, pcId, entryId: entry.entryId, name: entry.name, size: null, revision: null, sessionId: null, state: 'preparing', received: 0, bytesPerSecond: null, message: null, startedAt: Date.now(), autoRetries: 0, session };
  items = [...items.slice(-49), item];                            // bounded history
  publish();
  return item;
}
const find = (id: number) => items.find((item) => item.id === id);

/** Streams [offset, end) into the chosen file. Resumes only when the PC confirms the content is still the same revision. */
async function run(item: Internal, offset: number): Promise<void> {
  const abort = new AbortController();
  update(item, { state: 'preparing', abort, message: null, bytesPerSecond: null, received: offset });
  let writable: WritableFile | null = null;
  try {
    const prepared = await orStall(api.prepareDownload(item.session, item.pcId, item.entryId));
    if (abort.signal.aborted) return;
    if (offset > 0 && prepared.revision !== item.revision) {
      update(item, { state: 'changed', sessionId: prepared.sessionId ?? null, message: 'The file changed on the PC after part of it was saved. Start again to save the new version; the two are never mixed.' });
      return;
    }
    const total = parseInt64(prepared.size);
    update(item, { size: total, revision: prepared.revision, sessionId: prepared.sessionId ?? null });
    const headers: Record<string, string> = offset > 0 ? { Range: `bytes=${offset}-`, 'If-Range': `"${prepared.revision}"` } : {};
    const response = await orStall(fetch(GATEWAY_URL + prepared.urlPath, { headers, signal: abort.signal, cache: 'no-store', referrerPolicy: 'no-referrer' }));
    if (!response.ok || !response.body) {
      const failure = (await response.json().catch(() => null)) as { error?: { message?: string } } | null;
      throw new ApiError('internal', failure?.error?.message ?? `The service answered ${response.status}.`, response.status);
    }
    if (offset > 0 && response.status !== 206) {                 // the service would send the whole file: do not append it
      abort.abort();
      update(item, { state: 'changed', message: 'The file changed on the PC after part of it was saved. Start again to save the new version; the two are never mixed.' });
      return;
    }
    writable = await item.destination!.createWritable({ keepExistingData: offset > 0 });
    if (offset > 0) await writable.seek(offset); else await writable.write({ type: 'truncate', size: 0 });
    update(item, { state: 'saving' });
    const reader = response.body.getReader();
    let received = offset, windowStart = performance.now(), windowBytes = 0, lastPaint = 0;
    for (;;) {
      const { done, value } = await orStall(reader.read());
      if (done) break;
      await writable.write(value);                               // each chunk goes to disk before the next is read
      received += value.length; windowBytes += value.length;
      item.received = received;                                  // exact count of bytes written: the resume point
      const now = performance.now();
      if (now - lastPaint > 200) {
        const elapsed = (now - windowStart) / 1000;
        update(item, { received, bytesPerSecond: elapsed > 0.5 ? windowBytes / elapsed : item.bytesPerSecond });
        if (elapsed > 3) { windowStart = now; windowBytes = 0; }
        lastPaint = now;
      }
    }
    if (BigInt(received) !== total) throw new Error('short');
    await writable.close();
    writable = null;
    update(item, { state: 'saved', received, bytesPerSecond: null, message: null, autoRetries: 0 });
  } catch (e) {
    // Keep what was written so the download can continue from there.
    if (writable) await writable.close().catch(() => undefined);
    if (item.state === 'cancelled' || abort.signal.aborted) return;
    if (e instanceof Stalled) abort.abort();                     // give up on the silent connection before trying again
    const fatal = e instanceof ApiError && e.code !== 'network' && ![502, 503, 504].includes(e.status);
    if (fatal) { update(item, { state: 'failed', bytesPerSecond: null, message: (e as ApiError).message }); return; }
    update(item, { state: 'interrupted', bytesPerSecond: null, message: 'The connection was lost. What was saved so far is kept.' });
    if (item.autoRetries < AUTO_RETRIES) {
      const attempt = ++item.autoRetries;
      setTimeout(() => { if (item.state === 'interrupted' && item.autoRetries === attempt) void run(item, item.received); }, 1500 * attempt);
    }
  }
}
