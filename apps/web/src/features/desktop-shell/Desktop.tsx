import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import type { Prefs, WindowLayout } from '../../prefs';

export interface DesktopApp { id: string; title: string; icon: ReactNode; render: () => ReactNode; /** A count shown on the taskbar icon (for example files waiting). */ badge?: number; /** A viewer that is not listed in the launcher and sits on the taskbar only while its window is open. */ transient?: boolean }

interface Props {
  apps: DesktopApp[];
  account: string;
  prefs: Prefs;
  onPrefs: (next: Prefs) => void;
  onSignOut: () => void;
  /** Lets an app ask the shell to open another app (for example "Pair another PC" opening Devices). */
  openRef?: { current: ((id: string) => void) | null };
}

interface WindowState { open: boolean; minimized: boolean; z: number }
const MIN_W = 360, MIN_H = 280, TASKBAR = 52;

/** Phones (narrow or short viewports) always show one maximised window; dragging and resizing are desktop/tablet only. */
function useCompact(): boolean {
  const query = '(max-width: 720px), (max-height: 520px)';
  const [compact, setCompact] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const update = () => setCompact(media.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  return compact;
}

function defaultLayout(index: number): WindowLayout {
  const w = Math.min(1100, Math.max(MIN_W, window.innerWidth - 120)), h = Math.min(720, Math.max(MIN_H, window.innerHeight - TASKBAR - 80));
  // The cascade never pushes a new window past the edge of the desktop, however many apps there are.
  const x = Math.min((window.innerWidth - w) / 2 + index * 28, window.innerWidth - w - 8), y = Math.min((window.innerHeight - TASKBAR - h) / 2 + index * 28, window.innerHeight - TASKBAR - h - 8);
  return { x: Math.max(8, x), y: Math.max(8, y), w, h, maximized: index === 0 && window.innerWidth < 1100 };
}

/** Keeps a window reachable: its title bar and 200 px of width (the buttons plus room to grab it) stay on the desktop. */
function clamp(layout: WindowLayout): WindowLayout {
  const maxW = Math.max(MIN_W, window.innerWidth), maxH = Math.max(MIN_H, window.innerHeight - TASKBAR);
  const w = Math.min(Math.max(layout.w, MIN_W), maxW), h = Math.min(Math.max(layout.h, MIN_H), maxH);
  return { ...layout, w, h, x: Math.min(Math.max(layout.x, 200 - w), window.innerWidth - 200), y: Math.min(Math.max(layout.y, 0), Math.max(0, window.innerHeight - TASKBAR - 44)) };
}

/**
 * After the screen changes (a tablet rotates, the browser window shrinks) or a layout saved on a bigger screen is restored, the whole window is brought back on
 * screen. Dragging keeps the looser {@link clamp}, so a window can still be parked partly off the edge on purpose.
 */
function refit(layout: WindowLayout): WindowLayout {
  const fitted = clamp(layout);
  const w = Math.min(fitted.w, Math.max(MIN_W, window.innerWidth - 16)), h = Math.min(fitted.h, Math.max(MIN_H, window.innerHeight - TASKBAR - 16));
  const x = Math.max(8, Math.min(fitted.x, window.innerWidth - w - 8)), y = Math.max(0, Math.min(fitted.y, window.innerHeight - TASKBAR - h));
  return { ...fitted, w, h, x, y };
}

function Clock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => { const timer = setInterval(() => setNow(new Date()), 20_000); return () => clearInterval(timer); }, []);
  return <time className="clock" dateTime={now.toISOString()}>{now.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</time>;
}

/** A popup menu anchored to the taskbar: arrow keys move, Escape or leaving it closes, focus returns to the opener. */
function Flyout({ label, className, onClose, children }: { label: string; className: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    ref.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    const outside = (event: PointerEvent) => { if (!ref.current?.contains(event.target as Node)) onClose(); };
    document.addEventListener('pointerdown', outside, true);
    return () => { document.removeEventListener('pointerdown', outside, true); if (opener?.isConnected) opener.focus(); };
  }, [onClose]);
  const onKeyDown = (event: ReactKeyboardEvent) => {
    const items = [...(ref.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
    const index = items.indexOf(document.activeElement as HTMLElement);
    if (event.key === 'Escape') { event.stopPropagation(); onClose(); }
    else if (event.key === 'ArrowDown') { event.preventDefault(); items[(index + 1) % items.length]?.focus(); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); items[(index - 1 + items.length) % items.length]?.focus(); }
    else if (event.key === 'Home') { event.preventDefault(); items[0]?.focus(); }
    else if (event.key === 'End') { event.preventDefault(); items[items.length - 1]?.focus(); }
    else if (event.key === 'Tab') onClose();
  };
  return <div ref={ref} className={`flyout ${className}`} role="menu" aria-label={label} onKeyDown={onKeyDown}>{children}</div>;
}

