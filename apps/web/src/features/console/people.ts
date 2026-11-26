/** Who an account is, for the operator console only. The service sends emails to operators and nobody else (R65); nothing here is ever put in a URL or a log. */

export interface Person { uid: string; email: string | null; name: string | null }
export type People = ReadonlyMap<string, Person>;

export const NOT_KNOWN = 'Email not known yet (shows after their next sign-in)';

/** A short form of an account id for reading beside an email: the first six and last four characters. */
export function shortUid(uid: string): string { return uid.length > 12 ? `${uid.slice(0, 6)}…${uid.slice(-4)}` : uid; }

/** Builds the lookup from the directory and from any rows that carry an email themselves (the rows win: they are read in the same request as the table). */
export function buildPeople(directory: { uid: string; email: string; name?: string | null }[], rows: { uid: string; email?: string | null; name?: string | null }[] = []): People {
  const map = new Map<string, Person>();
  for (const p of directory) map.set(p.uid, { uid: p.uid, email: p.email || null, name: p.name || null });
  for (const r of rows) if (r.email) map.set(r.uid, { uid: r.uid, email: r.email, name: r.name || null });
  return map;
}

/** Accepts a bare account id or an "acct:" billing subject; returns the id, or null for anything else (organisation ids, order ids, "service"). */
export function uidOfSubject(value: string): string | null { return value.startsWith('acct:') ? value.slice(5) : value; }

export function lookup(people: People, uid: string): Person { return people.get(uid) ?? { uid, email: null, name: null }; }

/** One line that names an account for a dialog or a screen reader: "name, email (short id)" or "short id (email not known yet)". Never blank. */
export function accountLabel(people: People, uid: string): string {
  const p = lookup(people, uid);
  if (!p.email) return `${shortUid(uid)} (email not known yet)`;
  return `${p.name ? `${p.name}, ` : ''}${p.email} (${shortUid(uid)})`;
}

/** Everything a search box should match for an account: email, name, and the full id. */
export function searchText(people: People, uid: string): string {
  const p = lookup(people, uid);
  return `${p.email ?? ''} ${p.name ?? ''} ${uid}`;
}
