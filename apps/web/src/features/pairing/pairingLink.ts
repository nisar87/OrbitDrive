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
