import type { ReactNode } from 'react';

interface Props { title: string; account?: string; onSignOut?: () => void; onPairAnother?: () => void; children: ReactNode }

/** Single maximised window on a desktop surface. The multi-window shell, launcher and taskbar apps are phase W4. */
export function Shell({ title, account, onSignOut, onPairAnother, children }: Props) {
  return (
    <div className="desktop">
      <main className="window" aria-labelledby="window-title">
        <header className="titlebar">
          <span className="app-icon" aria-hidden="true" />
          <h1 id="window-title">{title}</h1>
          <span className="spacer" />
          {onPairAnother && <button type="button" className="ghost" onClick={onPairAnother}>Pair a PC</button>}
          {account && <span className="account" title={account}>{account}</span>}
          {onSignOut && <button type="button" className="ghost" onClick={onSignOut}>Sign out</button>}
        </header>
        <div className="window-body">{children}</div>
      </main>
      <footer className="taskbar" aria-label="Taskbar">
        <span className="app-icon active" aria-hidden="true" />
        <span className="taskbar-label">OrbitDrive</span>
        <span className="spacer" />
        <span className="privacy" title="Files travel over TLS through the OrbitDrive relay, which can process them in transit. Nothing is stored there. This is not end-to-end encryption.">TLS via relay · not end-to-end encrypted</span>
      </footer>
    </div>
  );
}
