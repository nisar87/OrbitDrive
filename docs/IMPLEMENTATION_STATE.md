# Implementation state

Updated: 2026-11-18 (session 1). Branch: `feature/orbitdrive-foundation`.

**Last code-verified commit: `d4c6dcb` (D6/W6 automated scope; seven workflows green on the feature branch — web 44210663837, security 44210663822, contracts 44210663874, integration 44210663965, gateway 44210663858, desktop 44210663831, desktop-ui 44210663883, relay-release 44210663786, contracts 44206498631, security 44206499702, desktop 44206498598, integration 44206498617, web 44206498609, desktop-ui 44206498670, gateway 44206498709; `main` fast-forwarded to it on 2026-11-18, relay-release succeeded). Previously `98e29a4`** (on `main`, fast-forwarded 2026-11-18 after seven green feature-branch workflows: security 44169390489, contracts 44169390588, desktop 44169390550, desktop-ui 44169390743, gateway 44169390732, integration 44169390695, web 44169390564; the security run performed every audit incl. NuGet, see `docs/security/accepted-risks.md` AR-1, expires 2026-12-17). Earlier reference: `d21dffa` — seven workflows green; `relay-release` run 44000402541 on `main`:
test → publish `relay-dev-0.1.3` → verify-deployment (live relay reported 0.1.3) → **live-agent on windows-2025 and
ubuntu-24.04: a real agent connected outbound over the internet to https://139-185-60-75.sslip.io, authenticated and
obtained a pairing code (3/3 each)**.

Owner instruction (2026-11-17): keep developing through every remaining phase without routine check-ins; commits to
`main` are authorised (practice kept: land on the feature branch, fast-forward `main` only when all workflows are green).

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

