# Dependency licence review

**What is checked.** `node scripts/licence-check.mjs` (run in the `security` workflow, with a self-test) reads every npm lock file (`apps/web`, `tests/integration`,
`tests/emulators`) and fails if any package has a licence that is not on the reviewed allowlist. A new dependency with an unexpected licence therefore needs a
decision in this file, not an accident. NuGet packages that ship in the desktop build are covered by `THIRD_PARTY_NOTICES.md`, generated from the packages that
were actually restored for the release (`tools/release/release-candidate.mjs`, which refuses to ship without the .NET runtime notice).

**Allowlist (reviewed 2026-11-22).** MIT, ISC, Apache-2.0, BSD-2-Clause, BSD-3-Clause, BSD, 0BSD, BlueOak-1.0.0, CC0-1.0, Unlicense, Python-2.0, "public domain", and
MPL-2.0. An `OR` expression passes if one alternative is allowed; an `AND` expression needs every term allowed. GPL, LGPL, AGPL, SSPL, "UNLICENSED" and
"SEE LICENSE IN" are refused.

**Decisions.**
- *MPL-2.0 (14 packages in the web build tooling):* file-level copyleft. Accepted because the packages are used unmodified as build tooling; OrbitDrive neither patches
  nor redistributes their source. If one were ever patched, the patched files would have to be published.
- *`fuzzy` and `valid-url` (in `tests/emulators`, pulled in by `firebase-tools`):* the lock file carries no licence field; both are published under the MIT licence.
  They are development-time only and never ship.
- *Nothing in the web bundle or the desktop build depends on a copyleft licence beyond the MPL tooling above.*

**Not covered.** The licences of fonts, icons and images: the web app and console use none that are third-party (the file-type icons are CSS, the tray icon is generated
in this repository). Re-check when that changes.
