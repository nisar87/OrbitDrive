import { api, ApiError, type Session } from '../../api';

/**
 * Uploads from this browser into a folder the PC's owner enabled. The browser never names a path: it names the folder it was shown and
 * a file name. A file is sent in numbered pieces, each with its SHA-256; the PC stores a piece only if it is the next one and its
 * checksum matches, and tells us how many bytes it really holds. So progress below is "bytes the PC has stored", and a file is called
 * saved only after the PC committed it (a separate, final step). Nothing is ever opened, run or unpacked.
 */
export type UploadPhase = 'queued' | 'preparing' | 'sending' | 'finishing' | 'paused' | 'done' | 'failed' | 'cancelled';
export interface UploadTarget { pcId: string; pcName: string; rootId: string; folderEntryId: string | null; /** For display: shared folder name, then folders. */ path: string[] }
export interface UploadJob {
  id: number;
  pcId: string;
  pcName: string;
  fileName: string;
  size: number;
  collision: 'reject' | 'rename';
  phase: UploadPhase;
  /** Bytes the PC has stored so far (not merely sent). */
  storedBytes: number;
  destination: string[];
  finalName: string | null;
  renamed: boolean;
  sha256: string | null;
  originMarked: boolean;
  message: string | null;
}

interface Internal extends UploadJob {
  session: Session;
  target: UploadTarget;
  file: File;
  uploadId: string | null;
  stop: boolean;
  abort: AbortController | null;
  wake: (() => void) | null;
}

export const MAX_FILES_PER_BATCH = 200;
const MAX_QUICK_RETRIES = 4;
let jobs: Internal[] = [];
let snapshot: UploadJob[] = [];
let nextId = 1;
let running = false;
let lock: { release: () => Promise<void> } | null = null;
const listeners = new Set<() => void>();

function publish() {
  snapshot = jobs.map(({ session: _s, target: _t, file: _f, uploadId: _u, stop: _st, abort: _a, wake: _w, ...visible }) => visible).reverse();
  for (const listener of listeners) listener();
}
function update(job: Internal, patch: Partial<Internal>) { Object.assign(job, patch); publish(); }
/** Test hook: shrinks every wait so retry paths can be exercised quickly. */
export const tuning = { scale: 1, chunkTimeoutMs: 60_000 };
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms * tuning.scale));

async function sha256Hex(data: ArrayBuffer): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) throw new Error('This browser cannot check files while sending. Open OrbitDrive over a secure (https) connection.');
  return [...new Uint8Array(await subtle.digest('SHA-256', data))].map((b) => b.toString(16).padStart(2, '0')).join('');
}

const transient = (e: unknown) => e instanceof ApiError && (e.code === 'network' || e.code === 'timeout' || e.code === 'pc_offline' || e.code === 'unavailable' || e.code === 'internal' || e.status >= 502);
const fatalText = (e: ApiError): string => {
  switch (e.code) {
    case 'not_found': case 'expired': return 'The PC no longer has this upload (it expired, or the PC restarted). Add the file again to start over.';
    case 'forbidden': return 'The PC owner has not allowed uploads here, or has turned them off.';
    case 'unauthenticated': return 'You were signed out. Sign in again and add the file again.';
    default: return e.message;
  }
};

/** Pauses a job until the user (or the network coming back) resumes it. */
function waitForResume(job: Internal, why: string): Promise<void> {
  update(job, { phase: 'paused', message: why });
  return new Promise<void>((resolve) => { job.wake = () => { job.wake = null; resolve(); }; });
}

async function holdScreenAwake() {
  try {
    const wakeLock = (navigator as Navigator & { wakeLock?: { request: (t: 'screen') => Promise<{ release: () => Promise<void> }> } }).wakeLock;
    if (wakeLock && !lock) lock = await wakeLock.request('screen');
  } catch { /* not allowed or not supported: uploads still work, the phone may just sleep */ }
}
async function releaseScreen() { try { await lock?.release(); } catch { /* ignore */ } lock = null; }

