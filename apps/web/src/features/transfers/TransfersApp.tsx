import { useEffect, useState, useSyncExternalStore } from 'react';
import { FileName } from '../explorer/FileName';
import { api, type Session } from '../../api';
import type { DownloadSessionSummary } from '../../protocol';
import { formatBytes } from '../../format';
import { transfers, type Transfer } from './transfers';

const pct = (part: number, whole: bigint | null) => (whole === null || whole === 0n ? null : Math.min(100, Math.floor((part / Number(whole)) * 100)));

function describe(item: Transfer, relay: DownloadSessionSummary | undefined): string {
  const size = item.size === null ? '' : formatBytes(item.size.toString());
  switch (item.state) {
    case 'preparing': return 'Checking the file on the PC…';
    case 'saving': return `Saving… ${formatBytes(String(item.received))} of ${size}${item.bytesPerSecond ? ` · ${formatBytes(String(Math.round(item.bytesPerSecond)))}/s` : ''}`;
    case 'saved': return `Saved to the place you chose (${size}).`;
    case 'interrupted': return `Interrupted at ${formatBytes(String(item.received))} of ${size}.`;
    case 'changed': return 'Stopped: the file changed on the PC.';
    case 'cancelled': return 'Cancelled.';
    case 'failed': return 'Could not download.';
    case 'handed': {
      // The browser's own download manager has it. Only the relay's side is observable from here.
      if (!relay) return `Handed to your browser (${size}). Check your browser's downloads.`;
      if (relay.state === 'sent_to_browser') return `All ${size} left the relay for your browser. MyOrbitDrive cannot see whether your browser finished saving it.`;
      if (relay.state === 'sending') return `Your browser is receiving it: ${formatBytes(relay.bytesCovered)} of ${size} has left the relay.`;
      if (relay.state === 'interrupted') return `Stopped after ${formatBytes(relay.bytesCovered)} of ${size} left the relay. Your browser may retry by itself; otherwise download it again.`;
      if (relay.state === 'cancelled') return 'Cancelled.';
      return `Handed to your browser (${size}). Waiting for it to start.`;
    }
  }
}

/** Downloads started from this page: what this page did, and separately what the relay observed. */
export function TransfersApp({ session }: { session: Session }) {
  const items = useSyncExternalStore(transfers.subscribe, transfers.list);
  const [relay, setRelay] = useState<Record<string, DownloadSessionSummary>>({});
  const watching = items.some((item) => item.sessionId !== null && (item.state === 'handed' || item.state === 'saving'));

  useEffect(() => {
    let stopped = false;
    const refresh = () => api.downloadSessions(session)
      .then((list) => { if (!stopped) setRelay(Object.fromEntries(list.sessions.map((s) => [s.sessionId, s]))); })
      .catch(() => undefined);
    void refresh();
    if (!watching) return () => { stopped = true; };
    const timer = setInterval(() => void refresh(), 2000);
    return () => { stopped = true; clearInterval(timer); };
  }, [session, watching, items.length]);

  return (
    <div className="app-page transfers">
      <p className="hint">
        This list covers downloads started on this page and is kept until the page is closed or reloaded.
        {transfers.canSaveToFile()
          ? ' “Save to a file…” (right-click a file) lets MyOrbitDrive write the file itself, show real progress and resume after an interruption.'
          : ' This browser saves files with its own download manager, so MyOrbitDrive can show what the relay sent but not whether the file was saved.'}
      </p>
      {items.length === 0 && <p className="empty">No downloads yet. Open File Explorer and choose a file.</p>}
      <ul className="transfer-list">
        {items.map((item) => {
          const observed = item.sessionId ? relay[item.sessionId] : undefined;
          const done = item.mode === 'file' ? item.received : observed ? Number(observed.bytesCovered) : 0;
          const percent = item.state === 'saved' ? 100 : pct(done, item.size);
          const showBar = item.state === 'saving' || item.state === 'interrupted' || item.state === 'saved' || (item.state === 'handed' && observed !== undefined && observed.state !== 'prepared');
          return (
            <li key={item.id} data-state={item.state} data-testid="transfer">
              <div className="transfer-head">
                <strong><FileName name={item.name} /></strong>
                <span className="badge">{item.mode === 'file' ? 'Written by MyOrbitDrive' : 'Browser download'}</span>
              </div>
              <p role="status" className={item.state === 'failed' || item.state === 'changed' ? 'transfer-text problem' : 'transfer-text'}>{describe(item, observed)}{item.message ? ` ${item.message}` : ''}</p>
              {showBar && percent !== null && <progress max={100} value={percent} aria-label={`${item.name}: ${percent}%`} />}
              <div className="transfer-actions">
                {item.state === 'interrupted' && item.mode === 'file' && <button type="button" onClick={() => transfers.resume(item.id)}>Resume</button>}
                {(item.state === 'changed' || item.state === 'failed') && item.mode === 'file' && <button type="button" onClick={() => transfers.restart(item.id)}>Start again</button>}
                {['preparing', 'saving', 'interrupted', 'changed'].includes(item.state) && <button type="button" className="ghost" onClick={() => void transfers.cancel(item.id)}>Cancel</button>}
                {item.state === 'handed' && observed && ['prepared', 'sending', 'interrupted'].includes(observed.state) && <button type="button" className="ghost" onClick={() => void transfers.cancel(item.id)}>Cancel link</button>}
                {['saved', 'cancelled', 'failed', 'handed'].includes(item.state) && <button type="button" className="ghost" onClick={() => transfers.remove(item.id)}>Remove from list</button>}
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
