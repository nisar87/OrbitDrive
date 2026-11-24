# MyOrbitDrive marketing site: implementation notes (Phases C to E)

Location: `apps/site/` (static HTML/CSS/JS, no framework, no build step, no third-party requests, no storage, no cookies). Separate from `apps/web` and from every deploy workflow: nothing here is published. Run locally with `cd apps/site && npm ci && npm run serve`, tests with `PW_LOCAL_CHROME=1 npx playwright test` (CI: workflow `site`).

## Sections built
Header with mobile menu; hero (headline, two actions, real screen); three-step flow with a labelled pairing simulation; product tabs (Explorer, Preview, Transfers, Windows app, phone) with arrow-key navigation; three alternating feature stories; "how a transfer works" diagram that states the relay ends TLS; scenarios; "what it does / does not do" security section; filterable gallery with enlargement dialog (15 screens); FAQ; final call to action; footer.

## Screenshots (Phase E)
`apps/site/capture/capture.mjs` starts the real gateway and agent, creates disposable synthetic files (`make-demo-files.py`), pairs a demo account (dev auth, local only), and captures desktop (1440x900 @2x) and phone-sized (390x844 @3x) views plus `manifest.json` with commit, account, data set and surface. Optimised copies and the manifest are in `apps/site/assets/shots`.

Honest limits:
- Phone shots are a **phone-sized desktop-Chrome viewport, not a physical phone**, and the page says so.
- Windows app shots (3) are existing real captures from the CI Windows runner (`docs/evidence/screenshots/owner-console`), 980x680 at 1x, with test data. Higher-resolution, curated Windows captures need the Windows UI-automation run extended or the owner's PC.
- Picture thumbnails are not shown in icon view because the Linux capture agent has no Windows Shell thumbnailer; a Windows capture would show them.
- Not captured: the Inbox with a waiting file, the Explorer "Send to my phone" context menu (needs real Windows 11 Explorer), the QR code screen, real devices. Storyboard items still open: 16-item list items for Inbox, Devices, Properties, QR and Windows-side flows.

## Tests (`apps/site/tests/site.spec.mjs`, desktop and phone projects)
No third-party requests; no console errors; no sideways scroll; every image loads; every image has alt text; one h1 and landmarks; in-page links resolve; axe (WCAG 2.0/2.1/2.2 A and AA rules) clean; tab keyboard behaviour; gallery filter and lightbox with focus return; reduced motion; simulation labelled; claims guard (no zero-knowledge, guarantee, competitor names, or un-negated end-to-end).

## Config the owner must set before any publication
`CONFIG.appUrl` and `CONFIG.downloadUrl` in `site.js` (currently the dev web app and the `desktop-dev` release page). Hosting target, domain and any DNS change are the owner's decisions.
