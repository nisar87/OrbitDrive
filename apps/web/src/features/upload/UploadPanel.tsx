import { useState, useSyncExternalStore } from 'react';
import type { Session } from '../../api';
import { formatBytes } from '../../format';
import type { RootSummary } from '../../protocol';
import { MAX_FILES_PER_BATCH, uploads, type UploadJob, type UploadTarget } from './upload';

/** What the person is shown before anything is sent: where the files will go, how big they are, what the limits are, and what happens to a name that is taken. */
export function UploadConfirm({ session, files, target, root, onDone }: { session: Session; files: File[]; target: UploadTarget; root: RootSummary; onDone: () => void }) {
  const [collision, setCollision] = useState<'reject' | 'rename'>('reject');
  const maxFile = BigInt(root.maxUploadBytes);
  const quotaLeft = root.uploadQuotaLeftBytes != null ? BigInt(root.uploadQuotaLeftBytes) : null;
  const tooBig = files.filter((f) => maxFile > 0n && BigInt(f.size) > maxFile);
  const sendable = files.filter((f) => !tooBig.includes(f)).slice(0, MAX_FILES_PER_BATCH);
  const total = sendable.reduce((sum, f) => sum + BigInt(f.size), 0n);
  const overQuota = quotaLeft !== null && total > quotaLeft;
  const skippedByCount = files.length - tooBig.length - sendable.length;
  return (
    <section className="upload-confirm" aria-label="Upload files" data-testid="upload-confirm">
      <h3>Upload to <bdi>{target.pcName}</bdi></h3>
      <p>Folder on the PC: <strong data-testid="upload-destination"><bdi>{target.path.join(' / ')}</bdi></strong></p>
      <ul className="upload-files">
        {sendable.slice(0, 6).map((f, i) => <li key={i}><bdi>{f.name}</bdi> <span className="hint">{formatBytes(String(f.size))}</span></li>)}
      </ul>
      {sendable.length > 6 && <p className="hint">…and {sendable.length - 6} more.</p>}
      <p><strong>{sendable.length}</strong> file{sendable.length === 1 ? '' : 's'}, <strong>{formatBytes(total.toString())}</strong> in total.
        {' '}The PC owner allows up to {formatBytes(maxFile.toString())} per file{quotaLeft !== null ? ` and ${formatBytes(quotaLeft.toString())} more in this folder` : ''}.</p>
      {tooBig.length > 0 && <p role="alert" className="notice error" data-testid="upload-toobig">{tooBig.length} file{tooBig.length === 1 ? ' is' : 's are'} larger than the limit and will not be sent: <bdi>{tooBig.slice(0, 3).map((f) => f.name).join(', ')}</bdi>{tooBig.length > 3 ? '…' : ''}</p>}
      {skippedByCount > 0 && <p role="alert" className="notice error">Only the first {MAX_FILES_PER_BATCH} files are sent at a time; {skippedByCount} more were left out.</p>}
      {overQuota && <p role="alert" className="notice error" data-testid="upload-overquota">These files are more than the space the PC owner allowed for this folder. The PC will refuse the ones that do not fit.</p>}
      <fieldset className="upload-collision">
        <legend>If a file with the same name is already there</legend>
        <label><input type="radio" name="collision" checked={collision === 'reject'} onChange={() => setCollision('reject')} /> Do not send it (nothing is ever replaced)</label>
        <label><input type="radio" name="collision" checked={collision === 'rename'} onChange={() => setCollision('rename')} /> Keep both: add a number, like <bdi>photo (2).jpg</bdi></label>
      </fieldset>
      <p className="hint">Files are only stored in that folder. OrbitDrive never opens, runs or unpacks them, and an upload cannot overwrite or delete anything on the PC.</p>
      <p className="actions">
        <button type="button" disabled={sendable.length === 0} onClick={() => { uploads.enqueue(session, target, sendable, collision); onDone(); }}>Upload {sendable.length} file{sendable.length === 1 ? '' : 's'}</button>
        <button type="button" className="ghost" onClick={onDone}>Cancel</button>
      </p>
    </section>
  );
}

/** Uploads in progress and finished for this PC. Progress counts bytes the PC has stored; "Saved" appears only after the PC committed the file. */
export function UploadJobs({ pcId }: { pcId: string }) {
  const jobs = useSyncExternalStore(uploads.subscribe, uploads.list).filter((job) => job.pcId === pcId);
  if (jobs.length === 0) return null;
  const finished = jobs.some((j) => ['done', 'failed', 'cancelled'].includes(j.phase));
  return (
    <section className="uploads" aria-label="Uploads">
      {jobs.map((job) => <Job key={job.id} job={job} />)}
      {finished && <p><button type="button" className="ghost" onClick={() => uploads.clearFinished()}>Clear finished</button></p>}
    </section>
  );
}

function Job({ job }: { job: UploadJob }) {
  const percent = job.size > 0 ? Math.min(100, Math.floor((job.storedBytes / job.size) * 100)) : job.phase === 'done' ? 100 : 0;
  return (
    <article className={`upload upload-${job.phase}`} data-testid="upload-job" aria-label={`Upload of ${job.fileName}`}>
      <header><strong><bdi>{job.fileName}</bdi></strong> <span className="hint">{formatBytes(String(job.size))}</span></header>
      {(job.phase === 'queued' || job.phase === 'preparing') && <p role="status">{job.phase === 'queued' ? 'Waiting for its turn…' : 'Asking the PC…'}</p>}
      {(job.phase === 'sending' || job.phase === 'finishing' || job.phase === 'paused') && (
        <>
          <progress max={Math.max(job.size, 1)} value={job.storedBytes} aria-label={`Stored on the PC: ${percent}%`} />
          <p role="status">{job.phase === 'finishing' ? 'All data arrived. Waiting for the PC to check and save the file…' : job.phase === 'paused' ? job.message : `Stored on the PC: ${percent}% (${formatBytes(String(job.storedBytes))}). Not saved yet.`}</p>
        </>
      )}
      {job.phase === 'done' && (
        <p role="status" data-testid="upload-saved">
          Saved on the PC{job.renamed ? <> as <strong><bdi>{job.finalName}</bdi></strong> because that name was already taken</> : null}. The PC checked every piece.
          {job.sha256 && <span className="hint"> SHA-256 {job.sha256.slice(0, 12)}…</span>}
          {job.originMarked && <span className="hint"> Marked as downloaded from the internet, so Windows will warn before it is opened.</span>}
        </p>
      )}
      {(job.phase === 'failed' || job.phase === 'cancelled') && <p role="alert" className={job.phase === 'cancelled' ? 'notice' : 'notice error'}>{job.message}</p>}
      <p className="actions">
        {job.phase === 'paused' && <button type="button" onClick={() => uploads.resume(job.id)}>Resume</button>}
        {!['done', 'failed', 'cancelled'].includes(job.phase) && <button type="button" className="ghost" onClick={() => uploads.cancel(job.id)}>Cancel</button>}
        {['done', 'failed', 'cancelled'].includes(job.phase) && <button type="button" className="ghost" onClick={() => uploads.dismiss(job.id)}>Dismiss</button>}
      </p>
    </article>
  );
}
