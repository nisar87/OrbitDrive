import { lookup, NOT_KNOWN, shortUid, type People } from './people';

/** Names an account everywhere in the console: email first (with the display name when the sign-in gave one), then the short id. Never blank. */
export function Who({ uid, people, stacked = true }: { uid: string; people: People; stacked?: boolean }) {
  const p = lookup(people, uid);
  return (
    <span className={`who${stacked ? ' stacked' : ''}`} title={uid}>
      {p.email ? <><bdi className="who-email">{p.name ? `${p.name} · ` : ''}{p.email}</bdi></> : <span className="who-missing">{NOT_KNOWN}</span>}
      <bdi className="who-id idtext">{shortUid(uid)}</bdi>
    </span>
  );
}
