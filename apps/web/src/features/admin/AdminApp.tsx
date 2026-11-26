import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, ApiError, type Session } from '../../api';
import type { AdminOverview, AuditEntry } from '../../protocol';
import { adminApi, billingApi, type AdminAccountRow, type AdminOrgRow, type Order } from '../../billing';
import { Accounts } from '../console/Accounts';
import { Audit } from '../console/Audit';
import { buildPeople } from '../console/people';
import { Billing } from '../console/Billing';
import { Orgs } from '../console/Orgs';
import { Overview } from '../console/Overview';
import { ServiceSettings } from '../console/Settings';
import { ReasonDialog } from '../console/ui';
import type { AskSpec, Ctx, PageId } from '../console/shared';
import '../console/console.css';

interface Props { session: Session; onSignOut: () => void }

const PAGES: { id: PageId; label: string }[] = [
  { id: 'overview', label: 'Overview' }, { id: 'accounts', label: 'Accounts' }, { id: 'orgs', label: 'Organisations' },
  { id: 'billing', label: 'Billing' }, { id: 'audit', label: 'Audit log' }, { id: 'settings', label: 'Settings' },
];

/**
 * Operator console. It is only offered to accounts the service itself says are operators, and the service checks again on every request, so
 * reaching this page by any other route shows nothing. Every number is measured by the relay; there is no file, folder, path or
 * user-impersonation view anywhere in it, and every change asks for a reason that goes in the audit log.
 */
export default function AdminApp({ session, onSignOut }: Props) {
  const [page, setPage] = useState<PageId>('overview');
  const [overview, setOverview] = useState<AdminOverview | null>(null);
  const [accounts, setAccounts] = useState<AdminAccountRow[]>([]);
  const [orgs, setOrgs] = useState<AdminOrgRow[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [directory, setDirectory] = useState<{ uid: string; email: string; name: string | null }[]>([]);
  const [audit, setAudit] = useState<AuditEntry[]>([]);
  const [minVersion, setMinVersion] = useState('');
  const [error, setError] = useState<{ text: string; signInAgain: boolean } | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [asking, setAsking] = useState<AskSpec | null>(null);
  const [busy, setBusy] = useState(false);

  const fail = useCallback((e: unknown) => setError({ text: e instanceof ApiError ? e.message : 'Something went wrong.', signInAgain: e instanceof ApiError && e.code === 'unauthenticated' }), []);
  const load = useCallback(async () => {
    const [o, users, rich, a, s, org, queue, people] = await Promise.allSettled([
      api.adminOverview(session), api.adminUsers(session), adminApi.accounts(session), api.adminAudit(session), api.adminSettings(session), adminApi.orgs(session), billingApi.orders(session, null), adminApi.directory(session),
    ]);
    const problems = [o, users, a, s].filter((r) => r.status === 'rejected') as PromiseRejectedResult[];
    if (o.status === 'fulfilled') setOverview(o.value);
    if (rich.status === 'fulfilled') setAccounts(rich.value);
    else if (users.status === 'fulfilled') setAccounts(users.value.users.map((u) => ({ uid: u.uid, suspended: u.suspended, planId: '', planName: 'Unknown', state: 'free' as const, endsAt: null, orgName: null, pcs: u.pcs, devices: u.devices, filesToday: 0, bytesToday: 0, email: null, name: null })));
    if (a.status === 'fulfilled') setAudit([...a.value.entries]);
    if (s.status === 'fulfilled') setMinVersion(s.value.minAgentVersion);
    if (org.status === 'fulfilled') setOrgs(org.value);
    if (queue.status === 'fulfilled') setOrders(queue.value);
    if (people.status === 'fulfilled') setDirectory(people.value.people);   // a failure here only means accounts show by id until the next refresh
    const first = problems[0]?.reason ?? (rich.status === 'rejected' && users.status === 'rejected' ? rich.reason : undefined);
    if (first) fail(first); else setError(null);
  }, [session, fail]);
  useEffect(() => { void load(); }, [load]);

  const confirm = async (reason: string) => {
    if (!asking) return;
    setBusy(true);
    try { await asking.run(reason); setNote(`${asking.done}. It is recorded in the audit log.`); setError(null); setAsking(null); await load(); }
    catch (e) { setAsking(null); fail(e); }
    finally { setBusy(false); }
  };
  const peopleMap = useMemo(() => buildPeople(directory, accounts), [directory, accounts]);
  const ctx: Ctx = useMemo(() => ({ session, people: peopleMap, ask: setAsking, report: (kind, text) => { if (kind === 'ok') { setNote(text); setError(null); } else setError({ text, signInAgain: false }); }, fail, reload: load, go: setPage }), [session, peopleMap, fail, load]);

  const waiting = orders.filter((o) => o.status === 'proof_submitted').length;
  const unpaid = orders.filter((o) => o.status === 'requested').length;
  const badge = (id: PageId): number => (id === 'billing' ? waiting : 0);
  return (
    <div className="con-box" data-testid="admin-app"><div className="con kit admin">
      <nav className="con-nav" aria-label="Console sections">
        <h1>Console</h1>
        {PAGES.map((p) => (
          <button key={p.id} type="button" aria-current={page === p.id ? 'page' : undefined} onClick={() => { setPage(p.id); setNote(null); }}>
            {p.label}{badge(p.id) > 0 && <span className="count" aria-label={`${badge(p.id)} waiting`}>{badge(p.id)}</span>}
          </button>
        ))}
      </nav>
      <main className="con-main" aria-label={PAGES.find((p) => p.id === page)?.label}>
        {error && <p role="alert" className="notice error toast" data-testid="admin-error">{error.text} {error.signInAgain && <button type="button" onClick={onSignOut}>Sign out and sign in again</button>}</p>}
        {note && <p role="status" className="notice toast" data-testid="admin-note">{note}</p>}
        {page === 'overview' && <Overview overview={overview} waiting={waiting} unpaid={unpaid} go={setPage} refresh={() => void load()} people={peopleMap} accounts={accounts} orders={orders} />}
        {page === 'accounts' && <Accounts ctx={ctx} rows={accounts} />}
        {page === 'orgs' && <Orgs ctx={ctx} rows={orgs} />}
        {page === 'billing' && <Billing ctx={ctx} orders={orders} waiting={waiting} />}
        {page === 'audit' && <Audit entries={audit} people={peopleMap} />}
        {page === 'settings' && <ServiceSettings key={minVersion} ctx={ctx} initial={minVersion} overview={overview} />}
      </main>
      {asking && <ReasonDialog title={asking.title} effect={asking.effect} confirmLabel={asking.confirmLabel} danger={asking.danger} busy={busy} onConfirm={(r) => void confirm(r)} onCancel={() => setAsking(null)} />}
    </div></div>
  );
}
