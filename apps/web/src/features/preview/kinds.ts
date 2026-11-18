export type PreviewKind = 'image' | 'pdf' | 'audio' | 'video' | 'text';

const BY_EXTENSION: Record<string, PreviewKind> = {
  png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', bmp: 'image',
  pdf: 'pdf',
  mp3: 'audio', m4a: 'audio', wav: 'audio', ogg: 'audio', flac: 'audio', weba: 'audio',
  mp4: 'video', webm: 'video', ogv: 'video',
  txt: 'text', md: 'text', log: 'text', csv: 'text', tsv: 'text', json: 'text', yml: 'text', yaml: 'text', ini: 'text', cfg: 'text', conf: 'text', toml: 'text',
  c: 'text', h: 'text', cpp: 'text', hpp: 'text', cs: 'text', js: 'text', mjs: 'text', ts: 'text', tsx: 'text', jsx: 'text', py: 'text', java: 'text', kt: 'text',
  go: 'text', rs: 'text', rb: 'text', php: 'text', sh: 'text', bat: 'text', ps1: 'text', sql: 'text', css: 'text',
};

/** A hint from the file name, used only to decide which button to offer. The PC judges what the bytes really are, and refuses the rest. */
export function previewKindOf(name: string): PreviewKind | null {
  const dot = name.lastIndexOf('.');
  return dot < 0 ? null : BY_EXTENSION[name.slice(dot + 1).toLowerCase()] ?? null;
}

/** Kinds opened by activating (double-click / tap) a file; text opens from the Preview button or menu so Download stays the default for it. */
export const opensOnActivate = (kind: PreviewKind | null): boolean => kind === 'image' || kind === 'pdf' || kind === 'audio' || kind === 'video';

export const TEXT_PREVIEW_BYTES = 256 * 1024;
export const IMAGE_AUTO_LOAD_BYTES = 30 * 1024 * 1024;
export const PDF_MAX_BYTES = 64 * 1024 * 1024;