Relay automation (owner request, 2026-11-17): pull-based, credential-free deployment and self-maintenance for the relay
host — see `docs/operations/relay.md`. Owner confirmed SSH login works (so the network has internet access); the first-boot
package installs failed as expected (no network at first boot); swap is active; 952 MB RAM, 8.7 % disk used.
**Relay is live (development):** owner ran the pinned bootstrap (commit e55f1b9) and the firewall-order repair
(`infra/relay/fix-firewall.sh`; cause: web-port ACCEPT rules sat after the image's REJECT rule, so the ACME challenge was
blocked — fixed in `bootstrap.sh`). Verified from GitHub's network (`relay-probe`): Let's Encrypt certificate for
`139-185-60-75.sslip.io` valid to 2027-02-15, TLS 1.3, `/v1/meta` returns the deployed version, host status file served.
Verification and monitoring are enabled (`infra/relay/environments/dev.json`). Not yet tested through the relay:
pairing, browsing and transfers with a real agent; behaviour under load in 1 GB; the monitor's alert → issue path;
rollback on the real host; unattended-upgrade reboot; certificate renewal. The server's bootstrap ran from e55f1b9, so its
installed updater/monitor/backup scripts are that version.

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
| D1 | AUTOMATED_VERIFIED (partial) | Owner console (7 pages) on the Windows Fluent theme (light/dark, follows Windows), tray, close-to-tray, Quit, single instance that shows the running window when launched again, same-user local channel, pause, persisted settings, optional start at sign-in (HKCU Run, hidden), optional pause-while-locked, bounded crash recovery (3 restarts / 10 min), Ctrl+1..7 shortcuts. UI Automation 63/63 on windows-2025; **screenshots reviewed** (light and dark; secondary-text contrast measured 5.5:1 / 8.5:1). Missing evidence: Windows 11 client, standard (non-elevated) user, second Windows user, real lock/unlock and sleep, high-contrast mode, 125–200 % DPI, tray-menu interaction |
| W2 / D2 | IN_PROGRESS | Pairing state machine, PC-side approval UI (account, browser, matching code), revocation of a browser; QR image on the PC (encoder verified against a reference encoder and a real decoder), device list + revoke on the PC, gateway device list / revoke / disconnect-PC APIs with isolation tests. Web: approved-browsers list, remove another browser, disconnect PC (two-step confirm). In-site QR scanner with manual fallback, per-address/per-account pairing limits, device rename. Missing: QR scan with a real phone camera; scanner evidence on Firefox/Safari; PC rename |
| W3 / D3 | IN_PROGRESS | Outbound control channel, generations, reconnect; file-backed durable registrations/grants (`OrbitDrive:DataDirectory`) with restart tests; heartbeat/lease liveness (ADR-12) and fault-injection suite. Missing: Firestore, idempotency keys, multi-instance leases, sleep/resume evidence on real Windows |
| W4 / D4 | IN_PROGRESS | **W4:** desktop shell (windows, taskbar, launcher, account menu, themes, phone layout) and full Explorer (tree, history, breadcrumbs, three views, sort, filter, selection, context menu, properties) — Playwright on 5 browser projects + vitest; screenshots reviewed. Missing: Inbox/Transfers apps, virtualised 10,000-entry list, real-device and accessibility-tool evidence. **D4:** roots bound to their disk identity, owner-only hidden-items option (console + local command), listings through a validated pinned handle (race fixed), adversarial suite (encoded separators, real volumes, ancestor-swap race, hard links), 10,000-entry folder usable in five browser projects. Missing: cloud-placeholder policy, EFS/network-share behaviour, mount points inside a root, real removable-disk evidence |
| W5 / D5 | AWAITING_DEVICE_TEST | Automated exit criteria met on `98e29a4`: relay Range/HEAD/206/416/conditional, content revisions, capabilities, 1 GiB relay checksum, 1 GiB save-to-file (slow receiver, bounded memory, resume, source mutation incl. same-size/same-time, cancel/cleanup, disk-full state), relay-observed states, owner bandwidth/concurrency limits, pause on metered connection. Still needs real hardware: the real save-location picker and disks, physical phones, a metered network on real Windows, Safari (WebKit save-to-file tests are skipped: its test sessions refuse page file writes). Not COMPLETE until those are run |
| W6 / D6 | AWAITING_DEVICE_TEST | Automated scope done on `a3a833e`: sealed one-shot grants, gateway invitations (durable, idempotent, expiry, revoke, per-receiver), agent `send_files`/spool/ctl fallback/classic SendTo shortcuts, native `IExplorerCommand` (built + COM-tested on windows-2025), web Inbox, notices, opt-in local notifications, PWA manifest + static-only service worker. Missing: modern menu in real Win 11 Explorer (signing identity), real FCM push (Firebase credentials), real phones; WPF console toggle for SendTo shortcuts not built. |
| W7 / D7 | AUTOMATED_VERIFIED (partial) | Automated scope done on `f79897c` (seven workflows green): content sniffer, image probe, thumbnail helper + cache, preview/derived read variants, inline-serving headers, preview sessions, owner controls (IPC), Preview app (image/text/PDF/media), Icons-view thumbnails, e2e on 5 browser projects, real-decoder corpus on Linux+Windows CI. Missing: WPF console previews switch + delete-stored-previews (done, 0.1.7), PDF origin isolation, real phones, codec matrix, fuzzing |
| W8 / D8 | AUTOMATED_VERIFIED (partial) | Staged ZIP archives (ADR-16), live search (ADR-17), entry checks, favorites/recents, one browser device across PCs (ADR-18); Core, gateway, integration (archives, search, multipc), vitest and Playwright (desktop + emulated phone) pass locally; CI result recorded in `phase-evidence.md`. Not done: WPF console toggles (thumbnails/cache clear), >4 GiB real ZIP64, physical-phone checks, server-side favorites |
| W9 / D9 | AUTOMATED_VERIFIED (partial) | Owner-enabled, add-only, chunked and checksummed uploads (ADR-19/20) with phone UI; local verification complete (Core 229 stand-in, integration uploads 20, regression 91/0, vitest 91, Playwright 69 passed/1 skipped); all seven workflows green on `91be9bf` (on `main`). Limits: no resume across agent restart, no folder upload or drag-and-drop, no Windows/phone evidence |
| W10 / D10 | IN_PROGRESS | **W10 done (automated):** operators from deployment config, recent-sign-in-gated admin API (overview, users, suspend/unsuspend, revoke-all, relay-allowance restore, minimum PC version, audit), owner controls (preferences, export, revoke-all, account deletion), per-account daily relay cap, lazy admin chunk. Local: integration 178 pass/0 fail, Core+gateway stand-in 287 pass, vitest 91, Playwright 74 pass/1 skip (+1 CPU-contention flake, passes alone). **D10 partial:** signed-update verification, staging, drain, interrupted-apply recovery and a release-signing tool are built and tested; **installer/MSIX packaging, a production signing key, and Windows 11 install/upgrade/uninstall evidence are not started and need the owner's signing identity and machine.** CI for the commit not yet inspected **CI:** all seven workflows green on `81c8351` (earlier `7fafe3a` failed `desktop` on a real Windows delete-while-open bug and `web` on a real upload-resume bug; both fixed, see phase-evidence). Packaging, production signing key and Windows 11 evidence remain owner-blocked. |
| W11 / D11 | AUTOMATED_VERIFIED (partial) | Security headers/CSP from one builder, gateway hardening (handshake cap, auth-before-validate), a11y sweep, PC activity history + consented support bundle, bounded soak and measured performance report, download-session retirement. All seven workflows green on `b3edc6c` (soak, perf guards and session retirement included; `main` fast-forwarded to it; a real keyboard-focus bug found via a Firefox CI failure was fixed). WPF History page written (Activity page: plain-language rows, copy, clear; all seven workflows green on eb40c32, published as 0.1.5). 30-minute soak passed on Windows and Linux after a real upload-slot leak was fixed (9cf9f37). Missing: real devices, visual rubric score, Windows service/sleep-wake soak, coverage-guided fuzzing (seeded gateway, validator, agent-channel and upload fuzzing added 2026-11-19). |
| W12 / D12 | AUTOMATED_VERIFIED (partial) — release candidate awaiting owner-side evidence | Automated scope done on `378ecf2`: environment templates + validator, maintenance mode and `/readyz`, CI-built Windows release-candidate artifact (zip, checksums, notices, manifest; independently verified and smoke-run in CI), keyless dev deploy, **in-app self-update (Update now) + `desktop-release` feed workflow, built and verified on `ec7c8e4`; waiting only for the owner's update key (GitHub secret `UPDATE_SIGNING_KEY` + public key committed) before the first feed is published**, owner handoff, support matrix, runbooks, acceptance checklist. **Not done (needs the owner):** installer + signing, Windows 11 evidence, real phones, staging/production projects, rollback rehearsal on staging. See `docs/release/OWNER_HANDOFF.md`. |

