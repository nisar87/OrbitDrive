import { useCallback, useEffect, useState } from 'react';
import { ApiError, type Session } from '../../api';
import {
  billingApi, formatPkr, formatUsd, limitText, monthsLabel, percentUsed, PAYMENT_METHODS, priceLabel,
  type AccountPlan, type Order, type PlansView, type Quote, type UsageDay,
} from '../../billing';
import { UsageTable } from './UsageTable';

interface Props { session: Session; onAuthLost: (error: unknown) => void }

const STATUS: Record<string, string> = {
  requested: 'Waiting for your payment', proof_submitted: 'Payment sent, being checked', verified: 'Paid and active', rejected: 'Not accepted', cancelled: 'Cancelled', expired: 'Expired (not paid in time)',
};

function Meter({ label, used, limit, text }: { label: string; used: number; limit: number | null; text: string }) {
  const percent = percentUsed(used, limit);
  return (
    <div className="meter">
      <label>{label}: <strong>{text}</strong>{percent !== null && <progress max={100} value={percent} aria-label={`${label} used`}>{percent}%</progress>}</label>
    </div>
  );
}

/** The person's plan, what they have used today, and the way to buy or extend a pass. Payment is made outside the app and checked by a person. */
export function MyPlan({ session, onAuthLost }: Props) {
  const [plan, setPlan] = useState<AccountPlan | null>(null);
  const [plans, setPlans] = useState<PlansView | null>(null);
  const [orders, setOrders] = useState<Order[]>([]);
  const [history, setHistory] = useState<UsageDay[]>([]);
  const [choice, setChoice] = useState({ planId: 'plus', months: 1, coupon: '' });
  const [quote, setQuote] = useState<Quote | null>(null);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [proof, setProof] = useState<Record<string, { method: string; reference: string }>>({});

  const fail = useCallback((e: unknown) => { onAuthLost(e); setMessage({ kind: 'error', text: e instanceof ApiError ? e.message : 'Something went wrong.' }); }, [onAuthLost]);
  const load = useCallback(async () => {
    try {
      const [p, o, list] = await Promise.all([billingApi.myPlan(session), billingApi.myOrders(session), billingApi.plans()]);
      setPlan(p); setOrders(o); setPlans(list);
      try { setHistory(await billingApi.myUsage(session)); } catch { setHistory([]); }   // the history is a convenience: the plan still shows without it
    } catch (e) { fail(e); }
  }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  // Ordering opens with the start date: the same switch as plan limits. Before it, nothing about buying is shown and the relay refuses orders anyway.
  const ordering = Boolean(plan?.limitsEnabled && plans?.limitsEnabled);
  const selectable = ordering ? plans?.plans.filter((p) => p.purchasable && p.prices.length > 0) ?? [] : [];
  const chosenPlan = selectable.find((p) => p.id === choice.planId) ?? selectable[0];
  const months = chosenPlan?.prices.some((x) => x.months === choice.months) ? choice.months : chosenPlan?.prices[0]?.months ?? 1;

  const price = async () => {
    if (!chosenPlan) return;
    try { setQuote(await billingApi.quote(session, chosenPlan.id, months, choice.coupon.trim() || null)); setMessage(null); } catch (e) { setQuote(null); fail(e); }
  };
  const place = async () => {
    if (!chosenPlan) return;
    try {
      await billingApi.order(session, chosenPlan.id, months, choice.coupon.trim() || null, null);
      setQuote(null); setChoice({ ...choice, coupon: '' }); setMessage({ kind: 'ok', text: 'Order created. Pay the amount below, then tell us how you paid.' }); await load();
    } catch (e) { fail(e); }
  };

  if (!plan) return <article className="panel" aria-labelledby="plan-title"><h2 id="plan-title">Your plan</h2>{message ? <p role="alert" className="notice error">{message.text}</p> : <p className="hint">Loading…</p>}</article>;
  const open = orders.filter((o) => o.status === 'requested' || o.status === 'proof_submitted');
  return (
    <article className="panel" aria-labelledby="plan-title" data-testid="my-plan">
      <h2 id="plan-title">Your plan</h2>
      <p data-testid="plan-name"><strong>{plan.planName}</strong>
        {plan.state === 'active' && plan.endsAt && <> · {plan.daysLeft} day{plan.daysLeft === 1 ? '' : 's'} left (until {new Date(plan.endsAt).toLocaleDateString()})</>}
        {plan.state === 'grace' && plan.graceEndsAt && <> · ended; your extra limits stay until {new Date(plan.graceEndsAt).toLocaleDateString()}</>}
        {plan.state === 'free' && <> · free for life</>}
      </p>
      {!plan.limitsEnabled && <p className="hint">Plan limits are not switched on yet, so nothing is held back today. These are the limits that will apply. Paid plans are not on sale yet.</p>}
      <Meter label="Files today" used={plan.filesToday} limit={plan.limits.filesPerDay} text={`${plan.filesToday} of ${limitText(plan.limits.filesPerDay, 'count')}`} />
      <Meter label="Data today" used={plan.bytesToday} limit={plan.limits.bytesPerDay} text={`${limitText(plan.bytesToday, 'bytes')} of ${limitText(plan.limits.bytesPerDay, 'bytes')}`} />
      <dl>
        <dt>Largest file</dt><dd>{limitText(plan.limits.bytesPerFile, 'bytes')}</dd>
        <dt>PCs</dt><dd>{plan.pcs} of {limitText(plan.limits.pcs, 'count')}</dd>
        <dt>Phones and browsers</dt><dd>{plan.devices} of {limitText(plan.limits.devices, 'count')}</dd>
      </dl>
      <p className="hint">The day resets at midnight Pakistan time ({new Date(plan.resetsAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} your time). A file counts when it has finished.</p>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={message.kind === 'error' ? 'notice error' : 'hint'}>{message.text}</p>}


      <h3>Your last 30 days</h3>
      <UsageTable history={history} caption="Your transfers" />

      {plans && plans.plans.length > 1 && (
        <>
          <h3>What each plan allows</h3>
          <table data-testid="plan-compare">
            <caption className="sr-only">Plan limits</caption>
            <thead><tr><th scope="col">Plan</th><th scope="col">PCs</th><th scope="col">Phones and browsers</th><th scope="col">Files a day</th><th scope="col">Largest file</th><th scope="col">Data a day</th></tr></thead>
            <tbody>
              {plans.plans.map((p) => (
                <tr key={p.id}>
                  <th scope="row">{p.name}{p.id === plan.planId ? ' (yours)' : ''}</th>
                  <td>{limitText(p.limits.pcs, 'count')}</td><td>{limitText(p.limits.devices, 'count')}</td><td>{limitText(p.limits.filesPerDay, 'count')}</td>
                  <td>{limitText(p.limits.bytesPerFile, 'bytes')}</td><td>{limitText(p.limits.bytesPerDay, 'bytes')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {selectable.length > 0 && (
        <>
          <h3>Buy or extend a pass</h3>
          <p className="hint">Passes are one-time and never renew by themselves. A pass keeps its price and end date. Buying the same plan again adds time after your pass; buying a higher plan starts now.</p>
          <label className="stack" htmlFor="buy-plan">Plan</label>
          <select id="buy-plan" value={chosenPlan?.id} onChange={(e) => { setChoice({ ...choice, planId: e.target.value }); setQuote(null); }}>
            {selectable.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <label className="stack" htmlFor="buy-months">Length</label>
          <select id="buy-months" value={months} onChange={(e) => { setChoice({ ...choice, months: Number(e.target.value) }); setQuote(null); }}>
            {chosenPlan?.prices.map((x) => <option key={x.months} value={x.months}>{monthsLabel(x.months)} — {priceLabel(x)}</option>)}
          </select>
          <label className="stack" htmlFor="buy-coupon">Discount code (optional)</label>
          <input id="buy-coupon" value={choice.coupon} autoComplete="off" maxLength={32} onChange={(e) => { setChoice({ ...choice, coupon: e.target.value }); setQuote(null); }} />
          <p className="actions"><button type="button" className="secondary" onClick={() => void price()}>See the price</button></p>
          {quote && (
            <div role="status" data-testid="quote">
              <p>{quote.planName}, {monthsLabel(quote.months)}: <strong>{formatUsd(quote.dueUsd)} ({formatPkr(quote.duePkr, true)})</strong>
                {quote.couponCode && <> — code {quote.couponCode} saves {formatUsd(quote.discountUsd)}</>}</p>
              <p className="hint">Payment is in Pakistani rupees. The rupee amount is fixed when you place the order.</p>
              <p className="actions"><button type="button" onClick={() => void place()}>Place order</button></p>
            </div>
          )}
        </>
      )}

      {ordering && open.map((order) => {
        const form = proof[order.id] ?? { method: order.method ?? 'jazzcash', reference: order.reference ?? '' };
        return (
          <section key={order.id} className="panel" aria-label={`Order ${order.id}`} data-testid="open-order">
            <h3>{STATUS[order.status]}</h3>
            <p>Pay <strong>{formatPkr(order.duePkr, false)}</strong> for {order.planId} · {monthsLabel(order.months)}{order.couponCode ? ` · code ${order.couponCode}` : ''}.</p>
            {plan.paymentInstructions
              ? <pre className="hint pre-wrap">{plan.paymentInstructions}</pre>
              : <p className="hint">Payment details are being set up. Contact support@myorbitdrive.app to pay.</p>}
            <label className="stack" htmlFor={`m-${order.id}`}>How did you pay?</label>
            <select id={`m-${order.id}`} value={form.method} onChange={(e) => setProof({ ...proof, [order.id]: { ...form, method: e.target.value } })}>
              {PAYMENT_METHODS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
            </select>
            <label className="stack" htmlFor={`r-${order.id}`}>Payment reference (transaction id)</label>
            <input id={`r-${order.id}`} value={form.reference} maxLength={80} autoComplete="off" onChange={(e) => setProof({ ...proof, [order.id]: { ...form, reference: e.target.value } })} />
            <p className="actions">
              <button type="button" disabled={form.reference.trim().length < 4} onClick={async () => { try { await billingApi.proof(session, order.id, form.method, form.reference); setMessage({ kind: 'ok', text: 'Thank you. We will check the payment and switch your pass on.' }); await load(); } catch (e) { fail(e); } }}>I have paid</button>
              <button type="button" className="ghost" onClick={async () => { try { await billingApi.cancel(session, order.id); await load(); } catch (e) { fail(e); } }}>Cancel this order</button>
            </p>
          </section>
        );
      })}

      {orders.some((o) => o.status !== 'requested' && o.status !== 'proof_submitted') && (
        <>
          <h3>Past orders</h3>
          <ul className="device-list">
            {orders.filter((o) => o.status !== 'requested' && o.status !== 'proof_submitted').map((o) => (
              <li key={o.id}><span><strong>{o.planId} · {monthsLabel(o.months)}</strong><small>{STATUS[o.status]} · {formatPkr(o.duePkr, false)} · {new Date(o.createdAt).toLocaleDateString()}</small></span></li>
            ))}
          </ul>
        </>
      )}
    </article>
  );
}
