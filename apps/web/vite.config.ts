import { loadEnv, type Plugin } from 'vite';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

/** Throws when a production bundle would ship development identities or a non-HTTPS relay. */
export function assertProductionSafe(mode: string, env: Record<string, string | undefined>): void {
  if (mode !== 'production') return;
  const problems: string[] = [];
  if (env.VITE_AUTH_MODE !== 'firebase') problems.push('VITE_AUTH_MODE must be "firebase" (development identities cannot ship)');
  if (!/^https:\/\/[^/]+$/.test(env.VITE_GATEWAY_URL ?? '')) problems.push('VITE_GATEWAY_URL must be the https origin of the dedicated relay');
  if (/localhost|127\.0\.0\.1|emulator/i.test(env.VITE_GATEWAY_URL ?? '')) problems.push('VITE_GATEWAY_URL must not point at a local or emulator host');
  if (env.VITE_USE_EMULATORS === 'true' || env.VITE_FIREBASE_AUTH_EMULATOR_URL) problems.push('Firebase emulator settings (VITE_USE_EMULATORS / VITE_FIREBASE_AUTH_EMULATOR_URL) must not be set');
  for (const name of ['VITE_FIREBASE_API_KEY', 'VITE_FIREBASE_AUTH_DOMAIN', 'VITE_FIREBASE_PROJECT_ID', 'VITE_FIREBASE_APP_ID'])
    if (!env[name]) problems.push(`${name} is required`);
  if (/^demo-/.test(env.VITE_FIREBASE_PROJECT_ID ?? '')) problems.push('VITE_FIREBASE_PROJECT_ID must be a real project, not a demo- emulator project');
  if (problems.length) throw new Error(`Refusing to build a production bundle:\n - ${problems.join('\n - ')}`);
}

function productionGuard(): Plugin {
  return { name: 'orbitdrive-production-guard', config(_config, { mode }) { assertProductionSafe(mode, { ...loadEnv(mode, process.cwd(), 'VITE_'), ...process.env }); } };
}

export default defineConfig({
  plugins: [react(), productionGuard()],
  build: { outDir: 'dist', sourcemap: false },
  server: { port: 5173, strictPort: true },
  test: { include: ['src/**/*.test.ts'] },
});
