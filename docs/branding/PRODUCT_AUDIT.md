# MyOrbitDrive: product and asset audit (Phase A, part 1: internal)

Written 2026-11-24 from the repository at commit `5003f67` (V1 complete, relay 0.1.32, desktop 0.1.16). The competitor half of Phase A (`COMPETITIVE_REVIEW.md`) is **not written yet**: fetching the eight reference sites needs an approval that was not answered. Nothing in this branch changes the product or any deployment.

## 1. What exists today

| Area | State |
|---|---|
| Public marketing site | **None.** `myorbitdrive.app` has no page of its own; the only web UI is the authenticated app at `app.myorbitdrive.app` (Windows-style browser desktop, Explorer, Transfers, Devices, Inbox, PWA). |
| Logo / brand | **None.** Web PWA icons (`apps/web/public/icons`, 4 PNGs) and the shell extension assets are placeholders; no SVG master, no wordmark, no favicon set, no Windows `.ico`, no social image. |
| Screenshots | 17 exist (`docs/evidence/screenshots`: 8 owner console, 9 web, plus `artifacts/web-screenshots`). They are **test evidence**: 1280x720 and 980x680 at 1x, taken with test fixtures. They prove behaviour; they are not marketing-grade (low resolution, test file names, no phone frames, no demo data set). |
| Screenshot automation | Playwright specs already write PNGs (`apps/web/e2e/*`), and `scripts/desktop-ui-test.ps1` captures the WPF console on a Windows runner (`Shot`). Both are tied to tests, not to a reproducible "marketing capture" run with a curated demo account. |
| Explorer "Send to" screenshot | Not capturable in CI (needs a real Windows 11 Explorer context menu). Needs the owner's machine. |

## 2. Name usage that must follow "MyOrbitDrive"

User-visible text still says "OrbitDrive": web `<title>`, manifest `name`/`short_name`, about 15 UI sentences in `apps/web/src` (privacy panel, offline/empty states, "Send to my phone via OrbitDrive"), and the WPF console (tray menu, buttons "Open/Start/Quit OrbitDrive", pairing and approval text, window title). Internal identifiers (namespaces, pipe names, `OrbitDrive__*` settings, protocol ids, repo name) stay unchanged by instruction. The change is a presentation-only string pass with regression tests (the UI-automation script and e2e specs look up some of these labels by name), done in its own reviewed commit.

## 3. Claims the site may and may not make (from `COMPLETION_AUDIT.md`)

May say (each is automated-verified): approved folders only and read-only by default; QR/code pairing with approval on the PC; revocable browsers and PCs; HTTPS to the relay; no router configuration; files are streamed through the relay, not kept as permanent cloud copies; Explorer, previews, downloads, resumable transfers, Send to my phone (Inbox), phone uploads into folders the owner opts in.

Must not say: end-to-end encryption or zero-knowledge (the relay terminates TLS; E2EE is an unbuilt, owner-pending design); files available while the PC is off or asleep; guaranteed speeds; background phone downloads; that it replaces cloud storage; independent security audit (none yet); any user counts or testimonials.

Needs a decision before it appears in the FAQ: whether a locked Windows session still serves files (the console has an opt-in "pause when locked"; default behaviour must be confirmed on a real PC).

## 4. Download call to action

The only distributable is the **unsigned portable zip** on the `desktop-dev` release (no installer, no code-signing; Windows SmartScreen will warn). The brief asks for a primary "Download for Windows" only "when a verified distributable is available". Proposal: label it honestly as a preview build with the "Run anyway" guide until a signed installer exists, and keep "Open web app" as the other action.

## 5. Hosting and approval boundaries

Web hosting is Firebase Hosting on the development project (`deploy-web`, keyless, `main` only). A marketing site would be a second hosting target or a separate folder served from the apex; **no DNS change, no new hosting site and no production publish happens without the owner's explicit approval.** Work stays on branch `feature/branding` and in a preview build until then.

## 6. Next steps (blocked on web access for item 1)

1. Study the eight reference sites and write `COMPETITIVE_REVIEW.md` with sourced findings.
2. Three rendered logo concepts, compared at icon size; one provisional pick (not published).
3. Reproducible marketing screenshot run (curated demo account and files, 2x, light/dark, phone frames) with a manifest recording the app version.
4. Responsive marketing site, preview only.
5. Security audit of existing abuse controls, then only justified, tested hardening.
