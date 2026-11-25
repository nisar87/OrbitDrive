import { useCallback, useEffect, useState } from 'react';
import { ApiError, type Session } from '../../api';
import { adminApi, formatSize, limitText, type AdminOrgDetail, type AdminOrgRow, type AdminUserDetail, type UsageDay } from '../../billing';

/** Daily counts for the last 30 days: a number of files and a size per day. There are no names or times of individual transfers anywhere in this data. */
export function UsageTable({ history, caption }: { history: UsageDay[]; caption: string }) {
  const shown = history.filter((d) => d.files > 0 || d.bytes > 0);
  const total = history.reduce((sum, d) => ({ files: sum.files + d.files, bytes: sum.bytes + d.bytes }), { files: 0, bytes: 0 });
  return (
    <>
      <p className="hint">{caption}: {total.files} file{total.files === 1 ? '' : 's'}, {formatSize(total.bytes)} in the last {history.length} days.</p>
      {shown.length === 0 ? <p className="empty">No transfers in this period.</p> : (
        <table>
          <caption className="sr-only">{caption}</caption>
          <thead><tr><th scope="col">Day</th><th scope="col">Files</th><th scope="col">Size</th></tr></thead>
          <tbody>{shown.map((d) => <tr key={d.date}><th scope="row">{d.date}</th><td>{d.files}</td><td>{formatSize(d.bytes)}</td></tr>)}</tbody>
        </table>
      )}
    </>
  );
}

interface DetailProps { session: Session; uid: string; onClose: () => void }

/** One account as the operator sees it: plan, daily counts, and which PCs and browsers belong to it. */
export function AccountDetail({ session, uid, onClose }: DetailProps) {
  const [detail, setDetail] = useState<AdminUserDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { setDetail(await adminApi.userDetail(session, uid)); setError(null); } catch (e) { setError(e instanceof ApiError ? e.message : 'Something went wrong.'); }
  }, [session, uid]);
  useEffect(() => { void load(); }, [load]);
  return (
    <article className="panel" aria-labelledby="account-detail-title" data-testid="admin-account-detail">
      <h2 id="account-detail-title">Account <bdi>{uid}</bdi></h2>
      <p className="actions"><button type="button" className="ghost" onClick={onClose}>Close</button></p>
      {error && <p role="alert" className="notice error">{error}</p>}
      {detail && (
        <>
          <p>Plan: <strong>{detail.plan.planName}</strong>{detail.plan.state !== 'free' && detail.plan.endsAt ? `, until ${new Date(detail.plan.endsAt).toLocaleDateString()}` : ''}{detail.suspended && <span className="notice error"> suspended</span>}</p>
          {detail.orgName && <p>Organisation: <bdi>{detail.orgName}</bdi> ({detail.orgRole}).</p>}
          <p className="hint">Today: {detail.plan.filesToday} of {limitText(detail.plan.limits.filesPerDay, 'count')} files, {formatSize(detail.plan.bytesToday)} of {limitText(detail.plan.limits.bytesPerDay, 'bytes')}.</p>
          <h3>Transfers</h3>
          <UsageTable history={detail.history} caption="This account" />
          <h3>PCs</h3>
          {detail.pcs.length === 0 ? <p className="empty">No PCs.</p> : (
            <ul className="device-list">{detail.pcs.map((p, i) => <li key={i}><span><strong><bdi>{p.name || 'PC'}</bdi></strong><small>version {p.agentVersion || 'unknown'} · last seen {new Date(p.lastSeen).toLocaleString()}</small></span></li>)}</ul>
          )}
          <h3>Browsers and phones</h3>
          {detail.devices.length === 0 ? <p className="empty">No approved browsers.</p> : (
            <ul className="device-list">{detail.devices.map((d, i) => <li key={i}><span><strong><bdi>{d.name}</bdi></strong><small>approved {new Date(d.createdAt).toLocaleDateString()} · last used {new Date(d.lastUsedAt).toLocaleString()}</small></span></li>)}</ul>
          )}
        </>
      )}
    </article>
  );
}

