import { useMemo } from 'react';
import type { AuditEntry } from '../../protocol';
import { formatTime } from '../../format';
import { DataTable, PageHead, type Column } from './ui';
import { lookup, shortUid, uidOfSubject, type People } from './people';

/**
 * Names the account in an audit entry: the email stored with the entry (as it was when the change was made, so it stays readable after the account is gone), else the
 * current email from the directory, else the plain id. Targets that are not accounts (an organisation id, "service", a pass id) are shown as they are.
 */
function entryWho(stored: string | null | undefined, raw: string, people: People): { email: string | null; id: string } {
  const uid = uidOfSubject(raw) ?? raw;
  return { email: stored || lookup(people, uid).email, id: uid };
}
function WhoText({ w }: { w: { email: string | null; id: string } }) {
  return w.email ? <span className="who stacked"><bdi className="who-email">{w.email}</bdi><bdi className="who-id idtext" title={w.id}>{shortUid(w.id)}</bdi></span> : <bdi title={w.id}>{w.id}</bdi>;
}

/** Everything an operator has changed, newest first. Read-only: there is no way to edit or remove an entry from here. */
export function Audit({ entries, people }: { entries: AuditEntry[]; people: People }) {
  const columns = useMemo<Column<AuditEntry>[]>(() => [
    { key: 'when', header: 'When', text: (e) => formatTime(e.atUnixMs), cell: (e) => formatTime(e.atUnixMs) },
    { key: 'action', header: 'Action', text: (e) => e.action, sort: (a, b) => a.action.localeCompare(b.action), cell: (e) => <strong>{e.action}</strong> },
    { key: 'target', header: 'Target', text: (e) => { const w = entryWho(e.targetEmail, e.target, people); return `${w.email ?? ''} ${e.target}`; }, cell: (e) => <WhoText w={entryWho(e.targetEmail, e.target, people)} /> },
    { key: 'by', header: 'By', text: (e) => { const w = entryWho(e.operatorEmail, e.operator, people); return `${w.email ?? ''} ${e.operator}`; }, cell: (e) => <WhoText w={entryWho(e.operatorEmail, e.operator, people)} /> },
    { key: 'why', header: 'Reason', text: (e) => e.detail, cell: (e) => e.detail },
  ], [people]);
  return (
    <div data-testid="admin-audit">
      <PageHead title="Audit log" summary="Every change an operator makes, with the reason given. Each entry keeps the email address as it was at the time, so it stays readable after an account is deleted. Nothing here can be edited." />
      <DataTable rows={entries} columns={columns} rowKey={(e) => `${e.atUnixMs}-${e.action}-${e.target}-${e.operator}`} caption="Audit log" searchLabel="Search the log by email, account id, action or reason" pageSize={50} empty="Nothing has been done yet." />
    </div>
  );
}
