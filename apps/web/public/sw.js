// OrbitDrive service worker. One job: keep the static application shell fast and let the app be installed.
// It NEVER caches (and never even sees a response for): sign-in, pairing, directory listings, invitations, download links or file
// bytes. Those requests go to the relay, which is another origin, and any same-origin request that is not a hashed build asset or an
// icon goes straight to the network. A new version waits until the page agrees to reload (never while a managed transfer runs).
const CACHE = 'orbitdrive-shell-v1';
const STATIC = /^\/(assets\/[^/]+\.(js|css|woff2?)|icons\/[^/]+\.png)$/;

self.addEventListener('install', () => { /* no skipWaiting: the page decides when to switch */ });
self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) if (name !== CACHE) await caches.delete(name);
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || !STATIC.test(url.pathname)) return;   // everything else: untouched, straight to the network
  event.respondWith((async () => {
    const cache = await caches.open(CACHE);
    const hit = await cache.match(request);
    if (hit) return hit;
    const response = await fetch(request);
    if (response.ok && response.type === 'basic') await cache.put(request, response.clone());
    return response;
  })());
});

self.addEventListener('message', (event) => {
  if (event.data === 'activate-update') self.skipWaiting();
  if (event.data === 'purge') event.waitUntil(caches.keys().then((names) => Promise.all(names.map((name) => caches.delete(name)))));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const target = windows[0];
    if (target) { await target.focus(); target.postMessage({ open: 'inbox' }); }
    else await self.clients.openWindow('/');
  })());
});
