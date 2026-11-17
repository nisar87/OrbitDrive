import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
import { applyTheme, loadPrefs, savePrefs, type Prefs } from './prefs';

const initialLink = takePairingLinkFromLocation(window.location, window.history);

interface WorkspaceProps { session: Session; onDevice: (device: Session['device']) => void; onSignOut: () => Promise<void>; onUnauthenticated: () => void }

/** Everything after sign-in: pair this browser, then browse. Shared by both identity modes. */
function Workspace({ session, onDevice, onSignOut, onUnauthenticated }: WorkspaceProps) {
  const [link, setLink] = useState<PairingLink | null>(initialLink);
  const [pairing, setPairing] = useState(initialLink !== null);

  const signOut = useCallback(async () => {
    // Signing out revokes this browser's approval on the service, then clears every local credential.
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
  const apps = useMemo<DesktopApp[]>(() => [
    { id: 'explorer', title: 'File Explorer', icon: ICONS.explorer, render: () => <Explorer session={session} prefs={prefs} onPrefs={updatePrefs} onAuthLost={onAuthLost} dataVersion={dataVersion} /> },
    { id: 'devices', title: 'Devices', icon: ICONS.devices, render: () => <DevicesApp session={session} onAuthLost={onAuthLost} onPairAnother={onPairAnother} onChanged={changed} dataVersion={dataVersion} /> },
    { id: 'settings', title: 'Settings', icon: ICONS.settings, render: () => <SettingsApp prefs={prefs} onPrefs={updatePrefs} account={session.email} onSignOut={onSignOut} /> },
  ], [session, prefs, updatePrefs, onAuthLost, dataVersion, onPairAnother, changed, onSignOut]);
  return <Desktop apps={apps} account={session.email} prefs={prefs} onPrefs={updatePrefs} onSignOut={onSignOut} openRef={openApp} />;
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
