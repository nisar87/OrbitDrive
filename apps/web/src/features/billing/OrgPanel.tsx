import { useCallback, useEffect, useState } from 'react';
import { ApiError, type Session } from '../../api';
import { formatSize, orgApi, type OrgEvent, type OrgView, type UsageDay } from '../../billing';
import { UsageTable } from './AdminDetails';

interface Props { session: Session; onAuthLost: (error: unknown) => void }

const ACTIONS: Record<string, string> = {
  'organisation-created': 'Created the organisation', 'invitation-created': 'Made an invitation', 'member-joined': 'Joined', 'member-removed': 'Removed',
  'member-left': 'Left', 'role-changed': 'Changed a role', 'devices-revoked': 'Signed out browsers', 'organisation-suspended': 'Suspended by support', 'organisation-restored': 'Restored by support',
};

/** Team sharing for Office: the owner invites people, they share the plan's limits, and admins see counts and who-did-what, never file or folder names. */
export function OrgPanel({ session, onAuthLost }: Props) {
  const [view, setView] = useState<OrgView | null>(null);
  const [events, setEvents] = useState<OrgEvent[]>([]);
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [label, setLabel] = useState('');
  const [usage, setUsage] = useState<{ label: string; history: UsageDay[] } | null>(null);
  const [invite, setInvite] = useState<{ code: string; expiresAt: string } | null>(null);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);

  const fail = useCallback((e: unknown) => { onAuthLost(e); setMessage({ kind: 'error', text: e instanceof ApiError ? e.message : 'Something went wrong.' }); }, [onAuthLost]);
  const load = useCallback(async () => {
    try {
      const v = await orgApi.get(session);
      setView(v);
      setEvents(v.org && v.org.role !== 'member' ? await orgApi.audit(session) : []);
    } catch (e) { fail(e); }
  }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  const run = async (action: () => Promise<OrgView | unknown>, ok: string) => {
    try { await action(); setMessage({ kind: 'ok', text: ok }); await load(); } catch (e) { fail(e); }
  };

  if (!view) return <article className="panel" aria-labelledby="org-title"><h2 id="org-title">Organisation</h2>{message ? <p role="alert" className="notice error">{message.text}</p> : <p className="hint">Loading…</p>}</article>;
  const org = view.org;
  const admin = org?.role === 'owner' || org?.role === 'admin';
  return (
    <article className="panel" aria-labelledby="org-title">
      <h2 id="org-title">Organisation</h2>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`notice ${message.kind}`}>{message.text}</p>}
      {!org ? (
        <>
          <p className="hint">An organisation lets the people in your office share one Office plan. Each person keeps their own sign-in and their own files. Organisations come with the Office plan.</p>
          <form className="grid2" onSubmit={(e) => { e.preventDefault(); void run(() => orgApi.create(session, name), 'Organisation created.'); }}>
            <label>Organisation name<input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} required minLength={2} /></label>
            <button type="submit">Create organisation</button>
          </form>
          <h3>Have an invitation?</h3>
          <form className="grid2" onSubmit={(e) => { e.preventDefault(); void run(() => orgApi.join(session, code, label), 'You have joined the organisation.'); }}>
            <label>Invitation code<input value={code} maxLength={16} onChange={(e) => setCode(e.target.value)} required autoComplete="off" /></label>
            <label>Your name in the organisation<input value={label} maxLength={40} onChange={(e) => setLabel(e.target.value)} required /></label>
            <button type="submit">Join</button>
          </form>
        </>
      ) : (
        <>
          <p><strong>{org.name}</strong> · you are {org.role === 'owner' ? 'the owner' : org.role === 'admin' ? 'an admin' : 'a member'}. Plan: {view.planId ?? 'Free'}.</p>
          <p className="hint">Today the organisation has used {view.filesToday} file{view.filesToday === 1 ? '' : 's'} and {formatSize(view.bytesToday)}. Everyone in the organisation shares the plan's daily limits.</p>
          <table>
            <caption className="sr-only">Members</caption>
            <thead><tr><th scope="col">Name</th><th scope="col">Role</th>{admin && <><th scope="col">PCs</th><th scope="col">Devices</th><th scope="col">Files today</th><th scope="col">Size today</th><th scope="col">Actions</th></>}</tr></thead>
            <tbody>
              {view.members.map((m) => (
                <tr key={m.uid || m.label}>
                  <th scope="row">{m.label}</th><td>{m.role}</td>
                  {admin && <>
                    <td>{m.pcs}</td><td>{m.devices}</td><td>{m.filesToday}</td><td>{formatSize(m.bytesToday)}</td>
                    <td>
                      <button type="button" onClick={() => { void (async () => { try { setUsage(await orgApi.memberUsage(session, m.uid)); } catch (e) { fail(e); } })(); }}>Usage of {m.label}</button>
                      {m.role !== 'owner' && (m.role === 'member' || org.role === 'owner') && <button type="button" onClick={() => void run(() => orgApi.revokeDevices(session, m.uid), `${m.label}'s browsers were signed out.`)}>Sign out {m.label}'s browsers</button>}
                      {m.role !== 'owner' && (m.role === 'member' || org.role === 'owner') && <button type="button" onClick={() => void run(() => orgApi.remove(session, m.uid), `${m.label} was removed.`)}>Remove {m.label}</button>}
                      {org.role === 'owner' && m.role !== 'owner' && <button type="button" onClick={() => void run(() => orgApi.role(session, m.uid, m.role === 'admin' ? 'member' : 'admin'), 'Role changed.')}>{m.role === 'admin' ? 'Make member' : 'Make admin'}</button>}
                    </td>
                  </>}
                </tr>
              ))}
            </tbody>
          </table>
          {usage && <section aria-label={`Usage of ${usage.label}`}><h3>{usage.label}</h3><UsageTable history={usage.history} caption={usage.label} /><button type="button" className="ghost" onClick={() => setUsage(null)}>Close</button></section>}
          {admin && (
            <>
              <button type="button" onClick={() => { void (async () => { try { setInvite(await orgApi.invite(session)); setMessage(null); await load(); } catch (e) { fail(e); } })(); }}>Make an invitation code</button>
              {invite && <p role="status">Give this code to one person: <strong>{invite.code}</strong>. It works once and expires on {new Date(invite.expiresAt).toLocaleDateString()}.</p>}
              <h3>What happened</h3>
              <p className="hint">This list records who did what and when. It never includes file or folder names.</p>
              {events.length === 0 ? <p className="hint">Nothing yet.</p> : (
                <ul>{events.map((ev, i) => <li key={i}>{new Date(ev.at).toLocaleString()} · {ev.by} · {ACTIONS[ev.action] ?? ev.action}{ev.detail ? ` (${ev.detail})` : ''}</li>)}</ul>
              )}
            </>
          )}
          {org.role === 'owner'
            ? <button type="button" className="danger" onClick={() => { if (window.confirm('Delete this organisation? Members go back to their own plans.')) void run(() => orgApi.destroy(session), 'Organisation deleted.'); }}>Delete organisation</button>
            : <button type="button" onClick={() => void run(() => orgApi.leave(session), 'You left the organisation.')}>Leave organisation</button>}
        </>
      )}
    </article>
  );
}
