# Implementation state

Updated: 2026-11-17 (session 1). Branch: `feature/orbitdrive-foundation`.

**Last code-verified commit: `c23e4ae`** — all seven workflows green: contracts 43961529396, gateway 43961529361,
desktop 43961529459, desktop-ui 43961529507 (29/29 UI Automation checks on the real console), integration 43961529432
(Windows + Linux, slice + IPC suites), web 43961529400, security 43961529697.

Branch policy (owner, 2026-11-17): `main` exists and is the default branch. Work lands on
`feature/orbitdrive-foundation` first; `main` is fast-forwarded only to commits whose seven workflows are green.
Firebase is parked by the owner for now: keep the development identity and build everything that does not need it.

## Phase status

| Phase | Status | Notes |
|---|---|---|
| W0 | IN_PROGRESS | Done: schema + generated models, drift/serialization tests, CI baseline, threat model, production guards, static/relay boundary. Missing: Firebase emulator config, dependency licence review, start scripts, **real-phone 1 GiB download spike (needs devices)** |
| D0 | IN_PROGRESS | Done: solution, Core/Agent separation, WPF launch + UI Automation + capture spike on windows-2025, bounded-memory 1 GiB stream, toolchain record. Missing: modern Explorer command spike (needs Windows 11 + package identity), packaging/app-identity route, reconnect-without-duplicates spike |
| W1 | NOT_STARTED | Parked with Firebase. Development identity stands in |
| D1 | AUTOMATED_VERIFIED (partial) | Owner console (7 pages), tray, close-to-tray, Quit, single instance (console + agent), same-user local channel with closed command set, pause, persisted settings. UI Automation 29/29 on windows-2025. Missing: start-at-sign-in, crash-restart policy, lock/sleep behaviour, dark/high-contrast themes, DPI review, standard-user and second-Windows-user evidence, tray interaction test, **visual review of screenshots (artifact `owner-console-ui`, not viewable from the build sandbox)** |
| W2 / D2 | IN_PROGRESS | Pairing state machine, PC-side approval UI (account, browser, matching code), revocation of a browser; no QR image yet (link + code shown), no in-site scanner, no device list/revoke on the PC, no web Devices page |
| W3 / D3 | IN_PROGRESS (early) | Outbound control channel, generations, reconnect; no leases/heartbeat expiry, no Firestore, no fault-injection suite |
| W4 / D4 | IN_PROGRESS (early) | Secure browsing core + minimal Explorer view; no shell, sorting, selection, properties, volume binding |
| W5 / D5 | IN_PROGRESS (early) | Range relay, revisions, capabilities, 1 GiB verified; no managed save mode, interrupted-resume e2e, quotas |
| W6–W12, D6–D12 | NOT_STARTED | |

The first vertical slice (pair → browse approved root → hash-verified ranged download, across Windows agent, gateway and
web) works end to end in CI. It is a slice through M1–M2, not completion of those milestones.

## Blockers needing the owner

1. **Firebase**: parked by the owner. Needed later for W1 (Auth), W3 (Firestore), W6 (FCM).
2. **Real Windows 11 machine (standard user)** for the Explorer modern-menu spike (D0/D6), lifecycle and DPI evidence.
3. **Physical Android / iPhone** for the 1 GiB native-download spike (W0) and all device claims.
4. **Relay host** (VM/VPS + TLS + DNS) — nothing provisioned; cross-network proof is impossible until one exists.
5. **Code-signing identity** for D10.
6. **Decision**: ADR-1 (plain WebSocket envelope instead of SignalR) — see `docs/architecture/decisions.md`.

## Known gaps in what exists

- Gateway state is in memory: a restart drops pairings and grants (fails closed).
- Device credential and dev token live in `localStorage`; no CSP yet.
- Same-size rewrite that restores the timestamp between prepare and read is not detected.
- Agent-side busy limits (16 requests, 4 streams) and pending-request caps have no dedicated tests yet.
- Hard-link refusal is Windows-only; root volume-identity binding not implemented.
- Relay Dockerfile and firebase.json are unvalidated drafts.
- Console screenshots (9 per run) exist as CI artifacts but have not been visually reviewed by anyone.
- The local channel relies on the OS same-user pipe restriction; a second-Windows-user denial test has not been run.

## Next steps (dependency order)

1. D2/W2 without Firebase: QR image on the PC (needs a QR encoder; no third-party package has been vetted yet),
   device list + revoke on the PC and in the web app, revoke PC, in-site QR scanner with manual fallback, per-IP rate limits.
2. D1 leftovers: start at sign-in (opt-in), bounded crash restart, themes/high contrast, keyboard pass, tray test.
3. W3/D3: durable gateway store (file-backed stand-in for Firestore), leases/heartbeats, fault-injection; decide ADR-1.
4. D4/W4: Windows-style web shell + full Explorer; root volume binding. Then D5/W5 completion, D6/W6.
5. When Firebase is back in scope: W1 (ID-token verification), Firestore store, FCM.

## Continue from here

```sh
git clone https://github.com/nisar87/OrbitDrive && cd OrbitDrive && git checkout feature/orbitdrive-foundation
dotnet build OrbitDrive.slnx -c Release
ORBIT_LARGE_MIB=1024 node --test tests/integration/slice.test.mjs
(cd apps/web && npm ci && npx tsc --noEmit && npx vitest run && npx playwright install chromium && npx playwright test)
gh api "repos/nisar87/OrbitDrive/actions/runs?per_page=10" --jq '.workflow_runs[] | "\(.id) \(.name) \(.head_sha[0:7]) \(.conclusion)"'
```

Toolchain: .NET SDK 10.0.x (CI 10.0.404), Node 22, Playwright 1.65, TypeScript 7, Vite 8, React 19.
