import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError } from '../../api';
import { adminApi, formatSize, type AdminOrgDetail, type AdminOrgRow } from '../../billing';
import { UsageTable } from '../billing/UsageTable';
import { Badge, DataTable, Drawer, PageHead, TabPanel, Tabs, type Column } from './ui';
import { dateTime, plural, shortDate, type Ctx } from './shared';
import { accountLabel, searchText } from './people';
import { Who } from './Who';

export function Orgs({ ctx, rows }: { ctx: Ctx; rows: AdminOrgRow[] }) {
  const [open, setOpen] = useState<string | null>(null);
  const columns = useMemo<Column<AdminOrgRow>[]>(() => [
    { key: 'name', header: 'Organisation', text: (r) => `${r.name} ${searchText(ctx.people, r.ownerUid)}`, sort: (a, b) => a.name.localeCompare(b.name), cell: (r) => <button type="button" className="rowlink" onClick={() => setOpen(r.id)} aria-label={`Open organisation ${r.name}`}><bdi>{r.name}</bdi></button> },
    { key: 'status', header: 'Status', sort: (a, b) => Number(a.suspended) - Number(b.suspended), cell: (r) => (r.suspended ? <Badge tone="bad">Suspended</Badge> : <Badge tone="ok">Active</Badge>) },
    { key: 'owner', header: 'Owner', text: (r) => searchText(ctx.people, r.ownerUid), cell: (r) => <Who uid={r.ownerUid} people={ctx.people} /> },
    { key: 'plan', header: 'Plan', sort: (a, b) => a.planId.localeCompare(b.planId), cell: (r) => r.planId },
    { key: 'members', header: 'Members', numeric: true, sort: (a, b) => a.members - b.members, cell: (r) => r.members },
    { key: 'today', header: 'Sent today', numeric: true, sort: (a, b) => a.bytesToday - b.bytesToday, cell: (r) => <>{plural(r.filesToday, 'file')}, {formatSize(r.bytesToday)}</> },
  ], [ctx.people]);
  return (
    <div data-testid="admin-orgs">
      <PageHead title="Organisations" summary="Members (with their email addresses, visible to operators only), plan and counts. File and folder names are never held here, and neither is a record of what a member moved." />
      <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} caption="Organisations" searchLabel="Search by organisation, or owner email or id" empty="No organisations yet. An Office customer creates one from their account page."
        filters={[{ label: 'Status', options: [{ value: 'active', label: 'Active' }, { value: 'suspended', label: 'Suspended' }], test: (r, v) => (v === 'suspended') === r.suspended }]} />
      {open && <OrgDrawer ctx={ctx} id={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

function OrgDrawer({ ctx, id, onClose }: { ctx: Ctx; id: string; onClose: () => void }) {
  const { session } = ctx;
  const [detail, setDetail] = useState<AdminOrgDetail | null>(null);
  const [tab, setTab] = useState('members');
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { setDetail(await adminApi.org(session, id)); setError(null); } catch (e) { setError(e instanceof ApiError ? e.message : 'Something went wrong.'); } }, [session, id]);
  useEffect(() => { void load(); }, [load]);
  const org = detail?.org;
  // This organisation's own detail carries its members' emails (operators only); the shared directory covers everyone else.
  const memberPeople = useMemo(() => {
    const all = new Map(ctx.people);
    for (const m of detail?.members ?? []) if (m.email) all.set(m.uid, { uid: m.uid, email: m.email, name: m.name });
    if (detail?.org.ownerEmail) all.set(detail.org.ownerUid, { uid: detail.org.ownerUid, email: detail.org.ownerEmail, name: detail.org.ownerName });
    return all;
  }, [ctx.people, detail]);
  const change = (action: 'suspend' | 'unsuspend' | 'delete', spec: { title: string; effect: string; confirm: string; danger?: boolean; done: string }) =>
    ctx.ask({ title: spec.title, effect: <>{spec.effect} Organisation <bdi>{org?.name}</bdi>, owned by <strong><bdi>{org ? accountLabel(memberPeople, org.ownerUid) : ''}</bdi></strong>.</>, confirmLabel: spec.confirm, danger: spec.danger, done: spec.done,
      run: async (reason) => { await adminApi.orgAction(session, id, action, reason); if (action === 'delete') onClose(); else await load(); } });
  return (
    <Drawer testId="admin-org-detail" title={org ? <bdi>{org.name}</bdi> : 'Organisation'} onClose={onClose} subtitle={org && <>{plural(org.members, 'member')} · {org.planId}{org.suspended ? ' · suspended' : ''}</>}>
      {error && <p role="alert" className="notice error">{error}</p>}
      {!detail || !org ? !error && <p className="empty" role="status">Loading…</p> : (
        <>
          <Tabs label="Organisation sections" active={tab} onChange={setTab} tabs={[{ id: 'members', label: 'Members' }, { id: 'usage', label: 'Usage' }, { id: 'activity', label: 'Activity' }, { id: 'actions', label: 'Actions' }]} />
          <TabPanel id={tab}>
            {tab === 'members' && (
              <section className="section" aria-label="Members">
                <table className="grid"><caption className="sr-only">Members</caption>
                  <thead><tr><th scope="col">Member</th><th scope="col">Name in the organisation</th><th scope="col">Role</th><th scope="col" className="num">PCs</th><th scope="col" className="num">Browsers</th><th scope="col" className="num">Sent today</th></tr></thead>
                  <tbody>{detail.members.map((m) => <tr key={m.uid}><th scope="row"><Who uid={m.uid} people={memberPeople} /></th><td><bdi>{m.label}</bdi></td><td>{m.role}</td><td className="num">{m.pcs}</td><td className="num">{m.devices}</td><td className="num">{plural(m.filesToday, 'file')}, {formatSize(m.bytesToday)}</td></tr>)}</tbody></table>
              </section>
            )}
            {tab === 'usage' && <section className="section" aria-label="Usage"><h3>Last 30 days</h3><UsageTable history={detail.history} caption="Whole organisation" /></section>}
            {tab === 'activity' && (
              <section className="section" aria-label="Activity"><h3>Changes to the organisation</h3>
                <p className="hint">Who joined, left or changed role. Transfers are never listed.</p>
                {detail.events.length === 0 ? <p className="empty">Nothing yet.</p> : (
                  <table className="grid"><caption className="sr-only">Changes</caption><thead><tr><th scope="col">When</th><th scope="col">What</th><th scope="col">By</th></tr></thead>
                    <tbody>{detail.events.map((ev, i) => <tr key={i}><th scope="row">{dateTime(ev.at)}</th><td>{ev.action}{ev.detail ? ` (${ev.detail})` : ''}</td><td>{ev.by ? <Who uid={ev.by} people={memberPeople} stacked={false} /> : ''}</td></tr>)}</tbody></table>)}
              </section>
            )}
            {tab === 'actions' && (
              <>
                <section className="section" aria-label="About"><dl className="facts"><dt>Owner</dt><dd><Who uid={org.ownerUid} people={memberPeople} /></dd><dt>Created</dt><dd>{shortDate(org.createdAt)}</dd></dl></section>
                <section className="zone" aria-label="Suspension"><h3>{org.suspended ? 'Suspended' : 'Suspend this organisation'}</h3>
                  <div className="row"><p>{org.suspended ? 'Members are using their own plans. Restoring brings the shared plan back.' : 'Members fall back to their own plans, and invites and joins are blocked until you restore it.'}</p>
                    {org.suspended
                      ? <button type="button" onClick={() => change('unsuspend', { title: 'Restore organisation', effect: 'The shared plan applies to the members again.', confirm: 'Restore', done: 'Organisation restored' })}>Restore</button>
                      : <button type="button" className="danger-solid" onClick={() => change('suspend', { title: 'Suspend organisation', effect: 'Members fall back to their own plans.', confirm: 'Suspend', danger: true, done: 'Organisation suspended' })}>Suspend</button>}
                  </div>
                </section>
                <section className="zone" aria-label="Deletion"><h3>Delete this organisation</h3>
                  <div className="row"><p>Members go back to their own plans. This cannot be undone.</p>
                    <button type="button" className="danger-solid" onClick={() => change('delete', { title: 'Delete organisation', effect: 'The organisation is removed for good and its members keep only their own plans.', confirm: 'Delete organisation', danger: true, done: 'Organisation deleted' })}>Delete</button></div>
                </section>
              </>
            )}
          </TabPanel>
        </>
      )}
    </Drawer>
  );
}
