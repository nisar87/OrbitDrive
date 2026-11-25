import type { AdminOverview } from '../../protocol';
import { formatBytes, formatTime } from '../../format';
import { Kpi, PageHead } from './ui';
import { plural, type PageId } from './shared';

/** The first screen: what needs the operator, then the numbers that say whether the service is healthy. */
export function Overview({ overview, waiting, unpaid, go, refresh }: { overview: AdminOverview | null; waiting: number; unpaid: number; go: (p: PageId) => void; refresh: () => void }) {
  if (!overview) return <><PageHead title="Overview" /><p className="empty" role="status">Loading…</p></>;
  const items: { key: string; text: string; action: string; to: PageId; tone?: 'bad' }[] = [];
  if (waiting > 0) items.push({ key: 'orders', text: `${plural(waiting, 'order')} paid and waiting for you to check the money`, action: 'Review orders', to: 'billing' });
  if (overview.costAlert) items.push({ key: 'cost', text: `Relayed data today is over the daily alert (${formatBytes(overview.relayBytesToday)} of ${formatBytes(overview.relayBytesDailyAlert)})`, action: 'See accounts', to: 'accounts', tone: 'bad' });
  if (overview.suspendedUsers > 0) items.push({ key: 'suspended', text: `${plural(overview.suspendedUsers, 'account')} suspended`, action: 'View accounts', to: 'accounts' });
  if (Number(overview.quotaRefusals) > 0) items.push({ key: 'refused', text: `${plural(Number(overview.quotaRefusals), 'transfer')} refused for the daily allowance since the relay started`, action: 'See accounts', to: 'accounts' });
  return (
    <>
      <PageHead title="Overview" summary={<>Relay version {overview.gatewayVersion}, running since {formatTime(overview.startedUnixMs)}. Counts restart when the relay restarts.</>}
        actions={<button type="button" className="ghost" onClick={refresh}>Refresh</button>} />
      <section className="attention" aria-labelledby="needs">
        <h3 id="needs">Needs attention</h3>
        {items.length === 0 ? <p className="none" role="status">Nothing is waiting for you.</p> : (
          <ul data-testid="admin-attention">{items.map((i) => <li key={i.key}><span>{i.text}</span><button type="button" className={i.tone === 'bad' ? '' : 'ghost'} onClick={() => go(i.to)}>{i.action}</button></li>)}</ul>
        )}
        {unpaid > 0 && waiting === 0 && <p className="hint none">{plural(unpaid, 'order')} placed but not paid yet. Nothing to do until a payment reference arrives.</p>}
      </section>
      <dl className="kpis" data-testid="admin-metrics">
        <Kpi label="Accounts" value={overview.users} testId="m-users" note={overview.suspendedUsers > 0 ? `${overview.suspendedUsers} suspended` : 'none suspended'} />
        <Kpi label="PCs online" value={`${overview.pcsOnline} of ${overview.pcs}`} testId="m-pcs" note={`${overview.browserDevices} approved browsers and phones`} />
        <Kpi label="Transfers running" value={overview.activeStreams} note={`${overview.downloadsFailed} downloads failed so far`} />
        <Kpi label="Relayed today" value={formatBytes(overview.relayBytesToday)} testId="m-today" note={overview.relayBytesPerOwnerPerDay === '0' ? 'No limit per account' : `${formatBytes(overview.relayBytesPerOwnerPerDay)} allowed per account`} />
      </dl>
      <details className="more">
        <summary>More about the relay</summary>
        <dl>
          <dt>Sent to browsers</dt><dd data-testid="m-down">{formatBytes(overview.relayBytesDown)}</dd>
          <dt>Received from browsers</dt><dd>{formatBytes(overview.relayBytesUp)}</dd>
          <dt>Downloads completed or failed</dt><dd>{overview.downloadsCompleted} / {overview.downloadsFailed}</dd>
          <dt>Upload pieces stored or refused</dt><dd>{overview.uploadPiecesRelayed} / {overview.uploadPiecesFailed}</dd>
          <dt>Refused for daily allowance</dt><dd>{overview.quotaRefusals}</dd>
          <dt>Oldest PC version allowed</dt><dd>{overview.minAgentVersion || 'No minimum'}</dd>
        </dl>
      </details>
    </>
  );
}
