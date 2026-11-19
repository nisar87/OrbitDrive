// W9: the upload sender's state machine against a fake PC (the network is mocked; the real wire is covered by tests/integration/uploads.test.mjs).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, type Session } from './api';
import { resetUploadsForTests, tuning, uploads, type UploadTarget } from './features/upload/upload';

const session: Session = { idToken: 't', email: 'a@b.c' };
const target: UploadTarget = { pcId: 'pc1', pcName: 'Desk', rootId: 'r1', folderEntryId: null, path: ['Photos'] };
const file = (name: string, bytes: number) => new File([new Uint8Array(bytes).map((_, i) => i % 251)], name);
const status = (committed: number, patch = {}) => ({ uploadId: 'u1', state: 'receiving', name: 'x', size: '0', committedBytes: String(committed), renamed: false, originMarked: false, expiresUnixMs: '0', ...patch });
const wait = async (cond: () => boolean) => { for (let i = 0; i < 400 && !cond(); i++) await new Promise((r) => setTimeout(r, 5)); };
const last = () => uploads.list()[0];

let started: { rootId: string; folderEntryId: string | null; name: string; size: string; collision: string }[];
beforeEach(() => {
  resetUploadsForTests();
  tuning.scale = 0.01;
  started = [];
  vi.spyOn(api, 'startUpload').mockImplementation(async (_s, _p, req) => {
    started.push(req);
    return { uploadId: 'u1', name: req.name, size: req.size, chunkBytes: 1000, expiresUnixMs: '0', collision: req.collision, destination: ['Photos'] } as never;
  });
});
afterEach(() => { tuning.scale = 1; vi.restoreAllMocks(); resetUploadsForTests(); });

