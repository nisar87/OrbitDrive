import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, type Session } from '../../api';
import type { AdminOverview, AdminUser, AuditEntry } from '../../protocol';
import { formatBytes, formatTime } from '../../format';

interface Props { session: Session; onSignOut: () => void }

type Action = 'suspend' | 'unsuspend' | 'revoke-all' | 'reset-relay-allowance';
const LABEL: Record<Action, string> = { suspend: 'Suspend', unsuspend: 'Lift suspension', 'revoke-all': 'Sign out all browsers', 'reset-relay-allowance': 'Restore today\'s allowance' };

/**
 * Operator area. It is only offered to accounts the service itself says are operators, and the service checks again on every request, so
 * reaching this page by any other route shows nothing. Every number is measured by the relay since it last started; there is no file,
 * folder, path or user-impersonation view anywhere in it.
 */
export default function AdminApp({ session, onSignOut }: Props) {
  const [overview, setOverview] = useState<AdminOverview | null>(null);
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [audit, setAudit] = useState<AuditEntry[]>([]);
  const [minVersion, setMinVersion] = useState('');
  const [error, setError] = useState<{ text: string; signInAgain: boolean } | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [pending, setPending] = useState<{ uid: string; action: Action } | null>(null);
  const [reason, setReason] = useState('');

  const fail = useCallback((e: unknown) => setError({ text: e instanceof ApiError ? e.message : 'Something went wrong.', signInAgain: e instanceof ApiError && e.code === 'unauthenticated' }), []);
  const load = useCallback(async () => {
    try {
      const [o, u, a, s] = await Promise.all([api.adminOverview(session), api.adminUsers(session), api.adminAudit(session), api.adminSettings(session)]);
      setOverview(o); setUsers([...u.users]); setAudit([...a.entries]); setMinVersion(s.minAgentVersion); setError(null);
    } catch (e) { fail(e); }
  }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  const run = async () => {
    if (!pending) return;
    try { await api.adminUserAction(session, pending.uid, pending.action, reason.trim()); setNote(`${LABEL[pending.action]}: done.`); setPending(null); setReason(''); await load(); } catch (e) { fail(e); }
  };

  return (
    <div className="app-page admin" data-testid="admin-app">
      <div className="commandbar"><button type="button" className="ghost" onClick={() => void load()}>Refresh</button></div>
      {error && (
        <p role="alert" className="notice error" data-testid="admin-error">{error.text} {error.signInAgain && <button type="button" onClick={onSignOut}>Sign out and sign in again</button>}</p>
      )}
      {note && <p role="status" className="hint">{note}</p>}
      {overview && (
        <article className="panel" aria-labelledby="admin-overview">
          <h2 id="admin-overview">Service health</h2>
          <p className="hint">Measured by the relay since it started {formatTime(overview.startedUnixMs)} (version {overview.gatewayVersion}). Counts restart when the service restarts.</p>
          <dl className="metrics" data-testid="admin-metrics">
            <dt>Accounts</dt><dd data-testid="m-users">{overview.users}</dd>
            <dt>Suspended</dt><dd>{overview.suspendedUsers}</dd>
            <dt>Approved browsers</dt><dd>{overview.browserDevices}</dd>
            <dt>PCs (online)</dt><dd data-testid="m-pcs">{overview.pcs} ({overview.pcsOnline})</dd>
            <dt>Transfers running now</dt><dd>{overview.activeStreams}</dd>
            <dt>Sent to browsers</dt><dd data-testid="m-down">{formatBytes(overview.relayBytesDown)}</dd>
            <dt>Received from browsers</dt><dd>{formatBytes(overview.relayBytesUp)}</dd>
            <dt>Downloads completed / failed</dt><dd>{overview.downloadsCompleted} / {overview.downloadsFailed}</dd>
            <dt>Upload pieces stored / refused</dt><dd>{overview.uploadPiecesRelayed} / {overview.uploadPiecesFailed}</dd>
            <dt>Refused for daily allowance</dt><dd>{overview.quotaRefusals}</dd>
            <dt>Daily allowance per account</dt><dd>{overview.relayBytesPerOwnerPerDay === '0' ? 'No limit' : formatBytes(overview.relayBytesPerOwnerPerDay)}</dd>
          </dl>
        </article>
      )}
      <article className="panel" aria-labelledby="admin-version">
        <h2 id="admin-version">Oldest PC version allowed</h2>
        <p className="hint">PCs running an older OrbitDrive are refused with an update message. Leave empty for no minimum.</p>
        <label className="stack" htmlFor="min-version">Version, like 1.2.0</label>
        <input id="min-version" value={minVersion} inputMode="decimal" onChange={(e) => setMinVersion(e.target.value)} />
        <p className="actions"><button type="button" onClick={async () => { try { await api.setAdminSettings(session, minVersion.trim()); setNote('Minimum version saved.'); await load(); } catch (e) { fail(e); } }}>Save</button></p>
      </article>
      <article className="panel" aria-labelledby="admin-users">
        <h2 id="admin-users">Accounts</h2>
        <p className="hint">Account ids and counts only. Email addresses, names, folders and files are not shown.</p>
        <ul className="device-list" data-testid="admin-users">
          {users.map((user) => (
            <li key={user.uid}>
              <span><strong><bdi>{user.uid}</bdi></strong>{user.suspended && <span className="notice error"> suspended</span>}
                <small>{user.devices} browser{user.devices === 1 ? '' : 's'} · {user.pcs} PC{user.pcs === 1 ? '' : 's'} · {formatBytes(user.relayBytesToday)} today</small></span>
              <span className="actions">
                {(['suspend', 'unsuspend', 'revoke-all', 'reset-relay-allowance'] as Action[]).filter((a) => a !== (user.suspended ? 'suspend' : 'unsuspend')).map((action) => (
                  <button key={action} type="button" className="ghost" onClick={() => { setPending({ uid: user.uid, action }); setReason(''); }}>{LABEL[action]}</button>
                ))}
              </span>
            </li>
          ))}
        </ul>
        {users.length === 0 && <p className="empty">No accounts yet.</p>}
        {pending && (
          <form className="panel" onSubmit={(e) => { e.preventDefault(); void run(); }} aria-label="Confirm action" data-testid="admin-confirm">
            <p><strong>{LABEL[pending.action]}</strong> for <bdi>{pending.uid}</bdi>. This is recorded in the audit log with your account id.</p>
            <label className="stack" htmlFor="admin-reason">Reason (required)</label>
            <input id="admin-reason" value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
            <p className="actions"><button type="submit" disabled={reason.trim().length < 3}>Confirm</button><button type="button" className="ghost" onClick={() => setPending(null)}>Cancel</button></p>
          </form>
        )}
      </article>
      <article className="panel" aria-labelledby="admin-audit">
        <h2 id="admin-audit">Audit log</h2>
        <ul className="device-list" data-testid="admin-audit">
          {audit.map((entry, i) => (
            <li key={i}><span><strong>{entry.action}</strong> <bdi>{entry.target}</bdi><small>{formatTime(entry.atUnixMs)} · by <bdi>{entry.operator}</bdi>{entry.detail ? ` · ${entry.detail}` : ''}</small></span></li>
          ))}
        </ul>
        {audit.length === 0 && <p className="empty">Nothing has been done yet.</p>}
      </article>
    </div>
  );
}