The first vertical slice (pair → browse approved root → hash-verified ranged download, across Windows agent, gateway and
web) works end to end in CI. It is a slice through M1–M2, not completion of those milestones.

## Blockers needing the owner

1. **Firebase service-account key** (owner to create and store as a secret) for Firestore (W3), revocation checks and FCM (W6). Auth itself needs none.
2. **Real Windows 11 machine (standard user)** for the Explorer modern-menu spike (D0/D6), lifecycle and DPI evidence.
3. **Physical Android / iPhone** for the 1 GiB native-download spike (W0) and all device claims.
4. **Relay host**: development relay exists (Oracle Always Free, `https://139-185-60-75.sslip.io`). Still needed for cross-network proof: a verified Firebase test account and the repo secrets `ORBIT_TEST_EMAIL`, `ORBIT_TEST_PASSWORD`, `ORBIT_FIREBASE_WEB_API_KEY`; a real domain later.
5. **Code-signing identity** for D10.
6. **Decision (optional)**: ADR-1 keeps a plain WebSocket envelope instead of SignalR — see `docs/architecture/decisions.md`; say so if you prefer SignalR.

## Known gaps in what exists

- Gateway registrations/grants persist only when `OrbitDrive:DataDirectory` is set. The live relay has it enabled since 2026-11-17 (owner re-ran the bootstrap; directory present, still empty). Pending pairings and download capabilities never survive a restart (by design).
- Device credential and dev token live in `localStorage`; no CSP yet.
- Same-size rewrite that restores the timestamp between prepare and read is not detected.
- Agent-side busy limits (16 requests, 4 streams) and pending-request caps have no dedicated tests yet.
- Hard-link refusal is Windows-only; root volume-identity binding not implemented.
- Relay Dockerfile and firebase.json are unvalidated drafts.
- Console screenshots (9 per run) exist as CI artifacts but have not been visually reviewed by anyone.
- The local channel relies on the OS same-user pipe restriction; a second-Windows-user denial test has not been run.

