import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError } from '../../api';
import { billingApi, formatPkr, formatUsd, GIB, MIB, monthsLabel, type AdminBilling, type Coupon, type EditablePlan, type Order } from '../../billing';
import { Badge, DataTable, PageHead, TabPanel, Tabs, type Column } from './ui';
import { plural, shortDate, type Ctx } from './shared';

const numberOrNull = (text: string): number | null => (text.trim() === '' ? null : Number(text));
const statusLabel: Record<string, [string, 'wait' | 'ok' | 'bad' | 'plain']> = {
  requested: ['Not paid yet', 'plain'], proof_submitted: ['Paid, check the money', 'wait'], verified: ['Activated', 'ok'], rejected: ['Rejected', 'bad'], cancelled: ['Cancelled', 'plain'], expired: ['Expired', 'plain'],
};

/** Money: orders to check, prices and limits, discount codes, and the words customers see when they pay. Every change asks for a reason in a dialog. */
export function Billing({ ctx, orders, waiting }: { ctx: Ctx; orders: Order[]; waiting: number }) {
  const [tab, setTab] = useState(waiting > 0 ? 'orders' : 'plans');
  const [data, setData] = useState<AdminBilling | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { session, reload } = ctx;
  const load = useCallback(async () => { try { setData(await billingApi.admin(session)); setError(null); } catch (e) { if (e instanceof ApiError && e.code === 'unauthenticated') ctx.fail(e); setError(e instanceof ApiError ? e.message : 'Something went wrong.'); } }, [session, ctx]);
  useEffect(() => { void load(); }, [load]);
  const after = async () => { await load(); await reload(); };
  return (
    <div data-testid="billing-admin">
      <PageHead title="Billing" summary="Prices are set in dollars and rupees follow the exchange rate unless you fix a rupee price." />
      <Tabs label="Billing sections" active={tab} onChange={setTab} tabs={[{ id: 'orders', label: 'Orders', badge: waiting }, { id: 'plans', label: 'Plans and prices' }, { id: 'codes', label: 'Discount codes' }, { id: 'pay', label: 'Payment details' }]} />
      <TabPanel id={tab}>
        {error && <p role="alert" className="notice error" data-testid="billing-error">{error}</p>}
        {tab === 'orders' && <OrdersTable ctx={ctx} orders={orders} after={after} />}
        {tab !== 'orders' && !data && !error && <p className="empty" role="status">Loading…</p>}
        {data && tab === 'plans' && <PlansEditor ctx={ctx} data={data} after={after} />}
        {data && tab === 'codes' && <Codes ctx={ctx} data={data} after={after} />}
        {data && tab === 'pay' && <PaymentText ctx={ctx} data={data} after={after} />}
      </TabPanel>
    </div>
  );
}

