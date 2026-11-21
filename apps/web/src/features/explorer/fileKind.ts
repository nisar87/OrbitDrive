/** What a file looks like in the list, from its name only (the PC judges the real content). Drives the icon's colour and short label. */
export type FileKind = 'image' | 'pdf' | 'audio' | 'video' | 'text' | 'code' | 'archive' | 'document' | 'sheet' | 'slides' | 'app' | 'other';

const GROUPS: Record<Exclude<FileKind, 'other'>, string[]> = {
  image: ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg', 'heic', 'tif', 'tiff', 'ico', 'raw', 'cr2', 'nef'],
  pdf: ['pdf'],
  audio: ['mp3', 'm4a', 'wav', 'ogg', 'flac', 'weba', 'aac', 'wma', 'opus'],
  video: ['mp4', 'webm', 'ogv', 'mkv', 'mov', 'avi', 'wmv', 'm4v', 'flv'],
  text: ['txt', 'md', 'log', 'rtf', 'ini', 'cfg', 'conf', 'toml'],
  code: ['c', 'h', 'cpp', 'hpp', 'cs', 'js', 'mjs', 'ts', 'tsx', 'jsx', 'py', 'java', 'kt', 'go', 'rs', 'rb', 'php', 'sh', 'bat', 'ps1', 'sql', 'css', 'html', 'htm', 'json', 'yml', 'yaml', 'xml'],
  archive: ['zip', '7z', 'rar', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'iso', 'cab'],
  document: ['doc', 'docx', 'odt', 'pages'],
  sheet: ['xls', 'xlsx', 'ods', 'csv', 'tsv', 'numbers'],
  slides: ['ppt', 'pptx', 'odp', 'key'],
  app: ['exe', 'msi', 'dll', 'apk', 'dmg', 'appx', 'msix'],
};

const BY_EXTENSION = new Map<string, FileKind>(Object.entries(GROUPS).flatMap(([kind, list]) => list.map((ext) => [ext, kind as FileKind] as const)));

export function fileKindOf(name: string): FileKind {
  const dot = name.lastIndexOf('.');
  return dot < 0 || dot === name.length - 1 ? 'other' : BY_EXTENSION.get(name.slice(dot + 1).toLowerCase()) ?? 'other';
}
