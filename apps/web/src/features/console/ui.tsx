import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import './kit.css';

/** Small building blocks of the operator console. Nothing here knows about accounts or money; pages compose these. */

export type Tone = 'ok' | 'wait' | 'bad' | 'plain';
export function Badge({ tone = 'plain', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>;
}

export function PageHead({ title, summary, actions }: { title: string; summary?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="page-head">
      <div><h2>{title}</h2>{summary && <p className="hint">{summary}</p>}</div>
      {actions && <div className="actions">{actions}</div>}
    </header>
  );
}

export function Kpi({ label, value, note, testId }: { label: string; value: ReactNode; note?: ReactNode; testId?: string }) {
  return <div className="kpi"><dt>{label}</dt><dd data-testid={testId}>{value}</dd>{note && <dd className="kpi-note">{note}</dd>}</div>;
}

export interface Tab { id: string; label: string; badge?: number }
/** Tabs with arrow-key movement. The caller renders the panel for `active` inside a role="tabpanel" region. */
export function Tabs({ tabs, active, onChange, label }: { tabs: Tab[]; active: string; onChange: (id: string) => void; label: string }) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const move = (from: number, step: number) => { const next = (from + step + tabs.length) % tabs.length; onChange(tabs[next]!.id); refs.current[next]?.focus(); };
  return (
    <div role="tablist" aria-label={label} className="tabs">
      {tabs.map((t, i) => (
        <button key={t.id} ref={(el) => { refs.current[i] = el; }} type="button" role="tab" id={`tab-${t.id}`} aria-selected={t.id === active} aria-controls={`panel-${t.id}`} tabIndex={t.id === active ? 0 : -1}
          className="tab" onClick={() => onChange(t.id)}
          onKeyDown={(e) => { if (e.key === 'ArrowRight') { e.preventDefault(); move(i, 1); } else if (e.key === 'ArrowLeft') { e.preventDefault(); move(i, -1); } else if (e.key === 'Home') { e.preventDefault(); move(i, -i); } else if (e.key === 'End') { e.preventDefault(); move(i, tabs.length - 1 - i); } }}>
          {t.label}{t.badge ? <span className="count" aria-label={`${t.badge} waiting`}>{t.badge}</span> : null}
        </button>
      ))}
    </div>
  );
}
export function TabPanel({ id, children }: { id: string; children: ReactNode }) {
  return <div role="tabpanel" id={`panel-${id}`} aria-labelledby={`tab-${id}`} className="tabpanel">{children}</div>;
}

export interface Column<T> { key: string; header: string; cell: (row: T) => ReactNode; sort?: (a: T, b: T) => number; numeric?: boolean; text?: (row: T) => string }
export interface Filter<T> { label: string; options: { value: string; label: string }[]; test: (row: T, value: string) => boolean }

