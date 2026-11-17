/**
 * Installable-app lifecycle. One service worker (public/sw.js) that caches only static build assets and icons.
 * An update is never forced: a waiting worker is applied only when the reader asks AND no managed transfer is running,
 * because replacing the page mid-transfer would abandon the write.
 */
type Listener = () => void;
const listeners = new Set<Listener>();
let waiting: ServiceWorker | null = null;
let busyCheck: () => boolean = () => false;
let reloading = false;

const publish = () => { for (const l of listeners) l(); };

export const pwa = {
  subscribe(listener: Listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  updateAvailable: () => waiting !== null,
  /** The app tells the lifecycle code when a download that OrbitDrive itself is writing is running. */
  setBusyCheck(check: () => boolean) { busyCheck = check; },
  canApplyUpdate: () => waiting !== null && !busyCheck(),
  applyUpdate(): boolean {
    if (!waiting || busyCheck()) return false;
    waiting.postMessage('activate-update');
    return true;
  },
  /** Sign-out or account switch: nothing private is ever stored by the worker, and what it holds is dropped anyway. */
  async purge() {
    try { (await navigator.serviceWorker?.getRegistration())?.active?.postMessage('purge'); } catch { /* no worker */ }
  },
  async register(): Promise<void> {
    if (!('serviceWorker' in navigator)) return;
    if (!import.meta.env.PROD && import.meta.env.VITE_REGISTER_SW !== 'true') return;
    try {
      const registration = await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' });
      const track = (worker: ServiceWorker | null) => {
        if (!worker) return;
        const check = () => { if (worker.state === 'installed' && navigator.serviceWorker.controller) { waiting = worker; publish(); } };
        worker.addEventListener('statechange', check);
        check();
      };
      track(registration.waiting);
      registration.addEventListener('updatefound', () => track(registration.installing));
      navigator.serviceWorker.addEventListener('controllerchange', () => {
        if (reloading) return;
        reloading = true;
        window.location.reload();
      });
    } catch { /* installation is optional; the site works in a normal tab */ }
  },
};
