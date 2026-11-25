/** Characters that change the drawing order of text around them: embeddings, overrides and isolates. A name that contains them can be drawn back to front. */
const BIDI_CONTROLS = /[‪-‮⁦-⁩]/g;

/** The name as it is drawn: the same characters, without the controls that could make "photo‮gpj.exe" read as "photoexe.jpg". The real name is unchanged elsewhere. */
export function displayName(name: string): string {
  return name.replace(BIDI_CONTROLS, '');
}

/**
 * A file name drawn the way Windows Explorer draws it: isolated from the text around it and laid out left to right as a whole, so the extension is always the
 * last thing on the right. Without this, a right-to-left name such as "اردو فائل.txt" is drawn as "txt.اردو فائل" because its first letter sets the direction (F-025).
 * The letters of an Arabic or Urdu word still run right to left inside their own run, as they should.
 */
export function FileName({ name }: { name: string }) {
  return <bdi dir="ltr" className="file-name">{displayName(name)}</bdi>;
}
