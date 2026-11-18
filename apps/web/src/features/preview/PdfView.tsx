import { useEffect, useRef, useState } from 'react';
import type { PDFDocumentLoadingTask, PDFDocumentProxy, RenderTask } from 'pdfjs-dist';
import { ApiError, type Session } from '../../api';
import { formatBytes } from '../../format';
import { PDF_MAX_BYTES } from './kinds';
import { previewSource, type PreviewItem } from './preview';

const MAX_PAGES = 2000;

type Doc = { state: 'loading' } | { state: 'ready'; doc: PDFDocumentProxy } | { state: 'failed'; message: string };

/**
 * PDF viewer on the maintained pdf.js renderer, loaded only when a PDF is opened. Restrictions: scripting and forms are never
 * enabled; pages are painted as pictures only (no link, annotation or text layer, so nothing in the document can be clicked or
 * navigated); fonts come from the document, not the system; size and page counts are capped; the file is fetched by the
 * renderer itself with ranges and without cookies. A document that cannot be opened safely is offered as a download.
 */
export default function PdfView({ session, pcId, item, onDownload }: { session: Session; pcId: string; item: PreviewItem; onDownload: () => void }) {
  const [doc, setDoc] = useState<Doc>({ state: 'loading' });
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState<'width' | number>('width');
  const [rendering, setRendering] = useState(false);
  const canvas = useRef<HTMLCanvasElement>(null);
  const holder = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    let task: PDFDocumentLoadingTask | null = null;
    setDoc({ state: 'loading' }); setPage(1); setZoom('width');
    (async () => {
      try {
        if (item.size > PDF_MAX_BYTES) { setDoc({ state: 'failed', message: `This PDF is larger than ${formatBytes(String(PDF_MAX_BYTES))}, which is too large to open here. Download it instead.` }); return; }
        const source = await previewSource(session, pcId, item.entryId, 'preview');
        const pdfjs = await import('pdfjs-dist');
        const worker = (await import('pdfjs-dist/build/pdf.worker.min.mjs?url')).default;
        pdfjs.GlobalWorkerOptions.workerSrc = worker;
        task = pdfjs.getDocument({ url: source.url, withCredentials: false, enableXfa: false, useSystemFonts: false, disableAutoFetch: true, stopAtErrors: false, maxImageSize: 16_777_216, verbosity: 0 });
        const loaded = await task.promise;
        if (cancelled) { void task.destroy(); return; }
        if (loaded.numPages > MAX_PAGES) { void task.destroy(); setDoc({ state: 'failed', message: `This PDF has more than ${MAX_PAGES} pages. Download it instead.` }); return; }
        setDoc({ state: 'ready', doc: loaded });
      } catch (e) {
        if (!cancelled) setDoc({ state: 'failed', message: e instanceof ApiError ? e.message : 'This PDF could not be opened. It may be damaged or protected. You can still download it.' });
      }
    })();
    return () => { cancelled = true; void task?.destroy(); };
  }, [session, pcId, item.entryId, item.size]);

  useEffect(() => {
    if (doc.state !== 'ready' || !canvas.current) return;
    let cancelled = false;
    let job: RenderTask | null = null;
    setRendering(true);
    (async () => {
      try {
        const pdfPage = await doc.doc.getPage(page);
        const base = pdfPage.getViewport({ scale: 1 });
        const available = Math.max(200, (holder.current?.clientWidth ?? 800) - 24);
        const scale = zoom === 'width' ? Math.min(3, available / base.width) : zoom;
        const viewport = pdfPage.getViewport({ scale });
        const target = canvas.current;
        if (!target || cancelled) return;
        const ratio = Math.min(2, window.devicePixelRatio || 1);
        target.width = Math.floor(viewport.width * ratio); target.height = Math.floor(viewport.height * ratio);
        target.style.width = `${Math.floor(viewport.width)}px`; target.style.height = `${Math.floor(viewport.height)}px`;
        job = pdfPage.render({ canvas: target, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0], annotationMode: 0 });
        await job.promise;
      } catch (e) {
        if (!cancelled && !(e instanceof Error && e.name === 'RenderingCancelledException')) setDoc({ state: 'failed', message: 'A page of this PDF could not be drawn. You can still download it.' });
      } finally { if (!cancelled) setRendering(false); }
    })();
    return () => { cancelled = true; job?.cancel(); };
  }, [doc, page, zoom]);

  if (doc.state === 'failed') return <div className="preview-failure" role="alert"><p>{doc.message}</p><button type="button" onClick={onDownload}>Download the original</button></div>;
  if (doc.state === 'loading') return <p className="hint" role="status">Opening the PDF…</p>;
  const pages = doc.doc.numPages;
  const step = (factor: number) => setZoom((z) => Math.min(4, Math.max(0.25, (z === 'width' ? 1 : z) * factor)));
  return (
    <div className="preview-pdf">
      <div className="preview-tools" role="toolbar" aria-label="PDF pages">
        <button type="button" className="ghost" aria-label="Previous page" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>‹</button>
        <span aria-live="polite">Page {page} of {pages}</span>
        <button type="button" className="ghost" aria-label="Next page" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>›</button>
        <button type="button" className="ghost" aria-label="Zoom out" onClick={() => step(0.8)}>−</button>
        <button type="button" className="ghost" aria-label="Zoom in" onClick={() => step(1.25)}>+</button>
        <button type="button" className="ghost" aria-pressed={zoom === 'width'} onClick={() => setZoom('width')}>Fit width</button>
      </div>
      <div className="preview-canvas pdf" ref={holder} tabIndex={0} aria-label={`PDF ${item.name}, page ${page}`} aria-busy={rendering}>
        <canvas ref={canvas} role="img" aria-label={`Page ${page} of ${item.name}`} />
      </div>
    </div>
  );
}
