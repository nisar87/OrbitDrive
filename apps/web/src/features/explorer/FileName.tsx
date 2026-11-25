/** Splits a file name into the part before the last dot and the extension (with its dot). A leading dot or a trailing dot is not an extension. */
export function splitName(name: string): { stem: string; extension: string } {
  const dot = name.lastIndexOf('.');
  if (dot <= 0 || dot === name.length - 1) return { stem: name, extension: '' };
  return { stem: name.slice(0, dot), extension: name.slice(dot) };
}

/**
 * A file name that cannot be made to look like another type. The name is isolated from the text around it (bdi), and the extension is isolated from the name
 * and always drawn on the right in its own left-to-right run, so a right-to-left name such as "اردو فائل.txt" is not shown as "txt.اردو فائل" (F-025).
 */
export function FileName({ name }: { name: string }) {
  const { stem, extension } = splitName(name);
  if (!extension) return <bdi>{name}</bdi>;
  return <bdi className="file-name"><bdi>{stem}</bdi><bdi dir="ltr" className="file-ext">{extension}</bdi></bdi>;
}