async function send(job: Internal): Promise<void> {
  const { session, target, file } = job;
  update(job, { phase: 'preparing', message: null });
  let started;
  for (let attempt = 0; ; attempt++) {
    try {
      started = await api.startUpload(session, target.pcId, { rootId: target.rootId, folderEntryId: target.folderEntryId, name: job.fileName, size: String(job.size), collision: job.collision });
      break;
    } catch (e) {
      if (job.stop) return;
      if (e instanceof ApiError && e.code === 'limit_exceeded' && attempt < 30) { await sleep(Math.min(5000, 1000 + attempt * 500)); continue; }   // too many uploads at once: wait for a slot
      if (transient(e) && attempt < MAX_QUICK_RETRIES) { await sleep(1000 * 2 ** attempt); continue; }
      if (transient(e)) { await waitForResume(job, 'The PC could not be reached. Waiting for the connection; nothing was sent yet.'); if (job.stop) return; attempt = -1; continue; }
      throw e;
    }
  }
  job.uploadId = started.uploadId;
  update(job, { destination: started.destination, collision: started.collision as 'reject' | 'rename' });
  const chunkBytes = started.chunkBytes;
  let stored = 0, failures = 0, damaged = 0;
  update(job, { phase: 'sending', storedBytes: 0 });
  while (stored < job.size) {
    if (job.stop) return;
    let piece: ArrayBuffer, checksum: string;
    try {
      piece = await file.slice(stored, Math.min(stored + chunkBytes, job.size)).arrayBuffer();
      checksum = await sha256Hex(piece);
    } catch (e) {
      throw new Error(e instanceof Error && e.message.startsWith('This browser') ? e.message : 'This file could not be read any more (it may have been moved or deleted). Nothing was saved.');
    }
    const abort = job.abort = new AbortController();
    let timedOut = false;
    const timer = setTimeout(() => { timedOut = true; abort.abort(); }, tuning.chunkTimeoutMs);   // a link that goes silent must not hang the upload forever
    try {
      let result;
      try { result = await api.uploadChunk(session, target.pcId, started.uploadId, stored, piece, checksum, abort.signal); }
      catch (e) { if (timedOut && !job.stop) throw new ApiError('network', 'The connection stopped answering.', 0); throw e; }
      stored = Number(result.status.committedBytes);      // the PC's own count, whether or not this piece was the one it wanted
      failures = 0; damaged = 0;
      update(job, { storedBytes: stored });
    } catch (e) {
      if (job.stop) return;
      if (e instanceof DOMException && e.name === 'AbortError') return;
      if (e instanceof ApiError && e.code === 'checksum_mismatch' && ++damaged <= 3) continue;                  // damaged in transit: send the same piece again
      if (e instanceof ApiError && e.code === 'limit_exceeded') { await sleep(2000); continue; }
      // 'conflict' on a piece means the PC is still holding the piece that was cut off a moment ago; it lets go when it notices the
      // connection ended. That is a wait-and-ask-again situation, not a failure (a conflict that is really "no longer receiving" is caught below).
      const stillHoldingOldPiece = e instanceof ApiError && e.code === 'conflict';
      if (transient(e) || stillHoldingOldPiece || (e instanceof ApiError && e.code === 'checksum_mismatch')) {
        failures++;
        if (failures > MAX_QUICK_RETRIES) { await waitForResume(job, 'The connection was interrupted. Tap Resume when you are back online; the PC keeps what it already stored.'); if (job.stop) return; failures = 0; update(job, { phase: 'sending', message: null }); }
        else await sleep(Math.min(8000, 700 * 2 ** failures));
        // Whatever happened to the piece in flight, ask the PC where it really is.
        try {
          const where = await api.uploadStatus(session, target.pcId, started.uploadId);
          if (stillHoldingOldPiece && where.state !== 'receiving') throw e;                     // really over (cancelled, failed, expired): do not wait for it
          stored = Number(where.committedBytes); update(job, { storedBytes: stored });
        } catch (inner) { if (!transient(inner)) throw inner; }
        continue;
      }
      throw e;
    } finally { clearTimeout(timer); }
  }
  update(job, { phase: 'finishing', message: null });
  for (let attempt = 0; ; attempt++) {
    try {
      const done = await api.finishUpload(session, target.pcId, started.uploadId);        // asking twice is safe: the PC returns the same result
      update(job, { phase: 'done', storedBytes: job.size, finalName: done.finalName ?? job.fileName, renamed: done.renamed, sha256: done.sha256 ?? null, originMarked: done.originMarked, message: null });
      return;
    } catch (e) {
      if (job.stop) return;
      if (transient(e) && attempt < 8) { await sleep(Math.min(8000, 700 * 2 ** attempt)); continue; }
      throw e;
    }
  }
}

