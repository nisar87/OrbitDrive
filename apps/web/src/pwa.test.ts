// The service worker must never touch private traffic. These tests run the real public/sw.js with a fake worker scope.
import { describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

type Handler = (event: Record<string, unknown>) => void;

function load() {
  const handlers: Record<string, Handler> = {};
  const stores = new Map<string, Map<string, unknown>>();
  const caches = {
    keys: async () => [...stores.keys()],
    delete: async (name: string) => stores.delete(name),
    open: async (name: string) => {
      if (!stores.has(name)) stores.set(name, new Map());
      const store = stores.get(name)!;
      return { match: async (request: { url: string }) => store.get(request.url), put: async (request: { url: string }, response: unknown) => { store.set(request.url, response); } };
    },
  };
  const network = vi.fn(async (request: { url: string }) => ({ ok: true, type: 'basic', url: request.url, clone() { return this; } }));
  const skipWaiting = vi.fn();
  const self = {
    location: { origin: 'https://app.example' },
    addEventListener: (name: string, handler: Handler) => { handlers[name] = handler; },
    skipWaiting, clients: { claim: async () => undefined, matchAll: async () => [], openWindow: async () => undefined },
  };
  runInNewContext(readFileSync(new URL('../public/sw.js', import.meta.url), 'utf8'), { self, caches, fetch: network, URL, Promise });
  const fetchEvent = (url: string, method = 'GET') => {
    let answered: Promise<unknown> | undefined;
    handlers.fetch({ request: { url, method }, respondWith: (p: Promise<unknown>) => { answered = p; } });
    return answered;
  };
  return { handlers, stores, network, skipWaiting, fetchEvent };
}

describe('service worker', () => {
  it('never intercepts anything private: relay calls, pairing, sign-in, listings, downloads, pages', () => {
    const sw = load();
    for (const url of [
      'https://relay.example/v1/downloads/abc', 'https://relay.example/v1/invitations', 'https://app.example/v1/invitations',
      'https://app.example/pair#id=1&s=2', 'https://app.example/', 'https://app.example/index.html', 'https://app.example/api/signin',
      'https://app.example/assets/evil/../../v1/x', 'https://app.example/assets/app.js.map', 'https://identitytoolkit.googleapis.com/v1/accounts:signIn',
    ]) expect(sw.fetchEvent(url), url).toBeUndefined();
    expect(sw.fetchEvent('https://app.example/assets/app-1a2b.js', 'POST')).toBeUndefined();
    expect(sw.network).not.toHaveBeenCalled();
    expect(sw.stores.size).toBe(0);
  });

  it('caches only hashed build assets and icons from its own origin', async () => {
    const sw = load();
    await sw.fetchEvent('https://app.example/assets/index-a1b2.js');
    await sw.fetchEvent('https://app.example/icons/icon-192.png');
    expect([...sw.stores.get('orbitdrive-shell-v1')!.keys()].sort()).toEqual(['https://app.example/assets/index-a1b2.js', 'https://app.example/icons/icon-192.png']);
    await sw.fetchEvent('https://app.example/assets/index-a1b2.js');
    expect(sw.network).toHaveBeenCalledTimes(2);                      // the second asset request was served from the cache
  });

  it('does not take over the page by itself and applies an update only when told to', () => {
    const sw = load();
    sw.handlers.install({});
    expect(sw.skipWaiting).not.toHaveBeenCalled();
    sw.handlers.message({ data: 'something-else' });
    expect(sw.skipWaiting).not.toHaveBeenCalled();
    sw.handlers.message({ data: 'activate-update' });
    expect(sw.skipWaiting).toHaveBeenCalledTimes(1);
  });

  it('purges everything it holds on request (sign-out, account switch)', async () => {
    const sw = load();
    await sw.fetchEvent('https://app.example/icons/icon-192.png');
    expect(sw.stores.size).toBe(1);
    let done: Promise<unknown> | undefined;
    sw.handlers.message({ data: 'purge', waitUntil: (p: Promise<unknown>) => { done = p; } });
    await done;
    expect(sw.stores.size).toBe(0);
  });
});

describe('page lifecycle', () => {
  it('a worker taking control on the first install never reloads the page; an update the user applied does', async () => {
    vi.resetModules();
    const listeners: Record<string, () => void> = {};
    const reload = vi.fn();
    const registration = { waiting: null as unknown, installing: null, addEventListener: () => undefined };
    const container = { controller: null as unknown, register: async () => registration, addEventListener: (n: string, h: () => void) => { listeners[n] = h; }, getRegistration: async () => undefined };
    vi.stubGlobal('navigator', { serviceWorker: container });
    vi.stubGlobal('window', { location: { reload } });
    vi.stubEnv('VITE_REGISTER_SW', 'true');
    const { pwa } = await import('./pwa');
    await pwa.register();
    listeners.controllerchange();                         // first install: clients.claim() fires this
    expect(reload).not.toHaveBeenCalled();

    const postMessage = vi.fn();
    registration.waiting = { state: 'installed', postMessage, addEventListener: () => undefined };
    container.controller = {};
    await pwa.register();                                 // a later visit finds a waiting update
    expect(pwa.applyUpdate()).toBe(true);
    listeners.controllerchange();
    expect(reload).toHaveBeenCalledTimes(1);
    vi.unstubAllGlobals(); vi.unstubAllEnvs();
  });
});
