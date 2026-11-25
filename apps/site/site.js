// MyOrbitDrive marketing site behaviour. No third-party scripts, no tracking, no storage.
const CONFIG = {
  // Preview values; the owner sets the real addresses when the site is approved.
  appUrl: 'https://app.myorbitdrive.app/',
  downloadUrl: 'https://github.com/nisar87/OrbitDrive/releases/tag/desktop-dev',
};
document.querySelectorAll('[data-app]').forEach((a) => { a.href = CONFIG.appUrl; });
document.querySelectorAll('[data-dl]').forEach((a) => { a.href = CONFIG.downloadUrl; });
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;


// Mobile menu
const mb = document.querySelector('.menu-btn'), nav = document.getElementById('nav');
mb.addEventListener('click', () => { const o = nav.classList.toggle('open'); mb.setAttribute('aria-expanded', String(o)); });
nav.addEventListener('click', (e) => { if (e.target.closest('a')) { nav.classList.remove('open'); mb.setAttribute('aria-expanded', 'false'); } });

// Tabs (arrow keys, Home, End)
const tabs = [...document.querySelectorAll('[role=tab]')];
function select(t) {
  tabs.forEach((x) => { const on = x === t; x.setAttribute('aria-selected', on); x.tabIndex = on ? 0 : -1; document.getElementById(x.getAttribute('aria-controls')).hidden = !on; });
  t.focus();
}
tabs.forEach((t, i) => {
  t.addEventListener('click', () => select(t));
  t.addEventListener('keydown', (e) => {
    const k = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (k === undefined) return; e.preventDefault(); select(tabs[(k + tabs.length) % tabs.length]);
  });
});

// Reveal on scroll (skipped entirely when reduced motion is requested)
const items = document.querySelectorAll('.reveal');
if (reduce || !('IntersectionObserver' in window)) items.forEach((e) => e.classList.add('in'));
else { const io = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } }), { threshold: .15 }); items.forEach((e) => io.observe(e)); }

// Pairing simulation (illustrative only)
let timer;
document.getElementById('simbtn').addEventListener('click', () => {
  clearInterval(timer);
  const code = document.getElementById('simcode'), msg = document.getElementById('simmsg');
  code.textContent = String(Math.floor(Math.random() * 900000) + 100000);
  let left = 120;
  const tick = () => { msg.textContent = left > 0 ? `Demo code. In the real app it expires in two minutes and you must also approve it on the PC. ${left}s left.` : 'Expired. Real codes expire after two minutes.'; if (left-- <= 0) clearInterval(timer); };
  tick(); timer = setInterval(tick, reduce ? 10000 : 1000);
});

// Gallery with filter and enlargement
const SHOTS = [
  ['desktop-04-pc-online', 'desktop', 'The PC shows online with its approved folder', 'Browser'],
  ['desktop-05-explorer', 'desktop', 'Browsing the approved folder', 'Browser'],
  ['desktop-06-photos', 'desktop', 'A folder of pictures', 'Browser'],
  ['desktop-07-preview-image', 'desktop', 'A picture opened in Preview', 'Browser'],
  ['desktop-08-preview-pdf', 'desktop', 'A PDF opened in Preview', 'Browser'],
  ['desktop-09-icons', 'desktop', 'Icon view', 'Browser'],
  ['desktop-10-transfers', 'desktop', 'Transfers say what the app can and cannot see', 'Browser'],
  ['desktop-13-inbox', 'desktop', 'A file offered from the PC waits in the Inbox', 'Browser'],
  ['desktop-11-dark', 'desktop', 'Dark theme', 'Browser'],
  ['phone-05-explorer', 'phone', 'Folder list on a phone-sized screen', 'Phone-sized'],
  ['phone-07-preview-image', 'phone', 'Picture preview on a phone-sized screen', 'Phone-sized'],
  ['phone-10-transfers', 'phone', 'Transfers on a phone-sized screen', 'Phone-sized'],
  ['phone-13-inbox', 'phone', 'The Inbox on a phone-sized screen', 'Phone-sized'],
  ['phone-11-dark', 'phone', 'Dark theme on a phone-sized screen', 'Phone-sized'],
];
const WIN = [['win-home', 'The Windows app home'], ['win-devices', 'Paired devices, removable at any time'], ['win-pair', 'Pairing with a QR code'], ['win-dark', 'The Windows app in dark theme']];
const gal = document.getElementById('gal');
const entries = [
  ...SHOTS.map(([f, k, c, l]) => ({ src: `assets/shots/${f}.webp`, kind: k, cap: c, label: l })),
  ...WIN.map(([f, c]) => ({ src: `assets/win/${f}.webp`, kind: 'windows', cap: c, label: 'Windows app' })),
];
entries.forEach((e) => {
  const b = document.createElement('button'); b.type = 'button'; b.className = e.kind === 'phone' ? 'phone' : ''; b.dataset.kind = e.kind;
  const alt = `${e.cap} (${e.label})`;
  b.innerHTML = `<img loading="lazy" alt="" src="${e.src}"><span></span>`; b.querySelector('span').textContent = alt; b.setAttribute('aria-label', `Enlarge: ${alt}`);
  b.addEventListener('click', () => { const lb = document.getElementById('lb'); const im = document.createElement('img'); im.src = e.src; im.alt = alt; document.getElementById('lbbox').replaceChildren(im); document.getElementById('lbcap').textContent = alt; lb.showModal(); });
  gal.append(b);
});
document.querySelectorAll('.filters button').forEach((fb) => fb.addEventListener('click', () => {
  document.querySelectorAll('.filters button').forEach((x) => x.setAttribute('aria-pressed', String(x === fb)));
  gal.querySelectorAll('button').forEach((b) => { b.hidden = fb.dataset.f !== 'all' && b.dataset.kind !== fb.dataset.f; });
}));
const lb = document.getElementById('lb');
document.getElementById('lbx').addEventListener('click', () => lb.close());
lb.addEventListener('close', () => document.getElementById('lbbox').replaceChildren());
lb.addEventListener('click', (e) => { if (e.target === lb) lb.close(); });

