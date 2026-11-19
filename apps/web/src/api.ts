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

/** Requests that START work on a PC. A lost answer must never make the PC do it twice, so these carry an Idempotency-Key and may be retried with the same one. */
export function startsWork(method: string, path: string): boolean {
  return method === 'POST' && (path === '/v1/download-sessions' || /^\/v1\/pcs\/[^/]+\/(archives|searches|uploads)$/.test(path));
}
export const retryTuning = { networkRetries: 2, delayMs: 600 };
const newKey = (): string => (globalThis.crypto?.randomUUID?.() ?? `k${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`);

async function call<K extends keyof ProtocolTypeMap>(type: K, session: Session | null, method: string, path: string, body?: unknown): Promise<ProtocolTypeMap[K]> {
  const headers: Record<string, string> = {};
  const idempotent = startsWork(method, path);
  if (idempotent) headers['Idempotency-Key'] = newKey();
  const token = session ? (tokenProvider ? await tokenProvider() : session.idToken) : null;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (session?.device) headers['X-OrbitDrive-Device'] = `${session.device.id}.${session.device.secret}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let response: Response | undefined;
  for (let attempt = 0; response === undefined; attempt++) {
    try {
      response = await fetch(GATEWAY_URL + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', referrerPolicy: 'no-referrer' });
    } catch {
      // Only a request that carries its Idempotency-Key is retried: the service answers the repeat with the first answer instead of starting the work again.
      if (!idempotent || attempt >= retryTuning.networkRetries) throw new ApiError('network', 'Cannot reach the OrbitDrive service. Check your connection.', 0);
      await new Promise((resolve) => setTimeout(resolve, retryTuning.delayMs * (attempt + 1)));
    }
  }
  const json: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    try { const failure = validate('ErrorResponse', json); throw new ApiError(failure.error.code, failure.error.message, response.status); }
    catch (e) { if (e instanceof ApiError) throw e; throw new ApiError('internal', `The service answered ${response.status}.`, response.status); }
  }
  return validate(type, json);
}

/**
 * One piece of an upload: the raw bytes, with their SHA-256 in a header. The PC checks the checksum and the position, and its answer
 * (accepted or not, and how many bytes it really holds) is the only thing that moves the upload forward.
 */
async function sendChunk(s: Session, pcId: string, uploadId: string, offset: number, data: ArrayBuffer, sha256: string, signal: AbortSignal): Promise<ProtocolTypeMap['WriteChunkResult']> {
  const headers: Record<string, string> = { 'Content-Type': 'application/octet-stream', 'X-Chunk-Sha256': sha256 };
  const token = tokenProvider ? await tokenProvider() : s.idToken;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (s.device) headers['X-OrbitDrive-Device'] = `${s.device.id}.${s.device.secret}`;
  let response: Response;
  try {
    response = await fetch(`${GATEWAY_URL}/v1/pcs/${encodeURIComponent(pcId)}/uploads/${encodeURIComponent(uploadId)}/chunk?offset=${offset}`,
      { method: 'PUT', headers, body: data, cache: 'no-store', referrerPolicy: 'no-referrer', signal });
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e;
    throw new ApiError('network', 'Cannot reach the OrbitDrive service. Check your connection.', 0);
  }
  const json: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    try { const failure = validate('ErrorResponse', json); throw new ApiError(failure.error.code, failure.error.message, response.status); }
    catch (e) { if (e instanceof ApiError) throw e; throw new ApiError('internal', `The service answered ${response.status}.`, response.status); }
  }
  return validate('WriteChunkResult', json);
}

export const INVITATION_PREFIX = 'invitation:';
/** A finished archive on a PC is downloaded through the same path as a file, written "archive:<id>". */
export const ARCHIVE_PREFIX = 'archive:';

export const api = {
  devSignIn: (email: string) => call('DevSignInResponse', null, 'POST', '/v1/dev/sign-in', { email }),
  claimPairing: (s: Session, id: string, secret: string, browserName: string) => call('ClaimPairingResponse', s, 'POST', `/v1/pairing-requests/${encodeURIComponent(id)}/claim`, { secret, browserName }),
  pairingStatus: (s: Session, id: string) => call('PairingStatusResponse', s, 'GET', `/v1/pairing-requests/${encodeURIComponent(id)}`),
  pcs: (s: Session) => call('PcListResponse', s, 'GET', '/v1/pcs'),
  roots: (s: Session, pcId: string) => call('RootListResponse', s, 'GET', `/v1/pcs/${encodeURIComponent(pcId)}/roots`),
  directory: (s: Session, pcId: string, query: DirectoryQuery) => call('DirectoryPage', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/directory-queries`, query),
  /** A real entry id is an opaque base64url token; an invitation source is written "invitation:<id>" so one download path serves both. */
  prepareDownload: (s: Session, pcId: string, entryId: string) => entryId.startsWith(INVITATION_PREFIX)
    ? call('DownloadSession', s, 'POST', `/v1/invitations/${encodeURIComponent(entryId.slice(INVITATION_PREFIX.length))}/accept`)
    : entryId.startsWith(ARCHIVE_PREFIX)
      ? call('DownloadSession', s, 'POST', '/v1/download-sessions', { pcId, entryId: entryId.slice(ARCHIVE_PREFIX.length), variant: 'archive' })
      : call('DownloadSession', s, 'POST', '/v1/download-sessions', { pcId, entryId }),
  startArchive: (s: Session, pcId: string, entryIds: string[], strict: boolean) => call('ArchiveRef', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/archives`, { entryIds, strict }),
  archiveStatus: (s: Session, pcId: string, archiveId: string) => call('ArchiveStatus', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/archive-status`, { archiveId }),
  cancelArchive: (s: Session, pcId: string, archiveId: string) => call('Ack', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/archive-cancel`, { archiveId }),
  search: (s: Session, pcId: string, request: { searchId: string; query: string; rootId?: string | null; entryId?: string | null; limit: number }) =>
    call('SearchResult', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/searches`, request),
  cancelSearch: (s: Session, pcId: string, searchId: string) => call('Ack', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/search-cancel`, { searchId }),
  startUpload: (s: Session, pcId: string, request: { rootId: string; folderEntryId: string | null; name: string; size: string; collision: 'reject' | 'rename' }) =>
    call('UploadStarted', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/uploads`, request),
  uploadStatus: (s: Session, pcId: string, uploadId: string) => call('UploadStatus', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/upload-status`, { uploadId }),
  uploadChunk: sendChunk,
  finishUpload: (s: Session, pcId: string, uploadId: string) => call('UploadStatus', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/upload-finish`, { uploadId }),
  cancelUpload: (s: Session, pcId: string, uploadId: string) => call('Ack', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/upload-cancel`, { uploadId }),
  checkEntries: (s: Session, pcId: string, entryIds: string[]) => call('CheckEntriesResult', s, 'POST', `/v1/pcs/${encodeURIComponent(pcId)}/entry-checks`, { entryIds }),
  /** In-page preview of a file ("preview") or a generated thumbnail ("thumb:N"): a revocable capability, never a transfer. */
  previewSession: (s: Session, pcId: string, entryId: string, variant: string) => call('DownloadSession', s, 'POST', '/v1/download-sessions', { pcId, entryId, variant }),
  invitations: (s: Session) => call('InvitationList', s, 'GET', '/v1/invitations'),
  declineInvitation: (s: Session, id: string) => call('Ack', s, 'POST', `/v1/invitations/${encodeURIComponent(id)}/decline`),
  devices: (s: Session) => call('BrowserDeviceListResponse', s, 'GET', '/v1/browser-devices'),
  downloadSessions: (s: Session) => call('DownloadSessionList', s, 'GET', '/v1/download-sessions'),
  cancelDownload: (s: Session, sessionId: string) => call('Ack', s, 'DELETE', `/v1/download-sessions/${encodeURIComponent(sessionId)}`),
  revokeDevice: (s: Session, deviceId: string) => call('Ack', s, 'DELETE', `/v1/browser-devices/${encodeURIComponent(deviceId)}`),
  renameDevice: (s: Session, deviceId: string, name: string) => call('Ack', s, 'PATCH', `/v1/browser-devices/${encodeURIComponent(deviceId)}`, { name }),
  disconnectPc: (s: Session, pcId: string) => call('Ack', s, 'DELETE', `/v1/pcs/${encodeURIComponent(pcId)}`),
  account: (s: Session) => call('AccountSummary', s, 'GET', '/v1/account'),
  setPreferences: (s: Session, preferences: { historyRetentionDays: number; notifyInvitations: boolean; diagnosticsConsent: boolean }) => call('Ack', s, 'PUT', '/v1/account/preferences', preferences),
  revokeAllBrowsers: (s: Session) => call('AccountRevokeResult', s, 'POST', '/v1/account/revoke-all'),
  exportAccount: (s: Session) => call('AccountSummary', s, 'GET', '/v1/account/export'),
  deleteAccount: (s: Session, confirm: string) => call('Ack', s, 'DELETE', '/v1/account', { confirm }),
  adminOverview: (s: Session) => call('AdminOverview', s, 'GET', '/v1/admin/overview'),
  adminUsers: (s: Session) => call('AdminUserList', s, 'GET', '/v1/admin/users'),
  adminAudit: (s: Session) => call('AuditList', s, 'GET', '/v1/admin/audit'),
  adminSettings: (s: Session) => call('AdminSettings', s, 'GET', '/v1/admin/settings'),
  setAdminSettings: (s: Session, minAgentVersion: string) => call('Ack', s, 'PUT', '/v1/admin/settings', { minAgentVersion }),
  adminUserAction: (s: Session, uid: string, action: 'suspend' | 'unsuspend' | 'revoke-all' | 'reset-relay-allowance', reason: string) =>
    call(action === 'revoke-all' ? 'AccountRevokeResult' : 'Ack', s, 'POST', `/v1/admin/users/${encodeURIComponent(uid)}/${action}`, { reason }),
  revokeThisBrowser: (s: Session) => call('Ack', s, 'DELETE', '/v1/browser-devices/current'),
};
