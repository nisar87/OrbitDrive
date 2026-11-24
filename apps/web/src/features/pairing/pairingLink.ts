export interface PairingLink { id: string; secret: string }

const CODE = /^[A-Z2-9]{6}-[A-Z2-9]{10}$/;

/** Parses the manual code "ABCDEF-GHJKLMNPQR" shown on the PC. */
export function parseManualCode(input: string): PairingLink | null {
  const code = input.trim().toUpperCase().replace(/\s+/g, '');
  if (!CODE.test(code)) return null;
  const [id, secret] = code.split('-');
  return { id, secret };
}

/**
 * Reads a pairing link from the URL fragment (never sent to any server) and immediately scrubs it
 * from the address bar and history so it cannot leak through screenshots, sync or the Referer header.
 */
export function takePairingLinkFromLocation(location: Location, history: History): PairingLink | null {
  if (!location.pathname.startsWith('/pair') || !location.hash) return null;
  const params = new URLSearchParams(location.hash.slice(1));
  history.replaceState(null, '', '/pair');
  return parseManualCode(`${params.get('id') ?? ''}-${params.get('s') ?? ''}`);
}

export type ScanResult = { link: PairingLink; problem?: undefined } | { link: null; problem: string };

/**
 * Interprets the text of a scanned QR code. Accepts only this site's own pairing link or the bare manual code.
 * The scanned URL is never opened or fetched: a QR pointing anywhere else is refused with an explanation.
 */
export function parseScannedText(text: string, siteOrigin: string): ScanResult {
  const notOurs = 'That QR code is not a MyOrbitDrive pairing code. Scan the one shown under “Pair a phone” on your PC.';
  const trimmed = text.trim();
  if (trimmed.length > 512) return { link: null, problem: notOurs };
  const manual = parseManualCode(trimmed);
  if (manual) return { link: manual };
  let url: URL;
  try { url = new URL(trimmed); } catch { return { link: null, problem: notOurs }; }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') return { link: null, problem: notOurs };
  if (!url.pathname.startsWith('/pair') || !url.hash) return { link: null, problem: notOurs };
  if (url.origin !== siteOrigin) return { link: null, problem: `That code belongs to ${url.host}, not this site. Open that address, or scan the code from a PC set up for ${new URL(siteOrigin).host}.` };
  const params = new URLSearchParams(url.hash.slice(1));
  const link = parseManualCode(`${params.get('id') ?? ''}-${params.get('s') ?? ''}`);
  return link ? { link } : { link: null, problem: notOurs };
}
