import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, api } from '../../api';
import { adminApi, billingApi, formatSize, limitText, monthsLabel, type AdminAccountRow, type AdminUserDetail, type UserBilling } from '../../billing';
import { formatBytes } from '../../format';
import { UsageTable } from '../billing/UsageTable';
import { Badge, CopyId, DataTable, Drawer, PageHead, TabPanel, Tabs, type Column } from './ui';
import { dateTime, plural, shortDate, type Ctx } from './shared';
import { accountLabel, lookup, NOT_KNOWN, searchText, shortUid, type People } from './people';
import { Who } from './Who';

const statusBadge = (r: AdminAccountRow) => (r.suspended ? <Badge tone="bad">Suspended</Badge> : <Badge tone="ok">Active</Badge>);
const planCell = (r: AdminAccountRow) => <>{r.planName}{r.state === 'grace' && <> <Badge tone="wait">Ended, grace period</Badge></>}</>;

export function Accounts({ ctx, rows }: { ctx: Ctx; rows: AdminAccountRow[] }) {
  const [open, setOpen] = useState<string | null>(null);
  const columns = useMemo<Column<AdminAccountRow>[]>(() => [
    { key: 'uid', header: 'Account', text: (r) => `${searchText(ctx.people, r.uid)} ${r.orgName ?? ''}`, sort: (a, b) => (lookup(ctx.people, a.uid).email ?? a.uid).localeCompare(lookup(ctx.people, b.uid).email ?? b.uid),
      cell: (r) => <button type="button" className="rowlink who-link" onClick={() => setOpen(r.uid)}><span className="sr-only">Open account </span><Who uid={r.uid} people={ctx.people} /></button> },
    { key: 'status', header: 'Status', sort: (a, b) => Number(a.suspended) - Number(b.suspended), cell: statusBadge },
    { key: 'plan', header: 'Plan', sort: (a, b) => a.planName.localeCompare(b.planName), cell: planCell },
    { key: 'org', header: 'Organisation', text: (r) => r.orgName ?? '', sort: (a, b) => (a.orgName ?? '').localeCompare(b.orgName ?? ''), cell: (r) => r.orgName ? <bdi>{r.orgName}</bdi> : <span className="hint">None</span> },
    { key: 'pcs', header: 'PCs', numeric: true, sort: (a, b) => a.pcs - b.pcs, cell: (r) => r.pcs },
    { key: 'dev', header: 'Browsers', numeric: true, sort: (a, b) => a.devices - b.devices, cell: (r) => r.devices },
    { key: 'today', header: 'Sent today', numeric: true, sort: (a, b) => a.bytesToday - b.bytesToday, cell: (r) => <>{plural(r.filesToday, 'file')}, {formatSize(r.bytesToday)}</> },
  ], [ctx.people]);
  const plans = [...new Set(rows.map((r) => r.planName))];
  return (
    <>
      <PageHead title="Accounts" summary="Each account is shown by its email address (and name, if the sign-in gave one) with a short id, so you always know whom a change affects. Only you and the other operators see these. Folders and files are never shown." />
      <DataTable testId="admin-users" rows={rows} columns={columns} rowKey={(r) => r.uid} caption="Accounts" searchLabel="Search by email, name, account id or organisation" empty="No accounts yet. They appear here when someone signs in."
        filters={[
          { label: 'Status', options: [{ value: 'active', label: 'Active' }, { value: 'suspended', label: 'Suspended' }], test: (r, v) => (v === 'suspended') === r.suspended },
          { label: 'Plan', options: plans.map((p) => ({ value: p, label: p })), test: (r, v) => r.planName === v },
        ]} />
      {open && <AccountDrawer ctx={ctx} uid={open} onClose={() => setOpen(null)} />}
    </>
  );
}

