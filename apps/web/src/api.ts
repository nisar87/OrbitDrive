import { validate, type ErrorCode, type ProtocolTypeMap, type DirectoryQuery } from './protocol';

export const GATEWAY_URL: string = (import.meta.env.VITE_GATEWAY_URL ?? '').replace(/\/$/, '');
export const AUTH_MODE: string = import.meta.env.VITE_AUTH_MODE ?? 'firebase';

export class ApiError extends Error {
  constructor(public readonly code: ErrorCode | 'network', message: string, public readonly status: number) { super(message); }
}

export interface Session { idToken: string; email: string; device?: { id: string; secret: string } }

const KEY = 'orbitdrive.session.v1';
export function loadSession(): Session | null {
  try { return JSON.parse(localStorage.getItem(KEY) ?? 'null') as Session | null; } catch { return null; }
}
export function saveSession(session: Session | null): void {
  if (session) localStorage.setItem(KEY, JSON.stringify(session)); else localStorage.removeItem(KEY);
}

let tokenProvider: (() => Promise<string | null>) | null = null;
/** Firebase mode: tokens come from the SDK (which refreshes them) instead of being stored by OrbitDrive. */
export function setTokenProvider(provider: (() => Promise<string | null>) | null): void { tokenProvider = provider; }

const deviceKey = (uid: string) => `orbitdrive.device.v1.${uid}`;
export function loadDevice(uid: string): Session['device'] {
  try { return (JSON.parse(localStorage.getItem(deviceKey(uid)) ?? 'null') as Session['device']) ?? undefined; } catch { return undefined; }
}
export function saveDevice(uid: string, device: Session['device']): void {
  if (device) localStorage.setItem(deviceKey(uid), JSON.stringify(device)); else localStorage.removeItem(deviceKey(uid));
}

async function call<K extends keyof ProtocolTypeMap>(type: K, session: Session | null, method: string, path: string, body?: unknown): Promise<ProtocolTypeMap[K]> {
  const headers: Record<string, string> = {};
  const token = session ? (tokenProvider ? await tokenProvider() : session.idToken) : null;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (session?.device) headers['X-OrbitDrive-Device'] = `${session.device.id}.${session.device.secret}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let response: Response;
  try {
    response = await fetch(GATEWAY_URL + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', referrerPolicy: 'no-referrer' });
  } catch {
    throw new ApiError('network', 'Cannot reach the OrbitDrive service. Check your connection.', 0);
  }
  const json: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    try { const failure = validate('ErrorResponse', json); throw new ApiError(failure.error.code, failure.error.message, response.status); }
    catch (e) { if (e instanceof ApiError) throw e; throw new ApiError('internal', `The service answered ${response.status}.`, response.status); }
  }
  return validate(type, json);
}

export const api = {
  devSignIn: (email: string) => call('DevSignInResponse', null, 'POST', '/v1/dev/sign-in', { email }),
  claimPairing: (s: Session, id: string, secret: string, browserName: string) => call('ClaimPairingResponse', s, 'POST', `/v1/pairing-requests/${encodeURIComponent(id)}/claim`, { secret, browserName }),
  pairingStatus: (s: Session, id: string) => call('PairingStatusResponse', s, 'GET', `/v1/pairing-requests/${encodeURIComponent(id)}`),
  pcs: (s: Session) => call('PcListResponse', s, 'GET', '/v1/pcs'),
  roots: (s: Session, pcId: string) => call('RootListResponse', s, 'GET', `/v1/pcs/${encodeURIComponent(pcId)}/roots`),
  directory: (s: Session, pcId: string, query: DirectoryQuery) => call('DirectoryPage', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/directory-queries`, query),
  prepareDownload: (s: Session, pcId: string, entryId: string) => call('DownloadSession', s, 'POST', '/v1/download-sessions', { pcId, entryId }),
  devices: (s: Session) => call('BrowserDeviceListResponse', s, 'GET', '/v1/browser-devices'),
  revokeDevice: (s: Session, deviceId: string) => call('Ack', s, 'DELETE', `/v1/browser-devices/${encodeURIComponent(deviceId)}`),
  disconnectPc: (s: Session, pcId: string) => call('Ack', s, 'DELETE', `/v1/pcs/${encodeURIComponent(pcId)}`),
  revokeThisBrowser: (s: Session) => call('Ack', s, 'DELETE', '/v1/browser-devices/current'),
};
