import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, type Session } from '../../api';
import type { PcSummary } from '../../protocol';
import { formatTime } from '../../format';
import { ConfirmButton } from '../explorer/Explorer';
import { Devices } from './Devices';

interface Props { session: Session; onAuthLost: (error: unknown) => void; onPairAnother: () => void; onChanged: () => void; dataVersion: number }

/** PCs this account can reach and the browsers approved to reach them. Ending access is always a two-step action. */
export function DevicesApp({ session, onAuthLost, onPairAnother, onChanged, dataVersion }: Props) {
  const [pcs, setPcs] = useState<PcSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);

  const fail = useCallback((e: unknown) => { onAuthLost(e); setError(e instanceof ApiError ? e.message : 'Something went wrong.'); }, [onAuthLost]);
  useEffect(() => {
    let cancelled = false;
    setError(null);
    api.pcs(session).then((result) => { if (!cancelled) setPcs(result.pcs); }).catch((e: unknown) => { if (!cancelled) fail(e); });
    return () => { cancelled = true; };
  }, [session, fail, reload, dataVersion]);

  return (
    <div className="app-page">
      <div className="commandbar">
        <button type="button" className="ghost" onClick={() => setReload((n) => n + 1)}>Refresh</button>
        <span className="spacer" />
        <button type="button" className="secondary" onClick={onPairAnother}>Pair a PC</button>
      </div>
      {error && <p role="alert" className="notice error">{error}</p>}
      <article aria-labelledby="pcs-title" className="panel">
        <h2 id="pcs-title">PCs</h2>
        {pcs === null && !error && <p className="empty" role="status">Loading…</p>}
        {pcs?.length === 0 && <p className="empty">No PC is paired. Choose “Pair a PC” and follow the steps on your Windows PC.</p>}
        <ul className="device-list">
          {pcs?.map((pc) => (
            <li key={pc.pcId}>
              <span><strong><span className={pc.online ? 'dot online' : 'dot'} aria-hidden="true" /> <bdi>{pc.name}</bdi></strong>
                <small>{pc.online ? 'Online' : `Offline · last seen ${formatTime(pc.lastSeenUnixMs)}`} · OrbitDrive {pc.agentVersion}</small></span>
              <ConfirmButton label="Disconnect this PC" confirmLabel={`Yes, disconnect ${pc.name}`}
                onConfirm={async () => { try { await api.disconnectPc(session, pc.pcId); setReload((n) => n + 1); onChanged(); } catch (e) { fail(e); } }} />
            </li>
          ))}
        </ul>
      </article>
      {pcs !== null && <Devices key={`${reload}-${dataVersion}`} session={session} pcs={pcs} onError={fail} />}
    </div>
  );
}
