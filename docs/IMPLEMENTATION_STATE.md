# Implementation state

Updated: 2026-11-17 (session 1). Branch: `feature/orbitdrive-foundation`.

**Last code-verified commit: `8390f81`** — all seven workflows green: contracts 43974825117, gateway 43974825202,
desktop 43974825184, desktop-ui 43974825160, integration 43974825234, web 43974825166, security 43974825223.

Relay host decision (owner, 2026-11-17): Oracle Cloud, tenancy `nisar87`, home region UAE East (Dubai, `me-dubai-1`),
Free Tier (in trial). Target: `orbitdrive-relay`, VM.Standard.A1.Flex (Always Free-eligible) 1 OCPU / 6 GB, Canonical
Ubuntu 24.04, 50 GB boot volume, new VCN + public subnet, Oracle-generated SSH key pair held only by the owner,
first-boot script `infra/relay/cloud-init.yaml`. **Create attempt on 2026-11-17 failed: "Out of capacity for shape
VM.Standard.A1.Flex in availability domain AD-1".** Owner then chose the temporary x86-64 Always Free shape. **Created 2026-11-17 00:27 UTC and Running:** `orbitdrive-relay`,
VM.Standard.E2.1.Micro (Always Free-eligible, 1 OCPU / 1 GB), Canonical Ubuntu 24.04 build 2026.11.05-0, 50 GB Balanced
boot volume, new VCN `vcn-20261117-0500` + subnet `subnet-20261117-0500` (10.0.0.0/24), private IP 10.0.0.129, **no public
IP yet**, owner's SSH public key (`ssh-key-2026-11-17`), first-boot script = `infra/relay/cloud-init.yaml` (result not yet
checked on the host). Instance OCID ends `...zymwlqjcq`. Done 2026-11-17: default security list ingress is now TCP 22 from 175.107.226.244/32 only (owner's IP at the
time), TCP 80 and TCP 443 from anywhere, plus Oracle's default ICMP rules; ephemeral public IP **139.185.60.75** attached
(ephemeral: it changes if the instance is terminated/recreated). NOT verified: that the VCN has an internet gateway and
route (the console offers a "Connect public subnet to internet" quick action, which suggests it may not), and SSH login.
The first-boot script ran before the server had internet access, so its package installs very likely failed and must be
re-run. Still to do: owner confirms SSH login and
`/var/log/orbitdrive-init.log`, deploy the gateway behind Caddy, test, check the billing page. A1.Flex remains the
intended final shape (migrate when capacity exists; the script and deployment stay architecture-neutral). The owner's rules:
Always Free only, no Pay As You Go, SSH restricted to the owner's IP, do not deploy the public website until the relay is tested.

Hosting decision (owner, 2026-11-17): no subdomain of the AI CV Studio domain. The web app will be served from the
`orbitdrive-dev` project's own Hosting address until the owner buys a new domain. **Nothing is deployed.** A live site
is pointless until a public relay exists, and the relay host (possible cost) is still the owner's open decision.

Branch policy (owner, 2026-11-17): `main` exists and is the default branch. Work lands on
`feature/orbitdrive-foundation` first; `main` is fast-forwarded only to commits whose seven workflows are green.
Firebase is back in scope (owner, 2026-11-17): project `orbitdrive-dev` was created for development.

## Phase status

