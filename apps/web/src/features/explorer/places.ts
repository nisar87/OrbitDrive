/**
 * Favorites and recents. Each is remembered with the PC and shared folder it belongs to and an ordinary entry id, so nothing a place
 * points to can be reached unless the PC still says it is shared (entry checks), and an id from one PC is never used on another.
 * Stored only in this browser, per signed-in account; names are kept so the list is readable when a PC is offline.
 */
export interface Place {
  pcId: string;
  rootId: string;
  entryId: string;
  name: string;
  kind: 'file' | 'directory';
  /** Names of the folders above it inside the shared folder (display only). */
  path: string[];
  at: number;
}
export interface Places { favorites: Place[]; recents: Place[] }

export const MAX_FAVORITES = 100;
export const MAX_RECENTS = 20;
export const emptyPlaces: Places = { favorites: [], recents: [] };
const storageKey = (account: string) => `orbitdrive.places.v1.${account.toLowerCase()}`;

/** The identity of a place across listings (entry ids are issued fresh each time). */
export const placeKey = (p: Pick<Place, 'pcId' | 'rootId' | 'kind' | 'path' | 'name'>) => [p.pcId, p.rootId, p.kind, ...p.path, p.name].join('\u0001');

function clean(value: unknown): Place | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const text = (x: unknown, max: number) => (typeof x === 'string' && x.length > 0 && x.length <= max ? x : null);
  const pcId = text(v.pcId, 128), rootId = text(v.rootId, 128), entryId = text(v.entryId, 8192), name = text(v.name, 255);
  if (!pcId || !rootId || !entryId || !name || (v.kind !== 'file' && v.kind !== 'directory')) return null;
  const path = Array.isArray(v.path) ? v.path.filter((p): p is string => typeof p === 'string' && p.length <= 255).slice(0, 64) : [];
  return { pcId, rootId, entryId, name, kind: v.kind, path, at: typeof v.at === 'number' && Number.isFinite(v.at) ? v.at : 0 };
}

/** Reads stored places defensively: anything unexpected is dropped, never trusted. */
export function parsePlaces(raw: string | null): Places {
  try {
    const parsed: unknown = JSON.parse(raw ?? '{}');
    const read = (list: unknown, max: number) => (Array.isArray(list) ? list.map(clean).filter((p): p is Place => p !== null).slice(0, max) : []);
    const object = (parsed && typeof parsed === 'object' ? parsed : {}) as Record<string, unknown>;
    return { favorites: read(object.favorites, MAX_FAVORITES), recents: read(object.recents, MAX_RECENTS) };
  } catch { return emptyPlaces; }
}

export function loadPlaces(account: string): Places {
  try { return parsePlaces(localStorage.getItem(storageKey(account))); } catch { return emptyPlaces; }
}
export function savePlaces(account: string, places: Places): void {
  try { localStorage.setItem(storageKey(account), JSON.stringify(places)); } catch { /* storage full or blocked: the lists live for this visit only */ }
}
export function clearPlaces(account: string): void { try { localStorage.removeItem(storageKey(account)); } catch { /* nothing stored */ } }

export const isFavorite = (places: Places, place: Pick<Place, 'pcId' | 'rootId' | 'kind' | 'path' | 'name'>) => places.favorites.some((f) => placeKey(f) === placeKey(place));

export function toggleFavorite(places: Places, place: Place): Places {
  if (isFavorite(places, place)) return { ...places, favorites: places.favorites.filter((f) => placeKey(f) !== placeKey(place)) };
  return { ...places, favorites: [place, ...places.favorites].slice(0, MAX_FAVORITES) };
}
export const removeFavorite = (places: Places, place: Place): Places => ({ ...places, favorites: places.favorites.filter((f) => f !== place && placeKey(f) !== placeKey(place)) });
export const removeRecent = (places: Places, place: Place): Places => ({ ...places, recents: places.recents.filter((r) => r !== place && placeKey(r) !== placeKey(place)) });

/** Newest first, one entry per place, bounded. */
export function addRecent(places: Places, place: Place): Places {
  return { ...places, recents: [place, ...places.recents.filter((r) => placeKey(r) !== placeKey(place))].slice(0, MAX_RECENTS) };
}

/** Forgets everything that belongs to a PC (for example when it was disconnected). */
export const forgetPc = (places: Places, pcId: string): Places => ({ favorites: places.favorites.filter((p) => p.pcId !== pcId), recents: places.recents.filter((p) => p.pcId !== pcId) });

export type Availability = { state: 'unknown' } | { state: 'available'; name: string; size: string } | { state: 'offline' } | { state: 'gone'; reason: string };

/** Groups places by PC so each PC is asked only about its own ids (at most 100 at a time). */
export function groupByPc(places: readonly Place[]): Map<string, Place[]> {
  const map = new Map<string, Place[]>();
  for (const place of places) { const list = map.get(place.pcId) ?? []; list.push(place); map.set(place.pcId, list); }
  return map;
}