## Next steps (dependency order) — what is still unfinished

Blocked on the owner (cannot be done from the build environment):
- ~~**Update key**~~ DONE 2026-11-19: public key `orbitdrive-update-1` committed (owner created the private key and the GitHub secret `UPDATE_SIGNING_KEY`). Original note: (free, 2 minutes): `node tools/release/make-update-key.mjs`, add the printed private key as GitHub secret `UPDATE_SIGNING_KEY`, give the PUBLIC line to be committed into `update-keys.json`. Then fast-forward `main` to a green commit and `desktop-release` publishes `desktop-dev`.
- **Verified Firebase test account for CI**: create a user in `orbitdrive-dev`, verify its email, and add repo secrets
  `ORBIT_TEST_EMAIL`, `ORBIT_TEST_PASSWORD`, `ORBIT_FIREBASE_WEB_API_KEY`. Unblocks claim → browse → transfer through the
  live relay in `live-relay.test.mjs` (today it stops at pairing-code issuance) and the first real sign-in.
- Windows 11 standard-user machine (Explorer modern menu, install, lifecycle, DPI), physical Android/iPhone, a
  code-signing identity, a service-account key (Firestore, FCM, token revocation), review of the console screenshots.

Not blocked, in order:
1. W3/D3 remainder: Firestore when credentials exist. (Durable store, leases, fault suite and ADR-1 done; **idempotency keys for retried "start work" requests done** — see phase evidence, 2026-11-19.)
2. W2 remainder: real-phone camera scan (owner device needed). (`OrbitDrive__TrustedProxies` was set on the live relay by the owner on 2026-11-17.)
   **Accepted risk AR-1 (owner-authorised 2026-11-18, expires 2026-12-17):** `picomatch` GHSA-fc5q-39xc-vjpf in build/test tooling only — NOT fixed; see `docs/security/accepted-risks.md`. The `security` workflow fails again on expiry, on any new picomatch release, or if it becomes reachable some other way. Re-review before 2026-12-17.
   After that: D6/W6 (Explorer send-to-phone, incoming files).
3. D1 remainder needs the owner's Windows 11 PC: standard user, lock/sleep, high contrast, DPI, tray menu.
4. D4/W4: Windows-style web shell (desktop, taskbar, windows), full Explorer (views, sort, selection, properties,
   context menus, back/forward/up), root volume binding. Visual iteration against the rubric starts here.
5. D5/W5: only the physical-device checks listed in the phase row remain. D6/W6 automated scope is done (see phase row). D7/W7 automated scope is done (see phase row). D8/W8 and D9/W9 done (see phase rows). **Next dependency-ready work: D10/W10.**
6. D6/W6: native Explorer command + package identity, invitations, Inbox, FCM, PWA/service worker.
7. D8/W8 archives, search, favorites, multi-PC; D9/W9 uploads; D10 packaging/signing/updates;
   W10 admin and owner controls; D11/W11 hardening, soak, accessibility, visual gate; D12/W12 release candidate.
8. Relay: build-provenance attestation, off-host backup, self-updating host scripts, migrate to A1.Flex when capacity
   exists, a real domain, production channel.

## Continue from here

```sh
git clone https://github.com/nisar87/OrbitDrive && cd OrbitDrive && git checkout feature/orbitdrive-foundation
dotnet build OrbitDrive.slnx -c Release
ORBIT_LARGE_MIB=1024 node --test tests/integration/slice.test.mjs
(cd apps/web && npm ci && npx tsc --noEmit && npx vitest run && npx playwright install chromium && npx playwright test)
gh api "repos/nisar87/OrbitDrive/actions/runs?per_page=10" --jq '.workflow_runs[] | "\(.id) \(.name) \(.head_sha[0:7]) \(.conclusion)"'
```

Toolchain: .NET SDK 10.0.x (CI 10.0.404), Node 22, Playwright 1.65, TypeScript 7, Vite 8, React 19.