| Phase | Status | Notes |
|---|---|---|
| W0 | IN_PROGRESS | Done: schema + generated models, drift/serialization tests, CI baseline, threat model, production guards, static/relay boundary. Missing: Firebase emulator config, dependency licence review, start scripts, **real-phone 1 GiB download spike (needs devices)** |
| D0 | IN_PROGRESS | Done: solution, Core/Agent separation, WPF launch + UI Automation + capture spike on windows-2025, bounded-memory 1 GiB stream, toolchain record. Missing: modern Explorer command spike (needs Windows 11 + package identity), packaging/app-identity route, reconnect-without-duplicates spike |
| W1 | AUTOMATED_VERIFIED (partial) | Gateway verifies Firebase ID tokens (23 real-signature cases). Web: sign-up with email verification, sign-in, password reset (no account enumeration), sign-out, optional "keep me signed in", tokens never stored by OrbitDrive; tested in 5 browser projects against the local Auth emulator with the real gateway and agent. Firebase project `orbitdrive-dev` exists (Email/Password on). Missing: **a real sign-in against `orbitdrive-dev` with real email delivery**, token-revocation check (needs service credentials), revoke-all-devices, Google sign-in, MFA decision |
| D1 | AUTOMATED_VERIFIED (partial) | Owner console (7 pages), tray, close-to-tray, Quit, single instance (console + agent), same-user local channel with closed command set, pause, persisted settings. UI Automation 29/29 on windows-2025. Missing: start-at-sign-in, crash-restart policy, lock/sleep behaviour, dark/high-contrast themes, DPI review, standard-user and second-Windows-user evidence, tray interaction test, **visual review of screenshots (artifact `owner-console-ui`, not viewable from the build sandbox)** |
| W2 / D2 | IN_PROGRESS | Pairing state machine, PC-side approval UI (account, browser, matching code), revocation of a browser; QR image on the PC (encoder verified against a reference encoder and a real decoder), device list + revoke on the PC, gateway device list / revoke / disconnect-PC APIs with isolation tests. Web: approved-browsers list, remove another browser, disconnect PC (two-step confirm). Missing: in-site QR scanner, rename device, per-IP rate limits, QR scan with a real phone camera |
| W3 / D3 | IN_PROGRESS (early) | Outbound control channel, generations, reconnect; no leases/heartbeat expiry, no Firestore, no fault-injection suite |
| W4 / D4 | IN_PROGRESS (early) | Secure browsing core + minimal Explorer view; no shell, sorting, selection, properties, volume binding |
| W5 / D5 | IN_PROGRESS (early) | Range relay, revisions, capabilities, 1 GiB verified; no managed save mode, interrupted-resume e2e, quotas |
| W6–W12, D6–D12 | NOT_STARTED | |

The first vertical slice (pair → browse approved root → hash-verified ranged download, across Windows agent, gateway and
web) works end to end in CI. It is a slice through M1–M2, not completion of those milestones.

## Blockers needing the owner

1. **Firebase service-account key** (owner to create and store as a secret) for Firestore (W3), revocation checks and FCM (W6). Auth itself needs none.
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

1. W1 live check: build the web app with the real `orbitdrive-dev` config, run a gateway with
   `OrbitDrive__Firebase__ProjectId=orbitdrive-dev`, create a real account, confirm the verification email arrives and
   the gateway accepts the real token. Needs `localhost` (or the chosen web origin) in Firebase authorized domains.
2. W2 remainder: in-site QR scanner with manual fallback (camera test needs a fake video feed), per-IP rate limits.
3. D1 leftovers: start at sign-in (opt-in), bounded crash restart, themes/high contrast, keyboard pass, tray test.
4. W3/D3: durable gateway store (file-backed stand-in for Firestore), leases/heartbeats, fault-injection; decide ADR-1.
5. D4/W4: Windows-style web shell + full Explorer; root volume binding. Then D5/W5 completion, D6/W6.

## Continue from here

```sh
git clone https://github.com/nisar87/OrbitDrive && cd OrbitDrive && git checkout feature/orbitdrive-foundation
dotnet build OrbitDrive.slnx -c Release
ORBIT_LARGE_MIB=1024 node --test tests/integration/slice.test.mjs
(cd apps/web && npm ci && npx tsc --noEmit && npx vitest run && npx playwright install chromium && npx playwright test)
gh api "repos/nisar87/OrbitDrive/actions/runs?per_page=10" --jq '.workflow_runs[] | "\(.id) \(.name) \(.head_sha[0:7]) \(.conclusion)"'
```

Toolchain: .NET SDK 10.0.x (CI 10.0.404), Node 22, Playwright 1.65, TypeScript 7, Vite 8, React 19.