function OrdersTable({ ctx, orders, after }: { ctx: Ctx; orders: Order[]; after: () => Promise<void> }) {
  const decide = (o: Order, kind: 'verify' | 'reject') => ctx.ask({
    title: kind === 'verify' ? 'Activate this order' : 'Reject this order',
    effect: kind === 'verify' ? <>You have received the money for {o.planId}, {monthsLabel(o.months)}, {formatPkr(o.duePkr, false)} ({formatUsd(o.dueUsd)}). The pass starts now.</> : <>The customer is told the order was not accepted. No pass is added.</>,
    confirmLabel: kind === 'verify' ? 'Money received: activate' : 'Reject order', danger: kind === 'reject', done: kind === 'verify' ? 'Order activated' : 'Order rejected',
    run: async (reason) => { await (kind === 'verify' ? billingApi.verify(ctx.session, o.id, reason) : billingApi.reject(ctx.session, o.id, reason)); await after(); },
  });
  const columns = useMemo<Column<Order>[]>(() => [
    { key: 'order', header: 'Order', sort: (a, b) => a.createdAt.localeCompare(b.createdAt), text: (o) => `${o.planId} ${o.id}`, cell: (o) => <><strong>{o.planId}</strong>, {monthsLabel(o.months)}<small className="hint block">Placed {shortDate(o.createdAt)}{o.couponCode ? `, code ${o.couponCode}` : ''}</small></> },
    { key: 'who', header: 'Account', text: (o) => o.subject, cell: (o) => <bdi className="idtext" title={o.subject}>{o.subject.replace(/^acct:/, '')}</bdi> },
    { key: 'amount', header: 'Amount', numeric: true, sort: (a, b) => a.dueUsd - b.dueUsd, cell: (o) => <>{formatPkr(o.duePkr, false)} <span className="hint">({formatUsd(o.dueUsd)})</span></> },
    { key: 'paid', header: 'Payment', text: (o) => `${o.method ?? ''} ${o.reference ?? ''}`, cell: (o) => (o.status === 'proof_submitted' ? <>{o.method}, reference <bdi>{o.reference}</bdi>{o.sameReferenceAs && o.sameReferenceAs.length > 0 && <Badge tone="bad">Same reference on {plural(o.sameReferenceAs.length, 'other order')}</Badge>}</> : <span className="hint">None yet</span>) },
    { key: 'status', header: 'Status', sort: (a, b) => a.status.localeCompare(b.status), cell: (o) => { const [text, tone] = statusLabel[o.status] ?? [o.status, 'plain' as const]; return <Badge tone={tone}>{text}</Badge>; } },
    { key: 'do', header: 'Decision', cell: (o) => (o.status === 'proof_submitted' || o.status === 'requested')
      ? <span className="actions"><button type="button" className="small" onClick={() => decide(o, 'verify')}>Activate</button><button type="button" className="ghost small" onClick={() => decide(o, 'reject')}>Reject</button></span> : null },
  ], []); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div data-testid="billing-orders">
      <DataTable rows={orders} columns={columns} rowKey={(o) => o.id} caption="Orders" searchLabel="Search by plan or account" initialSort={{ key: 'order', dir: 'desc' }} empty="No orders yet."
        filters={[{ label: 'Status', options: [{ value: 'proof_submitted', label: 'Paid, to check' }, { value: 'requested', label: 'Not paid yet' }, { value: 'closed', label: 'Finished' }],
          test: (o, v) => (v === 'closed' ? o.status !== 'requested' && o.status !== 'proof_submitted' : o.status === v) }]} />
    </div>
  );
}