/**
 * Tells the PC to drop a half-received file. If the connection is what failed, the message is lost and the PC would keep that upload slot
 * taken until it times out, refusing new uploads meanwhile (found by the 30-minute Windows soak). So a lost cancel is retried for a while.
 */
function cancelOnPc(job: Internal): void {
  const { session, pcId, uploadId } = job;
  if (!uploadId) return;
  void (async () => {
    for (let attempt = 0; attempt < 6; attempt++) {
      try { await api.cancelUpload(session, pcId, uploadId); return; }
      catch (e) { if (!transient(e)) return; await sleep(Math.min(8000, 500 * 2 ** attempt)); }
    }
  })();
}

async function pump() {
  if (running) return;
  running = true;
  void holdScreenAwake();
  try {
    for (let job = jobs.find((j) => j.phase === 'queued'); job; job = jobs.find((j) => j.phase === 'queued')) {
      try { await send(job); }
      catch (e) {
        if (!job.stop) update(job, { phase: 'failed', message: e instanceof ApiError ? fatalText(e) : e instanceof Error ? e.message : 'The upload failed.' });
        cancelOnPc(job);
      }
    }
  } finally { running = false; await releaseScreen(); }
}

export const uploads = {
  subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  list: () => snapshot,

  /** Queues files for one folder on one PC. The caller has already shown the destination, sizes and the choice for existing names. */
  enqueue(session: Session, target: UploadTarget, files: readonly File[], collision: 'reject' | 'rename'): number {
    const accepted = files.slice(0, MAX_FILES_PER_BATCH);
    for (const file of accepted) {
      jobs.push({
        id: nextId++, pcId: target.pcId, pcName: target.pcName, fileName: file.name, size: file.size, collision, phase: 'queued', storedBytes: 0, destination: target.path,
        finalName: null, renamed: false, sha256: null, originMarked: false, message: null, session, target, file, uploadId: null, stop: false, abort: null, wake: null,
      });
    }
    publish();
    void pump();
    return accepted.length;
  },

  /** Stops a file. What the PC stored so far is deleted; a file that was already committed stays. */
  cancel(id: number) {
    const job = jobs.find((j) => j.id === id);
    if (!job || job.phase === 'done' || job.phase === 'failed' || job.phase === 'cancelled') return;
    job.stop = true;
    job.abort?.abort();
    job.wake?.();
    cancelOnPc(job);
    update(job, { phase: 'cancelled', message: 'Cancelled. Nothing from this file was kept on the PC.' });
  },

  resume(id: number) { const job = jobs.find((j) => j.id === id); if (job?.phase === 'paused') job.wake?.(); },
  resumeAll() { for (const job of jobs) if (job.phase === 'paused') job.wake?.(); },

  dismiss(id: number) {
    const job = jobs.find((j) => j.id === id);
    if (job && !['done', 'failed', 'cancelled'].includes(job.phase)) this.cancel(id);
    jobs = jobs.filter((j) => j.id !== id);
    publish();
  },
  clearFinished() { jobs = jobs.filter((j) => !['done', 'failed', 'cancelled'].includes(j.phase)); publish(); },
  active: () => jobs.some((j) => !['done', 'failed', 'cancelled'].includes(j.phase)),
};

if (typeof window !== 'undefined') {
  // A phone that comes back online or returns to the page carries on by itself.
  window.addEventListener('online', () => uploads.resumeAll());
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') uploads.resumeAll(); });
}

/** Test hook: forget everything. */
export function resetUploadsForTests() { for (const j of jobs) { j.stop = true; j.wake?.(); } jobs = []; snapshot = []; nextId = 1; running = false; lock = null; }
