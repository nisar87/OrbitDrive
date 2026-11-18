# Firebase (development project `orbitdrive-dev`; Hosting deployed 2026-11-18)

`firebase.json` serves the static web build with an SPA fallback only. It deliberately contains **no rewrite to a
backend**: Hosting rewrites time out after 60 seconds, so the web app calls the relay origin (`VITE_GATEWAY_URL`) directly.

Nothing has been deployed and no project exists in this repository's configuration. Before any deployment the owner
must create separate development/staging/production projects and supply their IDs; see `docs/IMPLEMENTATION_STATE.md`.
The hosting configuration has not been validated with the Firebase CLI or emulator yet (W12 / W0 follow-up).

## Deploying the development web app (owner action)

`orbitdrive-dev` exists (Spark plan, Email/Password on, web app "OrbitDrive Web"; see `PROJECTS.md`). The Hosting site is created by the
first deploy. Deploying needs the owner's Firebase login, so it is run by the owner:

1. Copy `apps/web/.env.example` to `apps/web/.env.local` and fill the public values (relay: `infra/relay/environments/dev.json`).
2. `npx firebase-tools login`, then `node scripts/deploy-web.mjs`. It writes the page headers for that relay, builds, deploys **hosting only**
   and always restores the committed fail-closed `firebase.json`.
3. Afterwards: open `https://orbitdrive-dev.web.app`, check the response headers and that `/assets/*.mjs` is served as JavaScript.

## Automatic deploys (`.github/workflows/deploy-web.yml`)

On every push to `main` that touches the web app or hosting config, the workflow checks that **all seven verification workflows are green for that exact
commit**, then runs `scripts/deploy-web.mjs` (hosting only). The public web configuration comes from `web-dev.env` (public identifiers, committed);
`apps/web/.env.local` overrides it on the owner's PC. There is **no stored secret**: authentication is keyless.

- GitHub's OIDC token is exchanged through Google Workload Identity Federation (pool `github-actions`, provider `github` in `orbitdrive-dev`) for
  short-lived credentials of `github-deploy-hosting@orbitdrive-dev.iam.gserviceaccount.com`, which has only the Firebase Hosting Admin role.
- The provider's attribute condition accepts only repository `nisar87/OrbitDrive` on `refs/heads/main`; the service account trusts only that repository.
- Revoke by disabling the pool/provider or removing the service account's Workload Identity User binding in Google Cloud. Production would need its own
  project and approval; none exists.
