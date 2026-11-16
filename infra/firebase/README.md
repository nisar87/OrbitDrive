# Firebase (prepared, not deployed)

`firebase.json` serves the static web build with an SPA fallback only. It deliberately contains **no rewrite to a
backend**: Hosting rewrites time out after 60 seconds, so the web app calls the relay origin (`VITE_GATEWAY_URL`) directly.

Nothing has been deployed and no project exists in this repository's configuration. Before any deployment the owner
must create separate development/staging/production projects and supply their IDs; see `docs/IMPLEMENTATION_STATE.md`.
The hosting configuration has not been validated with the Firebase CLI or emulator yet (W12 / W0 follow-up).
