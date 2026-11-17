// Layout and appearance preferences. Never contains file names, folder contents or credentials.
export type ThemeChoice = 'system' | 'light' | 'dark';
export type ViewMode = 'details' | 'list' | 'icons';
export type SortKey = 'name' | 'size' | 'modified' | 'type';
export interface WindowLayout { x: number; y: number; w: number; h: number; maximized: boolean }
export interface Prefs {
  theme: ThemeChoice;
  view: ViewMode;
  sortKey: SortKey;
  sortDescending: boolean;
  showHidden: boolean;
  treeOpen: boolean;
  windows: Record<string, WindowLayout>;
}

const KEY = 'orbitdrive.prefs.v1';
export const defaultPrefs: Prefs = { theme: 'system', view: 'details', sortKey: 'name', sortDescending: false, showHidden: false, treeOpen: true, windows: {} };

const oneOf = <T extends string>(value: unknown, allowed: readonly T[], fallback: T): T => (allowed.includes(value as T) ? (value as T) : fallback);
const finite = (value: unknown, fallback: number) => (typeof value === 'number' && Number.isFinite(value) ? value : fallback);

/** Reads stored preferences defensively: anything unexpected falls back to the default for that field. */
export function parsePrefs(raw: string | null): Prefs {
  let stored: Record<string, unknown> = {};
  try { const parsed: unknown = JSON.parse(raw ?? '{}'); if (parsed && typeof parsed === 'object') stored = parsed as Record<string, unknown>; } catch { /* defaults */ }
  const windows: Record<string, WindowLayout> = {};
  if (stored.windows && typeof stored.windows === 'object') {
    for (const [id, value] of Object.entries(stored.windows as Record<string, unknown>).slice(0, 16)) {
      if (!/^[a-z]{1,20}$/.test(id) || !value || typeof value !== 'object') continue;
      const layout = value as Record<string, unknown>;
      windows[id] = { x: finite(layout.x, 0), y: finite(layout.y, 0), w: finite(layout.w, 0), h: finite(layout.h, 0), maximized: layout.maximized === true };
    }
  }
  return {
    theme: oneOf(stored.theme, ['system', 'light', 'dark'] as const, defaultPrefs.theme),
    view: oneOf(stored.view, ['details', 'list', 'icons'] as const, defaultPrefs.view),
    sortKey: oneOf(stored.sortKey, ['name', 'size', 'modified', 'type'] as const, defaultPrefs.sortKey),
    sortDescending: stored.sortDescending === true,
    showHidden: stored.showHidden === true,
    treeOpen: stored.treeOpen !== false,
    windows,
  };
}

export function loadPrefs(): Prefs {
  try { return parsePrefs(localStorage.getItem(KEY)); } catch { return { ...defaultPrefs }; }
}
export function savePrefs(prefs: Prefs): void {
  try { localStorage.setItem(KEY, JSON.stringify(prefs)); } catch { /* storage unavailable: preferences last for this visit only */ }
}
export function applyTheme(theme: ThemeChoice): void {
  if (theme === 'system') delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = theme;
}
