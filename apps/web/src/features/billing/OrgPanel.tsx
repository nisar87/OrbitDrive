import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError, type Session } from '../../api';
import { formatSize, orgApi, type OrgEvent, type OrgMember, type OrgView, type UsageDay } from '../../billing';
import { Badge, ConfirmDialog, CopyId, DataTable, Drawer, Kpi, Tabs, TabPanel, type Column } from '../console/ui';
import { UsageTable } from './UsageTable';

interface Props { session: Session; onAuthLost: (error: unknown) => void }

const ACTIONS: Record<string, string> = {
  'organisation-created': 'Created the organisation', 'invitation-created': 'Made an invitation', 'member-joined': 'Joined', 'member-removed': 'Removed',
  'member-left': 'Left', 'role-changed': 'Changed a role', 'devices-revoked': 'Signed out browsers', 'organisation-suspended': 'Suspended by support', 'organisation-restored': 'Restored by support',
};
const ROLE: Record<string, string> = { owner: 'Owner', admin: 'Admin', member: 'Member' };
const plural = (n: number, one: string): string => `${n} ${one}${n === 1 ? '' : 's'}`;

interface Ask { title: string; effect: ReactNode; confirmLabel: string; danger?: boolean; done: string; run: () => Promise<unknown> }

/**
 * Team sharing for Office. The owner invites people and they share the plan's limits. Owners and admins manage people and see counts and
 * who-did-what; nobody here, including the owner, ever sees a file or folder name or what a member moved.
 */
