import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, GATEWAY_URL, type Session } from '../../api';
import type { Breadcrumb, DownloadSession, FileEntry, PcSummary, RootSummary } from '../../protocol';
import { formatBytes, formatTime } from '../../format';
import { Devices } from '../devices/Devices';

interface Location { pc: PcSummary; root: RootSummary; entryId: string | null }
type Transfer = { name: string; phase: 'preparing' } | { name: string; phase: 'handed'; session: DownloadSession } | { name: string; phase: 'failed'; message: string };

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

export function Explorer({ session, onAuthLost }: { session: Session; onAuthLost: (error: unknown) => void }) {
  const [pcs, setPcs] = useState<PcSummary[] | null>(null);
  const [roots, setRoots] = useState<Record<string, RootSummary[] | string>>({});
  const [location, setLocation] = useState<Location | null>(null);
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [breadcrumbs, setBreadcrumbs] = useState<Breadcrumb[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [skipped, setSkipped] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [transfer, setTransfer] = useState<Transfer | null>(null);

  const fail = useCallback((e: unknown) => {
    onAuthLost(e);
    setError(e instanceof ApiError ? e.message : 'Something went wrong.');
  }, [onAuthLost]);

  const loadHome = useCallback(async () => {
    setError(null); setLocation(null);
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

  useEffect(() => { void loadHome(); }, [loadHome]);

  const open = useCallback(async (target: Location, more?: string) => {
    setLoading(true); setError(null);
    try {
      const page = await api.directory(session, target.pc.pcId, { rootId: target.root.rootId, entryId: target.entryId, cursor: more ?? null, pageSize: 100 });
      setLocation(target);
      setEntries((previous) => (more ? [...previous, ...page.entries] : page.entries));
      setBreadcrumbs(page.breadcrumbs);
      setCursor(page.nextCursor ?? null);
      setSkipped(page.skippedLinks);
    } catch (e) { fail(e); }
    finally { setLoading(false); }
  }, [session, fail]);

  async function download(entry: FileEntry) {
    if (!location) return;
    setTransfer({ name: entry.name, phase: 'preparing' });
    try {
      const prepared = await api.prepareDownload(session, location.pc.pcId, entry.entryId);
      setTransfer({ name: entry.name, phase: 'handed', session: prepared });
      // Native browser download: the relay streams with Content-Disposition; nothing is buffered in page memory.
      const anchor = document.createElement('a');
      anchor.href = GATEWAY_URL + prepared.urlPath;
      anchor.rel = 'noreferrer';
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } catch (e) {
      onAuthLost(e);
      setTransfer({ name: entry.name, phase: 'failed', message: e instanceof ApiError ? e.message : 'Download could not start.' });
    }
  }

  if (!location) {
    return (
      <section aria-label="This account's PCs">
        <div className="commandbar"><button type="button" className="ghost" onClick={() => void loadHome()}>Refresh</button></div>
        {error && <p role="alert" className="notice error">{error}</p>}
        {pcs === null && !error && <p className="empty">Loading your PCs…</p>}
        {pcs?.length === 0 && <p className="empty">No PCs are paired with this browser yet.</p>}
        {pcs?.map((pc) => {
          const state = roots[pc.pcId];
          return (
            <article key={pc.pcId} className="pc">
              <h2><span className={pc.online ? 'dot online' : 'dot'} aria-hidden="true" />{pc.name} <small>{pc.online ? 'Online' : `Offline · last seen ${formatTime(pc.lastSeenUnixMs)}`}</small></h2>
              {typeof state === 'string' && <p className="notice">{state}</p>}
              {Array.isArray(state) && state.length === 0 && <p className="empty">No folders are shared from this PC. Choose folders to share in OrbitDrive on the PC.</p>}
              {Array.isArray(state) && (
                <ul className="tiles">
                  {state.map((root) => (
                    <li key={root.rootId}>
                      <button type="button" className="tile" disabled={!root.available} onClick={() => void open({ pc, root, entryId: null })}>
                        <span className="icon folder" aria-hidden="true" />
                        <span>{root.name}<small>{root.available ? 'Read-only' : 'Unavailable on the PC'}</small></span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              <ConfirmButton label="Disconnect this PC" confirmLabel={`Yes, disconnect ${pc.name}`}
                onConfirm={async () => { try { await api.disconnectPc(session, pc.pcId); await loadHome(); } catch (e) { fail(e); } }} />
            </article>
          );
        })}
        {pcs !== null && <Devices session={session} pcs={pcs} onError={fail} />}
      </section>
    );
  }

  return (
    <section aria-label="Files">
      <nav className="commandbar" aria-label="Breadcrumb">
        <button type="button" className="ghost" onClick={() => void loadHome()}>{location.pc.name}</button>
        {breadcrumbs.map((crumb, index) => (
          <span key={index} className="crumb">
            <span aria-hidden="true">›</span>
            <button type="button" className="ghost" aria-current={index === breadcrumbs.length - 1 ? 'page' : undefined}
              onClick={() => void open({ ...location, entryId: crumb.entryId ?? null })}>{crumb.name}</button>
          </span>
        ))}
        <span className="spacer" />
        <button type="button" className="ghost" onClick={() => void open(location)}>Refresh</button>
      </nav>
      {error && <p role="alert" className="notice error">{error}</p>}
      {transfer && (
        <p role="status" className={transfer.phase === 'failed' ? 'notice error' : 'notice'} data-testid="transfer-status">
          {transfer.phase === 'preparing' && <>Checking <strong>{transfer.name}</strong> on the PC…</>}
          {transfer.phase === 'handed' && <><strong>{transfer.name}</strong> ({formatBytes(transfer.session.size)}) was handed to your browser. Check your browser's downloads — OrbitDrive cannot see when the file has finished saving.</>}
          {transfer.phase === 'failed' && <><strong>{transfer.name}</strong>: {transfer.message}</>}
        </p>
      )}
      <table className="details">
        <thead><tr><th scope="col">Name</th><th scope="col" className="num">Size</th><th scope="col" className="when">Modified</th></tr></thead>
        <tbody>
          {entries.map((entry) => (
            <tr key={entry.entryId}>
              <td>
                <button type="button" className="row-button"
                  onClick={() => (entry.kind === 'directory' ? void open({ ...location, entryId: entry.entryId }) : void download(entry))}
                  aria-label={entry.kind === 'directory' ? `Open folder ${entry.name}` : `Download ${entry.name}`}>
                  <span className={`icon ${entry.kind === 'directory' ? 'folder' : 'file'}`} aria-hidden="true" /><bdi>{entry.name}</bdi>
                </button>
              </td>
              <td className="num">{entry.kind === 'file' ? formatBytes(entry.size) : ''}</td>
              <td className="when">{formatTime(entry.modifiedUnixMs)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {!loading && entries.length === 0 && !error && <p className="empty">This folder is empty.</p>}
      {skipped > 0 && <p className="notice">{skipped} link{skipped === 1 ? '' : 's'} or shortcut target{skipped === 1 ? '' : 's'} not shown. OrbitDrive does not follow links out of a shared folder.</p>}
      {cursor && <button type="button" onClick={() => void open(location, cursor)} disabled={loading}>Load more</button>}
      {loading && <p className="empty" role="status">Loading…</p>}
    </section>
  );
}