/** A real table: search over the columns that give text, filters, click-to-sort headers with aria-sort, and paging. Rows are opened with a button in the first column. */
export function DataTable<T>({ rows, columns, rowKey, caption, searchLabel, filters = [], pageSize = 25, empty, testId, initialSort, bare }: {
  rows: T[]; columns: Column<T>[]; rowKey: (row: T) => string; caption: string; searchLabel: string; filters?: Filter<T>[]; pageSize?: number; empty: ReactNode; testId?: string; initialSort?: { key: string; dir: 'asc' | 'desc' };
  /** No search box or filters, for short lists where they would only be noise. */
  bare?: boolean;
}) {
  const [query, setQuery] = useState('');
  const [chosen, setChosen] = useState<Record<number, string>>({});
  const [sort, setSort] = useState<{ key: string; dir: 'asc' | 'desc' } | null>(initialSort ?? null);
  const [page, setPage] = useState(0);
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    let out = rows.filter((r) => (q === '' || columns.some((c) => c.text?.(r).toLowerCase().includes(q))) && filters.every((f, i) => !chosen[i] || f.test(r, chosen[i]!)));
    const col = sort && columns.find((c) => c.key === sort.key);
    if (col?.sort) { const sign = sort!.dir === 'asc' ? 1 : -1; out = [...out].sort((a, b) => sign * col.sort!(a, b)); }
    return out;
  }, [rows, columns, filters, query, chosen, sort]);
  const pages = Math.max(1, Math.ceil(shown.length / pageSize));
  const at = Math.min(page, pages - 1);
  const slice = shown.slice(at * pageSize, at * pageSize + pageSize);
  const id = useId();
  return (
    <div className="data" data-testid={testId}>
      {!bare && <div className="toolbar">
        <label className="search"><span className="sr-only">{searchLabel}</span>
          <input type="search" placeholder={searchLabel} value={query} onChange={(e) => { setQuery(e.target.value); setPage(0); }} /></label>
        {filters.map((f, i) => (
          <label key={f.label} className="filter"><span className="sr-only">{f.label}</span>
            <select aria-label={f.label} value={chosen[i] ?? ''} onChange={(e) => { setChosen({ ...chosen, [i]: e.target.value }); setPage(0); }}>
              <option value="">{f.label}: all</option>{f.options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select></label>
        ))}
        <span className="found" role="status">{shown.length === rows.length ? `${rows.length} in total` : `${shown.length} of ${rows.length} shown`}</span>
      </div>}
      {rows.length === 0 ? <p className="empty">{empty}</p> : shown.length === 0 ? <p className="empty">Nothing matches. Clear the search or the filters.</p> : (
        <div className="scroll" role="region" aria-label={`${caption} table`} tabIndex={0}>
          <table className="grid" aria-describedby={id}>
            <caption className="sr-only" id={id}>{caption}</caption>
            <thead><tr>{columns.map((c) => (
              <th key={c.key} scope="col" className={c.numeric ? 'num' : undefined} aria-sort={sort?.key === c.key ? (sort.dir === 'asc' ? 'ascending' : 'descending') : c.sort ? 'none' : undefined}>
                {c.sort ? <button type="button" className="sorter" onClick={() => setSort(sort?.key === c.key && sort.dir === 'asc' ? { key: c.key, dir: 'desc' } : { key: c.key, dir: 'asc' })}>{c.header}<span aria-hidden="true">{sort?.key === c.key ? (sort.dir === 'asc' ? ' ▲' : ' ▼') : ''}</span></button> : c.header}
              </th>))}</tr></thead>
            <tbody>{slice.map((r) => <tr key={rowKey(r)}>{columns.map((c, i) => i === 0 ? <th key={c.key} scope="row">{c.cell(r)}</th> : <td key={c.key} className={c.numeric ? 'num' : undefined}>{c.cell(r)}</td>)}</tr>)}</tbody>
          </table>
        </div>
      )}
      {pages > 1 && (
        <nav className="pager" aria-label={`${caption} pages`}>
          <button type="button" className="ghost" disabled={at === 0} onClick={() => setPage(at - 1)}>Previous</button>
          <span>Page {at + 1} of {pages}</span>
          <button type="button" className="ghost" disabled={at >= pages - 1} onClick={() => setPage(at + 1)}>Next</button>
        </nav>
      )}
    </div>
  );
}

/** Keeps Tab inside `box` while it is open, closes on Escape and returns focus to what opened it. */
const openModals: symbol[] = [];
function useModal(open: boolean, onClose: () => void) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return undefined;
    const me = Symbol('modal');
    openModals.push(me);
    const before = document.activeElement as HTMLElement | null;
    const el = box.current;
    const focusables = () => [...(el?.querySelectorAll<HTMLElement>('button:not([disabled]), input, select, textarea, [href], [tabindex]:not([tabindex="-1"])') ?? [])];
    (el?.querySelector<HTMLElement>('[data-first]') ?? focusables()[0])?.focus();
    const key = (e: KeyboardEvent) => {
      if (openModals[openModals.length - 1] !== me) return;   // only the top-most dialog reacts, so Escape in a confirmation does not also close the drawer behind it
      if (e.key === 'Escape') { e.stopPropagation(); onClose(); return; }
      if (e.key !== 'Tab') return;
      const list = focusables(); if (list.length === 0) return;
      const first = list[0]!; const last = list[list.length - 1]!;
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); } else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', key, true);
    return () => { document.removeEventListener('keydown', key, true); openModals.splice(openModals.indexOf(me), 1); before?.focus?.(); };
  }, [open, onClose]);
  return box;
}

