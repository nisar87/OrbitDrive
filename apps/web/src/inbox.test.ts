// The Inbox store: duplicates, lost and repeated events, account switching, stale responses, notification text.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { InvitationSummary } from './protocol';

const make = (id: string, patch: Partial<InvitationSummary> = {}): InvitationSummary => ({
  invitationId: id, pcId: 'pc1', pcName: 'Tower', receiverId: 'r1', receiverName: 'Phone', name: `${id}.txt`, size: '10', modifiedUnixMs: '1', state: 'pending',
  sourceOnline: true, createdAtUnixMs: '1', expiresAtUnixMs: String(Date.now() + 3_600_000), ...patch,
});

let responses: Array<InvitationSummary[] | Error | Promise<InvitationSummary[]>> = [];
vi.mock('./api', () => ({
  api: { invitations: vi.fn(async () => { const next = responses.shift() ?? []; if (next instanceof Error) throw next; return { invitations: await next }; }), declineInvitation: vi.fn(async () => ({ ok: true })) },
  INVITATION_PREFIX: 'invitation:',
}));

const session = { idToken: 't', email: 'a@b.c', device: { id: 'd1', secret: 's' } };
async function load() {
  vi.resetModules();
  const module = await import('./features/inbox/inbox');
  return module;
}

const store = new Map<string, string>();
const doc = { visibilityState: 'visible', addEventListener: () => undefined, removeEventListener: () => undefined };
beforeEach(() => {
  vi.useFakeTimers(); responses = []; store.clear(); doc.visibilityState = 'visible';
  vi.stubGlobal('localStorage', { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); }, removeItem: (k: string) => { store.delete(k); } });
  vi.stubGlobal('document', doc);
  vi.stubGlobal('navigator', {});
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('inbox', () => {
  it('lists offers without announcing the ones that were already there, and announces a new one once', async () => {
    const { inbox } = await load();
    responses = [[make('a')], [make('a')], [make('a'), make('b')], [make('b'), make('a')]];
    inbox.start(session);
    await vi.advanceTimersByTimeAsync(0);
    expect(inbox.list().map((i) => i.invitationId)).toEqual(['a']);
    expect(inbox.announcements()).toEqual([]);                           // the first load is not "new"
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.announcements()).toEqual([]);                           // the same event twice is not new
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.announcements()).toEqual(['New file from Tower: b.txt']);
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.announcements()).toHaveLength(1);                       // reordering does not repeat it
    expect(inbox.pending()).toBe(2);
    inbox.reset();
  });

  it('a lost poll keeps the last known list and shows the problem; the next poll recovers', async () => {
    const { inbox } = await load();
    responses = [[make('a')], new Error('offline'), [make('a'), make('c')]];
    inbox.start(session);
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.error()).toBe('offline');
    expect(inbox.list()).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.error()).toBeNull();
    expect(inbox.list()).toHaveLength(2);
    inbox.reset();
  });

  it('forgets everything on reset and ignores an answer that arrives after the account changed', async () => {
    const { inbox } = await load();
    let release!: (value: InvitationSummary[]) => void;
    responses = [new Promise<InvitationSummary[]>((resolve) => { release = resolve; })];
    inbox.start(session);
    inbox.reset();                                                       // signed out while the request is running
    release([make('private')]);
    await vi.advanceTimersByTimeAsync(0);
    expect(inbox.list()).toEqual([]);
    expect(inbox.pending()).toBe(0);
    responses = [[make('mine')]];
    inbox.start({ ...session, device: { id: 'd2', secret: 'x' } });
    await vi.advanceTimersByTimeAsync(0);
    expect(inbox.list().map((i) => i.invitationId)).toEqual(['mine']);
    inbox.reset();
  });

  it('polls slowly while the page is hidden and sends only generic text to the system when notifications are on', async () => {
    const shown: Array<[string, NotificationOptions]> = [];
    class FakeNotification { static permission = 'granted'; constructor(title: string, options: NotificationOptions) { shown.push([title, options]); } }
    vi.stubGlobal('Notification', FakeNotification);
    store.set('orbitdrive.notify.v1', 'on');
    doc.visibilityState = 'hidden';
    const { inbox } = await load();
    responses = [[], [make('secret-name')]];
    inbox.start(session);
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(4000);
    expect(inbox.list()).toHaveLength(0);                                // hidden: not polled at the fast rate
    await vi.advanceTimersByTimeAsync(30_000);
    expect(shown).toHaveLength(1);
    expect(JSON.stringify(shown)).not.toContain('secret-name');
    expect(JSON.stringify(shown)).not.toContain('Tower');
    expect(JSON.stringify(shown)).not.toMatch(/https?:|invitation/i);
    inbox.reset();
  });

  it('denied or unsupported notifications change nothing about the Inbox', async () => {
    class Denied { static permission = 'default'; static async requestPermission() { return 'denied'; } }
    vi.stubGlobal('Notification', Denied);
    const { enableNotifications, notificationsEnabled } = await load();
    expect(await enableNotifications()).toBe('denied');
    expect(notificationsEnabled()).toBe(false);
    vi.stubGlobal('Notification', undefined);
    expect(await enableNotifications()).toBe('unsupported');
  });
});
