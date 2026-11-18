import { api, ApiError, GATEWAY_URL, type Session } from '../../api';
import { parseInt64 } from '../../protocol';

export interface PreviewItem { entryId: string; name: string; size: number }
export interface PreviewState { pcId: string; items: PreviewItem[]; index: number }

let state: PreviewState | null = null;
const listeners = new Set<() => void>();
const publish = () => { for (const l of listeners) l(); };

/** What the Preview window is showing: one file out of the previewable files of the folder it was opened from. */
export const previewStore = {
  subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  get: (): PreviewState | null => state,
  open(pcId: string, items: PreviewItem[], index: number) { state = items.length ? { pcId, items, index: Math.min(Math.max(index, 0), items.length - 1) } : null; publish(); },
  go(delta: number) { if (state) { state = { ...state, index: (state.index + delta + state.items.length) % state.items.length }; publish(); } },
  /** Sign-out or account switch: nothing about the previous account's files stays in memory. */
  reset() { state = null; thumbnails.reset(); publish(); },
};

/** A preview is a short-lived, revocable capability URL for one file revision; the original is never altered. */
export interface PreviewSource { url: string; size: number; contentType: string; name: string; expiresAt: number }

export async function previewSource(session: Session, pcId: string, entryId: string, variant: 'preview' | `thumb:${128 | 256 | 512}`): Promise<PreviewSource> {
  const created = await api.previewSession(session, pcId, entryId, variant);
  return { url: GATEWAY_URL + created.urlPath, size: Number(created.size), contentType: created.contentType ?? '', name: created.name, expiresAt: Number(parseInt64(created.expiresAtUnixMs)) };
}

/**
 * Thumbnails for the Icons view: requested lazily, at most four at a time, remembered for a few minutes, and given up for a PC
 * that says it cannot make them (switched off, helper missing) so a folder of pictures never turns into a stream of failing requests.
 */
const MAX_PARALLEL = 4;
const KEEP_MS = 10 * 60 * 1000;
let running = 0;
const queue: (() => void)[] = [];
const remembered = new Map<string, { at: number; url: Promise<string | null> }>();
const unavailable = new Set<string>();

function turn<T>(job: () => Promise<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    const start = () => { running++; job().then(resolve, reject).finally(() => { running--; queue.shift()?.(); }); };
    if (running < MAX_PARALLEL) start(); else queue.push(start);
  });
}

export const thumbnails = {
  /** The image URL, or null when this picture has no thumbnail (the caller then shows an ordinary icon). */
  url(session: Session, pcId: string, entryId: string): Promise<string | null> {
    if (unavailable.has(pcId)) return Promise.resolve(null);
    const key = `${pcId}|${entryId}`;
    const hit = remembered.get(key);
    if (hit && Date.now() - hit.at < KEEP_MS) return hit.url;
    const url = turn(async () => {
      try { return (await previewSource(session, pcId, entryId, 'thumb:256')).url; }
      catch (e) {
        if (e instanceof ApiError && e.code === 'unavailable') unavailable.add(pcId);
        return null;
      }
    });
    remembered.set(key, { at: Date.now(), url });
    if (remembered.size > 800) for (const [k, v] of remembered) { if (Date.now() - v.at > KEEP_MS) remembered.delete(k); }
    return url;
  },
  reset() { remembered.clear(); unavailable.clear(); queue.length = 0; },
};
