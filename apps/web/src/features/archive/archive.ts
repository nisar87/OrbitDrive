import { api, ApiError, ARCHIVE_PREFIX, type Session } from '../../api';
import type { ArchiveStatus } from '../../protocol';

/**
 * ZIP archives of folders and multi-selections. The PC prepares ("stages") the archive first, so a download of it can be resumed;
 * this page only starts it, shows progress, can cancel it, and then hands the finished archive to the normal download path
 * (entry id "archive:<id>"). An archive that left items out says so, loudly: it is never presented as complete.
 */
export type ArchivePhase = 'starting' | 'building' | 'ready' | 'failed' | 'cancelled' | 'gone';
export interface ArchiveJob {
  id: number;
  pcId: string;
  pcName: string;
  label: string;
  items: number;
  archiveId: string | null;
  phase: ArchivePhase;
  status: ArchiveStatus | null;
  message: string | null;
  /** The id that the download path understands. */
  downloadId: string | null;
}

interface Internal extends ArchiveJob { session: Session; stop: boolean; timer?: ReturnType<typeof setTimeout> }

let jobs: Internal[] = [];
let snapshot: ArchiveJob[] = [];
let nextId = 1;
const listeners = new Set<() => void>();
const FIRST_DELAY_MS = 500, MAX_DELAY_MS = 2000, MAX_NETWORK_FAILURES = 6;

function publish() {
  snapshot = jobs.map(({ session: _s, stop: _st, timer: _t, ...visible }) => visible).reverse();
  for (const listener of listeners) listener();
}
function update(job: Internal, patch: Partial<Internal>) { Object.assign(job, patch); publish(); }

/** The same PC for every item. A selection that mixes PCs is refused before anything is sent. */
export function assertOnePc(pcId: string, items: readonly { pcId: string }[]): void {
  if (items.some((item) => item.pcId !== pcId)) throw new Error('These items belong to different PCs. An archive can only hold items from one PC.');
}

export const archives = {
  subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  list: () => snapshot,

  /** Starts preparing an archive of items that all live on `pc` (the caller's current folder). */
  start(session: Session, pc: { pcId: string; name: string }, entries: readonly { entryId: string; name: string }[], strict = false): number {
    const label = entries.length === 1 ? entries[0].name : `${entries.length} items`;
    const job: Internal = { id: nextId++, pcId: pc.pcId, pcName: pc.name, label, items: entries.length, archiveId: null, phase: 'starting', status: null, message: null, downloadId: null, session, stop: false };
    jobs.push(job);
    if (jobs.length > 10) jobs = jobs.filter((j) => j.phase === 'starting' || j.phase === 'building' || j.phase === 'ready').slice(-10);
    publish();
    void (async () => {
      try {
        const started = await api.startArchive(session, pc.pcId, entries.map((e) => e.entryId), strict);
        if (job.stop) { void api.cancelArchive(session, pc.pcId, started.archiveId).catch(() => undefined); return; }
        update(job, { archiveId: started.archiveId, phase: 'building' });
        poll(job, FIRST_DELAY_MS, 0);
      } catch (e) {
        update(job, { phase: 'failed', message: e instanceof ApiError ? e.message : 'The archive could not be started.' });
      }
    })();
    return job.id;
  },

  /** Cancels a running preparation, or discards a finished archive from the PC. */
  cancel(id: number) {
    const job = jobs.find((j) => j.id === id);
    if (!job) return;
    job.stop = true;
    clearTimeout(job.timer);
    if (job.archiveId) void api.cancelArchive(job.session, job.pcId, job.archiveId).catch(() => undefined);
    update(job, { phase: 'cancelled', message: 'Cancelled. Nothing is kept on the PC.', downloadId: null });
  },

  dismiss(id: number) {
    const job = jobs.find((j) => j.id === id);
    if (job && (job.phase === 'ready' || job.phase === 'building')) this.cancel(id);
    jobs = jobs.filter((j) => j.id !== id);
    publish();
  },

  clearAll() { for (const job of [...jobs]) this.dismiss(job.id); },
};

function poll(job: Internal, delay: number, failures: number) {
  job.timer = setTimeout(async () => {
    if (job.stop || !job.archiveId) return;
    try {
      const status = await api.archiveStatus(job.session, job.pcId, job.archiveId);
      if (job.stop) return;
      if (status.state === 'ready') update(job, { phase: 'ready', status, downloadId: ARCHIVE_PREFIX + job.archiveId, message: null });
      else if (status.state === 'failed') update(job, { phase: 'failed', status, message: status.error ?? 'The archive could not be prepared.' });
      else if (status.state === 'cancelled') update(job, { phase: 'cancelled', status, message: 'The PC stopped preparing this archive.' });
      else { update(job, { phase: 'building', status }); poll(job, Math.min(MAX_DELAY_MS, delay + 250), 0); }
    } catch (e) {
      if (job.stop) return;
      if (e instanceof ApiError && (e.code === 'not_found' || e.code === 'forbidden')) update(job, { phase: 'gone', message: 'The PC no longer has this archive. It may have expired, or the folder is no longer shared. Prepare it again.' });
      else if (e instanceof ApiError && e.code === 'pc_offline') update(job, { phase: 'failed', message: 'The PC went offline while the archive was being prepared.' });
      else if (failures + 1 >= MAX_NETWORK_FAILURES) update(job, { phase: 'failed', message: 'Lost contact with the MyOrbitDrive service. Check your connection and try again.' });
      else poll(job, Math.min(MAX_DELAY_MS, delay * 2), failures + 1);
    }
  }, delay);
}

/** Test hook: forget everything. */
export function resetArchivesForTests() { for (const j of jobs) clearTimeout(j.timer); jobs = []; snapshot = []; nextId = 1; }
