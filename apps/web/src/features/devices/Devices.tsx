import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, type Session } from '../../api';
import type { BrowserDeviceSummary, PcSummary } from '../../protocol';
import { ConfirmButton } from '../explorer/Explorer';

/** Inline rename: a labelled field with Save/Cancel; the service validates and trims the name. */
function RenameForm({ device, onSave, onCancel }: { device: BrowserDeviceSummary; onSave: (name: string) => Promise<void>; onCancel: () => void }) {
  const [name, setName] = useState(device.name);
  const [saving, setSaving] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    setSaving(true);
    try { await onSave(name); } finally { setSaving(false); }
  }
  return (
    <form className="rename" onSubmit={submit}>
      <label htmlFor={`rename-${device.deviceId}`}>New name for {device.name}</label>
      <input id={`rename-${device.deviceId}`} value={name} maxLength={64} autoComplete="off" autoFocus onChange={(e) => setName(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); }} />
      <button type="submit" disabled={saving || !name.trim()}>Save</button>
      <button type="button" className="ghost" onClick={onCancel}>Cancel</button>
    </form>
  );
}

/** Browsers approved for this account. "Last seen" is not shown as "online": only PCs report presence. */
export function Devices({ session, pcs, onError }: { session: Session; pcs: PcSummary[]; onError: (error: unknown) => void }) {
  const [devices, setDevices] = useState<BrowserDeviceSummary[] | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
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
            {renaming === device.deviceId ? (
              <RenameForm device={device} onCancel={() => setRenaming(null)}
                onSave={async (name) => { try { await api.renameDevice(session, device.deviceId, name); setRenaming(null); await load(); } catch (e) { onError(e); } }} />
            ) : (<>
            <span>
              <strong>{device.name}</strong>{device.current && <em> (this browser)</em>}
              <small>{device.pcIds.length === 0 ? 'No PC access' : `Can reach ${device.pcIds.map(pcName).join(', ')}`}</small>
            </span>
            <span className="device-actions">
              <button type="button" className="ghost" aria-label={`Rename ${device.name}`} onClick={() => setRenaming(device.deviceId)}>Rename</button>
              {device.current
                ? <small>Use Sign out to remove this browser.</small>
                : <ConfirmButton label={`Remove ${device.name}`} confirmLabel="Yes, remove its access"
                    onConfirm={async () => { try { await api.revokeDevice(session, device.deviceId); await load(); } catch (e) { onError(e); } }} />}
            </span>
            </>)}
          </li>
        ))}
      </ul>
    </article>
  );
}
