import { useEffect, useRef, useState } from 'react';
import { parseScannedText, type PairingLink } from './pairingLink';

type Decoder = (pixels: ImageData) => Promise<string | null>;

/** Whether this browser can offer in-site scanning at all (needs a secure context and a camera API). */
export function canScan(): boolean {
  return typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia;
}

/**
 * Prefers the browser's built-in QR detector; falls back to the bundled jsQR decoder, loaded only when scanning
 * starts. Both run on this device: camera frames are never uploaded or stored.
 */
async function createDecoder(): Promise<Decoder> {
  const Native = (globalThis as { BarcodeDetector?: { new (init: { formats: string[] }): { detect(source: ImageData): Promise<{ rawValue: string }[]> }; getSupportedFormats?: () => Promise<string[]> } }).BarcodeDetector;
  if (Native?.getSupportedFormats) {
    try {
      if ((await Native.getSupportedFormats()).includes('qr_code')) {
        const detector = new Native({ formats: ['qr_code'] });
        return async (pixels) => (await detector.detect(pixels))[0]?.rawValue ?? null;
      }
    } catch { /* fall through to the bundled decoder */ }
  }
  const { default: jsQR } = await import('jsqr');
  return async (pixels) => jsQR(pixels.data, pixels.width, pixels.height, { inversionAttempts: 'attemptBoth' })?.data ?? null;
}

function cameraProblem(error: unknown): string {
  const name = error instanceof DOMException ? error.name : '';
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'Camera access was not allowed. Type the code shown on the PC instead.';
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'No camera was found on this device. Type the code shown on the PC instead.';
  return 'The camera could not be started. Type the code shown on the PC instead.';
}

interface Props { onScan: (link: PairingLink) => void; onClose: (problem?: string) => void }

export function QrScanner({ onScan, onClose }: Props) {
  const video = useRef<HTMLVideoElement>(null);
  const [hint, setHint] = useState('Starting camera…');

  useEffect(() => {
    let stopped = false;
    let stream: MediaStream | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const stop = () => { stopped = true; clearTimeout(timer); stream?.getTracks().forEach((track) => track.stop()); };

    void (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false });
      } catch (e) { if (!stopped) onClose(cameraProblem(e)); return; }
      if (stopped) { stop(); return; }
      const element = video.current!;
      element.srcObject = stream;
      try { await element.play(); } catch { /* autoplay blocked: the frame loop below simply waits for data */ }
      const decode = await createDecoder().catch(() => null);
      if (!decode) { stop(); onClose('This browser cannot read QR codes. Type the code shown on the PC instead.'); return; }
      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) { stop(); onClose('This browser cannot read QR codes. Type the code shown on the PC instead.'); return; }
      setHint('Point the camera at the QR code on your PC.');

      const tick = async () => {
        if (stopped) return;
        if (element.videoWidth > 0) {
          const scale = Math.min(1, 720 / Math.max(element.videoWidth, element.videoHeight));
          canvas.width = Math.round(element.videoWidth * scale); canvas.height = Math.round(element.videoHeight * scale);
          context.drawImage(element, 0, 0, canvas.width, canvas.height);
          let text: string | null = null;
          try { text = await decode(context.getImageData(0, 0, canvas.width, canvas.height)); } catch { /* unreadable frame */ }
          context.clearRect(0, 0, canvas.width, canvas.height);     // nothing from the camera is kept
          if (stopped) return;
          if (text !== null) {
            const result = parseScannedText(text, window.location.origin);
            if (result.link) { stop(); onScan(result.link); return; }
            setHint(result.problem);
          }
        }
        timer = setTimeout(() => void tick(), 180);
      };
      void tick();
    })();
    return stop;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="scanner" data-testid="qr-scanner">
      <video ref={video} playsInline muted aria-label="Camera preview" />
      <p className="notice" role="status">{hint}</p>
      <p className="scanner-privacy">The picture stays on this device. Only the pairing code is used.</p>
      <button type="button" className="ghost" onClick={() => onClose()}>Stop camera</button>
    </div>
  );
}
