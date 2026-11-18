import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import type { Session } from '../../api';
import type { PcSummary, RootSummary, SearchHit } from '../../protocol';
import { formatBytes, formatTime } from '../../format';
import { describeOutcome, isComplete, startSearch, type SearchRun, type SearchScope } from './search';

export type ScopeChoice = 'folder' | 'root' | 'pc' | 'all';
export interface SearchHere { pc: PcSummary; root: RootSummary; entryId: string | null; folderName: string }
export interface OpenHit { pc: PcSummary; hit: SearchHit }

interface Props {
  session: Session;
  pcs: PcSummary[];
  here: SearchHere | null;
  onClose: () => void;
  onOpenFolder: (target: OpenHit) => void;
  onOpenFile: (target: OpenHit) => void;
}

/** Filename search. Results are grouped per PC, each hit names its PC and folder, and nothing unavailable is passed off as "no results". */
export function SearchPanel({ session, pcs, here, onClose, onOpenFolder, onOpenFile }: Props) {
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<ScopeChoice>(here ? (here.entryId ? 'folder' : 'root') : 'all');
  const [run, setRun] = useState<SearchRun | null>(null);
  const [asked, setAsked] = useState('');
  const handle = useRef<ReturnType<typeof startSearch> | null>(null);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { input.current?.focus(); return () => handle.current?.cancel(); }, []);

  const scopes = useMemo<SearchScope[]>(() => {
    if (scope === 'all' || !here) return pcs.map((pc) => ({ pc }));
    if (scope === 'pc') return [{ pc: here.pc }];
    if (scope === 'root') return [{ pc: here.pc, rootId: here.root.rootId }];
    return [{ pc: here.pc, rootId: here.root.rootId, entryId: here.entryId }];
  }, [scope, here, pcs]);

  function submit(event: FormEvent) {
    event.preventDefault();
    const text = query.trim();
    if (!text) return;
    handle.current?.cancel();
    setAsked(text);
    handle.current = startSearch(session, text, scopes, setRun);
  }
  const searching = run !== null && !run.finished;
  const total = run?.outcomes.reduce((n, o) => n + o.hits.length, 0) ?? 0;

  return (
    <section className="search-panel" role="search" aria-label="Search by file name">
      <form onSubmit={submit} className="search-form">
        <input ref={input} type="search" aria-label="Search by file name" placeholder="Search by file name" value={query} maxLength={100} onChange={(e) => setQuery(e.target.value)} />
        <label className="inline"><span className="label-text">Where</span>
          <select aria-label="Where to search" value={scope} onChange={(e) => setScope(e.target.value as ScopeChoice)}>
            {here && <option value="folder" disabled={here.entryId === null}>This folder ({here.folderName})</option>}
            {here && <option value="root">Shared folder {here.root.name}</option>}
            {here && <option value="pc">All of {here.pc.name}</option>}
            <option value="all">All PCs</option>
          </select>
        </label>
        {searching ? <button type="button" onClick={() => handle.current?.cancel()}>Stop</button> : <button type="submit" disabled={query.trim().length === 0}>Search</button>}
        <button type="button" className="ghost" onClick={onClose}>Close search</button>
      </form>

      <div aria-live="polite" className="search-results">
        {run && (
          <>
            {run.outcomes.map((o) => {
              const note = describeOutcome(o);
              return (
                <div key={o.pcId} className="search-pc" data-testid="search-pc">
                  <h3><bdi>{o.pcName}</bdi> <small>{o.state === 'searching' ? 'Searching…' : o.state === 'done' ? `${o.hits.length} match${o.hits.length === 1 ? '' : 'es'}` : o.state === 'offline' ? 'Offline' : o.state === 'cancelled' ? 'Stopped' : 'Problem'}</small></h3>
                  {note && <p className={o.state === 'done' ? 'notice' : 'notice error'} role={o.state === 'done' ? 'status' : 'alert'}>{note}</p>}
                  {o.hits.length > 0 && (
                    <ul className="hits">
                      {o.hits.map((hit) => {
                        const pc = pcs.find((p) => p.pcId === o.pcId)!;
                        const folder = [hit.rootName, ...hit.path].join(' › ');
                        const directory = hit.entry.kind === 'directory';
                        return (
                          <li key={hit.entry.entryId}>
                            <button type="button" className="hit" onClick={() => (directory ? onOpenFolder({ pc, hit }) : onOpenFile({ pc, hit }))} aria-label={`${directory ? 'Open folder' : 'Open'} ${hit.entry.name} in ${folder} on ${o.pcName}`}>
                              <span className={`icon ${directory ? 'folder' : 'file'}`} aria-hidden="true" />
                              <span className="hit-main"><bdi>{hit.entry.name}</bdi><small><bdi>{o.pcName} › {folder}</bdi></small></span>
                              <span className="hit-meta">{directory ? '' : formatBytes(hit.entry.size)} {formatTime(hit.entry.modifiedUnixMs)}</span>
                            </button>
                            {!directory && hit.parentEntryId !== undefined && <button type="button" className="ghost" onClick={() => onOpenFolder({ pc, hit: { ...hit, entry: { ...hit.entry, kind: 'directory', entryId: hit.parentEntryId ?? '', name: hit.path[hit.path.length - 1] ?? hit.rootName } } })} aria-label={`Show ${hit.entry.name} in its folder`}>Show in folder</button>}
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </div>
              );
            })}
            {run.finished && total === 0 && (isComplete(run)
              ? <p className="empty">Nothing matches “<bdi>{asked}</bdi>”.</p>
              : <p className="empty">No matches were found, but not everything could be searched — see the notes above.</p>)}
          </>
        )}
      </div>
    </section>
  );
}
