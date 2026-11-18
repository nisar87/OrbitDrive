import { api, ApiError, type Session } from '../../api';
import type { PcSummary, SearchHit, SearchResult } from '../../protocol';

/**
 * Filename search across one folder, one shared folder, one PC or every online PC. Each PC answers for itself; what comes back is
 * kept per PC so identical names on two PCs stay apart. A PC that is offline, busy or failing is SHOWN as such: it is never
 * counted as "no matches", and an incomplete answer (limit, time, unreadable folders) says so.
 */
export interface SearchScope { pc: PcSummary; rootId?: string | null; entryId?: string | null }
export type PcSearchState = 'searching' | 'done' | 'offline' | 'error' | 'cancelled';
export interface PcOutcome {
  pcId: string;
  pcName: string;
  state: PcSearchState;
  hits: SearchHit[];
  complete: boolean;
  reason: string | null;
  unavailableFolders: string[];
  message: string | null;
}
export interface SearchRun { outcomes: PcOutcome[]; finished: boolean }

export const HITS_PER_PC = 50;

const newId = () => 's' + Array.from(crypto.getRandomValues(new Uint8Array(10)), (b) => b.toString(36).padStart(2, '0')).join('');

export function describeOutcome(o: PcOutcome): string | null {
  if (o.state === 'offline') return `${o.pcName} is offline, so it was not searched.`;
  if (o.state === 'error') return `${o.pcName} could not be searched: ${o.message ?? 'unknown problem'}.`;
  if (o.state === 'cancelled') return `Search stopped before ${o.pcName} finished.`;
  if (o.state === 'searching') return null;
  const parts: string[] = [];
  if (o.unavailableFolders.length > 0) parts.push(`${o.unavailableFolders.join(', ')} ${o.unavailableFolders.length === 1 ? 'is' : 'are'} unavailable on ${o.pcName} and was not searched`);
  if (o.reason === 'limit') parts.push(`only the first ${o.hits.length} matches on ${o.pcName} are shown; make the search more specific`);
  else if (o.reason === 'time') parts.push(`${o.pcName} ran out of search time; there may be more matches`);
  else if (o.reason === 'scan_limit') parts.push(`${o.pcName} has more files than one search covers; there may be more matches`);
  else if (o.reason === 'unreadable') parts.push(`some folders on ${o.pcName} could not be read or are nested too deeply`);
  else if (o.reason === 'cancelled') parts.push(`search stopped before ${o.pcName} finished`);
  return parts.length ? parts.join('; ') + '.' : null;
}

/** True only when every PC answered completely. An empty list from an incomplete search is never "nothing found". */
export const isComplete = (run: SearchRun) => run.finished && run.outcomes.every((o) => o.state === 'done' && o.complete);

/**
 * Starts a search over the given scopes. `onUpdate` is called with the whole picture each time a PC answers. The returned handle
 * cancels it: the PCs are told to stop, and answers that arrive later are ignored.
 */
export function startSearch(session: Session, query: string, scopes: readonly SearchScope[], onUpdate: (run: SearchRun) => void): { cancel: () => void; done: Promise<void> } {
  const ids = new Map<string, string>();
  const outcomes: PcOutcome[] = scopes.map((scope) => ({
    pcId: scope.pc.pcId, pcName: scope.pc.name, state: scope.pc.online ? 'searching' : 'offline', hits: [], complete: false, reason: null, unavailableFolders: [], message: null,
  }));
  let cancelled = false;
  const emit = () => onUpdate({ outcomes: outcomes.map((o) => ({ ...o })), finished: outcomes.every((o) => o.state !== 'searching') });
  emit();
  const work = scopes.map(async (scope, index) => {
    const outcome = outcomes[index];
    if (!scope.pc.online) return;
    const searchId = newId();
    ids.set(scope.pc.pcId + '|' + index, searchId);
    try {
      const result: SearchResult = await api.search(session, scope.pc.pcId, { searchId, query, rootId: scope.rootId ?? null, entryId: scope.entryId ?? null, limit: HITS_PER_PC });
      if (cancelled) { outcome.state = 'cancelled'; return; }
      Object.assign(outcome, { state: 'done', hits: result.hits, complete: result.complete, reason: result.reason ?? null, unavailableFolders: result.rootsUnavailable });
    } catch (e) {
      if (cancelled) { outcome.state = 'cancelled'; return; }
      if (e instanceof ApiError && e.code === 'pc_offline') outcome.state = 'offline';
      else { outcome.state = 'error'; outcome.message = e instanceof ApiError ? e.message : 'unknown problem'; }
    } finally { emit(); }
  });
  return {
    cancel() {
      if (cancelled) return;
      cancelled = true;
      scopes.forEach((scope, index) => { const id = ids.get(scope.pc.pcId + '|' + index); if (id && outcomes[index].state === 'searching') void api.cancelSearch(session, scope.pc.pcId, id).catch(() => undefined); });
      for (const o of outcomes) if (o.state === 'searching') o.state = 'cancelled';
      emit();
    },
    done: Promise.all(work).then(() => undefined),
  };
}
