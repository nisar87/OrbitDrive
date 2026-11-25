import { useCallback, useEffect, useState } from 'react';
import { ApiError, type Session } from '../../api';
import { billingApi, formatPkr, formatUsd, GIB, MIB, monthsLabel, type AdminBilling, type EditablePlan, type Order } from '../../billing';

interface Props { session: Session; onFail: (e: unknown) => void }

const numberOrNull = (text: string): number | null => (text.trim() === '' ? null : Number(text));

/**
 * The owner's billing tools: prices and limits, the exchange rate, payment instructions, coupons, orders waiting to be checked, and passes given by hand.
 * Every change asks for a reason and is written to the audit log by the service.
 */
export function BillingAdmin({ session, onFail }: Props) {
  const [data, setData] = useState<AdminBilling | null>(null);
  const [orders, setOrders] = useState<Order[]>([]);
  const [plans, setPlans] = useState<EditablePlan[]>([]);
  const [rate, setRate] = useState('');
  const [instructions, setInstructions] = useState('');
  const [reason, setReason] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [coupon, setCoupon] = useState({ code: '', kind: 'percent', value: '10', max: '', expires: '', plan: '' });
  const [grant, setGrant] = useState({ uid: '', planId: 'plus', months: '1', source: 'grant' as 'grant' | 'manual' });

  const fail = useCallback((e: unknown) => { if (e instanceof ApiError && e.code === 'unauthenticated') onFail(e); setError(e instanceof ApiError ? e.message : 'Something went wrong.'); }, [onFail]);
  const load = useCallback(async () => {
    try {
      const [billing, queue] = await Promise.all([billingApi.admin(session), billingApi.orders(session, null)]);
      setData(billing); setPlans(structuredClone(billing.editable)); setRate(String(billing.plans.pkrPerUsd)); setInstructions(billing.paymentInstructions);
      setOrders(queue.filter((o) => o.status === 'requested' || o.status === 'proof_submitted')); setError(null);
    } catch (e) { fail(e); }
  }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  const why = reason.trim();
  const ready = why.length >= 3;
  const act = async (label: string, work: () => Promise<unknown>) => {
    if (!ready) { setError('Write a short reason first (at least 3 characters). It goes in the audit log.'); return; }
    try { await work(); setNote(`${label}: done.`); setError(null); await load(); } catch (e) { fail(e); }
  };
  const setPlan = (id: string, change: (plan: EditablePlan) => void) => setPlans((all) => all.map((p) => { if (p.id !== id) return p; const copy = structuredClone(p); change(copy); return copy; }));

  if (!data) return <article className="panel" aria-labelledby="billing-title"><h2 id="billing-title">Plans and billing</h2>{error ? <p role="alert" className="notice error">{error}</p> : <p className="hint">Loading…</p>}</article>;
  return (
    <article className="panel" aria-labelledby="billing-title" data-testid="billing-admin">
      <h2 id="billing-title">Plans and billing</h2>
      <p className="hint">Prices are set in dollars; rupees follow the exchange rate unless you fix a rupee price. Every change below needs a reason.</p>
      <label className="stack" htmlFor="billing-reason">Reason for the next change</label>
      <input id="billing-reason" value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
      {error && <p role="alert" className="notice error" data-testid="billing-error">{error}</p>}
      {note && <p role="status" className="hint">{note}</p>}

      <h3>Plan limits</h3>
      <p>Limits are <strong>{data.plans.limitsEnabled ? 'switched on' : 'switched off'}</strong>. Usage is counted either way.</p>
      <p className="actions"><button type="button" onClick={() => void act(data.plans.limitsEnabled ? 'Limits off' : 'Limits on', () => billingApi.setLimits(session, !data.plans.limitsEnabled, why))}>
        {data.plans.limitsEnabled ? 'Switch limits off' : 'Switch limits on'}</button></p>

      <h3>Exchange rate</h3>
      <label className="stack" htmlFor="billing-rate">Rupees per dollar</label>
      <input id="billing-rate" inputMode="decimal" value={rate} onChange={(e) => setRate(e.target.value)} />
      <p className="hint">Set on {new Date(data.plans.rateUpdatedAt).toLocaleDateString()}. Rupee prices that are not fixed follow this rate and show as approximate. Orders already placed keep their price.</p>
      <p className="actions"><button type="button" onClick={() => void act('Rate saved', () => billingApi.setRate(session, Number(rate), why))}>Save rate</button></p>

      <h3>Prices and limits</h3>
      {plans.map((plan) => (
        <fieldset key={plan.id} data-testid={`plan-${plan.id}`}>
          <legend>{plan.name}{plan.purchasable ? '' : ' (not for sale)'}</legend>
          <div className="grid2">
            {([['pcs', 'PCs'], ['devices', 'Phones and browsers'], ['filesPerDay', 'Files a day']] as const).map(([key, label]) => (
              <label key={key}>{label}<input inputMode="numeric" placeholder="No limit" aria-label={`${plan.name} ${label}`} value={plan.limits[key] ?? ''} onChange={(e) => setPlan(plan.id, (p) => { p.limits[key] = numberOrNull(e.target.value); })} /></label>
            ))}
            <label>Largest file (MB)<input inputMode="numeric" placeholder="No limit" aria-label={`${plan.name} largest file in MB`} value={plan.limits.bytesPerFile === null ? '' : Math.round(plan.limits.bytesPerFile / MIB)}
              onChange={(e) => setPlan(plan.id, (p) => { const v = numberOrNull(e.target.value); p.limits.bytesPerFile = v === null ? null : v * MIB; })} /></label>
            <label>Data a day (GB)<input inputMode="decimal" placeholder="No limit" aria-label={`${plan.name} data a day in GB`} value={plan.limits.bytesPerDay === null ? '' : +(plan.limits.bytesPerDay / GIB).toFixed(2)}
              onChange={(e) => setPlan(plan.id, (p) => { const v = numberOrNull(e.target.value); p.limits.bytesPerDay = v === null ? null : Math.round(v * GIB); })} /></label>
          </div>
          {plan.prices.map((price, index) => (
            <p key={price.months} className="grid2">
              <label>{monthsLabel(price.months)} — dollars<input inputMode="decimal" aria-label={`${plan.name} ${monthsLabel(price.months)} dollars`} value={price.usd} onChange={(e) => setPlan(plan.id, (p) => { p.prices[index]!.usd = Number(e.target.value); })} /></label>
              <label>Fixed rupees (blank = follow rate)<input inputMode="numeric" aria-label={`${plan.name} ${monthsLabel(price.months)} rupees`} value={price.pkrOverride ?? ''} onChange={(e) => setPlan(plan.id, (p) => { p.prices[index]!.pkrOverride = numberOrNull(e.target.value); })} /></label>
            </p>
          ))}
        </fieldset>
      ))}
      <p className="actions"><button type="button" onClick={() => void act('Plans saved', () => billingApi.setPlans(session, plans, why))}>Save prices and limits</button></p>

      <h3>How customers pay</h3>
      <label className="stack" htmlFor="billing-instructions">Payment instructions (shown to a customer after they place an order)</label>
      <textarea id="billing-instructions" rows={5} maxLength={1500} value={instructions} onChange={(e) => setInstructions(e.target.value)} />
      <p className="actions"><button type="button" onClick={() => void act('Instructions saved', () => billingApi.setInstructions(session, instructions, why))}>Save instructions</button></p>

      <h3>Orders waiting ({orders.length})</h3>
      <ul className="device-list" data-testid="billing-orders">
        {orders.map((order) => (
          <li key={order.id}>
            <span><strong>{order.planId} · {monthsLabel(order.months)} · {formatPkr(order.duePkr, false)} ({formatUsd(order.dueUsd)})</strong>
              <small><bdi>{order.subject}</bdi> · {order.status === 'proof_submitted' ? `paid by ${order.method}, reference ${order.reference}` : 'not paid yet'}{order.couponCode ? ` · code ${order.couponCode}` : ''}</small></span>
            <span className="actions">
              <button type="button" onClick={() => void act('Order verified', () => billingApi.verify(session, order.id, why))}>Money received: activate</button>
              <button type="button" className="ghost" onClick={() => void act('Order rejected', () => billingApi.reject(session, order.id, why))}>Reject</button>
            </span>
          </li>
        ))}
      </ul>
      {orders.length === 0 && <p className="empty">No orders waiting.</p>}

      <h3>Discount codes</h3>
      <ul className="device-list" data-testid="billing-coupons">
        {data.coupons.map((c) => (
          <li key={c.code}><span><strong>{c.code}</strong> {c.kind === 'percent' ? `${c.value}% off` : `${formatUsd(c.value)} off`}{!c.active && ' (off)'}
            <small>used {c.redeemed}{c.maxRedemptions ? ` of ${c.maxRedemptions}` : ''}{c.expiresAt ? ` · ends ${new Date(c.expiresAt).toLocaleDateString()}` : ''}{c.planIds.length ? ` · ${c.planIds.join(', ')}` : ' · all plans'}</small></span>
            <span className="actions"><button type="button" className="ghost" onClick={() => void act(c.active ? 'Code switched off' : 'Code switched on', () => billingApi.saveCoupon(session, c.code, { kind: c.kind, value: c.value, planIds: c.planIds, expiresAt: c.expiresAt, maxRedemptions: c.maxRedemptions, active: !c.active, note: c.note, reason: why }))}>
              {c.active ? 'Switch off' : 'Switch on'}</button></span></li>
        ))}
      </ul>
      <div className="grid2">
        <label>Code<input aria-label="New code" value={coupon.code} maxLength={32} onChange={(e) => setCoupon({ ...coupon, code: e.target.value })} /></label>
        <label>Kind<select aria-label="Code kind" value={coupon.kind} onChange={(e) => setCoupon({ ...coupon, kind: e.target.value })}><option value="percent">Percent off</option><option value="fixed">Dollars off</option></select></label>
        <label>Amount<input aria-label="Code amount" inputMode="decimal" value={coupon.value} onChange={(e) => setCoupon({ ...coupon, value: e.target.value })} /></label>
        <label>Most uses (blank = no limit)<input aria-label="Code most uses" inputMode="numeric" value={coupon.max} onChange={(e) => setCoupon({ ...coupon, max: e.target.value })} /></label>
        <label>Ends on (blank = never)<input aria-label="Code end date" type="date" value={coupon.expires} onChange={(e) => setCoupon({ ...coupon, expires: e.target.value })} /></label>
        <label>Only for plan (blank = all)<input aria-label="Code plan" value={coupon.plan} onChange={(e) => setCoupon({ ...coupon, plan: e.target.value })} /></label>
      </div>
      <p className="actions"><button type="button" onClick={() => void act('Code saved', () => billingApi.saveCoupon(session, coupon.code, {
        kind: coupon.kind, value: Number(coupon.value), planIds: coupon.plan.trim() ? [coupon.plan.trim()] : [], expiresAt: coupon.expires ? new Date(`${coupon.expires}T23:59:59+05:00`).toISOString() : null,
        maxRedemptions: numberOrNull(coupon.max), active: true, note: null, reason: why }))}>Save code</button></p>

      <h3>Give a pass by hand</h3>
      <p className="hint">For a payment you checked yourself, a gift, or a partner. Use the account id shown in the accounts list below.</p>
      <div className="grid2">
        <label>Account id<input aria-label="Account id for pass" value={grant.uid} onChange={(e) => setGrant({ ...grant, uid: e.target.value })} /></label>
        <label>Plan<select aria-label="Pass plan" value={grant.planId} onChange={(e) => setGrant({ ...grant, planId: e.target.value })}>{plans.filter((p) => p.purchasable).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        <label>Months<input aria-label="Pass months" inputMode="numeric" value={grant.months} onChange={(e) => setGrant({ ...grant, months: e.target.value })} /></label>
        <label>Kind<select aria-label="Pass kind" value={grant.source} onChange={(e) => setGrant({ ...grant, source: e.target.value as 'grant' | 'manual' })}><option value="grant">Gift / grant</option><option value="manual">Paid by hand</option></select></label>
      </div>
      <p className="actions"><button type="button" onClick={() => void act('Pass added', () => billingApi.addPass(session, grant.uid.trim(), grant.planId, Number(grant.months), why, grant.source))}>Add pass</button></p>
    </article>
  );
}