/** Overlays are drawn on the page itself, not inside the console: the console is a scrolling container (it uses container queries, which make a fixed child scroll with it), so a drawer inside it would drift away when the console is scrolled. */
function Overlay({ className, onClose, children }: { className: string; onClose: () => void; children: ReactNode }) {
  return createPortal(<div className="kit"><div className={className} onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>{children}</div></div>, document.body);
}

/** Side panel for one record. Modal, so the page behind it cannot be reached by keyboard until it is closed. */
export function Drawer({ title, subtitle, onClose, children, testId }: { title: ReactNode; subtitle?: ReactNode; onClose: () => void; children: ReactNode; testId?: string }) {
  const box = useModal(true, onClose);
  const titleId = useId();
  return (
    <Overlay className="overlay" onClose={onClose}>
      <div className="drawer" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={box} data-testid={testId}>
        <header className="drawer-head">
          <div><h2 id={titleId}>{title}</h2>{subtitle && <p className="hint">{subtitle}</p>}</div>
          <button type="button" className="ghost" onClick={onClose} data-first>Close</button>
        </header>
        <div className="drawer-body">{children}</div>
      </div>
    </Overlay>
  );
}

/**
 * The one way to do anything with consequences: says what will happen, asks for a reason (at least 3 characters, same as the service),
 * and keeps the safe choice, Cancel, as the first focus. The service records the reason in the audit log.
 */
export function ReasonDialog({ title, effect, confirmLabel, danger, onConfirm, onCancel, busy }: {
  title: string; effect: ReactNode; confirmLabel: string; danger?: boolean; onConfirm: (reason: string) => void; onCancel: () => void; busy?: boolean;
}) {
  const [reason, setReason] = useState('');
  const box = useModal(true, onCancel);
  const titleId = useId(); const fieldId = useId();
  return (
    <Overlay className="overlay top" onClose={onCancel}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby={titleId} data-testid="admin-confirm" onSubmit={(e) => { e.preventDefault(); if (reason.trim().length >= 3) onConfirm(reason.trim()); }}
        ref={box as unknown as React.RefObject<HTMLFormElement>}>
        <h2 id={titleId}>{title}</h2>
        <p>{effect}</p>
        <label htmlFor={fieldId}>Reason (required)</label>
        <input id={fieldId} value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} autoComplete="off" />
        <p className="hint">Saved in the audit log with your account id.</p>
        <p className="actions">
          <button type="button" className="ghost" onClick={onCancel} data-first>Cancel</button>
          <button type="submit" className={danger ? 'danger-solid' : undefined} disabled={busy || reason.trim().length < 3}>{confirmLabel}</button>
        </p>
      </form>
    </Overlay>
  );
}

/** The same dialog without a reason, for a person managing their own organisation. Cancel is the first focus. */
export function ConfirmDialog({ title, effect, confirmLabel, danger, onConfirm, onCancel, busy }: { title: string; effect: ReactNode; confirmLabel: string; danger?: boolean; onConfirm: () => void; onCancel: () => void; busy?: boolean }) {
  const box = useModal(true, onCancel);
  const titleId = useId();
  return (
    <Overlay className="overlay top" onClose={onCancel}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby={titleId} onSubmit={(e) => { e.preventDefault(); onConfirm(); }} ref={box as unknown as React.RefObject<HTMLFormElement>}>
        <h2 id={titleId}>{title}</h2>
        <p>{effect}</p>
        <p className="actions">
          <button type="button" className="ghost" onClick={onCancel} data-first>Cancel</button>
          <button type="submit" className={danger ? 'danger-solid' : undefined} disabled={busy}>{confirmLabel}</button>
        </p>
      </form>
    </Overlay>
  );
}

export function CopyId({ value, label }: { value: string; label: string }) {
  const [done, setDone] = useState(false);
  return <button type="button" className="ghost small" aria-label={`Copy ${label}`} onClick={() => { void navigator.clipboard?.writeText(value).then(() => { setDone(true); setTimeout(() => setDone(false), 1500); }, () => undefined); }}>{done ? 'Copied' : 'Copy'}</button>;
}
