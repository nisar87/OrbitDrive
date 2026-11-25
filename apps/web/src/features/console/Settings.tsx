import { useState } from 'react';
import { api } from '../../api';
import { PageHead } from './ui';
import type { Ctx } from './shared';

export function ServiceSettings({ ctx, initial }: { ctx: Ctx; initial: string }) {
  const [version, setVersion] = useState(initial);
  const clean = version.trim();
  return (
    <>
      <PageHead title="Settings" summary="Rules the relay applies to every account." />
      <section className="section" aria-labelledby="minv">
        <h3 id="minv">Oldest PC version allowed</h3>
        <p className="hint">PCs running an older MyOrbitDrive are refused with an update message. Leave empty for no minimum.</p>
        <div className="fields"><label>Version, like 1.2.0<input value={version} inputMode="decimal" onChange={(e) => setVersion(e.target.value)} /></label></div>
        <p className="actions"><button type="button" disabled={clean === initial.trim()} onClick={async () => { try { await api.setAdminSettings(ctx.session, clean); ctx.report('ok', 'Minimum version saved.'); await ctx.reload(); } catch (e) { ctx.fail(e); } }}>Save minimum version</button></p>
      </section>
    </>
  );
}
