import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { api, ApiError, AUTH_MODE, GATEWAY_URL, loadDevice, loadSession, saveDevice, saveSession, setTokenProvider, type Session } from './api';
import { firebaseAuth, type AuthUser } from './features/auth/firebaseAuth';
import { AccountForms, VerifyEmail } from './features/auth/AccountForms';
import { takePairingLinkFromLocation, type PairingLink } from './features/pairing/pairingLink';
import { SignIn } from './features/auth/SignIn';
import { Pairing } from './features/pairing/Pairing';
import { Explorer } from './features/explorer/Explorer';
import { Shell } from './features/desktop-shell/Shell';
import { Desktop, type DesktopApp } from './features/desktop-shell/Desktop';
import { DevicesApp } from './features/devices/DevicesApp';
import { SettingsApp } from './features/settings/SettingsApp';
import { TransfersApp } from './features/transfers/TransfersApp';
import { transfers } from './features/transfers/transfers';
import { PreviewApp } from './features/preview/PreviewApp';
import { previewStore } from './features/preview/preview';
import { InboxApp } from './features/inbox/InboxApp';
import { inbox } from './features/inbox/inbox';
import { pwa } from './pwa';
import { applyTheme, loadPrefs, savePrefs, type Prefs } from './prefs';

const initialLink = takePairingLinkFromLocation(window.location, window.history);

interface WorkspaceProps { session: Session; onDevice: (device: Session['device']) => void; onSignOut: () => Promise<void>; onUnauthenticated: () => void }

/** Everything after sign-in: pair this browser, then browse. Shared by both identity modes. */
function Workspace({ session, onDevice, onSignOut, onUnauthenticated }: WorkspaceProps) {
  const [link, setLink] = useState<PairingLink | null>(initialLink);
  const [pairing, setPairing] = useState(initialLink !== null);

  const signOut = useCallback(async () => {
    // Signing out revokes this browser's approval on the service, then clears every local credential.
    transfers.reset();
    inbox.reset();
    void pwa.purge();
    if (session.device) await api.revokeThisBrowser(session).catch(() => undefined);
    await onSignOut();
  }, [session, onSignOut]);

  const onAuthLost = useCallback((error: unknown) => {
    if (error instanceof ApiError && error.code === 'unauthenticated') onUnauthenticated();
    else if (error instanceof ApiError && error.code === 'forbidden') { onDevice(undefined); setPairing(true); }
  }, [onDevice, onUnauthenticated]);

  if (pairing || !session.device) {
    return (
      <Shell title="Pair with your PC" account={session.email} onSignOut={signOut}>
        <Pairing session={session} link={link} onLinkUsed={() => setLink(null)} onPaired={(device) => { onDevice(device); setPairing(false); }} />
      </Shell>
    );
  }
  return <Workbench session={session} onAuthLost={onAuthLost} onSignOut={() => void signOut()} onPairAnother={() => setPairing(true)} />;
}

const glyph = (path: string) => <svg viewBox="0 0 24 24" aria-hidden="true"><path d={path} /></svg>;
const ICONS = {
  explorer: glyph('M3 7.5A1.5 1.5 0 0 1 4.5 6h4.2l2 2h8.8A1.5 1.5 0 0 1 21 9.5v8A1.5 1.5 0 0 1 19.5 19h-15A1.5 1.5 0 0 1 3 17.5z'),
  devices: glyph('M4 5h11a1 1 0 0 1 1 1v2h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1v-1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zm11 5v8h3v-8z'),
  preview: glyph('M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z'),
  inbox: glyph('M4 13l2.2-6.2A2 2 0 0 1 8.1 5.5h7.8a2 2 0 0 1 1.9 1.3L20 13m-16 0v4.5A1.5 1.5 0 0 0 5.5 19h13a1.5 1.5 0 0 0 1.5-1.5V13m-16 0h4l1 2h6l1-2h4'),
  transfers: glyph('M8 4v12m0 0l-4-4m4 4l4-4M16 20V8m0 0l-4 4m4-4l4 4'),
  settings: glyph('M4 7h9m4 0h3M4 12h3m4 0h9M4 17h11m4 0h1M15 5v4M9 10v4M17 15v4'),
};

