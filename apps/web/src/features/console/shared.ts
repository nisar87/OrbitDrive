import type { Session } from '../../api';
import type { ReactNode } from 'react';

/** What every console page gets from the shell: the session, a way to report the result of a change, and a way to refresh the shared lists. */
export interface Ctx {
  session: Session;
  /** Ask for a reason, then run the change. The shell shows the outcome and refreshes. */
  ask: (spec: AskSpec) => void;
  report: (kind: 'ok' | 'error', text: string) => void;
  fail: (e: unknown) => void;
  reload: () => Promise<void>;
  go: (page: PageId) => void;
}
export interface AskSpec { title: string; effect: ReactNode; confirmLabel: string; danger?: boolean; done: string; run: (reason: string) => Promise<unknown> }
export type PageId = 'overview' | 'accounts' | 'orgs' | 'billing' | 'audit' | 'settings';

export const shortDate = (iso: string | null): string => (iso ? new Date(iso).toLocaleDateString(undefined, { dateStyle: 'medium' }) : '');
export const dateTime = (iso: string): string => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
export const plural = (n: number, one: string, many = `${one}s`): string => `${n} ${n === 1 ? one : many}`;