function AccountDrawer({ ctx, uid, onClose }: { ctx: Ctx; uid: string; onClose: () => void }) {
  const { session } = ctx;
  const [detail, setDetail] = useState<AdminUserDetail | null>(null);
  const [money, setMoney] = useState<UserBilling | null>(null);
  const [tab, setTab] = useState('summary');
  const [pass, setPass] = useState({ planId: 'plus', months: '1', source: 'grant' as 'grant' | 'manual' });
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { const [d, m] = await Promise.all([adminApi.userDetail(session, uid), billingApi.userBilling(session, uid)]); setDetail(d); setMoney(m); setError(null); }
    catch (e) { setError(e instanceof ApiError ? e.message : 'Something went wrong.'); }
  }, [session, uid]);
  useEffect(() => { void load(); }, [load]);

  // The drawer's own detail is the freshest source for this account's email; the shared directory covers every other account.
  const people = useMemo<People>(() => (detail?.email ? new Map(ctx.people).set(uid, { uid, email: detail.email, name: detail.name }) : ctx.people), [ctx.people, detail, uid]);
  const label = accountLabel(people, uid);
  const act = (action: 'suspend' | 'unsuspend' | 'revoke-all' | 'reset-relay-allowance', spec: { title: string; effect: string; confirm: string; danger?: boolean; done: string }) =>
    ctx.ask({ title: spec.title, effect: <>{spec.effect} Account: <strong><bdi>{label}</bdi></strong>.</>, confirmLabel: spec.confirm, danger: spec.danger, done: spec.done, run: async (reason) => { await api.adminUserAction(session, uid, action, reason); await load(); } });
  const plan = detail?.plan;
  return (
    <Drawer testId="admin-account-detail" title={<>Account <bdi>{lookup(people, uid).email ?? shortUid(uid)}</bdi></>} onClose={onClose}
      subtitle={detail && <>{shortUid(uid)} · {detail.plan.planName}{detail.suspended ? ' · suspended' : ''}{detail.orgName ? ` · ${detail.orgName}` : ''}</>}>
      {error && <p role="alert" className="notice error">{error}</p>}
      {!detail || !plan ? !error && <p className="empty" role="status">Loading…</p> : (
        <>
          <Tabs label="Account sections" active={tab} onChange={setTab} tabs={[{ id: 'summary', label: 'Summary' }, { id: 'usage', label: 'Usage' }, { id: 'devices', label: 'Devices' }, { id: 'plan', label: 'Plan and passes' }, { id: 'actions', label: 'Actions' }]} />
          <TabPanel id={tab}>
            {tab === 'summary' && (
              <section className="section" aria-label="Summary">
                <dl className="facts">
                  <dt>Status</dt><dd>{detail.suspended ? <Badge tone="bad">Suspended</Badge> : <Badge tone="ok">Active</Badge>}</dd>
                  <dt>Email</dt><dd>{detail.email ? <><bdi>{detail.email}</bdi> <CopyId value={detail.email} label="email address" /></> : <span className="hint">{NOT_KNOWN}</span>}</dd>
                  {detail.name && <><dt>Name</dt><dd><bdi>{detail.name}</bdi></dd></>}
                  <dt>Account id</dt><dd><bdi>{uid}</bdi> <CopyId value={uid} label="account id" /></dd>
                  <dt>Plan</dt><dd>{plan.planName}{plan.state !== 'free' && plan.endsAt ? `, until ${shortDate(plan.endsAt)}` : ''}{plan.state === 'grace' ? ' (grace period)' : ''}</dd>
                  <dt>Organisation</dt><dd>{detail.orgName ? <><bdi>{detail.orgName}</bdi> ({detail.orgRole})</> : 'None'}</dd>
                  <dt>PCs</dt><dd>{plan.pcs} of {limitText(plan.limits.pcs, 'count')}</dd>
                  <dt>Browsers and phones</dt><dd>{plan.devices} of {limitText(plan.limits.devices, 'count')}</dd>
                  <dt>Sent today</dt><dd>{plural(plan.filesToday, 'file')} of {limitText(plan.limits.filesPerDay, 'count')}, {formatSize(plan.bytesToday)} of {limitText(plan.limits.bytesPerDay, 'bytes')}</dd>
                </dl>
              </section>
            )}
            {tab === 'usage' && <section className="section" aria-label="Usage"><h3>Last 30 days</h3><UsageTable history={detail.history} caption="This account" /></section>}
            {tab === 'devices' && (
              <>
                <section className="section" aria-label="PCs"><h3>PCs</h3>
                  {detail.pcs.length === 0 ? <p className="empty">No PCs are paired to this account.</p> : (
                    <table className="grid"><caption className="sr-only">PCs</caption><thead><tr><th scope="col">Name</th><th scope="col">Version</th><th scope="col">Last seen</th></tr></thead>
                      <tbody>{detail.pcs.map((p, i) => <tr key={i}><th scope="row"><bdi>{p.name || 'PC'}</bdi></th><td>{p.agentVersion || 'unknown'}</td><td>{dateTime(p.lastSeen)}</td></tr>)}</tbody></table>)}
                </section>
                <section className="section" aria-label="Browsers and phones"><h3>Browsers and phones</h3>
                  {detail.devices.length === 0 ? <p className="empty">No approved browsers or phones.</p> : (
                    <table className="grid"><caption className="sr-only">Browsers and phones</caption><thead><tr><th scope="col">Name</th><th scope="col">Approved</th><th scope="col">Last used</th></tr></thead>
                      <tbody>{detail.devices.map((d, i) => <tr key={i}><th scope="row"><bdi>{d.name}</bdi></th><td>{shortDate(d.createdAt)}</td><td>{dateTime(d.lastUsedAt)}</td></tr>)}</tbody></table>)}
                </section>
              </>
            )}
            {tab === 'plan' && money && (
              <>
                <section className="section" aria-label="Passes"><h3>Passes</h3>
                  {money.passes.length === 0 ? <p className="empty">No passes. This account is on the Free plan.</p> : (
                    <table className="grid"><caption className="sr-only">Passes</caption><thead><tr><th scope="col">Plan</th><th scope="col">From</th><th scope="col">Until</th><th scope="col">How</th></tr></thead>
                      <tbody>{money.passes.map((p) => <tr key={p.id}><th scope="row">{p.planId}</th><td>{shortDate(p.startsAt)}</td><td>{shortDate(p.endsAt)}</td><td>{p.source}</td></tr>)}</tbody></table>)}
                </section>
                <section className="section" aria-label="Add a pass"><h3>Add a pass</h3>
                  <p className="hint">For a payment you checked yourself, a gift or a partner. It starts now, or when the current pass ends.</p>
                  <div className="fields">
                    <label>Plan<select value={pass.planId} onChange={(e) => setPass({ ...pass, planId: e.target.value })}><option value="plus">Plus</option><option value="office">Office</option></select></label>
                    <label>Months<input inputMode="numeric" value={pass.months} onChange={(e) => setPass({ ...pass, months: e.target.value })} /></label>
                    <label>How it was paid<select value={pass.source} onChange={(e) => setPass({ ...pass, source: e.target.value as 'grant' | 'manual' })}><option value="grant">Gift or partner</option><option value="manual">Paid by hand</option></select></label>
                  </div>
                  <p className="actions"><button type="button" disabled={!(Number(pass.months) >= 1)} onClick={() => ctx.ask({ title: 'Add a pass', effect: <>Give {pass.planId === 'office' ? 'Office' : 'Plus'} for {monthsLabel(Number(pass.months))} to account: <strong><bdi>{label}</bdi></strong>.</>, confirmLabel: 'Add pass', done: 'Pass added',
                    run: async (reason) => { await billingApi.addPass(session, uid, pass.planId, Number(pass.months), reason, pass.source); await load(); } })}>Add pass</button></p>
                </section>
                <section className="section" aria-label="Orders"><h3>Orders</h3>
                  {money.orders.length === 0 ? <p className="empty">No orders.</p> : (
                    <table className="grid"><caption className="sr-only">Orders</caption><thead><tr><th scope="col">Placed</th><th scope="col">Plan</th><th scope="col">Status</th></tr></thead>
                      <tbody>{money.orders.map((o) => <tr key={o.id}><th scope="row">{shortDate(o.createdAt)}</th><td>{o.planId}, {monthsLabel(o.months)}</td><td>{o.status.replace('_', ' ')}</td></tr>)}</tbody></table>)}
                </section>
              </>
            )}
            {tab === 'actions' && (
              <>
                <section className="section" aria-label="Support actions"><h3>Support</h3>
                  <div className="zone-row"><p>Signs this account out of every browser and phone. Their PCs stay paired.</p>
                    <p className="actions"><button type="button" className="ghost" onClick={() => act('revoke-all', { title: 'Sign out all browsers', effect: 'Every browser and phone of this account must be approved again.', confirm: 'Sign out all', done: 'Browsers signed out' })}>Sign out all browsers</button></p></div>
                  <div className="zone-row"><p>Lets this account send again today if it hit the relay's daily allowance ({formatBytes(String(plan.bytesToday))} used).</p>
                    <p className="actions"><button type="button" className="ghost" onClick={() => act('reset-relay-allowance', { title: 'Restore today’s allowance', effect: 'The relay’s count for today is reset for this account.', confirm: 'Restore allowance', done: 'Allowance restored' })}>Restore today’s allowance</button></p></div>
                </section>
                <section className="zone" aria-label="Suspension"><h3>{detail.suspended ? 'Suspended' : 'Suspend this account'}</h3>
                  <div className="row"><p>{detail.suspended ? 'This account cannot use the service. Lifting the suspension restores access straight away.' : 'The account is refused everywhere until you lift the suspension. Nothing is deleted and its PCs are not touched.'}</p>
                    {detail.suspended
                      ? <button type="button" onClick={() => act('unsuspend', { title: 'Lift suspension', effect: 'This account can use the service again.', confirm: 'Lift suspension', done: 'Suspension lifted' })}>Lift suspension</button>
                      : <button type="button" className="danger-solid" onClick={() => act('suspend', { title: 'Suspend this account', effect: 'This account is refused everywhere until you lift the suspension.', confirm: 'Suspend account', danger: true, done: 'Account suspended' })}>Suspend</button>}
                  </div>
                </section>
              </>
            )}
          </TabPanel>
        </>
      )}
    </Drawer>
  );
}