export function Desktop({ apps, account, prefs, onPrefs, onSignOut, openRef }: Props) {
  const compact = useCompact();
  const [windows, setWindows] = useState<Record<string, WindowState>>(() => ({ [apps[0].id]: { open: true, minimized: false, z: 1 } }));
  const [layouts, setLayouts] = useState<Record<string, WindowLayout>>(() => {
    const initial: Record<string, WindowLayout> = {};
    apps.forEach((app, index) => { const saved = prefs.windows[app.id]; initial[app.id] = saved && saved.w >= MIN_W && saved.h >= MIN_H ? refit(saved) : defaultLayout(index); });
    return initial;
  });
  // An app can appear after the desktop was created (the operator area, once the service confirms the account is an operator): it gets a default window.
  const layoutOf = (id: string): WindowLayout => layouts[id] ?? defaultLayout(Math.max(0, apps.findIndex((app) => app.id === id)));
  const [menu, setMenu] = useState<'launcher' | 'account' | null>(null);
  const zTop = useRef(1);
  const prefsRef = useRef(prefs);
  prefsRef.current = prefs;
  const windowRefs = useRef<Record<string, HTMLElement | null>>({});
  const [focusRequest, setFocusRequest] = useState<string | null>(apps[0].id);

  const active = useMemo(() => {
    const visible = Object.entries(windows).filter(([, state]) => state.open && !state.minimized);
    return visible.length === 0 ? null : visible.reduce((a, b) => (a[1].z >= b[1].z ? a : b))[0];
  }, [windows]);

  const focusWindow = useCallback((id: string) => {
    setWindows((current) => ({ ...current, [id]: { open: true, minimized: false, z: ++zTop.current } }));
    setFocusRequest(id);
  }, []);
  useEffect(() => { if (openRef) openRef.current = focusWindow; return () => { if (openRef) openRef.current = null; }; }, [openRef, focusWindow]);

  // Move keyboard focus into a window when it is opened or brought forward from the taskbar or launcher.
  useLayoutEffect(() => {
    if (focusRequest === null) return;
    const element = windowRefs.current[focusRequest];
    if (element && !element.contains(document.activeElement)) element.focus();
    setFocusRequest(null);
  }, [focusRequest, windows]);

  const saveLayout = useCallback((id: string, layout: WindowLayout) => {
    onPrefs({ ...prefsRef.current, windows: { ...prefsRef.current.windows, [id]: layout } });
  }, [onPrefs]);

  const setLayout = useCallback((id: string, next: WindowLayout, persist: boolean) => {
    setLayouts((current) => ({ ...current, [id]: next }));
    if (persist) saveLayout(id, next);
  }, [saveLayout]);

  useEffect(() => {
    const onResize = () => setLayouts((current) => Object.fromEntries(Object.entries(current).map(([id, layout]) => [id, refit(layout)])));
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  const taskbarClick = (id: string) => {
    const state = windows[id];
    if (!state?.open || state.minimized || active !== id) focusWindow(id);
    else setWindows((current) => ({ ...current, [id]: { ...current[id], minimized: true } }));
  };

  /** Pointer drag for moving (title bar) and resizing (edges/corner). Mouse, pen and touch on tablets. */
  const startDrag = (id: string, mode: 'move' | 'e' | 's' | 'se') => (event: ReactPointerEvent<HTMLElement>) => {
    if (compact || layoutOf(id).maximized || event.button !== 0) return;
    if (mode === 'move' && (event.target as HTMLElement).closest('button')) return;
    event.preventDefault();
    const start = layoutOf(id), startX = event.clientX, startY = event.clientY;
    let latest = start;
    const target = event.currentTarget;
    target.setPointerCapture(event.pointerId);
    const move = (e: PointerEvent) => {
      const dx = e.clientX - startX, dy = e.clientY - startY;
      latest = clamp(mode === 'move' ? { ...start, x: start.x + dx, y: start.y + dy }
        : { ...start, w: mode === 's' ? start.w : start.w + dx, h: mode === 'e' ? start.h : start.h + dy });
      setLayout(id, latest, false);
    };
    const end = () => {
      target.removeEventListener('pointermove', move); target.removeEventListener('pointerup', end); target.removeEventListener('pointercancel', end);
      saveLayout(id, latest);
    };
    target.addEventListener('pointermove', move); target.addEventListener('pointerup', end); target.addEventListener('pointercancel', end);
  };

  const toggleMaximize = (id: string) => setLayout(id, { ...layoutOf(id), maximized: !layoutOf(id).maximized }, true);
  const close = (id: string) => setWindows((current) => ({ ...current, [id]: { open: false, minimized: false, z: 0 } }));
  const minimize = (id: string) => setWindows((current) => ({ ...current, [id]: { ...current[id], minimized: true } }));
  const closeMenu = useCallback(() => setMenu(null), []);

  return (
    <div className="desktop shell">
      <div className="surface">
        {active === null && (
          <div className="desktop-icons" aria-label="Apps">
            {apps.filter((app) => !app.transient).map((app) => (
              <button key={app.id} type="button" className="desktop-icon" onClick={() => focusWindow(app.id)}>
                <span className="glyph" aria-hidden="true">{app.icon}</span>{app.title}
              </button>
            ))}
          </div>
        )}
        {apps.map((app) => {
          const state = windows[app.id];
          if (!state?.open) return null;
          const layout = layoutOf(app.id);
          const maximized = compact || layout.maximized;
          const style = maximized ? { zIndex: state.z } : { zIndex: state.z, left: layout.x, top: layout.y, width: layout.w, height: layout.h };
          return (
            <section key={app.id} ref={(element) => { windowRefs.current[app.id] = element; }} role="dialog" aria-label={app.title} tabIndex={-1}
              className={`window win${maximized ? ' maximized' : ''}${active === app.id ? ' active' : ''}`} hidden={state.minimized || (compact && active !== app.id)}
              style={style} onPointerDownCapture={() => { if (active !== app.id) setWindows((current) => ({ ...current, [app.id]: { ...current[app.id], z: ++zTop.current } })); }}>
              <header className="titlebar" onPointerDown={startDrag(app.id, 'move')} onDoubleClick={(event) => { if (!compact && !(event.target as HTMLElement).closest('button')) toggleMaximize(app.id); }}>
                <span className="glyph small" aria-hidden="true">{app.icon}</span>
                <h1>{app.title}</h1>
                <span className="spacer" />
                <div className="window-controls">
                  <button type="button" className="chrome" aria-label={`Minimize ${app.title}`} onClick={() => minimize(app.id)}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M2 6.5h8" /></svg></button>
                  {!compact && (
                    <button type="button" className="chrome" aria-label={`${layout.maximized ? 'Restore' : 'Maximize'} ${app.title}`} onClick={() => toggleMaximize(app.id)}>
                      <svg viewBox="0 0 12 12" aria-hidden="true">{layout.maximized ? <path d="M2.5 4.5h5v5h-5zM4.5 4.5v-2h5v5h-2" /> : <path d="M2.5 2.5h7v7h-7z" />}</svg>
                    </button>
                  )}
                  <button type="button" className="chrome close" aria-label={`Close ${app.title}`} onClick={() => close(app.id)}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7" /></svg></button>
                </div>
              </header>
              <div className="window-body">{app.render()}</div>
              {!maximized && (<>
                <div className="resize e" onPointerDown={startDrag(app.id, 'e')} />
                <div className="resize s" onPointerDown={startDrag(app.id, 's')} />
                <div className="resize se" onPointerDown={startDrag(app.id, 'se')} />
              </>)}
            </section>
          );
        })}
      </div>

      {menu === 'launcher' && (
        <Flyout label="Apps" className="launcher" onClose={closeMenu}>
          <p className="flyout-title">OrbitDrive</p>
          {apps.filter((app) => !app.transient).map((app) => (
            <button key={app.id} type="button" role="menuitem" onClick={() => { setMenu(null); focusWindow(app.id); }}>
              <span className="glyph" aria-hidden="true">{app.icon}</span>{app.title}
            </button>
          ))}
        </Flyout>
      )}
      {menu === 'account' && (
        <Flyout label="Account" className="account-menu" onClose={closeMenu}>
          <p className="flyout-title">Signed in as</p>
          <p className="flyout-account"><bdi>{account}</bdi></p>
          <p className="flyout-note">Files travel over TLS through the OrbitDrive relay, which can process them in transit and stores nothing. This is not end-to-end encryption.</p>
          <button type="button" role="menuitem" onClick={() => { setMenu(null); onSignOut(); }}>Sign out</button>
        </Flyout>
      )}

      <footer className="taskbar">
        <nav className="taskbar-apps" aria-label="Taskbar">
          <button type="button" className="task start" aria-label="Apps" aria-haspopup="menu" aria-expanded={menu === 'launcher'} onClick={() => setMenu(menu === 'launcher' ? null : 'launcher')}>
            <span className="app-icon" aria-hidden="true" />
          </button>
          {apps.filter((app) => !app.transient || windows[app.id]?.open).map((app) => {
            const state = windows[app.id];
            const running = !!state?.open;
            return (
              <button key={app.id} type="button" className={`task${running ? ' running' : ''}${active === app.id ? ' current' : ''}`} aria-label={app.badge ? `${app.title}, ${app.badge} waiting` : app.title}
                aria-pressed={active === app.id} title={app.title} onClick={() => taskbarClick(app.id)}>
                <span className="glyph" aria-hidden="true">{app.icon}</span>
                {app.badge ? <span className="count-badge" aria-hidden="true">{app.badge > 9 ? '9+' : app.badge}</span> : null}
              </button>
            );
          })}
        </nav>
        <span className="spacer" />
        <span className="privacy" title="Files travel over TLS through the OrbitDrive relay, which can process them in transit. Nothing is stored there. This is not end-to-end encryption.">TLS via relay · not end-to-end encrypted</span>
        <Clock />
        <button type="button" className="task account-button" aria-label="Account" aria-haspopup="menu" aria-expanded={menu === 'account'} onClick={() => setMenu(menu === 'account' ? null : 'account')}>
          <span className="avatar" aria-hidden="true">{(account.trim()[0] ?? '?').toUpperCase()}</span>
        </button>
      </footer>
    </div>
  );
}