describe('upload sender', () => {
  it('sends a file in strictly ordered, checksummed pieces and calls it saved only after the PC commits', async () => {
    const offsets: number[] = [], sums: string[] = [];
    let stored = 0;
    vi.spyOn(api, 'uploadChunk').mockImplementation(async (_s, _p, _u, offset, data, sha) => {
      offsets.push(offset); sums.push(sha); expect(sha).toMatch(/^[0-9a-f]{64}$/);
      stored = offset + data.byteLength;
      return { accepted: true, status: status(stored) } as never;
    });
    const fin = vi.spyOn(api, 'finishUpload').mockImplementation(async () => { expect(last().phase).toBe('finishing'); return status(2500, { state: 'committed', finalName: 'a.bin', sha256: 'ab'.repeat(32) }) as never; });
    uploads.enqueue(session, target, [file('a.bin', 2500)], 'reject');
    await wait(() => last()?.phase === 'done');
    expect(offsets).toEqual([0, 1000, 2000]);
    expect(new Set(sums).size).toBe(3);
    expect(fin).toHaveBeenCalledTimes(1);
    expect(last()).toMatchObject({ phase: 'done', finalName: 'a.bin', renamed: false, storedBytes: 2500 });
  });

  it('names a root and folder but never a path, and defaults callers to "reject" (no overwrite option exists)', async () => {
    vi.spyOn(api, 'uploadChunk').mockResolvedValue({ accepted: true, status: status(5) } as never);
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(5, { state: 'committed', finalName: 'n.txt' }) as never);
    uploads.enqueue(session, { ...target, folderEntryId: 'tok' }, [file('n.txt', 5)], 'reject');
    await wait(() => last()?.phase === 'done');
    expect(started[0]).toEqual({ rootId: 'r1', folderEntryId: 'tok', name: 'n.txt', size: '5', collision: 'reject' });
    expect(JSON.stringify(started[0])).not.toMatch(/\.\.|\\|C:/);
  });

  it('resyncs from the PC\'s own count when a piece is lost, instead of trusting what it sent', async () => {
    let calls = 0; const offsets: number[] = [];
    vi.spyOn(api, 'uploadChunk').mockImplementation(async (_s, _p, _u, offset, data) => {
      offsets.push(offset);
      if (++calls === 2) throw new ApiError('network', 'down', 0);
      return { accepted: true, status: status(offset + data.byteLength) } as never;
    });
    vi.spyOn(api, 'uploadStatus').mockResolvedValue(status(1000) as never);
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(2500, { state: 'committed', finalName: 'a.bin' }) as never);
    uploads.enqueue(session, target, [file('a.bin', 2500)], 'reject');
    await wait(() => last()?.phase === 'done');
    expect(offsets).toEqual([0, 1000, 1000, 2000]);
  });

  it('waits and asks again when the PC is still holding the piece that was cut off, instead of failing', async () => {
    let calls = 0; const offsets: number[] = [];
    vi.spyOn(api, 'uploadChunk').mockImplementation(async (_s, _p, _u, offset, data) => {
      offsets.push(offset);
      if (++calls <= 2) throw new ApiError('conflict', 'Another piece of this upload is still being received.', 409);
      return { accepted: true, status: status(offset + data.byteLength) } as never;
    });
    vi.spyOn(api, 'uploadStatus').mockResolvedValue(status(0) as never);
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(1000, { state: 'committed', finalName: 'a.bin' }) as never);
    uploads.enqueue(session, target, [file('a.bin', 1000)], 'reject');
    await wait(() => last()?.phase === 'done');
    expect(last().phase).toBe('done');
    expect(offsets).toEqual([0, 0, 0]);
  });

  it('does not wait on a conflict when the upload is really over', async () => {
    vi.spyOn(api, 'uploadChunk').mockRejectedValue(new ApiError('conflict', 'This upload is no longer receiving data.', 409));
    vi.spyOn(api, 'uploadStatus').mockResolvedValue(status(0, { state: 'failed' }) as never);
    vi.spyOn(api, 'cancelUpload').mockResolvedValue(undefined as never);
    uploads.enqueue(session, target, [file('a.bin', 1000)], 'reject');
    await wait(() => last()?.phase === 'failed');
    expect(last().phase).toBe('failed');
  });

  it('stops waiting on a conflict that never clears and pauses for the person to resume', async () => {
    vi.spyOn(api, 'uploadChunk').mockRejectedValue(new ApiError('conflict', 'Another piece of this upload is still being received.', 409));
    vi.spyOn(api, 'uploadStatus').mockResolvedValue(status(0) as never);
    uploads.enqueue(session, target, [file('a.bin', 1000)], 'reject');
    await wait(() => last()?.phase === 'paused');
    expect(last().phase).toBe('paused');
  });

  it('pauses after repeated failures and resumes from the stored offset', async () => {
    let online = false;
    vi.spyOn(api, 'uploadChunk').mockImplementation(async (_s, _p, _u, offset, data) => {
      if (!online) throw new ApiError('network', 'down', 0);
      return { accepted: true, status: status(offset + data.byteLength) } as never;
    });
    vi.spyOn(api, 'uploadStatus').mockImplementation(async () => { if (!online) throw new ApiError('network', 'down', 0); return status(0) as never; });
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(10, { state: 'committed', finalName: 'a.bin' }) as never);
    uploads.enqueue(session, target, [file('a.bin', 10)], 'reject');
    await wait(() => last()?.phase === 'paused');
    expect(last().phase).toBe('paused');
    online = true; uploads.resume(last().id);
    await wait(() => last()?.phase === 'done');
    expect(last().phase).toBe('done');
  }, 20000);

  it.each([
    ['forbidden', /not allowed uploads|turned them off/],
    ['not_found', /no longer has this upload/],
    ['disk_full', /./],
    ['too_large', /./],
  ] as const)('a "%s" answer fails the file with a plain message and asks the PC to discard it', async (code, text) => {
    vi.spyOn(api, 'uploadChunk').mockRejectedValue(new ApiError(code, 'The PC said ' + code, 400));
    const cancel = vi.spyOn(api, 'cancelUpload').mockResolvedValue({} as never);
    uploads.enqueue(session, target, [file('a.bin', 10)], 'reject');
    await wait(() => last()?.phase === 'failed');
    expect(last().phase).toBe('failed');
    expect(last().message).toMatch(text);
    expect(last().phase).not.toBe('done');
    expect(cancel).toHaveBeenCalled();
  });

  it('a name that already exists is reported, not overwritten', async () => {
    vi.mocked(api.startUpload).mockRejectedValue(new ApiError('name_exists', 'A file named a.bin is already there.', 409));
    const chunk = vi.spyOn(api, 'uploadChunk');
    uploads.enqueue(session, target, [file('a.bin', 10)], 'reject');
    await wait(() => last()?.phase === 'failed');
    expect(last().message).toMatch(/already there/);
    expect(chunk).not.toHaveBeenCalled();
  });

  it('reports a rename from the PC and shows the checksum only after commit', async () => {
    vi.spyOn(api, 'uploadChunk').mockResolvedValue({ accepted: true, status: status(4) } as never);
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(4, { state: 'committed', finalName: 'a (2).bin', renamed: true, sha256: 'cd'.repeat(32) }) as never);
    uploads.enqueue(session, target, [file('a.bin', 4)], 'rename');
    await wait(() => last()?.phase === 'done');
    expect(last()).toMatchObject({ finalName: 'a (2).bin', renamed: true, sha256: 'cd'.repeat(32) });
  });

  it('cancelling stops sending and tells the PC to delete what it stored', async () => {
    let release: () => void = () => undefined;
    vi.spyOn(api, 'uploadChunk').mockImplementation(() => new Promise((resolve) => { release = () => resolve({ accepted: true, status: status(1000) } as never); }));
    const cancel = vi.spyOn(api, 'cancelUpload').mockResolvedValue({} as never);
    const fin = vi.spyOn(api, 'finishUpload');
    uploads.enqueue(session, target, [file('a.bin', 5000)], 'reject');
    await wait(() => last()?.phase === 'sending');
    uploads.cancel(last().id);
    release();
    await new Promise((r) => setTimeout(r, 30));
    expect(last().phase).toBe('cancelled');
    expect(cancel).toHaveBeenCalledWith(session, 'pc1', 'u1');
    expect(fin).not.toHaveBeenCalled();
  });

  it('a cancel lost to a dropped connection is retried, so the PC gets its upload slot back', async () => {
    vi.spyOn(api, 'uploadChunk').mockRejectedValue(new ApiError('network', 'offline', 0));
    let tries = 0;
    const cancel = vi.spyOn(api, 'cancelUpload').mockImplementation(async () => { if (++tries < 3) throw new ApiError('network', 'offline', 0); return {} as never; });
    uploads.enqueue(session, target, [file('a.bin', 5000)], 'reject');
    await wait(() => last()?.phase === 'failed' || last()?.phase === 'paused');
    uploads.cancel(last().id);
    await wait(() => tries >= 3);
    await new Promise((r) => setTimeout(r, 30));
    expect(cancel).toHaveBeenCalledTimes(3);                                  // two lost, the third arrived, then it stopped asking
  });

  it('a cancel the PC really refuses is not retried', async () => {
    vi.spyOn(api, 'uploadChunk').mockImplementation(() => new Promise(() => undefined));
    const cancel = vi.spyOn(api, 'cancelUpload').mockRejectedValue(new ApiError('forbidden', 'no', 403));
    uploads.enqueue(session, target, [file('a.bin', 5000)], 'reject');
    await wait(() => last()?.phase === 'sending');
    uploads.cancel(last().id);
    await new Promise((r) => setTimeout(r, 60));
    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it('treats a silent link as a dropped connection and pauses instead of hanging', async () => {
    tuning.chunkTimeoutMs = 20;
    vi.spyOn(api, 'uploadChunk').mockImplementation((_s, _p, _u, _o, _d, _h, signal) => new Promise((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('x', 'AbortError')))));
    vi.spyOn(api, 'uploadStatus').mockRejectedValue(new ApiError('network', 'down', 0));
    uploads.enqueue(session, target, [file('a.bin', 10)], 'reject');
    await wait(() => last()?.phase === 'paused');
    tuning.chunkTimeoutMs = 60_000;
    expect(last().phase).toBe('paused');
  }, 20000);

  it('refuses to send when this browser cannot compute SHA-256', async () => {
    const subtle = Object.getOwnPropertyDescriptor(globalThis.crypto, 'subtle');
    vi.stubGlobal('crypto', {});
    const chunk = vi.spyOn(api, 'uploadChunk');
    uploads.enqueue(session, target, [file('a.bin', 10)], 'reject');
    await wait(() => last()?.phase === 'failed');
    vi.unstubAllGlobals();
    void subtle;
    expect(last().message).toMatch(/cannot check files/);
    expect(chunk).not.toHaveBeenCalled();
  });

  it('sends files one at a time and each to the folder it was queued for', async () => {
    const seen: string[] = [];
    vi.mocked(api.startUpload).mockImplementation(async (_s, _p, req) => { seen.push(req.name + '@' + String(req.folderEntryId)); return { uploadId: 'u' + seen.length, name: req.name, size: req.size, chunkBytes: 1000, expiresUnixMs: '0', collision: req.collision, destination: [] } as never; });
    vi.spyOn(api, 'uploadChunk').mockImplementation(async (_s, _p, _u, o, d) => ({ accepted: true, status: status(o + d.byteLength) }) as never);
    vi.spyOn(api, 'finishUpload').mockResolvedValue(status(1, { state: 'committed', finalName: 'x' }) as never);
    uploads.enqueue(session, { ...target, folderEntryId: 'F1' }, [file('a', 1), file('b', 1)], 'reject');
    uploads.enqueue(session, { ...target, folderEntryId: 'F2' }, [file('c', 1)], 'reject');
    await wait(() => uploads.list().length === 3 && uploads.list().every((j) => j.phase === 'done'));
    expect(seen).toEqual(['a@F1', 'b@F1', 'c@F2']);
  });
});