export function OrgPanel({ session, onAuthLost }: Props) {
  const [view, setView] = useState<OrgView | null>(null);
  const [events, setEvents] = useState<OrgEvent[]>([]);
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [label, setLabel] = useState('');
  const [tab, setTab] = useState('members');
  const [manage, setManage] = useState<OrgMember | null>(null);
  const [asking, setAsking] = useState<Ask | null>(null);
  const [busy, setBusy] = useState(false);
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

  const run = async (action: () => Promise<unknown>, ok: string) => {
    try { await action(); setMessage({ kind: 'ok', text: ok }); await load(); } catch (e) { fail(e); }
  };
  const confirm = async () => {
    if (!asking) return;
    setBusy(true);
    try { await asking.run(); setMessage({ kind: 'ok', text: `${asking.done}.` }); setAsking(null); setManage(null); await load(); }
    catch (e) { setAsking(null); fail(e); }
    finally { setBusy(false); }
  };

  const org = view?.org ?? null;
  const admin = org?.role === 'owner' || org?.role === 'admin';
  const owner = org?.role === 'owner';
  const canManage = (m: OrgMember): boolean => m.role !== 'owner' && (m.role === 'member' || owner);

  const columns = useMemo<Column<OrgMember>[]>(() => {
    const base: Column<OrgMember>[] = [
      { key: 'name', header: 'Name', text: (m) => m.label, sort: (a, b) => a.label.localeCompare(b.label), cell: (m) => <bdi>{m.label}</bdi> },
      { key: 'role', header: 'Role', sort: (a, b) => a.role.localeCompare(b.role), cell: (m) => <Badge tone={m.role === 'member' ? 'plain' : 'ok'}>{ROLE[m.role]}</Badge> },
    ];
    if (!admin) return base;
    return [...base,
      { key: 'pcs', header: 'PCs', numeric: true, sort: (a, b) => a.pcs - b.pcs, cell: (m) => m.pcs },
      { key: 'dev', header: 'Browsers', numeric: true, sort: (a, b) => a.devices - b.devices, cell: (m) => m.devices },
      { key: 'today', header: 'Sent today', numeric: true, sort: (a, b) => a.bytesToday - b.bytesToday, cell: (m) => `${plural(m.filesToday, 'file')}, ${formatSize(m.bytesToday)}` },
      { key: 'manage', header: 'Manage', cell: (m) => <button type="button" className="ghost small" aria-label={`Manage ${m.label}`} onClick={() => setManage(m)}>Manage</button> },
    ];
  }, [admin]);

  if (!view) return <section className="panel kit" aria-labelledby="org-title"><h2 id="org-title">Organisation</h2>{message ? <p role="alert" className="notice error">{message.text}</p> : <p className="hint">Loading…</p>}</section>;

  return (
    <section className="panel kit org" aria-labelledby="org-title" data-testid="org-panel">
      <h2 id="org-title">Organisation</h2>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`notice ${message.kind}`}>{message.text}</p>}
      {!org ? (
        <>
          <p className="hint">An organisation lets the people in your office share one Office plan. Everyone keeps their own sign-in and their own files, and nobody in the organisation can see another person's files.</p>
          <div className="choices">
            <form className="section" onSubmit={(e) => { e.preventDefault(); void run(() => orgApi.create(session, name), 'Organisation created.'); }}>
              <h3>Start an organisation</h3>
              <p className="hint">Needs the Office plan. You become the owner.</p>
              <div className="fields"><label>Organisation name<input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} required minLength={2} /></label></div>
              <p className="actions"><button type="submit">Create organisation</button></p>
            </form>
            <form className="section" onSubmit={(e) => { e.preventDefault(); void run(() => orgApi.join(session, code, label), 'You have joined the organisation.'); }}>
              <h3>Join with an invitation</h3>
              <p className="hint">Use the code the organisation owner gave you. It works once.</p>
              <div className="fields">
                <label>Invitation code<input value={code} maxLength={16} onChange={(e) => setCode(e.target.value)} required autoComplete="off" /></label>
                <label>Your name in the organisation<input value={label} maxLength={40} onChange={(e) => setLabel(e.target.value)} required /></label>
              </div>
              <p className="actions"><button type="submit">Join</button></p>
            </form>
          </div>
        </>
      ) : (
        <>
          <p className="orghead"><strong className="orgname"><bdi>{org.name}</bdi></strong> <Badge tone="ok">{ROLE[org.role]}</Badge> <Badge>{view.planId ? `${view.planId[0]!.toUpperCase()}${view.planId.slice(1)} plan` : 'Free plan'}</Badge></p>
          <dl className="kpis">
            <Kpi label="People" value={view.members.length} />
            <Kpi label="Sent today" value={formatSize(view.bytesToday)} note={`${plural(view.filesToday, 'file')}, shared limits for everyone`} />
          </dl>
          <Tabs label="Organisation sections" active={tab} onChange={setTab} tabs={[{ id: 'members', label: 'People' }, ...(admin ? [{ id: 'invite', label: 'Invitations' }, { id: 'activity', label: 'Activity' }] : []), { id: 'leave', label: owner ? 'Delete' : 'Leave' }]} />
          <TabPanel id={tab}>
            {tab === 'members' && (
              <>
                {!admin && <p className="hint">Only the owner and admins can see usage and manage people.</p>}
                <DataTable rows={view.members} columns={columns} rowKey={(m) => m.uid || m.label} caption="People in the organisation" searchLabel="Search people" bare={view.members.length <= 8} empty="No one yet." />
              </>
            )}
            {tab === 'invite' && admin && (
              <section className="section" aria-label="Invitations">
                <h3>Invite someone</h3>
                <p className="hint">Each code works for one person and expires after 7 days. Send it to them yourself; they enter it on their own account page.</p>
                <p className="actions"><button type="button" onClick={() => { void (async () => { try { setInvite(await orgApi.invite(session)); setMessage(null); await load(); } catch (e) { fail(e); } })(); }}>Make an invitation code</button></p>
                {invite && <p role="status" className="codebox">Give this code to one person: <strong>{invite.code}</strong> <CopyId value={invite.code} label="invitation code" /> <span className="hint">Expires {new Date(invite.expiresAt).toLocaleDateString()}.</span></p>}
              </section>
            )}
            {tab === 'activity' && admin && (
              <section className="section" aria-label="Activity">
                <h3>Changes to the organisation</h3>
                <p className="hint">Who joined, left or was changed, and when. It never lists files, folders or transfers.</p>
                {events.length === 0 ? <p className="empty">Nothing yet.</p> : (
                  <table className="grid"><caption className="sr-only">Changes to the organisation</caption>
                    <thead><tr><th scope="col">When</th><th scope="col">Who</th><th scope="col">What</th></tr></thead>
                    <tbody>{events.map((ev, i) => <tr key={i}><th scope="row">{new Date(ev.at).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</th><td><bdi>{ev.by}</bdi></td><td>{ACTIONS[ev.action] ?? ev.action}{ev.detail ? ` (${ev.detail})` : ''}</td></tr>)}</tbody></table>
                )}
              </section>
            )}
            {tab === 'leave' && (owner ? (
              <section className="zone" aria-label="Delete the organisation"><h3>Delete this organisation</h3>
                <div className="row"><p>Everyone goes back to their own plan and keeps their own files. This cannot be undone.</p>
                  <button type="button" className="danger-solid" onClick={() => setAsking({ title: 'Delete this organisation', effect: 'Everyone goes back to their own plan and keeps their own files. This cannot be undone.', confirmLabel: 'Delete organisation', danger: true, done: 'Organisation deleted', run: () => orgApi.destroy(session) })}>Delete organisation</button></div>
              </section>
            ) : (
              <section className="zone" aria-label="Leave the organisation"><h3>Leave this organisation</h3>
                <div className="row"><p>You go back to your own plan. Your files and PCs are not touched, and you can join again with a new invitation.</p>
                  <button type="button" className="danger-solid" onClick={() => setAsking({ title: 'Leave this organisation', effect: 'You go back to your own plan. Your files and PCs are not touched.', confirmLabel: 'Leave organisation', danger: true, done: 'You left the organisation', run: () => orgApi.leave(session) })}>Leave organisation</button></div>
              </section>
            ))}
          </TabPanel>
        </>
      )}
      {manage && org && admin && <MemberDrawer session={session} member={manage} owner={owner} canManage={canManage(manage)} onClose={() => setManage(null)} onFail={fail} ask={setAsking} />}
      {asking && <ConfirmDialog title={asking.title} effect={asking.effect} confirmLabel={asking.confirmLabel} danger={asking.danger} busy={busy} onConfirm={() => void confirm()} onCancel={() => setAsking(null)} />}
    </section>
  );
}