function PlansEditor({ ctx, data, after }: { ctx: Ctx; data: AdminBilling; after: () => Promise<void> }) {
  const [plans, setPlans] = useState<EditablePlan[]>(() => structuredClone(data.editable));
  const [rate, setRate] = useState(String(data.plans.pkrPerUsd));
  const dirty = JSON.stringify(plans) !== JSON.stringify(data.editable) || rate !== String(data.plans.pkrPerUsd);
  const setPlan = (id: string, change: (plan: EditablePlan) => void) => setPlans((all) => all.map((p) => { if (p.id !== id) return p; const copy = structuredClone(p); change(copy); return copy; }));
  const on = data.plans.limitsEnabled;
  return (
    <>
      <section className="section" aria-labelledby="lim">
        <h3 id="lim">Start date: plan limits and ordering</h3>
        <p>Limits and ordering are <Badge tone={on ? 'ok' : 'plain'}>{on ? 'Switched on' : 'Switched off'}</Badge> This one switch decides both. While it is off, nobody is held to a limit and customers cannot see or place orders. Usage is counted either way.</p>
        <p className="actions"><button type="button" className={on ? 'ghost' : ''} onClick={() => ctx.ask({ title: on ? 'Switch limits off' : 'Switch limits on', effect: on ? 'Nobody is held to a plan limit, and ordering closes, until you switch them on again.' : 'Every account is held to its plan from now on, and customers can order passes. Accounts over a limit will be refused.', confirmLabel: on ? 'Switch limits off' : 'Switch limits on', danger: !on, done: on ? 'Limits switched off' : 'Limits switched on', run: async (reason) => { await billingApi.setLimits(ctx.session, !on, reason); await after(); } })}>{on ? 'Switch limits off' : 'Switch limits on'}</button></p>
      </section>
      <section className="section" aria-labelledby="rate">
        <h3 id="rate">Exchange rate</h3>
        <div className="fields"><label>Rupees per dollar<input inputMode="decimal" aria-label="Rupees per dollar" value={rate} onChange={(e) => setRate(e.target.value)} /></label></div>
        <p className="hint">Set on {shortDate(data.plans.rateUpdatedAt)}. Rupee prices that are not fixed follow this rate and show as approximate. Orders already placed keep their price.</p>
      </section>
      {plans.map((plan) => (
        <section className="section" key={plan.id} aria-labelledby={`pl-${plan.id}`} data-testid={`plan-${plan.id}`}>
          <h3 id={`pl-${plan.id}`}>{plan.name}{plan.purchasable ? '' : ' (not for sale)'}</h3>
          <div className="fields">
            {([['pcs', 'PCs'], ['devices', 'Phones and browsers'], ['filesPerDay', 'Files a day']] as const).map(([key, label]) => (
              <label key={key}>{label}<input inputMode="numeric" placeholder="No limit" aria-label={`${plan.name} ${label}`} value={plan.limits[key] ?? ''} onChange={(e) => setPlan(plan.id, (p) => { p.limits[key] = numberOrNull(e.target.value); })} /></label>
            ))}
            <label>Largest file (MB)<input inputMode="numeric" placeholder="No limit" aria-label={`${plan.name} largest file in MB`} value={plan.limits.bytesPerFile === null ? '' : Math.round(plan.limits.bytesPerFile / MIB)} onChange={(e) => setPlan(plan.id, (p) => { const v = numberOrNull(e.target.value); p.limits.bytesPerFile = v === null ? null : v * MIB; })} /></label>
            <label>Data a day (GB)<input inputMode="decimal" placeholder="No limit" aria-label={`${plan.name} data a day in GB`} value={plan.limits.bytesPerDay === null ? '' : +(plan.limits.bytesPerDay / GIB).toFixed(2)} onChange={(e) => setPlan(plan.id, (p) => { const v = numberOrNull(e.target.value); p.limits.bytesPerDay = v === null ? null : Math.round(v * GIB); })} /></label>
          </div>
          {plan.prices.length > 0 && (
            <table className="grid"><caption className="sr-only">{plan.name} prices</caption>
              <thead><tr><th scope="col">Length</th><th scope="col">Dollars</th><th scope="col">Fixed rupees (empty follows the rate)</th></tr></thead>
              <tbody>{plan.prices.map((price, index) => (
                <tr key={price.months}><th scope="row">{monthsLabel(price.months)}</th>
                  <td><input inputMode="decimal" aria-label={`${plan.name} ${monthsLabel(price.months)} dollars`} value={price.usd} onChange={(e) => setPlan(plan.id, (p) => { p.prices[index]!.usd = Number(e.target.value); })} /></td>
                  <td><input inputMode="numeric" aria-label={`${plan.name} ${monthsLabel(price.months)} rupees`} value={price.pkrOverride ?? ''} onChange={(e) => setPlan(plan.id, (p) => { p.prices[index]!.pkrOverride = numberOrNull(e.target.value); })} /></td></tr>))}</tbody></table>
          )}
        </section>
      ))}
      {dirty && (
        <div className="savebar" role="region" aria-label="Unsaved changes">
          <span>You have unsaved changes to prices, limits or the exchange rate.</span>
          <span className="actions">
            <button type="button" className="ghost" onClick={() => { setPlans(structuredClone(data.editable)); setRate(String(data.plans.pkrPerUsd)); }}>Discard</button>
            <button type="button" onClick={() => ctx.ask({ title: 'Save prices and limits', effect: 'New orders use these prices and, when limits are on, accounts are held to these limits. Orders already placed keep their price.', confirmLabel: 'Save changes', done: 'Plans saved',
              run: async (reason) => { if (rate !== String(data.plans.pkrPerUsd)) await billingApi.setRate(ctx.session, Number(rate), reason); if (JSON.stringify(plans) !== JSON.stringify(data.editable)) await billingApi.setPlans(ctx.session, plans, reason); await after(); } })}>Save changes</button>
          </span>
        </div>
      )}
    </>
  );
}

