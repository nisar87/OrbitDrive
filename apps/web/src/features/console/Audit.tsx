import { useMemo } from 'react';
import type { AuditEntry } from '../../protocol';
import { formatTime } from '../../format';
import { DataTable, PageHead, type Column } from './ui';

/** Everything an operator has changed, newest first. Read-only: there is no way to edit or remove an entry from here. */
export function Audit({ entries }: { entries: AuditEntry[] }) {
  const columns = useMemo<Column<AuditEntry>[]>(() => [
    { key: 'when', header: 'When', text: (e) => formatTime(e.atUnixMs), cell: (e) => formatTime(e.atUnixMs) },
    { key: 'action', header: 'Action', text: (e) => e.action, sort: (a, b) => a.action.localeCompare(b.action), cell: (e) => <strong>{e.action}</strong> },
    { key: 'target', header: 'Target', text: (e) => e.target, cell: (e) => <bdi>{e.target}</bdi> },
    { key: 'by', header: 'By', text: (e) => e.operator, cell: (e) => <bdi>{e.operator}</bdi> },
    { key: 'why', header: 'Reason', text: (e) => e.detail, cell: (e) => e.detail },
  ], []);
  return (
    <div data-testid="admin-audit">
      <PageHead title="Audit log" summary="Every change an operator makes, with the reason given. Nothing here can be edited." />
      <DataTable rows={entries} columns={columns} rowKey={(e) => `${e.atUnixMs}-${e.action}-${e.target}-${e.operator}`} caption="Audit log" searchLabel="Search the log" pageSize={50} empty="Nothing has been done yet." />
    </div>
  );
}
