import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, AUTH_MODE, GATEWAY_URL, loadDevice, loadSession, saveDevice, saveSession, setTokenProvider, type Session } from './api';
import { firebaseAuth, type AuthUser } from './features/auth/firebaseAuth';
import { AccountForms, VerifyEmail } from './features/auth/AccountForms';
import { takePairingLinkFromLocation, type PairingLink } from './features/pairing/pairingLink';
import { SignIn } from './features/auth/SignIn';
import { Pairing } from './features/pairing/Pairing';
import { Explorer } from './features/explorer/Explorer';
import { Shell } from './features/desktop-shell/Shell';

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
  return (
    <Shell title="File Explorer" account={session.email} onSignOut={signOut} onPairAnother={() => setPairing(true)}>
      <Explorer session={session} onAuthLost={onAuthLost} />
    </Shell>
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
  useEffect(() => { document.title = 'OrbitDrive'; }, []);
  if (!GATEWAY_URL) return <Shell title="Setup needed"><p role="alert" className="notice error">This build has no relay address configured (VITE_GATEWAY_URL).</p></Shell>;
  return AUTH_MODE === 'dev' ? <DevApp /> : <FirebaseApp />;
}
