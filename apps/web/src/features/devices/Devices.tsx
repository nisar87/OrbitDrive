import { useCallback, useEffect, useState } from 'react';
import { api, type Session } from '../../api';
import type { BrowserDeviceSummary, PcSummary } from '../../protocol';
import { ConfirmButton } from '../explorer/Explorer';

/** Browsers approved for this account. "Last seen" is not shown as "online": only PCs report presence. */
export function Devices({ session, pcs, onError }: { session: Session; pcs: PcSummary[]; onError: (error: unknown) => void }) {
  const [devices, setDevices] = useState<BrowserDeviceSummary[] | null>(null);
  const load = useCallback(async () => {
    try { setDevices((await api.devices(session)).devices); } catch (e) { onError(e); }
  }, [session, onError]);
  useEffect(() => { void load(); }, [load, pcs]);

  const pcName = (pcId: string) => pcs.find((pc) => pc.pcId === pcId)?.name ?? 'a PC that is no longer listed';
  return (
    <article className="pc" aria-label="Approved browsers">
      <h2>Approved browsers</h2>
      {devices === null && <p className="empty">Loading…</p>}
      <ul className="device-list">
        {devices?.map((device) => (
          <li key={device.deviceId}>
            <span>
              <strong>{device.name}</strong>{device.current && <em> (this browser)</em>}
              <small>{device.pcIds.length === 0 ? 'No PC access' : `Can reach ${device.pcIds.map(pcName).join(', ')}`}</small>
            </span>
            {device.current
              ? <small>Use Sign out to remove this browser.</small>
              : <ConfirmButton label={`Remove ${device.name}`} confirmLabel="Yes, remove its access"
                  onConfirm={async () => { try { await api.revokeDevice(session, device.deviceId); await load(); } catch (e) { onError(e); } }} />}
          </li>
        ))}
      </ul>
    </article>
  );
}
