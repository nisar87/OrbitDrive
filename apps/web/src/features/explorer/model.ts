import type { FileEntry } from '../../protocol';
import { parseInt64 } from '../../protocol';
import type { SortKey } from '../../prefs';

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** "PDF file", "File" for no extension, "Folder" - derived only from the real name; nothing is guessed about content. */
export function typeLabel(entry: FileEntry): string {
  if (entry.kind === 'directory') return 'Folder';
  const dot = entry.name.lastIndexOf('.');
  if (dot <= 0 || dot === entry.name.length - 1) return 'File';
  const extension = entry.name.slice(dot + 1);
  return extension.length > 10 || /\s/.test(extension) ? 'File' : `${extension.toUpperCase()} file`;
}

/** Folders first (as in File Explorer), then by the chosen key; name breaks ties so the order is stable. */
export function sortEntries(entries: readonly FileEntry[], key: SortKey, descending: boolean): FileEntry[] {
  const direction = descending ? -1 : 1;
  const compare = (a: FileEntry, b: FileEntry): number => {
    switch (key) {
      case 'size': { const x = parseInt64(a.size), y = parseInt64(b.size); return x < y ? -1 : x > y ? 1 : 0; }
      case 'modified': { const x = parseInt64(a.modifiedUnixMs), y = parseInt64(b.modifiedUnixMs); return x < y ? -1 : x > y ? 1 : 0; }
      case 'type': return collator.compare(typeLabel(a), typeLabel(b));
      default: return 0;
    }
  };
  return [...entries].sort((a, b) => {
    if (a.kind !== b.kind) return a.kind === 'directory' ? -1 : 1;
    const primary = a.kind === 'directory' && key === 'size' ? 0 : compare(a, b);
    return (primary !== 0 ? primary : collator.compare(a.name, b.name)) * direction;
  });
}

export function filterEntries(entries: readonly FileEntry[], text: string, showHidden: boolean): FileEntry[] {
  const needle = text.trim().toLocaleLowerCase();
  return entries.filter((entry) => (showHidden || !entry.hidden) && (needle === '' || entry.name.toLocaleLowerCase().includes(needle)));
}

export interface Selection { ids: ReadonlySet<string>; anchor: string | null }
export const emptySelection: Selection = { ids: new Set(), anchor: null };

/**
 * Explorer-style selection over the visible order. Plain: only this item. Toggle (Ctrl / checkbox): add or remove.
 * Range (Shift): everything between the anchor and this item.
 */
export function select(current: Selection, order: readonly string[], id: string, mode: 'single' | 'toggle' | 'range'): Selection {
  if (mode === 'toggle') {
    const ids = new Set(current.ids);
    if (ids.has(id)) ids.delete(id); else ids.add(id);
    return { ids, anchor: id };
  }
  if (mode === 'range' && current.anchor !== null) {
    const from = order.indexOf(current.anchor), to = order.indexOf(id);
    if (from >= 0 && to >= 0) return { ids: new Set(order.slice(Math.min(from, to), Math.max(from, to) + 1)), anchor: current.anchor };
  }
  return { ids: new Set([id]), anchor: id };
}

/** Drops selected ids that are no longer visible (after a filter, refresh or navigation). */
export function pruneSelection(current: Selection, order: readonly string[]): Selection {
  const visible = new Set(order);
  const ids = new Set([...current.ids].filter((id) => visible.has(id)));
  return ids.size === current.ids.size ? current : { ids, anchor: current.anchor !== null && visible.has(current.anchor) ? current.anchor : null };
}

export function totalSize(entries: readonly FileEntry[]): bigint {
  return entries.reduce((sum, entry) => (entry.kind === 'file' ? sum + parseInt64(entry.size) : sum), 0n);
}
