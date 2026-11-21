import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, retryTuning, startsWork, type Session } from './api';

const session: Session = { idToken: 't', email: 'a@b.c' };
const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
afterEach(() => { vi.unstubAllGlobals(); retryTuning.delayMs = 600; });

describe('retrying a request that starts work', () => {
  it('knows exactly which requests start work', () => {
    expect(startsWork('POST', '/v1/download-sessions')).toBe(true);
    expect(startsWork('POST', '/v1/pcs/pc1/archives')).toBe(true);
    expect(startsWork('POST', '/v1/pcs/pc1/searches')).toBe(true);
    expect(startsWork('POST', '/v1/pcs/pc1/uploads')).toBe(true);
    expect(startsWork('GET', '/v1/download-sessions')).toBe(false);
    expect(startsWork('DELETE', '/v1/download-sessions/abc')).toBe(false);
    expect(startsWork('POST', '/v1/pcs/pc1/directory-queries')).toBe(false);
    expect(startsWork('POST', '/v1/pcs/pc1/uploads/u1/chunk')).toBe(false);
    expect(startsWork('POST', '/v1/pcs/a/b/archives')).toBe(false);
  });

  it('retries a lost answer with the SAME key, and every request gets a fresh key', async () => {
    retryTuning.delayMs = 1;
    const seen: string[] = [];
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      seen.push((init.headers as Record<string, string>)['Idempotency-Key']);
      if (seen.length < 3) throw new TypeError('network down');
      return ok({ archiveId: 'a1' });
    });
    vi.stubGlobal('fetch', fetchMock);
    expect(await api.startArchive(session, 'pc1', ['e1'], false)).toEqual({ archiveId: 'a1' });
    expect(seen).toHaveLength(3);
    expect(new Set(seen).size).toBe(1);
    expect(seen[0]).toMatch(/^[A-Za-z0-9_.:-]{8,128}$/);
    await api.startArchive(session, 'pc1', ['e1'], false);
    expect(seen[3]).not.toBe(seen[0]);
  });

  it('gives up with a plain network error after the bounded number of retries', async () => {
    retryTuning.delayMs = 1;
    const fetchMock = vi.fn(async () => { throw new TypeError('down'); });
    vi.stubGlobal('fetch', fetchMock);
    await expect(api.startArchive(session, 'pc1', ['e1'], false)).rejects.toMatchObject({ code: 'network' });
    expect(fetchMock).toHaveBeenCalledTimes(1 + retryTuning.networkRetries);
  });

  it('never retries (and sends no key) for a request that does not start work', async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => { expect((init.headers as Record<string, string>)['Idempotency-Key']).toBeUndefined(); throw new TypeError('down'); });
    vi.stubGlobal('fetch', fetchMock);
    await expect(api.downloadSessions(session)).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not retry an answer the service gave (an error is an answer)', async () => {
    const fetchMock = vi.fn(async () => ok({ error: { code: 'forbidden', message: 'no' } }, 403));
    vi.stubGlobal('fetch', fetchMock);
    await expect(api.startArchive(session, 'pc1', ['e1'], false)).rejects.toMatchObject({ code: 'forbidden', status: 403 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('a browser secret replaced by the service', () => {
  const device = { id: 'dev1', secret: 'old-secret-0123456789' };
  const listBody = { pcs: [] };

  it('adopts the new secret for its own approval and sends it on the next request', async () => {
    const seen: string[] = [];
    const rotated = 'dev1.new-secret-0123456789';
    vi.stubGlobal('fetch', vi.fn(async (_u: string, init: RequestInit) => {
      seen.push((init.headers as Record<string, string>)['X-OrbitDrive-Device']);
      const r = ok(listBody);
      if (seen.length === 1) r.headers.set('X-OrbitDrive-Device-Rotated', rotated);
      return r;
    }));
    const session: Session = { idToken: 't', email: 'a@b.c', device: { ...device } };
    await api.pcs(session).catch(() => undefined);
    await api.pcs(session).catch(() => undefined);
    expect(seen[0]).toBe('dev1.old-secret-0123456789');
    expect(seen[1]).toBe('dev1.new-secret-0123456789');
  });

  it('ignores a replacement that names a different approval or is too short', async () => {
    const seen: string[] = [];
    let n = 0;
    vi.stubGlobal('fetch', vi.fn(async (_u: string, init: RequestInit) => {
      seen.push((init.headers as Record<string, string>)['X-OrbitDrive-Device']);
      const r = ok(listBody);
      r.headers.set('X-OrbitDrive-Device-Rotated', n++ === 0 ? 'someone-else.new-secret-0123456789' : 'dev1.short');
      return r;
    }));
    const session: Session = { idToken: 't', email: 'a@b.c', device: { ...device } };
    for (let i = 0; i < 3; i++) await api.pcs(session).catch(() => undefined);
    expect(new Set(seen)).toEqual(new Set(['dev1.old-secret-0123456789']));
  });
});
