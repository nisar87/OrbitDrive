import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, AUTH_MODE, GATEWAY_URL, loadSession, saveSession, type Session } from './api';
import { takePairingLinkFromLocation, type PairingLink } from './features/pairing/pairingLink';
import { SignIn } from './features/auth/SignIn';
import { Pairing } from './features/pairing/Pairing';
import { Explorer } from './features/explorer/Explorer';
import { Shell } from './features/desktop-shell/Shell';

const initialLink = takePairingLinkFromLocation(window.location, window.history);

export function App() {
  const [session, setSession] = useState<Session | null>(loadSession);
  const [link, setLink] = useState<PairingLink | null>(initialLink);
  const [pairing, setPairing] = useState(initialLink !== null);
  const update = useCallback((next: Session | null) => { saveSession(next); setSession(next); }, []);

  useEffect(() => { document.title = session ? 'OrbitDrive — Files' : 'OrbitDrive — Sign in'; }, [session]);

  const signOut = useCallback(async () => {
    // Signing out revokes this browser's approval on the service, then clears every local credential.
    if (session?.device) await api.revokeThisBrowser(session).catch(() => undefined);
    update(null);
    setPairing(false);
  }, [session, update]);

  const onAuthLost = useCallback((error: unknown) => {
    if (error instanceof ApiError && error.code === 'unauthenticated') update(null);
    else if (error instanceof ApiError && error.code === 'forbidden' && session) { update({ ...session, device: undefined }); setPairing(true); }
  }, [session, update]);

  if (!GATEWAY_URL) return <Shell title="Setup needed"><p role="alert" className="notice error">This build has no relay address configured (VITE_GATEWAY_URL).</p></Shell>;
  if (!session) return <Shell title="Sign in"><SignIn mode={AUTH_MODE} onSignedIn={(s) => update(s)} /></Shell>;
  if (pairing || !session.device) {
    return (
      <Shell title="Pair with your PC" account={session.email} onSignOut={signOut}>
        <Pairing session={session} link={link} onLinkUsed={() => setLink(null)} onPaired={(device) => { update({ ...session, device }); setPairing(false); }} />
      </Shell>
    );
  }
  return (
    <Shell title="File Explorer" account={session.email} onSignOut={signOut} onPairAnother={() => setPairing(true)}>
      <Explorer session={session} onAuthLost={onAuthLost} />
    </Shell>
  );
}
