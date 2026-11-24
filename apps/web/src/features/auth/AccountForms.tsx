import { useState, type FormEvent } from 'react';
import { authMessage, firebaseAuth } from './firebaseAuth';

type Mode = 'signin' | 'signup' | 'reset';

export function AccountForms() {
  const [mode, setMode] = useState<Mode>('signin');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  function switchTo(next: Mode) { setMode(next); setError(null); setNotice(null); setPassword(''); }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null); setNotice(null);
    if (mode === 'signup' && password.length < 8) { setError('Choose a password with at least 8 characters.'); return; }
    setBusy(true);
    try {
      const auth = await firebaseAuth();
      if (mode === 'signin') await auth.signIn(email, password, remember);
      else if (mode === 'signup') await auth.signUp(email, password, remember);
      else {
        // Same answer whether or not the account exists, so this form cannot be used to discover accounts.
        await auth.resetPassword(email).catch((e: unknown) => { if (authMessage(e) !== 'Email or password is not correct.') throw e; });
        setNotice('If an account exists for that email, a password reset link is on its way.');
      }
    } catch (e) { setError(authMessage(e)); }
    finally { setBusy(false); }
  }

  const title = mode === 'signin' ? 'Sign in' : mode === 'signup' ? 'Create your account' : 'Reset your password';
  return (
    <form className="card" onSubmit={submit} noValidate={false}>
      <h2>{title}</h2>
      <label htmlFor="email">Email</label>
      <input id="email" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
      {mode !== 'reset' && (
        <>
          <label htmlFor="password">Password</label>
          <input id="password" type="password" autoComplete={mode === 'signup' ? 'new-password' : 'current-password'} required value={password} onChange={(e) => setPassword(e.target.value)} />
          <label className="check"><input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> Keep me signed in on this device</label>
          <p className="notice">Leave this off on a shared or borrowed phone: you will be signed out when the browser closes.</p>
        </>
      )}
      {error && <p role="alert" className="notice error">{error}</p>}
      {notice && <p role="status" className="notice">{notice}</p>}
      <button type="submit" disabled={busy}>{busy ? 'Please wait…' : mode === 'signin' ? 'Sign in' : mode === 'signup' ? 'Create account' : 'Send reset link'}</button>
      <div className="links">
        {mode !== 'signin' && <button type="button" className="ghost" onClick={() => switchTo('signin')}>I already have an account</button>}
        {mode !== 'signup' && <button type="button" className="ghost" onClick={() => switchTo('signup')}>Create an account</button>}
        {mode !== 'reset' && <button type="button" className="ghost" onClick={() => switchTo('reset')}>Forgot password?</button>}
      </div>
    </form>
  );
}

export function VerifyEmail({ email }: { email: string }) {
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(action: 'check' | 'resend') {
    setBusy(true); setMessage(null);
    try {
      const auth = await firebaseAuth();
      if (action === 'resend') { await auth.resendVerification(); setMessage('Verification email sent again.'); }
      else { await auth.refresh(); setMessage('Not verified yet. Open the link in the email, then try again.'); }
    } catch (e) { setMessage(authMessage(e)); }
    finally { setBusy(false); }
  }

  return (
    <section className="card" aria-live="polite">
      <h2>Check your inbox</h2>
      <p>We sent a verification link to <strong>{email}</strong>. MyOrbitDrive will not connect to any PC until the address is verified.</p>
      <button type="button" disabled={busy} onClick={() => void run('check')}>I have verified my email</button>
      <button type="button" className="ghost" disabled={busy} onClick={() => void run('resend')}>Send the email again</button>
      {message && <p role="status" className="notice">{message}</p>}
    </section>
  );
}
