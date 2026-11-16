# Implementation state

Updated: 2026-11-17 (session 1). Branch: `feature/orbitdrive-foundation`.

**Last code-verified commit: `9592d39`** — all seven workflows green (run IDs in `docs/evidence/phase-evidence.md`).
Commits after it in this session change documentation/infra drafts only; check their runs before relying on them.

## Phase status

| Phase | Status | Notes |
|---|---|---|
| W0 | IN_PROGRESS | Done: schema + generated models, drift/serialization tests, CI baseline, threat model, production guards, static/relay boundary. Missing: Firebase emulator config, dependency licence review, start scripts, **real-phone 1 GiB download spike (needs devices)** |
| D0 | IN_PROGRESS | Done: solution, Core/Agent separation, WPF launch + UI Automation + capture spike on windows-2025, bounded-memory 1 GiB stream, toolchain record. Missing: modern Explorer command spike (needs Windows 11 + package identity), packaging/app-identity route, reconnect-without-duplicates spike |
| W1 / D1 | NOT_STARTED | Development identity stands in for Firebase Auth; agent is a console host |
| W2 / D2 | IN_PROGRESS (early) | Pairing state machine, PC approval, revocation of a browser exist and are tested; no QR image, no scanner, no Devices page, no PC-side UI |
| W3 / D3 | IN_PROGRESS (early) | Outbound control channel, generations, reconnect; no leases/heartbeat expiry, no Firestore, no fault-injection suite |
| W4 / D4 | IN_PROGRESS (early) | Secure browsing core + minimal Explorer view; no shell, sorting, selection, properties, volume binding |
| W5 / D5 | IN_PROGRESS (early) | Range relay, revisions, capabilities, 1 GiB verified; no managed save mode, interrupted-resume e2e, quotas |
| W6–W12, D6–D12 | NOT_STARTED | |

The first vertical slice (pair → browse approved root → hash-verified ranged download, across Windows agent, gateway and
web) works end to end in CI. It is a slice through M1–M2, not completion of those milestones.

## Blockers needing the owner

1. **Firebase**: no project IDs/config. Needed for W1 (Auth), W3 (Firestore), W6 (FCM). Emulator work can proceed without it.
2. **Real Windows 11 machine (standard user)** for the Explorer modern-menu spike (D0/D6), lifecycle and DPI evidence.
3. **Physical Android / iPhone** for the 1 GiB native-download spike (W0) and all device claims.
4. **Relay host** (VM/VPS + TLS + DNS) — nothing provisioned; cross-network proof is impossible until one exists.
5. **Code-signing identity** for D10.
6. **Repository default branch**: the repo was empty, so GitHub made `feature/orbitdrive-foundation` the default.
   No `main` was created (not authorised). Create `main` and set it as default when you want a PR target.
7. **Decision**: ADR-1 (plain WebSocket envelope instead of SignalR) — see `docs/architecture/decisions.md`.

## Known gaps in what exists

- Gateway state is in memory: a restart drops pairings and grants (fails closed).
- Device credential and dev token live in `localStorage`; no CSP yet.
- Same-size rewrite that restores the timestamp between prepare and read is not detected.
- Agent-side busy limits (16 requests, 4 streams) and pending-request caps have no dedicated tests yet.
- Hard-link refusal is Windows-only; root volume-identity binding not implemented.
- Relay Dockerfile and firebase.json are unvalidated drafts. No draft PR exists (no base branch).
- WPF screenshot artifact exists in CI but was not visually reviewed.

## Next steps (dependency order)

1. Finish W0/D0: Firebase emulator config + gateway emulator tests; reconnect/duplicate spike; nightly 5 GiB soak workflow;
   tests for the untested limits above; build the relay container in CI.
2. W1 + D1: Firebase ID-token verification behind `IIdentityVerifier` (Admin SDK, emulator tests: expired, wrong-project,
   revoked); WPF owner console pages, tray, single instance, typed local IPC to the agent, UI Automation tests.
3. W2 + D2: QR rendering on the PC, in-site scanner + manual fallback, Devices page, revoke PC, per-IP rate limits.
4. W3 + D3: Firestore-backed `Store`, leases/heartbeats, fault-injection; decide ADR-1.
5. Then D4/W4 (shell + Explorer), D5/W5 completion, D6/W6 (Explorer send → Inbox).

## Continue from here

```sh
git clone https://github.com/nisar87/OrbitDrive && cd OrbitDrive && git checkout feature/orbitdrive-foundation
dotnet build OrbitDrive.slnx -c Release
ORBIT_LARGE_MIB=1024 node --test tests/integration/slice.test.mjs
(cd apps/web && npm ci && npx tsc --noEmit && npx vitest run && npx playwright install chromium && npx playwright test)
gh api "repos/nisar87/OrbitDrive/actions/runs?per_page=10" --jq '.workflow_runs[] | "\(.id) \(.name) \(.head_sha[0:7]) \(.conclusion)"'
```

Toolchain: .NET SDK 10.0.x (CI 10.0.404), Node 22, Playwright 1.65, TypeScript 7, Vite 8, React 19.
