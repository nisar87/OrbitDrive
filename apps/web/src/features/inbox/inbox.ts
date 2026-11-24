import { api, type Session } from '../../api';
import type { InvitationSummary } from '../../protocol';

/**
 * The Inbox of THIS browser: files a PC has offered to it. It only reads what the relay reports and never claims a transfer happened.
 * New offers are noticed while the page is open (polling, faster while visible). Optional system notifications are opt-in, are asked
 * for only after a click, and carry generic text: no file name, no link, nothing secret on a lock screen.
 */
type Listener = () => void;
const listeners = new Set<Listener>();
let items: InvitationSummary[] = [];
let seen = new Set<string>();
let announcements: string[] = [];
let timer: ReturnType<typeof setInterval> | null = null;
let session: Session | null = null;
let first = true;
let failure: string | null = null;
let generation = 0;

const NOTIFY_KEY = 'orbitdrive.notify.v1';
export const POLL_VISIBLE_MS = 4000;
export const POLL_HIDDEN_MS = 30000;
export const TOAST_MS = 8000;

function publish() { items = [...items]; for (const l of listeners) l(); }

export function notificationsSupported(): boolean { return typeof Notification !== 'undefined'; }
export function notificationsEnabled(): boolean {
  try { return notificationsSupported() && Notification.permission === 'granted' && localStorage.getItem(NOTIFY_KEY) === 'on'; } catch { return false; }
}
/** Must be called from a click. Denied or unsupported leaves the Inbox fully working. */
export async function enableNotifications(): Promise<'enabled' | 'denied' | 'unsupported'> {
  if (!notificationsSupported()) return 'unsupported';
  const result = Notification.permission === 'granted' ? 'granted' : await Notification.requestPermission();
  if (result !== 'granted') return 'denied';
  try { localStorage.setItem(NOTIFY_KEY, 'on'); } catch { /* the choice lasts for this visit only */ }
  publish();
  return 'enabled';
}
export function disableNotifications() { try { localStorage.removeItem(NOTIFY_KEY); } catch { /* ignore */ } publish(); }

async function notifyGeneric() {
  if (!notificationsEnabled() || document.visibilityState === 'visible') return;
  const options: NotificationOptions = { body: 'A file is waiting for you. Open MyOrbitDrive to accept it.', tag: 'orbitdrive-inbox', icon: '/icons/icon-192.png', data: { open: 'inbox' } };
  try {
    const registration = await navigator.serviceWorker?.getRegistration();
    if (registration) await registration.showNotification('MyOrbitDrive', options);
    else new Notification('MyOrbitDrive', options);
  } catch { /* a notification that cannot be shown changes nothing else */ }
}

async function refresh(mine: number) {
  if (!session) return;
  try {
    const list = await api.invitations(session);
    if (mine !== generation) return;                         // signed out or switched account while the request was running
    failure = null;
    const fresh = list.invitations.filter((i) => i.state === 'pending' && !seen.has(i.invitationId));
    for (const i of list.invitations) seen.add(i.invitationId);
    if (!first && fresh.length > 0) {
      announcements = [...announcements.slice(-4), fresh.length === 1 ? `New file from ${fresh[0].pcName}: ${fresh[0].name}` : `${fresh.length} new files from ${fresh[0].pcName}`];
      void notifyGeneric();
      const text = announcements[announcements.length - 1];
      setTimeout(() => { if (announcements.includes(text)) { announcements = announcements.filter((a) => a !== text); publish(); } }, TOAST_MS);
    }
    first = false;
    items = list.invitations;
  } catch (e) {
    if (mine !== generation) return;
    failure = e instanceof Error ? e.message : 'Could not check for files.';
  }
  publish();
}

function schedule(mine: number) {
  if (timer) clearInterval(timer);
  timer = setInterval(() => void refresh(mine), document.visibilityState === 'visible' ? POLL_VISIBLE_MS : POLL_HIDDEN_MS);
}

function onVisibility() { if (session) { void refresh(generation); schedule(generation); } }

export const inbox = {
  subscribe(listener: Listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  list: () => items,
  pending: () => items.filter((i) => i.state === 'pending').length,
  announcements: () => announcements,
  dismissAnnouncements() { announcements = []; publish(); },
  error: () => failure,
  start(next: Session) {
    if (session?.device?.id === next.device?.id && timer) { session = next; return; }
    inbox.reset();
    session = next;
    const mine = ++generation;
    document.addEventListener('visibilitychange', onVisibility);
    void refresh(mine);
    schedule(mine);
  },
  refresh: () => refresh(generation),
  async decline(id: string) { if (session) { await api.declineInvitation(session, id); await refresh(generation); } },
  /** Sign-out, unpairing or account switch: forget everything private and stop asking. */
  reset() {
    generation++;
    if (timer) clearInterval(timer);
    timer = null; session = null; items = []; seen = new Set(); announcements = []; first = true; failure = null;
    document.removeEventListener('visibilitychange', onVisibility);
    publish();
  },
};
