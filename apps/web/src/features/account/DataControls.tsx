import { useState } from 'react';
import { api, ApiError, type Session } from '../../api';

export const DELETE_PHRASE = 'delete my account';

interface Props {
  session: Session;
  /** A request failed in a way the rest of the app should know about (for example the session ended). Not used for "sign in again" answers. */
  onAuthLost: (error: unknown) => void;
  /** Called after the service has deleted the account. Also removes the sign-in record itself where the build can (Firebase). */
  onDeleted: () => Promise<void> | void;
}

/** True for the service's "Sign out and sign in again first" answer (export and delete need a recent sign-in). */
export function needsFreshSignIn(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'unauthenticated' && /sign in again/i.test(error.message);
}

/**
 * Taking a copy of what the service holds, and deleting the account. Both need only a sign-in (no approved browser), so they also work before a PC is paired.
 * They never touch files on a PC.
 */
export function DataControls({ session, onAuthLost, onDeleted }: Props) {
  const [phrase, setPhrase] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);

  const fail = (e: unknown) => {
    // A "sign in again" answer is explained here instead of silently signing the person out.
    if (needsFreshSignIn(e)) { setMessage({ kind: 'error', text: 'For your safety this needs a recent sign-in. Sign out, sign in again, then try once more.' }); return; }
    onAuthLost(e);
    setMessage({ kind: 'error', text: e instanceof ApiError ? e.message : 'Something went wrong.' });
  };

  return (
    <section aria-labelledby="your-data-title" data-testid="data-controls">
      <h3 id="your-data-title">Your data</h3>
      <p className="hint">The service holds your email address, your approved browsers, your PCs, your choices and, if you have ordered anything, your orders and payment references.</p>
      <p className="actions">
        <button type="button" className="secondary" onClick={async () => {
          setMessage(null);
          try {
            const data = await api.exportAccount(session);
            const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
            const link = Object.assign(document.createElement('a'), { href: url, download: 'orbitdrive-account.json' });
            link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
          } catch (e) { fail(e); }
        }}>Download what the service holds about me</button>
      </p>

      <h3>Delete my account</h3>
      <p className="hint">This disconnects every PC, removes every browser and setting the service holds for you, and then deletes your sign-in (your email address and password). It never deletes anything on your PCs.
        Paid orders are kept, cut down to the order, plan, length, amounts, payment method and reference, and dates, because they are payment records. A short record that the account was deleted (its id and the time, no email address) is kept in the service's audit log.</p>
      <label className="stack" htmlFor="delete-phrase">Type “{DELETE_PHRASE}” to confirm</label>
      <input id="delete-phrase" value={phrase} autoComplete="off" onChange={(e) => setPhrase(e.target.value)} />
      <p className="actions">
        <button type="button" className="danger" disabled={phrase !== DELETE_PHRASE || busy} onClick={async () => {
          setBusy(true); setMessage(null);
          try { await api.deleteAccount(session, phrase); await onDeleted(); } catch (e) { fail(e); } finally { setBusy(false); }
        }}>Delete my account</button>
      </p>
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={message.kind === 'error' ? 'notice error' : 'hint'}>{message.text}</p>}
    </section>
  );
}
