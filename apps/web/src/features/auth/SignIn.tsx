import { useState, type FormEvent } from 'react';
import { api, ApiError, type Session } from '../../api';

export function SignIn({ mode, onSignedIn }: { mode: string; onSignedIn: (session: Session) => void }) {
  const [email, setEmail] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (mode !== 'dev') {
    return <p role="alert" className="notice">Account sign-in (Firebase Authentication) is not available in this build yet.</p>;
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true); setError(null);
    try { const result = await api.devSignIn(email); onSignedIn({ idToken: result.idToken, email }); }
    catch (e) { setError(e instanceof ApiError ? e.message : 'Sign-in failed.'); }
    finally { setBusy(false); }
  }
  return (
    <form className="card" onSubmit={submit}>
      <h2>Sign in</h2>
      <p className="notice warn">Development sign-in. This identity works only against a development relay.</p>
      <label htmlFor="email">Email</label>
      <input id="email" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
      {error && <p role="alert" className="notice error">{error}</p>}
      <button type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
    </form>
  );
}
