import { lazy, Suspense, useCallback, useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent } from 'react';
import { ApiError, type Session } from '../../api';
import { formatBytes } from '../../format';
import { transfers } from '../transfers/transfers';
import { IMAGE_AUTO_LOAD_BYTES, previewKindOf, TEXT_PREVIEW_BYTES, type PreviewKind } from './kinds';
import { previewSource, previewStore, type PreviewItem, type PreviewSource } from './preview';

const PdfView = lazy(() => import('./PdfView'));

type Load<T> = { state: 'loading' } | { state: 'ready'; value: T } | { state: 'failed'; message: string };

function messageOf(e: unknown): string {
  if (e instanceof ApiError) return e.message;
  return 'This file could not be previewed. You can still download it.';
}

/** Fetches a preview capability for the current file, once per file and variant, and never after the viewer was closed or moved on. */
function useSource(session: Session, pcId: string, item: PreviewItem, variant: 'preview' | 'thumb:512', enabled = true): Load<PreviewSource> {
  const [load, setLoad] = useState<Load<PreviewSource>>({ state: 'loading' });
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    setLoad({ state: 'loading' });
    previewSource(session, pcId, item.entryId, variant).then(
      (value) => { if (!cancelled) setLoad({ state: 'ready', value }); },
      (e: unknown) => { if (!cancelled) setLoad({ state: 'failed', message: messageOf(e) }); },
    );
    return () => { cancelled = true; };
  }, [session, pcId, item.entryId, variant, enabled]);
  return load;
}

function Failure({ message, onDownload }: { message: string; onDownload: () => void }) {
  return (
    <div className="preview-failure" role="alert">
      <p>{message}</p>
      <button type="button" onClick={onDownload}>Download the original</button>
    </div>
  );
}