function Codes({ ctx, data, after }: { ctx: Ctx; data: AdminBilling; after: () => Promise<void> }) {
  const [c, setC] = useState({ code: '', kind: 'percent', value: '10', max: '', expires: '', plan: '' });
  const toggle = (k: Coupon) => ctx.ask({ title: k.active ? 'Switch code off' : 'Switch code on', effect: <>Code <strong>{k.code}</strong> {k.active ? 'stops working for new orders.' : 'works for new orders again.'}</>, confirmLabel: k.active ? 'Switch off' : 'Switch on', done: k.active ? 'Code switched off' : 'Code switched on',
    run: async (reason) => { await billingApi.saveCoupon(ctx.session, k.code, { kind: k.kind, value: k.value, planIds: k.planIds, expiresAt: k.expiresAt, maxRedemptions: k.maxRedemptions, active: !k.active, note: k.note, reason }); await after(); } });
  const columns = useMemo<Column<Coupon>[]>(() => [
    { key: 'code', header: 'Code', text: (k) => k.code, sort: (a, b) => a.code.localeCompare(b.code), cell: (k) => <strong>{k.code}</strong> },
    { key: 'off', header: 'Discount', cell: (k) => (k.kind === 'percent' ? `${k.value}% off` : `${formatUsd(k.value)} off`) },
    { key: 'used', header: 'Used', numeric: true, sort: (a, b) => a.redeemed - b.redeemed, cell: (k) => `${k.redeemed}${k.maxRedemptions ? ` of ${k.maxRedemptions}` : ''}` },
    { key: 'plans', header: 'For', cell: (k) => (k.planIds.length ? k.planIds.join(', ') : 'All plans') },
    { key: 'ends', header: 'Ends', cell: (k) => (k.expiresAt ? shortDate(k.expiresAt) : 'Never') },
    { key: 'status', header: 'Status', cell: (k) => (k.active ? <Badge tone="ok">On</Badge> : <Badge>Off</Badge>) },
    { key: 'do', header: 'Change', cell: (k) => <button type="button" className="ghost small" onClick={() => toggle(k)} aria-label={`${k.active ? 'Switch off' : 'Switch on'} code ${k.code}`}>{k.active ? 'Switch off' : 'Switch on'}</button> },
  ], []); // eslint-disable-line react-hooks/exhaustive-deps
  const valid = /^[A-Za-z0-9_-]{2,32}$/.test(c.code.trim()) && Number(c.value) > 0;
  return (
    <>
      <div data-testid="billing-coupons"><DataTable rows={data.coupons} columns={columns} rowKey={(k) => k.code} caption="Discount codes" searchLabel="Search codes" empty="No discount codes yet. Create one below." /></div>
      <section className="section" aria-labelledby="newcode">
        <h3 id="newcode">New discount code</h3>
        <div className="fields">
          <label>Code<input aria-label="New code" value={c.code} maxLength={32} onChange={(e) => setC({ ...c, code: e.target.value })} /></label>
          <label>Kind<select aria-label="Code kind" value={c.kind} onChange={(e) => setC({ ...c, kind: e.target.value })}><option value="percent">Percent off</option><option value="fixed">Dollars off</option></select></label>
          <label>Amount<input aria-label="Code amount" inputMode="decimal" value={c.value} onChange={(e) => setC({ ...c, value: e.target.value })} /></label>
          <label>Most uses (empty is no limit)<input aria-label="Code most uses" inputMode="numeric" value={c.max} onChange={(e) => setC({ ...c, max: e.target.value })} /></label>
          <label>Ends on (empty is never)<input aria-label="Code end date" type="date" value={c.expires} onChange={(e) => setC({ ...c, expires: e.target.value })} /></label>
          <label>Only for plan (empty is all)<input aria-label="Code plan" value={c.plan} onChange={(e) => setC({ ...c, plan: e.target.value })} /></label>
        </div>
        <p className="actions"><button type="button" disabled={!valid} onClick={() => ctx.ask({ title: 'Save discount code', effect: <>Code <strong>{c.code.trim()}</strong> works for new orders straight away.</>, confirmLabel: 'Save code', done: 'Code saved',
          run: async (reason) => { await billingApi.saveCoupon(ctx.session, c.code.trim(), { kind: c.kind, value: Number(c.value), planIds: c.plan.trim() ? [c.plan.trim()] : [], expiresAt: c.expires ? new Date(`${c.expires}T23:59:59+05:00`).toISOString() : null, maxRedemptions: numberOrNull(c.max), active: true, note: null, reason }); setC({ ...c, code: '' }); await after(); } })}>Save code</button></p>
      </section>
    </>
  );
}

function PaymentText({ ctx, data, after }: { ctx: Ctx; data: AdminBilling; after: () => Promise<void> }) {
  const [text, setText] = useState(data.paymentInstructions);
  return (
    <section className="section" aria-labelledby="pt">
      <h3 id="pt">How customers pay</h3>
      <p className="hint">Shown to a customer after they place an order: where to send the money and what to write in the reference.</p>
      <div className="fields"><label>Payment instructions<textarea rows={6} maxLength={1500} value={text} onChange={(e) => setText(e.target.value)} /></label></div>
      <p className="actions"><button type="button" disabled={text === data.paymentInstructions} onClick={() => ctx.ask({ title: 'Save payment instructions', effect: 'Customers see this text after placing an order.', confirmLabel: 'Save instructions', done: 'Instructions saved', run: async (reason) => { await billingApi.setInstructions(ctx.session, text, reason); await after(); } })}>Save instructions</button></p>
    </section>
  );
}