/** The signed-in, paired workspace: a desktop with the implemented apps only (Explorer, Devices, Settings). */
function Workbench({ session, onAuthLost, onSignOut, onPairAnother }: { session: Session; onAuthLost: (error: unknown) => void; onSignOut: () => void; onPairAnother: () => void }) {
  const [prefs, setPrefs] = useState<Prefs>(loadPrefs);
  const [dataVersion, setDataVersion] = useState(0);
  const openApp = useRef<((id: string) => void) | null>(null);
  const updatePrefs = useCallback((next: Prefs) => { savePrefs(next); setPrefs(next); }, []);
  useEffect(() => { applyTheme(prefs.theme); }, [prefs.theme]);
  const changed = useCallback(() => setDataVersion((n) => n + 1), []);
  useEffect(() => { inbox.start(session); }, [session]);
  useEffect(() => { pwa.setBusyCheck(() => transfers.list().some((t) => t.state === 'preparing' || t.state === 'saving')); }, []);
  useEffect(() => () => { inbox.reset(); previewStore.reset(); }, []);
  const waiting = useSyncExternalStore(inbox.subscribe, inbox.pending);
  const announcements = useSyncExternalStore(inbox.subscribe, inbox.announcements);
  // A system notification opens the Inbox (it never carries a link or a file name).
  useEffect(() => {
    const onMessage = (event: MessageEvent) => { if ((event.data as { open?: string } | null)?.open === 'inbox') openApp.current?.('inbox'); };
    navigator.serviceWorker?.addEventListener('message', onMessage);
    return () => navigator.serviceWorker?.removeEventListener('message', onMessage);
  }, []);
  const apps = useMemo<DesktopApp[]>(() => [
    { id: 'explorer', title: 'File Explorer', icon: ICONS.explorer, render: () => <Explorer session={session} prefs={prefs} onPrefs={updatePrefs} onAuthLost={onAuthLost} dataVersion={dataVersion} onOpenApp={(id) => openApp.current?.(id)} /> },
    { id: 'preview', title: 'Preview', icon: ICONS.preview, transient: true, render: () => <PreviewApp session={session} onAuthLost={onAuthLost} /> },
    { id: 'inbox', title: 'Inbox', icon: ICONS.inbox, badge: waiting, render: () => <InboxApp session={session} /> },
    { id: 'transfers', title: 'Transfers', icon: ICONS.transfers, render: () => <TransfersApp session={session} /> },
    { id: 'devices', title: 'Devices', icon: ICONS.devices, render: () => <DevicesApp session={session} onAuthLost={onAuthLost} onPairAnother={onPairAnother} onChanged={changed} dataVersion={dataVersion} /> },
    { id: 'settings', title: 'Settings', icon: ICONS.settings, render: () => <SettingsApp prefs={prefs} onPrefs={updatePrefs} account={session.email} onSignOut={onSignOut} /> },
  ], [session, prefs, updatePrefs, onAuthLost, dataVersion, onPairAnother, changed, onSignOut, waiting]);
  return (
    <>
      <Desktop apps={apps} account={session.email} prefs={prefs} onPrefs={updatePrefs} onSignOut={onSignOut} openRef={openApp} />
      <div className="toast-region" role="status" aria-live="polite">
        {announcements.slice(-1).map((text) => (
          <div className="toast" key={text} data-testid="inbox-toast">
            <span>{text}</span>
            <span><button type="button" onClick={() => { openApp.current?.('inbox'); inbox.dismissAnnouncements(); }}>Open Inbox</button> <button type="button" className="ghost" aria-label="Dismiss" onClick={() => inbox.dismissAnnouncements()}>✕</button></span>
          </div>
        ))}
      </div>
    </>
  );
}

function DevApp() {
  const [session, setSession] = useState<Session | null>(loadSession);
  const update = useCallback((next: Session | null) => { saveSession(next); setSession(next); }, []);
  if (!session) return <Shell title="Sign in"><SignIn mode="dev" onSignedIn={(s) => update(s)} /></Shell>;
  return <Workspace session={session} onDevice={(device) => update({ ...session, device })} onSignOut={async () => update(null)} onUnauthenticated={() => update(null)} />;
}

function FirebaseApp() {
  const [user, setUser] = useState<AuthUser | null | undefined>(undefined);
  const [device, setDevice] = useState<Session['device']>(undefined);
  const [failure, setFailure] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let stop = () => {};
    firebaseAuth().then((auth) => {
      if (cancelled) return;
      setTokenProvider(auth.getToken);
      stop = auth.onChange((next) => { setUser(next); setDevice(next ? loadDevice(next.uid) : undefined); });
    }).catch((e: unknown) => setFailure(e instanceof Error ? e.message : 'Sign-in is not configured.'));
    return () => { cancelled = true; stop(); };
  }, []);

  if (failure) return <Shell title="Setup needed"><p role="alert" className="notice error">{failure}</p></Shell>;
  if (user === undefined) return <Shell title="OrbitDrive"><p className="empty" role="status">Loading…</p></Shell>;
  if (user === null) return <Shell title="Sign in"><AccountForms /></Shell>;
  const signOut = async () => { saveDevice(user.uid, undefined); await (await firebaseAuth()).signOut(); };
  if (!user.emailVerified) return <Shell title="Verify your email" account={user.email} onSignOut={signOut}><VerifyEmail email={user.email} /></Shell>;
  return (
    <Workspace session={{ idToken: '', email: user.email, device }} onSignOut={signOut} onUnauthenticated={() => void signOut()}
      onDevice={(next) => { saveDevice(user.uid, next); setDevice(next); }} />
  );
}

export function App() {
  useEffect(() => { document.title = 'OrbitDrive'; applyTheme(loadPrefs().theme); }, []);
  if (!GATEWAY_URL) return <Shell title="Setup needed"><p role="alert" className="notice error">This build has no relay address configured (VITE_GATEWAY_URL).</p></Shell>;
  return AUTH_MODE === 'dev' ? <DevApp /> : <FirebaseApp />;
}
