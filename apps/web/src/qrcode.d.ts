// Test-only dependency (reference QR encoder); just the part the unit tests use.
declare module 'qrcode' {
  const QRCode: { create(text: string, options?: { errorCorrectionLevel?: 'L' | 'M' | 'Q' | 'H' }): { modules: { size: number; get(row: number, column: number): number } } };
  export default QRCode;
}
