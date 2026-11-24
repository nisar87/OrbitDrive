import { useState, useSyncExternalStore } from 'react';
import { ApiError, INVITATION_PREFIX, type Session } from '../../api';
import type { InvitationSummary } from '../../protocol';
import { formatBytes } from '../../format';
import { transfers } from '../transfers/transfers';
import { disableNotifications, enableNotifications, inbox, notificationsEnabled, notificationsSupported } from './inbox';

function when(unixMs: string) { return new Date(Number(unixMs)).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }); }
function expiresIn(unixMs: string): string {
  const minutes = Math.round((Number(unixMs) - Date.now()) / 60000);
  if (minutes <= 0) return 'expired';
  if (minutes < 90) return `expires in ${minutes} min`;
  const hours = Math.round(minutes / 60);
  return hours < 48 ? `expires in ${hours} h` : `expires in ${Math.round(hours / 24)} days`;
}

function status(item: InvitationSummary): { text: string; tone: 'ok' | 'wait' | 'bad' } {
  switch (item.state) {
    case 'pending': return item.sourceOnline
      ? { text: `Waiting for you. The file is still on ${item.pcName}; nothing has been copied anywhere yet.`, tone: 'ok' }
      : { text: `Waiting. ${item.pcName} is off, asleep or offline. The file stays on that PC and can be accepted when it is back.`, tone: 'wait' };
    case 'accepted': return { text: 'You accepted this file. You can start the download again until it expires.', tone: 'ok' };
    case 'declined': return { text: 'You declined this file.', tone: 'bad' };
    case 'expired': return { text: 'This offer expired. Ask for it to be sent again from the PC.', tone: 'bad' };
    case 'cancelled': return { text: item.failure ?? 'This offer is no longer available.', tone: 'bad' };
  }
}

/** Files that PCs have offered to this browser. Accepting is always a user action. */
export function InboxApp({ session }: { session: Session }) {
  const items = useSyncExternalStore(inbox.subscribe, inbox.list);
  const problem = useSyncExternalStore(inbox.subscribe, inbox.error);
  useSyncExternalStore(inbox.subscribe, () => notificationsEnabled());
  const [message, setMessage] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const accept = async (item: InvitationSummary, mode: 'browser' | 'file') => {
    setBusy(item.invitationId); setMessage((m) => ({ ...m, [item.invitationId]: '' }));
    try {
      const entry = { entryId: INVITATION_PREFIX + item.invitationId, name: item.name };
      if (mode === 'file') await transfers.toFile(session, item.pcId, entry);
      else await transfers.toBrowser(session, item.pcId, entry);
    } catch (e) {
      setMessage((m) => ({ ...m, [item.invitationId]: e instanceof ApiError ? e.message : 'Could not start the download.' }));
    } finally { setBusy(null); void inbox.refresh(); }
  };

  const decline = async (item: InvitationSummary) => {
    setBusy(item.invitationId);
    try { await inbox.decline(item.invitationId); } catch (e) { setMessage((m) => ({ ...m, [item.invitationId]: e instanceof ApiError ? e.message : 'Could not decline.' })); }
    finally { setBusy(null); }
  };

  const toggleNotifications = async () => {
    if (notificationsEnabled()) { disableNotifications(); setNotice('Notifications are off. New files still appear here while this page is open.'); return; }
    const result = await enableNotifications();
    setNotice(result === 'enabled' ? 'Notifications are on while this page or the installed app is open or in the background. They never show file names.'
      : result === 'denied' ? 'Notifications are blocked for this site. The Inbox works without them; check it when you expect a file.'
      : 'This browser cannot show notifications. The Inbox works without them.');
  };

  const open = items.filter((i) => i.state === 'pending' || i.state === 'accepted');
  const finished = items.filter((i) => !open.includes(i));
  const card = (item: InvitationSummary) => {
    const s = status(item);
    const actionable = item.state === 'pending' || item.state === 'accepted';
    return (
      <li key={item.invitationId} data-state={item.state} data-testid="invitation">
        <div className="transfer-head">
          <strong><bdi>{item.name}</bdi></strong>
          <span className="badge">{formatBytes(item.size)}</span>
        </div>
        <p className="hint">From <bdi>{item.pcName}</bdi> · offered {when(item.createdAtUnixMs)}{actionable ? ` · ${expiresIn(item.expiresAtUnixMs)}` : ''}</p>
        <p role="status" className={s.tone === 'bad' ? 'transfer-text problem' : 'transfer-text'}>{s.text}</p>
        {message[item.invitationId] && <p role="alert" className="notice error">{message[item.invitationId]}</p>}
        {actionable && (
          <div className="transfer-actions">
            <button type="button" disabled={busy === item.invitationId} onClick={() => void accept(item, 'browser')}>Accept and download</button>
            {transfers.canSaveToFile() && <button type="button" className="ghost" disabled={busy === item.invitationId} onClick={() => void accept(item, 'file')}>Accept and save to a file…</button>}
            {item.state === 'pending' && <button type="button" className="ghost" disabled={busy === item.invitationId} onClick={() => void decline(item)}>Decline</button>}
          </div>
        )}
      </li>
    );
  };

  return (
    <div className="app-page inbox">
      <p className="hint">
        Files you chose on a PC with “Send to my phone”. An offer is not a transfer: the file stays on the PC until you accept, so the PC must be on and connected.
      </p>
      <div className="transfer-actions">
        {notificationsSupported()
          ? <button type="button" className="ghost" onClick={() => void toggleNotifications()}>{notificationsEnabled() ? 'Turn off notifications' : 'Notify me about new files'}</button>
          : <span className="hint">This browser cannot show notifications; the Inbox updates while it is open.</span>}
      </div>
      {notice && <p role="status" className="hint">{notice}</p>}
      {problem && <p role="alert" className="notice error">{problem}</p>}
      {items.length === 0 && !problem && <p className="empty">Nothing is waiting. On your PC, right-click a file and choose “Send to my phone via MyOrbitDrive”.</p>}
      {open.length > 0 && <ul className="transfer-list" aria-label="Waiting files">{open.map(card)}</ul>}
      {finished.length > 0 && <><h3 className="section-title">Earlier</h3><ul className="transfer-list" aria-label="Earlier files">{finished.map(card)}</ul></>}
    </div>
  );
}
