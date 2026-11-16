import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError, type Session } from '../../api';
import type { ClaimPairingResponse, PairingState } from '../../protocol';
import { parseManualCode, type PairingLink } from './pairingLink';

interface Props { session: Session; link: PairingLink | null; onLinkUsed: () => void; onPaired: (device: { id: string; secret: string }) => void }

function browserName(): string {
  const ua = navigator.userAgent;
  const device = /iPhone|iPad/.test(ua) ? 'iPhone/iPad' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'Mac' : 'Device';
  const browser = /Firefox/.test(ua) ? 'Firefox' : /Edg\//.test(ua) ? 'Edge' : /Chrome/.test(ua) ? 'Chrome' : /Safari/.test(ua) ? 'Safari' : 'browser';
  return `${browser} on ${device}`;
}

export function Pairing({ session, link, onLinkUsed, onPaired }: Props) {
  const [code, setCode] = useState('');
  const [claim, setClaim] = useState<ClaimPairingResponse | null>(null);
  const [state, setState] = useState<PairingState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const started = useRef(false);

  async function start(target: PairingLink) {
    setBusy(true); setError(null);
    try {
      const result = await api.claimPairing(session, target.id, target.secret, browserName());
      setClaim(result); setState(result.state);
    } catch (e) { setError(e instanceof ApiError ? e.message : 'Pairing failed.'); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    if (link && !started.current) { started.current = true; onLinkUsed(); void start(link); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [link]);

  useEffect(() => {
    if (!claim || state !== 'claimed') return;
    const timer = setInterval(async () => {
      try {
        const status = await api.pairingStatus(session, claim.pairingId);
        setState(status.state);
        if (status.state === 'approved') onPaired({ id: claim.browserDeviceId, secret: claim.deviceSecret });
      } catch (e) { if (e instanceof ApiError && e.code !== 'network') { setError(e.message); setState('expired'); } }
    }, 1500);
    return () => clearInterval(timer);
  }, [claim, state, session, onPaired]);

  function submit(event: FormEvent) {
    event.preventDefault();
    const parsed = parseManualCode(code);
    if (!parsed) { setError('Enter the code exactly as shown on the PC, for example ABCDEF-GHJKLMNPQR.'); return; }
    void start(parsed);
  }

  if (claim && state === 'claimed') {
    return (
      <section className="card" aria-live="polite">
        <h2>Confirm on your PC</h2>
        <p>OrbitDrive on <strong>{claim.pcName}</strong> is showing a code. Approve there only if it matches:</p>
        <p className="confirm-code" data-testid="confirmation-code" aria-label="Confirmation code">{claim.confirmationCode}</p>
        <p className="notice">Nothing is shared with this browser until you approve on the PC.</p>
      </section>
    );
  }
  const ended = state === 'denied' ? 'The request was denied on the PC.' : state === 'expired' ? 'The pairing request expired. Create a new code on the PC.' : null;
  return (
    <form className="card" onSubmit={submit}>
      <h2>Pair with your PC</h2>
      <p>On your PC, open OrbitDrive and choose <strong>Pair a phone</strong>. Scan its QR code with your camera, or type the code below.</p>
      <label htmlFor="pair-code">Pairing code</label>
      <input id="pair-code" inputMode="text" autoCapitalize="characters" autoComplete="off" spellCheck={false} placeholder="ABCDEF-GHJKLMNPQR" value={code} onChange={(e) => setCode(e.target.value)} />
      {(error ?? ended) && <p role="alert" className="notice error">{error ?? ended}</p>}
      <button type="submit" disabled={busy}>{busy ? 'Contacting PC…' : 'Pair'}</button>
    </form>
  );
}