function MemberDrawer({ session, member, owner, canManage, onClose, onFail, ask }: { session: Session; member: OrgMember; owner: boolean; canManage: boolean; onClose: () => void; onFail: (e: unknown) => void; ask: (a: Ask) => void }) {
  const [history, setHistory] = useState<UsageDay[] | null>(null);
  useEffect(() => { void (async () => { try { setHistory((await orgApi.memberUsage(session, member.uid)).history); } catch (e) { onFail(e); } })(); }, [session, member.uid, onFail]);
  const who = <bdi>{member.label}</bdi>;
  return (
    <Drawer testId="org-member" title={who} subtitle={`${ROLE[member.role]} · ${plural(member.pcs, 'PC')} · ${plural(member.devices, 'browser')}`} onClose={onClose}>
      <section className="section" aria-label="Usage"><h3>Last 30 days</h3>
        <p className="hint">Counts only. What was moved is never shown to anyone in the organisation.</p>
        {history ? <UsageTable history={history} caption={member.label} /> : <p className="empty" role="status">Loading…</p>}
      </section>
      {canManage && (
        <section className="section" aria-label="Manage">
          <h3>Manage</h3>
          <div className="zone-row"><p>Signs {who} out of every browser and phone. Their PCs stay paired.</p>
            <p className="actions"><button type="button" className="ghost" onClick={() => ask({ title: 'Sign out browsers', effect: <>{who} must approve each browser and phone again.</>, confirmLabel: 'Sign out browsers', done: 'Browsers signed out', run: () => orgApi.revokeDevices(session, member.uid) })}>Sign out browsers</button></p></div>
          {owner && <div className="zone-row"><p>{member.role === 'admin' ? 'Admins can invite people and manage members.' : 'Admins can invite people and manage members, but cannot delete the organisation.'}</p>
            <p className="actions"><button type="button" className="ghost" onClick={() => ask({ title: member.role === 'admin' ? 'Make a member' : 'Make an admin', effect: <>{who} {member.role === 'admin' ? 'will no longer manage people.' : 'will be able to invite people and manage members.'}</>, confirmLabel: member.role === 'admin' ? 'Make member' : 'Make admin', done: 'Role changed', run: () => orgApi.role(session, member.uid, member.role === 'admin' ? 'member' : 'admin') })}>{member.role === 'admin' ? 'Make member' : 'Make admin'}</button></p></div>}
          <div className="zone-row"><p>Removes {who} from the organisation. They go back to their own plan and keep their files.</p>
            <p className="actions"><button type="button" className="danger-solid" onClick={() => ask({ title: 'Remove from organisation', effect: <>{who} goes back to their own plan and keeps their own files.</>, confirmLabel: 'Remove', danger: true, done: 'Person removed', run: () => orgApi.remove(session, member.uid) })}>Remove</button></p></div>
        </section>
      )}
    </Drawer>
  );
}
