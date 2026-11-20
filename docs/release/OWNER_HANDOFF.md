# Owner handoff

**State: release candidate awaiting owner-side evidence.** The code is built and tested in CI; nothing production exists. This is a list of what you decide and do, in order.

## What exists now (real, verified)
- Development web app: https://orbitdrive-dev.web.app (Firebase project `orbitdrive-dev`, free Spark plan), auto-deployed from `main` after all seven workflows pass; the deploy is keyless.
- Development relay: https://relay.myorbitdrive.app (Oracle Always Free, self-updating from GitHub releases; the old https://139-185-60-75.sslip.io name still answers)
- Development web app: https://app.myorbitdrive.app (Firebase Hosting custom domain; https://orbitdrive-dev.web.app still works).
- Windows build with an in-app **Update now** button: the `desktop-release` workflow (runs only on `main`, only after all seven workflows are green) publishes the GitHub pre-release `desktop-dev` (zip, `SHA256SUMS`, `update.json`). Unsigned for Windows (no certificate yet); updates are authenticated by your own free update key. Until you add that key nothing is published.
- Windows release-candidate zip from the `release-candidate` workflow (Artifacts of that run) is still produced for every desktop change; it publishes nothing.

## The update key (done 2026-11-19)
1. On your PC, in a clone of the repository: `node tools/release/make-update-key.mjs`. It saves the private key under `%USERPROFILE%\.orbitdrive\` (back it up; never commit it) and puts it on your clipboard. It never prints it.
2. Open https://github.com/nisar87/OrbitDrive/settings/secrets/actions/new, name `UPDATE_SIGNING_KEY`, paste, **Add secret**.
3. Send the one PUBLIC line it printed (`orbitdrive-update-1  MFkw...`) to Claude, who commits it into `apps/desktop/OrbitDrive.Core/Update/update-keys.json`. After that, every green `main` commit that changes the desktop app publishes a new build and the installed app offers it.
If you lose the private key, installed copies can no longer be updated (reinstall once with a new key). Rotating a key means shipping a build that trusts both.

## What you do, in order
1. **Run the acceptance checklist** (`acceptance-checklist.md`) on a real Windows 11 PC and real phones. Record results.
2. **Code signing** (needed for no-warning first installs and the Windows 11 Explorer menu): later, obtain a certificate, then ask for the signing and MSIX/installer step (not built). Updates inside the app keep working either way.
3. **Domain** (optional now): buy one; fill `infra/environments/production.json` and `staging.json`; run `node scripts/validate-environment.mjs <file> --deploy`.
4. **Firebase staging and production projects** (create in the Firebase console; the free plan is enough to start; Firestore and push need a region choice and may bill): then enable Hosting, add authorized domains, enable Email/Password, set email templates.
5. **Production relay**: a host with TLS for your domain, `OrbitDrive__AllowedWebOrigins`, `OrbitDrive__Firebase__ProjectId`, operator ids (`docs/operations/relay.md`).
6. **Approve production** in writing; nothing deploys to production automatically (the `deploy-web` workflow is development-only and locked to this repository's `main`).

## Things not to claim yet
iOS or Android support, real-device speeds, signed installers, end-to-end encryption, Firestore-backed metadata, push notifications, a production deployment.

## Where things are
Plan `docs/plans/OrbitDrive_Complete_Build_Plan.md` · state `docs/IMPLEMENTATION_STATE.md` · evidence `docs/evidence/phase-evidence.md` · requirement coverage `docs/evidence/REQUIREMENTS_MATRIX.md` · runbooks `docs/operations/runbooks.md` · relay `docs/operations/relay.md` · Firebase `infra/firebase/README.md` · security accepted risks `docs/security/accepted-risks.md` · support matrix `SUPPORT_MATRIX.md`.
