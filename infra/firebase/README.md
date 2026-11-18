# Firebase (development project `orbitdrive-dev`; Hosting not deployed yet)

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