function ImageView({ session, pcId, item, onDownload }: { session: Session; pcId: string; item: PreviewItem; onDownload: () => void }) {
  const [zoom, setZoom] = useState<'fit' | number>('fit');
  const [full, setFull] = useState(false);
  const [broken, setBroken] = useState(false);
  const [natural, setNatural] = useState<{ w: number; h: number } | null>(null);
  const big = item.size > IMAGE_AUTO_LOAD_BYTES;
  const thumb = useSource(session, pcId, item, 'thumb:512');
  const original = useSource(session, pcId, item, 'preview', !big || full);
  useEffect(() => { setZoom('fit'); setFull(false); setBroken(false); setNatural(null); }, [item.entryId]);
  const shown = original.state === 'ready' && !broken ? original.value.url : thumb.state === 'ready' ? thumb.value.url : null;
  const showingOriginal = original.state === 'ready' && !broken && shown === original.value.url;
  const step = (factor: number) => setZoom((z) => Math.min(8, Math.max(0.1, (z === 'fit' ? 1 : z) * factor)));
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === '+' || event.key === '=') { event.preventDefault(); step(1.25); }
    else if (event.key === '-') { event.preventDefault(); step(0.8); }
    else if (event.key === '0') { event.preventDefault(); setZoom('fit'); }
  };
  if (original.state === 'failed' && thumb.state !== 'ready') return <Failure message={original.message} onDownload={onDownload} />;
  return (
    <div className="preview-image" onKeyDown={onKeyDown}>
      <div className="preview-tools" role="toolbar" aria-label="Zoom">
        <button type="button" className="ghost" aria-label="Zoom out" onClick={() => step(0.8)}>−</button>
        <button type="button" className="ghost" aria-label="Zoom in" onClick={() => step(1.25)}>+</button>
        <button type="button" className="ghost" aria-pressed={zoom === 'fit'} onClick={() => setZoom('fit')}>Fit</button>
        <button type="button" className="ghost" aria-pressed={zoom === 1} onClick={() => setZoom(1)}>100%</button>
        {big && !full && <button type="button" onClick={() => setFull(true)}>Load the full picture ({formatBytes(String(item.size))})</button>}
        {thumb.state === 'ready' && !showingOriginal && !big && <span className="hint">Loading the full picture…</span>}
      </div>
      <div className="preview-canvas" tabIndex={0} aria-label={`Picture ${item.name}`}>
        {shown ? (
          <img src={shown} alt={item.name} referrerPolicy="no-referrer" draggable={false} data-full={showingOriginal ? 'true' : 'false'}
            onLoad={(event) => setNatural({ w: event.currentTarget.naturalWidth, h: event.currentTarget.naturalHeight })}
            onError={() => { if (showingOriginal) setBroken(true); }}
            style={zoom === 'fit' ? { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' } : { width: natural ? natural.w * zoom : undefined, maxWidth: 'none' }} />
        ) : original.state === 'failed' ? null : <p className="hint" role="status">Loading…</p>}
        {broken && thumb.state !== 'ready' && <Failure message="This picture could not be shown by your browser." onDownload={onDownload} />}
      </div>
      {broken && thumb.state === 'ready' && <p className="hint" role="status">The full picture could not be shown, so this is a smaller copy.</p>}
    </div>
  );
}

function TextView({ session, pcId, item, onDownload }: { session: Session; pcId: string; item: PreviewItem; onDownload: () => void }) {
  const source = useSource(session, pcId, item, 'preview');
  const [text, setText] = useState<Load<{ body: string; cut: boolean }>>({ state: 'loading' });
  useEffect(() => {
    if (source.state === 'failed') { setText({ state: 'failed', message: source.message }); return; }
    if (source.state !== 'ready') { setText({ state: 'loading' }); return; }
    const control = new AbortController();
    (async () => {
      try {
        // Only the start of the file is requested; a log of several gigabytes is never pulled into the page.
        const response = await fetch(source.value.url, { headers: { Range: `bytes=0-${TEXT_PREVIEW_BYTES - 1}` }, signal: control.signal, cache: 'no-store', referrerPolicy: 'no-referrer', credentials: 'omit' });
        if (!response.ok && response.status !== 206) throw new Error(String(response.status));
        const bytes = new Uint8Array(await response.arrayBuffer());
        setText({ state: 'ready', value: { body: new TextDecoder('utf-8', { fatal: false }).decode(bytes), cut: item.size > bytes.length } });
      } catch (e) {
        if (!control.signal.aborted) setText({ state: 'failed', message: e instanceof Error && /^\d+$/.test(e.message) && e.message === '409' ? 'The file changed on the PC. Open it again.' : 'This text could not be loaded.' });
      }
    })();
    return () => control.abort();
  }, [source, item.size]);
  if (text.state === 'failed') return <Failure message={text.message} onDownload={onDownload} />;
  if (text.state === 'loading') return <p className="hint" role="status">Loading…</p>;
  return (
    <div className="preview-text">
      {/* Text only: it is rendered as characters, never as markup, whatever the file contains. */}
      <pre dir="auto" tabIndex={0} aria-label={`Text of ${item.name}`}>{text.value.body}</pre>
      {text.value.cut && <p className="hint">Showing the first {formatBytes(String(TEXT_PREVIEW_BYTES))} of {formatBytes(String(item.size))}. Download the file to read the rest.</p>}
    </div>
  );
}

function MediaView({ session, pcId, item, kind, onDownload }: { session: Session; pcId: string; item: PreviewItem; kind: 'audio' | 'video'; onDownload: () => void }) {
  const source = useSource(session, pcId, item, 'preview');
  const [unplayable, setUnplayable] = useState(false);
  useEffect(() => setUnplayable(false), [item.entryId]);
  if (source.state === 'failed') return <Failure message={source.message} onDownload={onDownload} />;
  if (source.state === 'loading') return <p className="hint" role="status">Loading…</p>;
  if (unplayable) return <Failure message="This browser cannot play this file (the format or its codec is not supported here), or the file changed. MyOrbitDrive does not convert media. You can download it and play it elsewhere." onDownload={onDownload} />;
  const common = { src: source.value.url, controls: true, preload: 'metadata' as const, onError: () => setUnplayable(true), 'aria-label': item.name, referrerPolicy: 'no-referrer' as const };
  return (
    <div className="preview-media">
      {kind === 'video' ? <video {...common} playsInline /> : <audio {...common} />}
      <p className="hint">Played from the PC through the relay; nothing is saved. Seeking asks the PC for just that part.</p>
    </div>
  );
}

/** The Preview window: opens a file from Explorer without downloading it, and steps through the other previewable files of that folder. */
export function PreviewApp({ session, onAuthLost }: { session: Session; onAuthLost: (error: unknown) => void }) {
  const state = useSyncExternalStore(previewStore.subscribe, previewStore.get);
  const [notice, setNotice] = useState<string | null>(null);
  const body = useRef<HTMLDivElement>(null);
  const item = state?.items[state.index];
  useEffect(() => { setNotice(null); body.current?.focus(); }, [item?.entryId]);
  const download = useCallback(async () => {
    if (!state || !item) return;
    setNotice(null);
    try { await transfers.toBrowser(session, state.pcId, { entryId: item.entryId, name: item.name }); setNotice('The original was handed to your browser’s downloads.'); }
    catch (e) { onAuthLost(e); setNotice(e instanceof ApiError ? e.message : 'The download could not start.'); }
  }, [state, item, session, onAuthLost]);

  if (!state || !item) {
    return <div className="preview-app empty"><p className="hint">Nothing is open. Choose a picture, PDF, text, audio or video file in File Explorer and select Preview.</p></div>;
  }
  const kind: PreviewKind | null = previewKindOf(item.name);
  const onKeyDown = (event: KeyboardEvent) => {
    if ((event.target as HTMLElement).closest('video, audio, input, select')) return;
    if (event.key === 'ArrowRight' || event.key === 'PageDown') { event.preventDefault(); previewStore.go(1); }
    else if (event.key === 'ArrowLeft' || event.key === 'PageUp') { event.preventDefault(); previewStore.go(-1); }
  };
  return (
    <div className="preview-app" onKeyDown={onKeyDown}>
      <div className="preview-bar" role="toolbar" aria-label="Preview">
        <button type="button" className="ghost" aria-label="Previous file" disabled={state.items.length < 2} onClick={() => previewStore.go(-1)}>‹</button>
        <button type="button" className="ghost" aria-label="Next file" disabled={state.items.length < 2} onClick={() => previewStore.go(1)}>›</button>
        <strong className="preview-name" title={item.name}><bdi>{item.name}</bdi></strong>
        <span className="hint">{state.index + 1} of {state.items.length} · {formatBytes(String(item.size))}</span>
        <button type="button" onClick={() => void download()}>Download original</button>
      </div>
      {notice && <p className="hint" role="status">{notice}</p>}
      <div className="preview-body" ref={body} tabIndex={-1}>
        {kind === 'image' && <ImageView key={item.entryId} session={session} pcId={state.pcId} item={item} onDownload={() => void download()} />}
        {kind === 'text' && <TextView key={item.entryId} session={session} pcId={state.pcId} item={item} onDownload={() => void download()} />}
        {(kind === 'audio' || kind === 'video') && <MediaView key={item.entryId} session={session} pcId={state.pcId} item={item} kind={kind} onDownload={() => void download()} />}
        {kind === 'pdf' && <Suspense fallback={<p className="hint" role="status">Loading the PDF viewer…</p>}><PdfView key={item.entryId} session={session} pcId={state.pcId} item={item} onDownload={() => void download()} /></Suspense>}
        {kind === null && <Failure message="There is no preview for this kind of file." onDownload={() => void download()} />}
      </div>
    </div>
  );
}
