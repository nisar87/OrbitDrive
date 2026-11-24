# Delivery list and approval checklist

All work is on branch `feature/branding`. Nothing is published, no DNS or hosting setting changed, no paid resource created, the product and its deployments are untouched.

## What is in the branch
| Item | Location |
|---|---|
| Product and asset audit | `docs/branding/PRODUCT_AUDIT.md` |
| Competitive review | `docs/branding/COMPETITIVE_REVIEW.md` |
| Three logo concepts, board, provisional kit, tokens, guide | `docs/branding/concepts`, `CONCEPT_BOARD.png`, `kit-provisional`, `tokens.*`, `BRAND_GUIDE.md`, generators in `tools/` |
| Marketing site (preview) | `apps/site/` (open `index.html` via `npm run serve`), notes in `SITE_NOTES.md` |
| Screenshot capture, manifest | `apps/site/capture`, `apps/site/assets/shots/manifest.json` |
| Site tests and workflow | `apps/site/tests`, `.github/workflows/site.yml` |
| Security and abuse audit | `docs/branding/SECURITY_AUDIT.md`, `apps/site/headers.hosting.json` |
| Honest rubric | `docs/branding/RUBRIC.md` (77/100) |

## Online now
Preview at https://orbitdrive-dev.web.app/welcome/ (development hosting, owner-requested 24 Nov 2026). The custom domain myorbitdrive.app is not connected.

## Owner decisions and approvals needed (nothing proceeds without them)
1. Approve, change or reject logo Concept A; run a trademark and similarity search.
2. Approve the homepage direction and wording (especially the security section and the "preview, unsigned" download wording).
3. Provide or approve: the real app and download addresses (`CONFIG` in `site.js`), hosting target and domain for `myorbitdrive.app`, any DNS change.
4. Decide whether the app itself, the Windows console and the PWA icons take the new name and mark (a separate, reviewed string and icon pass with regression tests).
5. Capture on your PC: Send to my phone from Explorer, curated Windows app screens at 2x, a real phone photo if wanted.
6. Decide on App Check for the Firebase sign-in (console change, optional).
7. Say when to merge to `main`. That requires all seven gating workflows green on the exact SHA (the new `site` workflow is additional).

## Not done, and why
Full 16-item storyboard, real-device captures, Lighthouse and cross-browser checks, trademark search: see `RUBRIC.md`.

## CI evidence for the branch head (db1eda8)
contracts, security, desktop, gateway, integration, site: green on first attempt. `desktop-ui`: one failure in the UI-automation step "an agent stopped elsewhere is not silently relaunched by the console" (timing-sensitive; this branch changes no desktop or gateway code), green on rerun. `web`: first attempt stalled 27+ minutes in the Ubuntu browser-install step (known mirror stall), cancelled and rerun, green on the second attempt. All seven gating workflows plus `site` are therefore green for this exact SHA; the two reruns are recorded here rather than hidden.
