# Runbooks (rollback, maintenance, incidents, staging smoke test)

Environments: `infra/environments/{development,staging,production}.json`. Only development exists today. A runbook step marked **(owner)** needs a person with the right account; none of these steps is run automatically against production.

## Version compatibility
- **Protocol:** the web app, relay and PC agent speak one versioned protocol (`packages/protocol`). The relay refuses PCs older than the minimum PC version an operator sets (admin settings) and tells the web app/PC when they must update.
- **Deploy order:** relay first (it must understand old and new clients), then web, then ask PC owners to update. Roll back in the reverse order.
- Metadata (`DataDirectory` JSON files) is only ever extended by adding fields; an older relay ignores unknown fields. A release that cannot honour this must say so in its notes and requires a backup restore to roll back.

## Maintenance mode (relay)
1. Start: on the relay host `sudo touch /var/lib/orbitdrive-gateway/MAINTENANCE` (the data directory from `docs/operations/relay.md`). No restart.
2. Effect within a second: **new** pairing, downloads, archives, searches, uploads and invitation accepts get `503 unavailable` + `Retry-After: 60`. Everything already running continues to completion (byte streams, upload pieces, status, finish, cancel, revoke, sign-out). `/healthz` stays `ok`; `/readyz` answers `503 maintenance`.
3. Give running transfers time to finish (they are never cut by maintenance mode itself), then do the work. A relay restart does end them; browsers can resume.
4. End: `sudo rm /var/lib/orbitdrive-gateway/MAINTENANCE`. `/readyz` is `ready` again within a second.
Proof: `tests/integration/maintenance.test.mjs` (a 24 MiB download that starts before maintenance finishes byte-identical while maintenance is on).

## Roll back
- **Relay:** automatic if a new release fails its health check (`orbitdrive-update`); manual: `sudo ln -sfn /opt/orbitdrive/releases/<version> /opt/orbitdrive/current && sudo systemctl restart orbitdrive-gateway`. A restart ends running transfers (browsers can resume), so put the relay in maintenance first and wait.
- **Web (Firebase Hosting):** Firebase console → Hosting → Release history → **Rollback** on the previous release **(owner)**, or run the `deploy-web` workflow again for the previous good commit. The page is never cached and assets are hashed, so users get the old version on next load.
- **PC agent:** keep the previous zip; Quit, extract the old one over the folder. Settings and pairings are in `%LOCALAPPDATA%` and are compatible within a minor version.
- **Staging rehearsal:** rollback must be rehearsed on staging before production (see below). Not yet done: staging does not exist.

## Incident response (suspected abuse or compromise)
1. Contain: operator suspends the account (admin → Users → Suspend), or **revoke all** for it; for the whole service enter maintenance mode.
2. A stolen browser: the owner opens Devices on the PC (or the website) and removes it; its grants stop on the very next request.
3. A leaked deployment credential: there is none to leak for web deploys (keyless). For the relay host: rotate the SSH key in Oracle console **(owner)**. For any future service-account key: delete it in Google Cloud **(owner)**.
4. Preserve: the admin audit log (`admin-audit.jsonl`) and the relay's journal; do not delete before review. Do not collect file names or contents; OrbitDrive does not keep them.
5. Record what happened in `docs/evidence/` and, if a defect, add a regression test before closing.

## Staging smoke test (to run when staging exists)
1. `node scripts/validate-environment.mjs infra/environments/staging.json --deploy` must pass.
2. Deploy web to staging only; run the acceptance checklist steps 1–6 and 10 against it; label the results with the environment name and the workflow run ID.
3. Rollback rehearsal: deploy a deliberately older commit to staging, check reconnect behaviour of a paired PC and an in-flight download (relay in maintenance first).
4. Only then may the owner approve production.

## Health and readiness
`/healthz` = process alive (used by the updater and monitor). `/readyz` = willing to take new work (503 during maintenance). `/v1/meta` = version and protocol. Quotas: per-device and per-account limits are in `GatewayOptions` and the admin overview; backups: `orbitdrive-backup` on the relay host (see relay.md) with retention set there; Firestore backups do not exist because Firestore is not created.
