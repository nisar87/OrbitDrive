import { useSyncExternalStore } from 'react';
import { ApiError, type Session } from '../../api';
import { formatBytes } from '../../format';
import { transfers } from '../transfers/transfers';
import { archives, type ArchiveJob } from './archive';

const subscribe = archives.subscribe;
const snapshot = archives.list;

/** The PC's ZIPs in progress or ready for this PC: progress, cancel, honest omissions, and the way to save or download them. */
export function ArchivePanel({ session, pcId, onOpenTransfers }: { session: Session; pcId: string; onOpenTransfers: () => void }) {
  const jobs = useSyncExternalStore(subscribe, snapshot).filter((job) => job.pcId === pcId);
  if (jobs.length === 0) return null;
  return (
    <section className="archives" aria-label="ZIP archives">
      {jobs.map((job) => <Job key={job.id} job={job} session={session} onOpenTransfers={onOpenTransfers} />)}
    </section>
  );
}

function Job({ job, session, onOpenTransfers }: { job: ArchiveJob; session: Session; onOpenTransfers: () => void }) {
  const status = job.status;
  const total = status ? Number(status.bytesTotal) : 0, done = status ? Number(status.bytesDone) : 0;
  const name = status?.name ?? `${job.label}.zip`;
  const omitted = status?.omittedCount ?? 0;

  const save = async () => {
    if (!job.downloadId) return;
    const started = await transfers.toFile(session, job.pcId, { entryId: job.downloadId, name });
    if (started) onOpenTransfers();
  };
  const download = async () => { if (job.downloadId) await transfers.toBrowser(session, job.pcId, { entryId: job.downloadId, name }).catch((e: unknown) => { void (e instanceof ApiError); }); };

  return (
    <article className={`archive archive-${job.phase}`} data-testid="archive-job" aria-label={`ZIP of ${job.label}`}>
      <header>
        <strong><bdi>{job.label}</bdi></strong>
        <span className="hint"> from <bdi>{job.pcName}</bdi></span>
      </header>
      {(job.phase === 'starting' || job.phase === 'building') && (
        <>
          <p role="status">{job.phase === 'starting' ? 'Asking the PC to prepare a ZIP…' : status && status.filesTotal > 0
            ? `Preparing on the PC: ${status.filesDone.toLocaleString()} of ${status.filesTotal.toLocaleString()} files · ${formatBytes(String(done))} of ${formatBytes(String(total))}`
            : 'Preparing on the PC: counting files…'}</p>
          <progress max={total > 0 ? total : undefined} value={total > 0 ? done : undefined} aria-label="ZIP progress" />
          <p className="hint">The ZIP is built on the PC first so that saving it can be resumed if your connection drops. Nothing is changed in your files.</p>
          <button type="button" className="ghost" onClick={() => archives.cancel(job.id)}>Cancel</button>
        </>
      )}
      {job.phase === 'ready' && status && (
        <>
          <p role="status">{omitted === 0 ? <>The ZIP is ready: <strong><bdi>{name}</bdi></strong> ({formatBytes(String(status.size ?? 0))}).</> : <>The ZIP is ready but <strong>incomplete</strong>: <strong><bdi>{name}</bdi></strong> ({formatBytes(String(status.size ?? 0))}).</>}</p>
          {omitted > 0 && (
            <div role="alert" className="notice error" data-testid="archive-omissions">
              <p><strong>{omitted.toLocaleString()} item{omitted === 1 ? '' : 's'} could not be included.</strong> The ZIP also contains a file named <code>_OrbitDrive_omitted.txt</code> that lists them.</p>
              <ul>{status.omitted.slice(0, 5).map((o, i) => <li key={i}><bdi>{o.path}</bdi> — {o.reason}</li>)}</ul>
              {omitted > 5 && <p className="hint">…and {(omitted - 5).toLocaleString()} more.</p>}
            </div>
          )}
          <p className="actions">
            {transfers.canSaveToFile() && <button type="button" onClick={() => void save()}>Save to a file…</button>}
            <button type="button" className={transfers.canSaveToFile() ? 'ghost' : ''} onClick={() => void download()}>Download</button>
            <button type="button" className="ghost" onClick={() => archives.dismiss(job.id)}>Discard</button>
          </p>
          <p className="hint">Kept on the PC for a few hours; saving to a file can be resumed. Discard removes it from the PC now.</p>
        </>
      )}
      {(job.phase === 'failed' || job.phase === 'cancelled' || job.phase === 'gone') && (
        <>
          <p role="alert" className={job.phase === 'cancelled' ? 'notice' : 'notice error'}>{job.message}</p>
          <button type="button" className="ghost" onClick={() => archives.dismiss(job.id)}>Dismiss</button>
        </>
      )}
    </article>
  );
}
