import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, type Session } from '../../api';
import type { AccountSummary } from '../../protocol';
import { ConfirmButton } from '../explorer/Explorer';
import { MyPlan } from '../billing/MyPlan';

const RETENTION = [[0, 'Only while it is running'], [1, '1 day'], [7, '7 days'], [30, '30 days']] as const;
export const DELETE_PHRASE = 'delete my account';

interface Props { session: Session; onSummary: (summary: AccountSummary) => void; onAuthLost: (error: unknown) => void; onSignedOut: () => Promise<void> | void }

/** The signed-in person's own controls over what the service holds about them. None of these touch files on a PC. */
export function AccountPanel({ session, onSummary, onAuthLost, onSignedOut }: Props) {
  const [summary, setSummary] = useState<AccountSummary | null>(null);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [phrase, setPhrase] = useState('');
  const [busy, setBusy] = useState(false);

  const fail = useCallback((e: unknown) => { onAuthLost(e); setMessage({ kind: 'error', text: e instanceof ApiError ? e.message : 'Something went wrong.' }); }, [onAuthLost]);
  const load = useCallback(() => api.account(session).then((s) => { setSummary(s); onSummary(s); }).catch(fail), [session, fail, onSummary]);
  useEffect(() => { void load(); }, [load]);

  const save = async (next: AccountSummary['preferences']) => {
    try { await api.setPreferences(session, next); await load(); setMessage({ kind: 'ok', text: 'Saved.' }); } catch (e) { fail(e); }
  };

  if (!summary) return <article className="panel" aria-labelledby="owner-title"><h2 id="owner-title">Your account on the service</h2>{message ? <p role="alert" className="notice error">{message.text}</p> : <p className="empty" role="status">Loading…</p>}</article>;
  const prefs = summary.preferences;
  return (
    <>
    <MyPlan session={session} onAuthLost={onAuthLost} />
    <article className="panel" aria-labelledby="owner-title" data-testid="account-panel">
      <h2 id="owner-title">Your account on the service</h2>
      <p className="hint">The relay keeps only the links between your browsers and your PCs. It never keeps your files, folder names or file names.</p>
      <dl>
        <dt>Approved browsers</dt><dd data-testid="account-browsers">{summary.devices.length}</dd>
        <dt>Connected PCs</dt><dd data-testid="account-pcs">{summary.pcs.length === 0 ? 'None' : summary.pcs.map((p) => `${p.name} (${p.online ? 'online' : 'offline'})`).join(', ')}</dd>
      </dl>
      <label className="stack" htmlFor="retention">Keep finished transfers in the list for</label>
      <select id="retention" value={prefs.historyRetentionDays} onChange={(e) => void save({ ...prefs, historyRetentionDays: Number(e.target.value) })}>
        {RETENTION.map(([days, label]) => <option key={days} value={days}>{label}</option>)}
      </select>
      <p className="hint">{summary.retentionNote}</p>
      <label className="check"><input type="checkbox" checked={prefs.notifyInvitations} onChange={(e) => void save({ ...prefs, notifyInvitations: e.target.checked })} />Show a message when a PC sends me a file</label>
      <label className="check"><input type="checkbox" checked={prefs.diagnosticsConsent} onChange={(e) => void save({ ...prefs, diagnosticsConsent: e.target.checked })} />Allow support to ask for diagnostics</label>
      <p className="hint">This records your permission only. Nothing is sent from your PC or this browser unless you start it.</p>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={message.kind === 'error' ? 'notice error' : 'hint'}>{message.text}</p>}

      <h3>Your data</h3>
      <p className="actions">
        <button type="button" className="secondary" onClick={async () => {
          try {
            const data = await api.exportAccount(session);
            const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
            const link = Object.assign(document.createElement('a'), { href: url, download: 'orbitdrive-account.json' });
            link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
          } catch (e) { fail(e); }
        }}>Download what the service holds about me</button>
      </p>

      <h3>Ending access</h3>
      <p className="hint">Signing out everywhere stops every browser you approved, including this one. Your PCs and files are not changed.</p>
      <ConfirmButton label="Sign out everywhere" confirmLabel="Yes, stop all my browsers"
        onConfirm={async () => { try { await api.revokeAllBrowsers(session); await onSignedOut(); } catch (e) { fail(e); } }} />

      <h3>Delete my account</h3>
      <p className="hint">This disconnects every PC and removes every browser and setting the service holds for you. It never deletes anything on your PCs. A short record that the account was deleted (its id and the time, no email address) is kept in the service's audit log.</p>
      <label className="stack" htmlFor="delete-phrase">Type “{DELETE_PHRASE}” to confirm</label>
      <input id="delete-phrase" value={phrase} autoComplete="off" onChange={(e) => setPhrase(e.target.value)} />
      <p className="actions">
        <button type="button" className="danger" disabled={phrase !== DELETE_PHRASE || busy} onClick={async () => {
          setBusy(true);
          try { await api.deleteAccount(session, phrase); await onSignedOut(); } catch (e) { fail(e); } finally { setBusy(false); }
        }}>Delete my account</button>
      </p>
    </article>
    </>
  );
}
