# Owner handoff

**State: release candidate awaiting owner-side evidence.** The code is built and tested in CI; nothing production exists. This is a list of what you decide and do, in order.

## What exists now (real, verified)
- Development web app: https://orbitdrive-dev.web.app (Firebase project `orbitdrive-dev`, free Spark plan), auto-deployed from `main` after all seven workflows pass; the deploy is keyless.
- Development relay: https://139-185-60-75.sslip.io (Oracle Always Free, self-updating from GitHub releases).
- Windows release-candidate zip with checksums, notices and manifest: produced by the `release-candidate` workflow (download from that run's Artifacts). Unsigned. Nothing is published.

## What you do, in order
1. **Run the acceptance checklist** (`acceptance-checklist.md`) on a real Windows 11 PC and real phones. Record results.
2. **Code signing** (needed for no-warning installs and the Windows 11 Explorer menu): obtain a signing certificate, then ask for the MSIX/installer packaging step (not built).
3. **Domain** (optional now): buy one; fill `infra/environments/production.json` and `staging.json`; run `node scripts/validate-environment.mjs <file> --deploy`.
4. **Firebase staging and production projects** (create in the Firebase console; the free plan is enough to start; Firestore and push need a region choice and may bill): then enable Hosting, add authorized domains, enable Email/Password, set email templates.
5. **Production relay**: a host with TLS for your domain, `OrbitDrive__AllowedWebOrigins`, `OrbitDrive__Firebase__ProjectId`, operator ids (`docs/operations/relay.md`).
6. **Approve production** in writing; nothing deploys to production automatically (the `deploy-web` workflow is development-only and locked to this repository's `main`).

## Things not to claim yet
iOS or Android support, real-device speeds, signed installers, end-to-end encryption, Firestore-backed metadata, push notifications, a production deployment.

## Where things are
Plan `docs/plans/OrbitDrive_Complete_Build_Plan.md` · state `docs/IMPLEMENTATION_STATE.md` · evidence `docs/evidence/phase-evidence.md` · requirement coverage `docs/evidence/REQUIREMENTS_MATRIX.md` · runbooks `docs/operations/runbooks.md` · relay `docs/operations/relay.md` · Firebase `infra/firebase/README.md` · security accepted risks `docs/security/accepted-risks.md` · support matrix `SUPPORT_MATRIX.md`.
