// Firebase Authentication wrapper. Loaded lazily so development-identity builds never pull in the SDK.
export interface AuthUser { uid: string; email: string; emailVerified: boolean }

export interface AuthClient {
  onChange(callback: (user: AuthUser | null) => void): () => void;
  signIn(email: string, password: string, remember: boolean): Promise<void>;
  signUp(email: string, password: string, remember: boolean): Promise<void>;
  resendVerification(): Promise<void>;
  /** Re-reads the account and forces a fresh token so a just-verified email is reflected in it. */
  refresh(): Promise<void>;
  resetPassword(email: string): Promise<void>;
  signOut(): Promise<void>;
  /** Deletes the sign-in record itself (email address and password) after the service has deleted the account. Needs a recent sign-in. */
  deleteSignIn(): Promise<void>;
  getToken(): Promise<string | null>;
}

const MESSAGES: Record<string, string> = {
  'auth/invalid-credential': 'Email or password is not correct.',
  'auth/wrong-password': 'Email or password is not correct.',
  'auth/user-not-found': 'Email or password is not correct.',
  'auth/invalid-email': 'Enter a valid email address.',
  'auth/email-already-in-use': 'An account already exists for this email. Sign in, or reset your password.',
  'auth/weak-password': 'Choose a longer password.',
  'auth/too-many-requests': 'Too many attempts. Wait a few minutes and try again.',
  'auth/network-request-failed': 'Cannot reach the sign-in service. Check your connection.',
  'auth/user-disabled': 'This account has been disabled.',
  'auth/requires-recent-login': 'For your safety, sign in again and then repeat this.',
};

/** Turns an SDK error into a message that is safe and useful to show. */
export function authMessage(error: unknown): string {
  const code = typeof error === 'object' && error !== null && 'code' in error ? String((error as { code: unknown }).code) : '';
  return MESSAGES[code] ?? 'Something went wrong. Try again.';
}

let client: Promise<AuthClient> | null = null;

export function firebaseAuth(): Promise<AuthClient> {
  client ??= (async () => {
    const env = import.meta.env;
    const config = { apiKey: env.VITE_FIREBASE_API_KEY, authDomain: env.VITE_FIREBASE_AUTH_DOMAIN, projectId: env.VITE_FIREBASE_PROJECT_ID, appId: env.VITE_FIREBASE_APP_ID };
    if (!config.apiKey || !config.projectId) throw new Error('Account sign-in is not configured in this build (VITE_FIREBASE_* values are missing).');
    const [{ initializeApp }, sdk] = await Promise.all([import('@firebase/app'), import('@firebase/auth')]);
    const auth = sdk.getAuth(initializeApp(config));
    if (env.VITE_FIREBASE_AUTH_EMULATOR_URL) sdk.connectAuthEmulator(auth, env.VITE_FIREBASE_AUTH_EMULATOR_URL, { disableWarnings: true });
    const toUser = (user: import('@firebase/auth').User | null): AuthUser | null => (user ? { uid: user.uid, email: user.email ?? '', emailVerified: user.emailVerified } : null);
    const listeners = new Set<(user: AuthUser | null) => void>();
    const publish = () => listeners.forEach((listener) => listener(toUser(auth.currentUser)));
    const persist = (remember: boolean) => sdk.setPersistence(auth, remember ? sdk.browserLocalPersistence : sdk.browserSessionPersistence);
    return {
      onChange(callback) { listeners.add(callback); const stop = sdk.onAuthStateChanged(auth, (user) => callback(toUser(user))); return () => { listeners.delete(callback); stop(); }; },
      async signIn(email, password, remember) { await persist(remember); await sdk.signInWithEmailAndPassword(auth, email, password); },
      async signUp(email, password, remember) {
        await persist(remember);
        const created = await sdk.createUserWithEmailAndPassword(auth, email, password);
        await sdk.sendEmailVerification(created.user);
      },
      async resendVerification() { if (auth.currentUser) await sdk.sendEmailVerification(auth.currentUser); },
      async refresh() { if (!auth.currentUser) return; await auth.currentUser.reload(); await auth.currentUser.getIdToken(true); publish(); },
      resetPassword: (email) => sdk.sendPasswordResetEmail(auth, email),
      signOut: () => sdk.signOut(auth),
      async deleteSignIn() { if (auth.currentUser) await sdk.deleteUser(auth.currentUser); },
      getToken: async () => (auth.currentUser ? auth.currentUser.getIdToken() : null),
    };
  })();
  return client;
}