/** Every organisation, with the owner's controls: suspend, restore, delete (each with a recorded reason). Members, counts and who-did-what, never names of files. */
export function OrgAdmin({ session }: { session: Session }) {
  const [rows, setRows] = useState<AdminOrgRow[]>([]);
  const [open, setOpen] = useState<AdminOrgDetail | null>(null);
  const [pending, setPending] = useState<{ id: string; action: 'suspend' | 'unsuspend' | 'delete' } | null>(null);
  const [reason, setReason] = useState('');
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const fail = useCallback((e: unknown) => setMessage({ kind: 'error', text: e instanceof ApiError ? e.message : 'Something went wrong.' }), []);
  const load = useCallback(async () => { try { setRows(await adminApi.orgs(session)); } catch (e) { fail(e); } }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  const show = async (id: string) => { try { setOpen(await adminApi.org(session, id)); } catch (e) { fail(e); } };
  const confirm = async () => {
    if (!pending) return;
    try {
      await adminApi.orgAction(session, pending.id, pending.action, reason.trim());
      setMessage({ kind: 'ok', text: 'Done. It is recorded in the audit log.' }); setPending(null); setReason(''); setOpen(null); await load();
    } catch (e) { fail(e); }
  };
  const verb = { suspend: 'Suspend', unsuspend: 'Restore', delete: 'Delete' };
  return (
    <article className="panel" aria-labelledby="admin-orgs" data-testid="admin-orgs">
      <h2 id="admin-orgs">Organisations</h2>
      <p className="hint">Members, plan and counts only. File and folder names are never held here.</p>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`notice ${message.kind}`}>{message.text}</p>}
      {rows.length === 0 ? <p className="empty">No organisations yet.</p> : (
        <ul className="device-list">
          {rows.map((o) => (
            <li key={o.id}>
              <span><strong><bdi>{o.name}</bdi></strong>{o.suspended && <span className="notice error"> suspended</span>}
                <small>{o.members} member{o.members === 1 ? '' : 's'} · {o.planId} · {o.filesToday} file{o.filesToday === 1 ? '' : 's'}, {formatSize(o.bytesToday)} today</small></span>
              <span className="actions">
                <button type="button" className="ghost" onClick={() => void show(o.id)}>Open {o.name}</button>
                <button type="button" className="ghost" onClick={() => { setPending({ id: o.id, action: o.suspended ? 'unsuspend' : 'suspend' }); setReason(''); }}>{o.suspended ? 'Restore' : 'Suspend'} {o.name}</button>
                <button type="button" className="ghost danger" onClick={() => { setPending({ id: o.id, action: 'delete' }); setReason(''); }}>Delete {o.name}</button>
              </span>
            </li>
          ))}
        </ul>
      )}
      {pending && (
        <form className="panel" aria-label="Confirm organisation action" onSubmit={(e) => { e.preventDefault(); void confirm(); }}>
          <p><strong>{verb[pending.action]}</strong> this organisation. {pending.action === 'delete' ? 'Members go back to their own plans. This cannot be undone. ' : ''}It is recorded with your account id.</p>
          <label className="stack" htmlFor="org-reason">Reason (required)</label>
          <input id="org-reason" value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
          <p className="actions"><button type="submit" disabled={reason.trim().length < 3}>Confirm</button><button type="button" className="ghost" onClick={() => setPending(null)}>Cancel</button></p>
        </form>
      )}
      {open && (
        <section aria-label={`Organisation ${open.org.name}`} data-testid="admin-org-detail">
          <h3><bdi>{open.org.name}</bdi></h3>
          <table>
            <caption className="sr-only">Members</caption>
            <thead><tr><th scope="col">Name</th><th scope="col">Role</th><th scope="col">PCs</th><th scope="col">Browsers</th><th scope="col">Files today</th><th scope="col">Size today</th></tr></thead>
            <tbody>{open.members.map((m) => <tr key={m.uid}><th scope="row"><bdi>{m.label}</bdi></th><td>{m.role}</td><td>{m.pcs}</td><td>{m.devices}</td><td>{m.filesToday}</td><td>{formatSize(m.bytesToday)}</td></tr>)}</tbody>
          </table>
          <UsageTable history={open.history} caption="Whole organisation" />
          <h4>What happened</h4>
          <ul>{open.events.map((ev, i) => <li key={i}>{new Date(ev.at).toLocaleString()} · {ev.action}{ev.detail ? ` (${ev.detail})` : ''}</li>)}</ul>
          <p className="actions"><button type="button" className="ghost" onClick={() => setOpen(null)}>Close</button></p>
        </section>
      )}
    </article>
  );
}
