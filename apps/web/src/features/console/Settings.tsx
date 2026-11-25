import { useState } from 'react';
import { api } from '../../api';
import type { AdminOverview } from '../../protocol';
import { Badge, PageHead } from './ui';
import type { Ctx } from './shared';

export function ServiceSettings({ ctx, initial, overview }: { ctx: Ctx; initial: string; overview: AdminOverview | null }) {
  const [version, setVersion] = useState(initial);
  const clean = version.trim();
  return (
    <>
      <PageHead title="Settings" summary="Rules the relay applies to every account, and how this console is protected." />
      <section className="section" aria-labelledby="sec">
        <h3 id="sec">Extra sign-in step for this console</h3>
        <p>Status: {!overview ? 'loading' : overview.secondFactorRequired ? <Badge tone="ok">Required</Badge> : <Badge tone="wait">Not required</Badge>}</p>
        <p className="hint">With it on, a password alone cannot open this console: you also approve each sign-in with an authenticator app. Customers are not affected.</p>
        {overview && !overview.secondFactorRequired && (
          <ol className="steps">
            <li>In the Firebase console, turn on two-step sign-in for your project, then add an authenticator app to your own operator account.</li>
            <li>Sign out of MyOrbitDrive and sign in again, approving the extra step.</li>
            <li>On the relay server, run the setup script again with the option <code>--require-operator-second-factor</code>. The relay guide, section &ldquo;Operator second factor&rdquo;, has the exact command.</li>
          </ol>
        )}
      </section>
      {overview?.billingDamaged && (
        <section className="zone" aria-labelledby="bill"><h3 id="bill">Billing file could not be read</h3>
          <p>It was set aside and plans and ordering are off. Restore it from a backup on the relay server; the relay guide, section &ldquo;Billing file damaged&rdquo;, lists the steps.</p></section>
      )}
      <section className="section" aria-labelledby="minv">
        <h3 id="minv">Oldest PC version allowed</h3>
        <p className="hint">PCs running an older MyOrbitDrive are refused with an update message. Leave empty for no minimum.</p>
        <div className="fields"><label>Version, like 1.2.0<input value={version} inputMode="decimal" onChange={(e) => setVersion(e.target.value)} /></label></div>
        <p className="actions"><button type="button" disabled={clean === initial.trim()} onClick={() => ctx.ask({ title: 'Change the oldest PC version allowed', effect: clean === '' ? 'PCs of any version will be accepted.' : <>PCs older than {clean} will be refused with an update message.</>, confirmLabel: 'Save minimum version', danger: clean !== '', done: 'Minimum version saved.', run: async (reason) => { await api.setAdminSettings(ctx.session, clean, reason); await ctx.reload(); } })}>Save minimum version</button></p>
      </section>
    </>
  );
}
