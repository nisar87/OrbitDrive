import { useSyncExternalStore } from 'react';
import { pwa } from '../../pwa';
import type { Prefs, ThemeChoice, ViewMode } from '../../prefs';
import { AUTH_MODE, GATEWAY_URL, type Session } from '../../api';
import type { AccountSummary } from '../../protocol';
import { AccountPanel } from '../account/AccountPanel';

interface Props { prefs: Prefs; onPrefs: (next: Prefs) => void; account: string; onSignOut: () => void; session: Session; onSummary: (summary: AccountSummary) => void; onAuthLost: (error: unknown) => void }

/** Appearance and Explorer preferences (stored in this browser only) and a plain statement of how files travel. */
function AppPanel() {
  const hasUpdate = useSyncExternalStore(pwa.subscribe, pwa.updateAvailable);
  const canApply = useSyncExternalStore(pwa.subscribe, pwa.canApplyUpdate);
  return (
    <article className="panel" aria-labelledby="install-title">
      <h2 id="install-title">Install and notifications</h2>
      <p className="hint">
        OrbitDrive can be added to your home screen. On Android (Chrome) open the browser menu and choose “Install app”. On iPhone and iPad use Share, then
        “Add to Home Screen”; on iOS, notifications work only for the installed app, not for an ordinary Safari tab. Notifications are optional and
        are turned on from the Inbox. The installed app caches only its own program files and icons, never your files, listings or sign-in.
      </p>
      {hasUpdate && (
        <>
          <p role="status">A new version of OrbitDrive is ready.</p>
          <button type="button" disabled={!canApply} onClick={() => pwa.applyUpdate()}>Reload to update</button>
          {!canApply && <p className="hint">A download that OrbitDrive is saving is still running. The update waits until it finishes so nothing is cut off.</p>}
        </>
      )}
    </article>
  );
}

export function SettingsApp({ prefs, onPrefs, account, onSignOut, session, onSummary, onAuthLost }: Props) {
  return (
    <div className="app-page settings">
      <article className="panel" aria-labelledby="appearance-title">
        <h2 id="appearance-title">Appearance</h2>
        <fieldset>
          <legend>Theme</legend>
          {(['system', 'light', 'dark'] as ThemeChoice[]).map((theme) => (
            <label key={theme} className="check">
              <input type="radio" name="theme" checked={prefs.theme === theme} onChange={() => onPrefs({ ...prefs, theme })} />
              {theme === 'system' ? 'Same as this device' : theme === 'light' ? 'Light' : 'Dark'}
            </label>
          ))}
        </fieldset>
      </article>
      <article className="panel" aria-labelledby="explorer-title">
        <h2 id="explorer-title">File Explorer</h2>
        <label className="stack" htmlFor="default-view">Show folders as</label>
        <select id="default-view" value={prefs.view} onChange={(event) => onPrefs({ ...prefs, view: event.target.value as ViewMode })}>
          <option value="details">Details</option><option value="list">List</option><option value="icons">Icons</option>
        </select>
        <label className="check"><input type="checkbox" checked={prefs.showHidden} onChange={(event) => onPrefs({ ...prefs, showHidden: event.target.checked })} />Show hidden items</label>
        <p className="hint">These choices are saved in this browser only. No file names or contents are saved.</p>
      </article>
      <AppPanel />
      <AccountPanel session={session} onSummary={onSummary} onAuthLost={onAuthLost} onSignedOut={async () => onSignOut()} />
      <article className="panel" aria-labelledby="account-title">
        <h2 id="account-title">Account and connection</h2>
        <dl>
          <dt>Signed in as</dt><dd><bdi>{account}</bdi></dd>
          <dt>Relay</dt><dd><bdi>{GATEWAY_URL}</bdi></dd>
          <dt>Sign-in</dt><dd>{AUTH_MODE === 'dev' ? 'Development identities (not for real use)' : 'Email and password'}</dd>
        </dl>
        <p className="hint">Files travel over TLS through the OrbitDrive relay, which can process them in transit and stores nothing. This is not end-to-end encryption. Access is read-only.</p>
        <button type="button" className="secondary" onClick={onSignOut}>Sign out of this browser</button>
      </article>
    </div>
  );
}
