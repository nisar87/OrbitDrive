# Implementation state

Updated: 2026-11-18 (session 1). Branch: `feature/orbitdrive-foundation`.


## Audit-fix round (branch `feature/audit-fixes`, from `feature/operator-console-v2` 4c46a57), 2026-11-25

Owner-approved fixes for the AI-assisted review of 2026-11-25 (an AI-assisted review, not an independent audit; commit 24dfb04). Owner decisions: (a) the relay keeps no
file names (id, size, expiry only; the PC supplies or seals the name); (b) ordering is hidden until the start date behind one switch ("plan limits"); (c) thumbnails are
free, viewing counts toward daily data, the relay cap and the per-file size. Evidence: `docs/evidence/phase-evidence.md` (section "Audit-fix round"), matrix row R63.

| Finding | What changed | State |
|---|---|---|
| F-001, 002, 017 | Unpaired PCs live in memory only (cap 2,000, idle eviction, never saved); version text capped at 32; per-address handshake, connection and registration limits; handshake wait 4 s; the limiter keeps separate tables for anonymous and signed-in keys and groups IPv6 by /64 | Enforced; tested (unit, integration) |
| F-003 | Invitation names are sealed by the PC (`sn1.` prefix); the relay stores id, size, expiry and an opaque blob; the Inbox asks the PC to open names (offline PC: no name); old plaintext names are read into memory only and never written; backups exclude `invitations.json` | Enforced; tested; PC side verified on Linux only |
| F-004, 014, 018 | Thumbnails free; viewing counts toward daily data, relay cap and per-file size; a file counts once a day when the bytes delivered across all of the account's links for that file revision cover it (so splitting across links no longer avoids the count; the memory of covered ranges is cleared by a restart, which can only make a file count again); the daily relay cap lives in the billing store (survives restarts) | Enforced; unit + integration |
| F-005 | `RequireOperatorSecondFactor` (reads `firebase.sign_in_second_factor`), console banner, docs | **Ready but off** (owner enables in Firebase, then `--require-operator-second-factor`); relay check tested with a development token carrying the claim, not with a real Firebase second factor |
| F-006 | Every operator change needs a 3 to 200 character reason, writes the audit record first and does not change anything when the record cannot be written; null and wrong-type bodies are 4xx | Enforced; integration |
| F-007 | `set_pc_name` and `rotate_identity` added to the IPC command list; a test parses the handler switch against the list in both directions | Enforced; Core test runs in CI (not run locally: no NuGet) |
| F-008 | `tests/integration/run-all.mjs` runs every `*.test.mjs` (explicit, reasoned skip list); new `billing-http.test.mjs` and `audit-fixes.test.mjs` | Enforced in `integration` and `relay-release` |
| F-009 | A damaged `billing.json` is set aside; the relay starts; plan limits and ordering stay off; console banner and monitor alert `billing_data_damaged` | Enforced; unit |
| F-010 | Backup excludes invitations; optional encrypted off-host copy (gpg AES-256, refuses to send unencrypted); `orbitdrive-restore-verify` drill; docs | **Ready but off** until `backup.env` is written; a full restore on a fresh VM has not been done |
| F-011 | Updater refuses a lower version; verifies a detached ECDSA signature when `/etc/orbitdrive/release-signing.pub` exists; `scripts/relay-sign.mjs`; CI signs when the secret `RELAY_SIGNING_KEY` exists | Downgrade refusal enforced; signing **ready but off** (no key exists) |
| F-012 | Export and delete need only a recent sign-in (no paired browser); export includes billing; the web app deletes the Firebase sign-in record after the relay confirms; paid orders are kept in reduced form and the text says so | Enforced; relay side integration-tested; Firebase deletion (`deleteUser`) not run against real Firebase |
| F-013 | Ordering hidden in the web app and refused by the relay until the start date; site and privacy policy rewritten from the data inventory | Enforced; the policy text needs the owner's and a lawyer's review (not done) |
| F-015, 016 | References compared ignoring case and spaces; exact duplicates refused; owner's list flags shared references; coupon use counted at verification, one use per account; unpaid orders expire after 3 days | Enforced; unit + integration |
| F-019 | Exclusive `relay.lock` in the data directory | Enforced; unit |
| F-020 | Billing imported once into Firestore; audit log, minimum version and invitations stored in Firestore documents | Code written and compiled; **not run against real Firestore** |
| F-021 | Joining an organisation says that admins can sign out every browser of the account | Text only |
| F-022 | A test with a relay piece cap far below the PC cap, so only the relay can refuse | Added |
| F-025 | File names are isolated with `bdi` and the extension is drawn on the right | Unit-tested split; not seen in a real browser |
| F-028 | README, SECURITY.md, COMPLETION_AUDIT corrected | Done |
| Held items | Sign-out asks "also remove the pairing from this browser?" (default keep); relay address removed from desktop Settings and shown read-only under Diagnostics; `--operator-uid`; F-023 recorded as AR-2 with a placeholder 30-day expiry | Web: tsc and vitest only locally, Playwright in CI; desktop: CI only (WPF) |

**Not done in this round (owner decisions):** the Windows code-signing certificate and the lawyer review. **Not verifiable here:** anything on Windows, real browsers or phones, real Firestore, real Firebase second factor, the Oracle host.

Local verification (Linux sandbox, nuget.org blocked): the gateway was built in a scratch copy with two Firestore files and one Google credential file replaced by stubs, and its C# unit tests ran through a stand-in for xunit (148 passed). The agent, gateway and integration tests ran as real processes (all 34 integration files except the two that need an emulator or a live relay). Web: `tsc --noEmit` and `vitest` (133 tests). These are not Windows or phone results.

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
| W0 | IN_PROGRESS | Done: schema + generated models, drift/serialization tests, CI baseline, threat model, production guards, static/relay boundary. Added 2026-11-22: npm licence review (`scripts/licence-check.mjs`, `docs/security/dependency-licences.md`, run in `security`), `scripts/dev-start.mjs`; the Firestore emulator already runs in the `gateway` workflow. Missing: **real-phone 1 GiB download spike (needs devices)** |
| D0 | IN_PROGRESS | Done: solution, Core/Agent separation, WPF launch + UI Automation + capture spike on windows-2025, bounded-memory 1 GiB stream, toolchain record. Missing: modern Explorer command spike (needs Windows 11 + package identity), packaging/app-identity route, reconnect-without-duplicates spike |
| W1 | AUTOMATED_VERIFIED (partial) | Gateway verifies Firebase ID tokens (23 real-signature cases). Web: sign-up with email verification, sign-in, password reset (no account enumeration), sign-out, optional "keep me signed in", tokens never stored by OrbitDrive; tested in 5 browser projects against the local Auth emulator with the real gateway and agent. Firebase project `orbitdrive-dev` exists (Email/Password on). Missing: **a real sign-in against `orbitdrive-dev` with real email delivery**, token-revocation check (needs service credentials), revoke-all-devices, Google sign-in, MFA decision |
| D1 | AUTOMATED_VERIFIED (partial) | Owner console (7 pages) on the Windows Fluent theme (light/dark, follows Windows), tray, close-to-tray, Quit, single instance that shows the running window when launched again, same-user local channel, pause, persisted settings, optional start at sign-in (HKCU Run, hidden), optional pause-while-locked, bounded crash recovery (3 restarts / 10 min), Ctrl+1..7 shortcuts. UI Automation 63/63 on windows-2025; **screenshots reviewed** (light and dark; secondary-text contrast measured 5.5:1 / 8.5:1). Missing evidence: Windows 11 client, standard (non-elevated) user, second Windows user, real lock/unlock and sleep, high-contrast mode, 125–200 % DPI, tray-menu interaction |
| W2 / D2 | IN_PROGRESS | Pairing state machine, PC-side approval UI (account, browser, matching code), revocation of a browser; QR image on the PC (encoder verified against a reference encoder and a real decoder), device list + revoke on the PC, gateway device list / revoke / disconnect-PC APIs with isolation tests. Web: approved-browsers list, remove another browser, disconnect PC (two-step confirm). In-site QR scanner with manual fallback, per-address/per-account pairing limits, device rename. Missing: QR scan with a real phone camera; scanner evidence on Firefox/Safari. PC rename done 2026-11-22 (agent `set_pc_name`, console field, `pc-rename.test.mjs`) |
| W3 / D3 | IN_PROGRESS | Outbound control channel, generations, reconnect; file-backed durable registrations/grants (`OrbitDrive:DataDirectory`) with restart tests; heartbeat/lease liveness (ADR-12) and fault-injection suite. Missing: Firestore, idempotency keys, multi-instance leases, sleep/resume evidence on real Windows |
| W4 / D4 | IN_PROGRESS | **W4:** desktop shell (windows, taskbar, launcher, account menu, themes, phone layout) and full Explorer (tree, history, breadcrumbs, three views, sort, filter, selection, context menu, properties) — Playwright on 5 browser projects + vitest; screenshots reviewed. Missing: Inbox/Transfers apps, virtualised 10,000-entry list, real-device and accessibility-tool evidence. **D4:** roots bound to their disk identity, owner-only hidden-items option (console + local command), listings through a validated pinned handle (race fixed), adversarial suite (encoded separators, real volumes, ancestor-swap race, hard links), 10,000-entry folder usable in five browser projects. Missing: cloud-placeholder policy, EFS/network-share behaviour, mount points inside a root, real removable-disk evidence |
| W5 / D5 | AWAITING_DEVICE_TEST | Automated exit criteria met on `98e29a4`: relay Range/HEAD/206/416/conditional, content revisions, capabilities, 1 GiB relay checksum, 1 GiB save-to-file (slow receiver, bounded memory, resume, source mutation incl. same-size/same-time, cancel/cleanup, disk-full state), relay-observed states, owner bandwidth/concurrency limits, pause on metered connection. Still needs real hardware: the real save-location picker and disks, physical phones, a metered network on real Windows, Safari (WebKit save-to-file tests are skipped: its test sessions refuse page file writes). Not COMPLETE until those are run |
| W6 / D6 | AWAITING_DEVICE_TEST | Automated scope done on `a3a833e`: sealed one-shot grants, gateway invitations (durable, idempotent, expiry, revoke, per-receiver), agent `send_files`/spool/ctl fallback/classic SendTo shortcuts, native `IExplorerCommand` (built + COM-tested on windows-2025), web Inbox, notices, opt-in local notifications, PWA manifest + static-only service worker. Missing: modern menu in real Win 11 Explorer (signing identity), real FCM push (Firebase credentials), real phones; WPF console toggle for SendTo shortcuts not built. |
| W7 / D7 | AUTOMATED_VERIFIED (partial) | Automated scope done on `f79897c` (seven workflows green): content sniffer, image probe, thumbnail helper + cache, preview/derived read variants, inline-serving headers, preview sessions, owner controls (IPC), Preview app (image/text/PDF/media), Icons-view thumbnails, e2e on 5 browser projects, real-decoder corpus on Linux+Windows CI. Missing: WPF console previews switch + delete-stored-previews (done, 0.1.7), PDF origin isolation, real phones, codec matrix, fuzzing |
| W8 / D8 | AUTOMATED_VERIFIED (partial) | Staged ZIP archives (ADR-16), live search (ADR-17), entry checks, favorites/recents, one browser device across PCs (ADR-18); Core, gateway, integration (archives, search, multipc), vitest and Playwright (desktop + emulated phone) pass locally; CI result recorded in `phase-evidence.md`. Not done: WPF console toggles (thumbnails/cache clear), >4 GiB real ZIP64, physical-phone checks, server-side favorites |
| W9 / D9 | AUTOMATED_VERIFIED (partial) | Owner-enabled, add-only, chunked and checksummed uploads (ADR-19/20) with phone UI; local verification complete (Core 229 stand-in, integration uploads 20, regression 91/0, vitest 91, Playwright 69 passed/1 skipped); all seven workflows green on `91be9bf` (on `main`). Limits: no resume across agent restart, no folder upload or drag-and-drop, no Windows/phone evidence |
| W10 / D10 | IN_PROGRESS | **W10 done (automated):** operators from deployment config, recent-sign-in-gated admin API (overview, users, suspend/unsuspend, revoke-all, relay-allowance restore, minimum PC version, audit), owner controls (preferences, export, revoke-all, account deletion), per-account daily relay cap, lazy admin chunk. Local: integration 178 pass/0 fail, Core+gateway stand-in 287 pass, vitest 91, Playwright 74 pass/1 skip (+1 CPU-contention flake, passes alone). **D10 partial:** signed-update verification, staging, drain, interrupted-apply recovery and a release-signing tool are built and tested; **installer/MSIX packaging, a production signing key, and Windows 11 install/upgrade/uninstall evidence are not started and need the owner's signing identity and machine.** CI for the commit not yet inspected **CI:** all seven workflows green on `81c8351` (earlier `7fafe3a` failed `desktop` on a real Windows delete-while-open bug and `web` on a real upload-resume bug; both fixed, see phase-evidence). Packaging, production signing key and Windows 11 evidence remain owner-blocked. |
| W11 / D11 | AUTOMATED_VERIFIED (partial) | Security headers/CSP from one builder, gateway hardening (handshake cap, auth-before-validate), a11y sweep, PC activity history + consented support bundle, bounded soak and measured performance report, download-session retirement. All seven workflows green on `b3edc6c` (soak, perf guards and session retirement included; `main` fast-forwarded to it; a real keyboard-focus bug found via a Firefox CI failure was fixed). WPF History page written (Activity page: plain-language rows, copy, clear; all seven workflows green on eb40c32, published as 0.1.5). 30-minute soak passed on Windows and Linux after a real upload-slot leak was fixed (9cf9f37). Missing: real devices, visual rubric score, Windows service/sleep-wake soak, coverage-guided fuzzing beyond five parsers (AFL++/SharpFuzz harness for the content sniffer, image probe, path-segment rules, version labels and the console line reader added 2026-11-20: about 20 million executions, no crash or hang; the gateway, agent-channel and upload fuzzing remain seeded black-box) (seeded gateway, validator, agent-channel and upload fuzzing added 2026-11-19). |
| W12 / D12 | AUTOMATED_VERIFIED (partial) — release candidate awaiting owner-side evidence | Automated scope done on `378ecf2`: environment templates + validator, maintenance mode and `/readyz`, CI-built Windows release-candidate artifact (zip, checksums, notices, manifest; independently verified and smoke-run in CI), keyless dev deploy, **in-app self-update (Update now) + `desktop-release` feed workflow, built and verified on `ec7c8e4`; waiting only for the owner's update key (GitHub secret `UPDATE_SIGNING_KEY` + public key committed) before the first feed is published**, owner handoff, support matrix, runbooks, acceptance checklist. **Not done (needs the owner):** installer + signing, Windows 11 evidence, real phones, staging/production projects, rollback rehearsal on staging. See `docs/release/OWNER_HANDOFF.md`. |

The first vertical slice (pair → browse approved root → hash-verified ranged download, across Windows agent, gateway and
web) works end to end in CI. It is a slice through M1–M2, not completion of those milestones.

Latest (2026-11-20): QR/address fix released as 0.1.10 (a release's `defaults.json` `supersedes` list moves a PC off earlier default addresses; owner confirmed the QR scans again). Gateway durable metadata now sits behind `ISnapshotBackend` (file backend unchanged, contract tests with an in-memory backend; seven workflows green on `9744006`, `main` fast-forwarded). Firestore database `(default)` created in `orbitdrive-dev` by the agent through the owner's console session on 2026-11-21: Standard edition, location me-central1 (Doha, permanent), production mode (all client access denied), Spark plan (no backups, no billing). **Firestore backend built and verified against the real Firestore emulator in CI (`f32146c`, seven workflows green; web needed one rerun after a cancelled browser-test step — another instance of the open CI-stability item):** per-document pcs/devices/owners, write-behind with retry and shutdown flush, one-time import of the previous file store, unreadable data quarantined; opt-in on the relay via `bootstrap.sh --use-firestore` (refuses until the owner has placed the key at `/etc/orbitdrive/firebase-key.json`). **Live switch done by the owner 2026-11-21 (`--use-firestore`); relay 0.1.25 (published after fixing a real agent bug that failed the 0.1.24 build's tests: two support bundles in one millisecond collided on the file name and the IOException dropped the console's connection) now has `devices`, `pcs` and `meta` documents in Firestore (seen in the console, not read back through the relay).** **0.1.25 shutdown bug (owner-found, live 2026-11-20 23:16:55 UTC): `ObjectDisposedException` in `FirestoreSnapshotBackend.DisposeAsync`, process aborted (core dump), systemd restarted it. Cause: Program.cs registers the one instance three times, so the container disposed it three times. Fixed in `f773326` (idempotent, thread-safe single shutdown; late saves refused and logged; accepted saves flushed): unit tests (repeated/concurrent disposal, the container path, writes in flight, saves racing shutdown) FAIL with the exact production exception on the 0.1.25 code (proved on a scratch branch) and pass on the fix; real gateway processes on the Firestore emulator require a clean SIGTERM exit (status 0, no exception in the log) across three restarts and a burst of writes. Relay 0.1.26 published and reported live by the verify job. NOT yet shown on the live host: the update that installed 0.1.26 restarted the OLD 0.1.25 process, so its own shutdown may still have crashed; the first restart OF 0.1.26 is the first clean-shutdown evidence (owner to check `coredumpctl list` and the journal). Two scratch branches (`feature/scratch-prove-regression`, `scratch/prove-regression`) could not be deleted through the sandbox proxy; the owner can delete them.** Previously open: a gateway restart reloads from Firestore, and no permission errors in `journalctl -u orbitdrive-gateway` (needs host access). The owner's first key passed through chat and should be replaced after the first live run. **Next (W3):** live switch done. It cannot be built or run in the agent's sandbox (no NuGet, emulator download blocked), so it will be verified only in CI against the Firestore emulator; live Firestore still needs the owner's service-account key.

Latest (2026-11-21): Firebase account-status check built and published in relay 0.1.27 but **OFF** on the live relay (`OrbitDrive:Firebase:CheckRevocation`, enabled by `bootstrap.sh --check-revocation`, needs the key on the host): disabled/deleted accounts and "revoke sessions" cut access within about a minute; Firebase unreachable → a ≤15-minute-old answer is used, none → 503, never silently allowed. Verified with a fake clock/source (7 unit tests) and real RS256 tokens against a stand-in lookup server (5 integration tests); NOT verified against the live Firebase project (permissions of the service account for accounts:lookup unconfirmed). Also corrected: `defaults-migration.test.mjs` (the QR fix) was not in the integration workflow until `c9417be`; it now runs in CI.

## Blockers needing the owner

1. **Firebase service-account key** (owner to create and store as a secret) for Firestore (W3), revocation checks and FCM (W6). Auth itself needs none.
2. **Real Windows 11 machine (standard user)** for the Explorer modern-menu spike (D0/D6), lifecycle and DPI evidence.
3. **Physical Android / iPhone** for the 1 GiB native-download spike (W0) and all device claims.
4. **Relay host**: development relay exists (Oracle Always Free, `https://139-185-60-75.sslip.io`). Still needed for cross-network proof: a verified Firebase test account and the repo secrets `ORBIT_TEST_EMAIL`, `ORBIT_TEST_PASSWORD`, `ORBIT_FIREBASE_WEB_API_KEY`; a real domain later.
5. **Code-signing identity** for D10. DEFERRED by the owner (2026-11-20) until the product is final; builds stay unsigned meanwhile.
6. **Decision (optional)**: ADR-1 keeps a plain WebSocket envelope instead of SignalR — see `docs/architecture/decisions.md`; say so if you prefer SignalR.

## Known gaps in what exists

- Gateway registrations/grants persist only when `OrbitDrive:DataDirectory` is set. The live relay has it enabled since 2026-11-17 (owner re-ran the bootstrap; directory present, still empty). Pending pairings and download capabilities never survive a restart (by design).
- Device credential and dev token live in `localStorage` (the CSP and security headers are set from one builder, W11).
- Same-size rewrite that restores the timestamp between prepare and read is not detected.
- Agent-side busy limits: burst test added (`busy.test.mjs`, 2026-11-20); the 4-stream cap is in `transfers.test.mjs`.
- Hard-link refusal is Windows-only; root volume-identity binding not implemented.
- Relay Dockerfile and firebase.json are unvalidated drafts.
- Console screenshots are CI-run (Windows Server) captures; the owner reviewed the redesigned set on 2026-11-21. Windows 11 behaviour (Mica, DPI, high contrast, lock/unlock, real toasts) is still the owner's to test.
- The local channel relies on the OS same-user pipe restriction. A second-Windows-user denial test now runs in CI (2026-11-20, `scripts/second-user-pipe-test.ps1`, real throwaway local account on windows-2025: refused with access denied). It does not cover a user in a remote or elevated session on a real PC.

Latest (2026-11-21): browser-device secrets now rotate (30 days, old valid 24 h) and unused approvals lapse (90 days); durable in file and Firestore stores; tests in `DeviceCredentialTests`, `device-rotation.test.mjs`, web vitests. Relay release follows the merge to `main`. See `docs/evidence/phase-evidence.md`.

## Next steps (dependency order) — what is still unfinished

Blocked on the owner (cannot be done from the build environment):
- ~~**Update key**~~ DONE 2026-11-19: public key `orbitdrive-update-1` committed (owner created the private key and the GitHub secret `UPDATE_SIGNING_KEY`). Original note: (free, 2 minutes): `node tools/release/make-update-key.mjs`, add the printed private key as GitHub secret `UPDATE_SIGNING_KEY`, give the PUBLIC line to be committed into `update-keys.json`. Then fast-forward `main` to a green commit and `desktop-release` publishes `desktop-dev`.
- **Verified Firebase test account for CI**: create a user in `orbitdrive-dev`, verify its email, and add repo secrets
  `ORBIT_TEST_EMAIL`, `ORBIT_TEST_PASSWORD`, `ORBIT_FIREBASE_WEB_API_KEY`. Unblocks claim → browse → transfer through the
  live relay in `live-relay.test.mjs` (today it stops at pairing-code issuance) and the first real sign-in.
- Windows 11 standard-user machine (Explorer modern menu, install, lifecycle, DPI), physical Android/iPhone, a
  code-signing identity, a service-account key (Firestore, FCM, token revocation).

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


## Console redesign (merged to `main` 2026-11-21; released as desktop 0.1.13)
- Done and green in CI (7db0537): shell + tokens + shared components; Home, Shared folders, Devices, Activity, Settings (all XAML + view models). Design doc: `docs/design/console-redesign.md`; screenshots: `docs/evidence/screenshots/console-redesign/`.
- Toasts done and green (0808bef): Deny + Review only (never Approve), plain finished-transfer notice, tray-balloon fallback; notice rules unit-tested in Core. File-type icons in the web explorer (cae9a4a).
- Next: tray flyout, rebuild the update panel as XAML, accessibility checks on Windows 11 (owner).

## 2026-11-24 completion pass
Added: PC identity key rotation (R07), two-PC isolation test (R32), relay log digest, health history and daily review (R46), ZipWriter fuzzing (R47), Firestore single-writer lease (R41), tablet/landscape viewport test and window re-fit fix (R33), whole-drive confirmation UI test (R08), opt-in real >4 GiB archive job (R26). Stale matrix rows corrected. Full split of built vs owner/device-blocked: `docs/evidence/COMPLETION_AUDIT.md`.

## 2026-11-25 plans, licences, billing and organisations (branch feature/rename-myorbitdrive)
Design and owner decisions: `docs/design/ADMIN_AND_LICENSING_DESIGN.md` (no trial, 3-day grace, panel is the source of prices, free = 5 files/day and 100 MB each, tax later, prices in USD with approximate PKR).
- **Gateway:** plan catalogue and entitlements (`Plans.cs`), billing store with usage counters, coupons, manual-payment orders and organisations (`Billing.cs`), Firestore/file/memory persistence, endpoints (`BillingApi.cs`, `Program.cs`). Enforcement is behind `BillingStore.LimitsEnabled` (owner's start-date switch, default off); counting is always on. Orders are manual payment checked by the owner; this is the seam for a later gateway webhook.
- **Organisations:** Office owner invites people with single-use 7-day codes; members share the owner's Office plan and its daily limits (usage pooled, each member's own count kept); owner/admin roles; owner pass ending (after grace) returns members to their own Free accounts; the audit log holds who/what/when and never file or folder names.
- **Web:** Account page has "Your plan" (usage meters, quote, order, payment proof) and "Organisation"; owner console has a Billing section (plans, prices, rate, limits switch, coupons, orders, per-user passes). **Site:** prices read from public `/v1/plans` with baked USD+PKR fallback (`prices.js`).
- **Verified locally:** web `tsc`, vitest 128/128, site Playwright 36/36, gateway billing/plan/org logic 46/46 through a standalone harness (nuget.org is blocked here).
- **Not verified until CI:** compilation of `Program.cs`, `FirestoreBilling.cs`, and the xunit run; no integration/e2e test exercises the billing HTTP endpoints yet; real Firestore persistence untested; the live exchange rate is a stored number the owner sets (no automatic refresh).
- **Owner to supply:** payment instructions text, confirm USD prices, the date to switch limits on, whether to offer international cards, sales tax advice, operator MFA availability.
- **Admin panels (2026-11-25):** the owner console has per-account detail (30-day daily counts, PCs, browsers, plan, organisation) and an Organisations section (list, open, suspend/restore/delete with a recorded reason). Organisation admins see their members' counts, can sign out a member's browsers and keep a settings-only log. Everything is counts: no file or folder names exist in these views or in the stored data.
- **Settings for every plan:** Account page now shows the person's own 30-day usage history and a "what each plan allows" table beside plan, usage meters, orders and the existing privacy/data controls. There are no Plus-only settings: Plus differs from Free only in limits.
- **Still to verify in CI:** gateway compile of the new endpoints, the fuzz sweep over them, and the web e2e specs that need a rebuilt gateway (admin, a11y, inbox could not run locally).


## 2026-11-25 operator console and organisation page redesigned (branch feature/console-redesign)
Owner feedback: the administrator panel was one scrolling page of stacked forms with a button on every row. Design spec: `docs/design/ADMIN_CONSOLE_REDESIGN.md`.
- **Operator console** (`apps/web/src/features/console/`, `features/admin/AdminApp.tsx`): left navigation (Overview, Accounts, Organisations, Billing, Audit log, Settings; becomes a tab row in a narrow window). Overview opens with "Needs attention" (orders to check, relay cost alert, suspended accounts, refused transfers) then four key numbers; the rest of the relay numbers are folded away. Accounts and Organisations are searchable, filterable, sortable tables (new `GET /v1/admin/accounts` adds plan, organisation and today's counts) that open a detail drawer with tabs (Summary, Usage, Devices, Plan and passes, Actions). Billing has Orders, Plans and prices (one "unsaved changes" bar), Discount codes and Payment details. Every change goes through one dialog that says what will happen and asks for a reason (3 characters or more, as the service requires); Cancel is the first focus and Escape closes only the top dialog.
- **Customer Organisation page** uses the same kit: People table with a Manage drawer (30-day counts, sign out browsers, role, remove), Invitations, Activity, and Leave or Delete behind a confirmation dialog (the browser `confirm()` was removed).
- **Unchanged safety rules:** operator list re-checked on every request with a recent sign-in, no file or folder names anywhere, reasons audited by the service, operator code (and its CSS) is a separate chunk that ordinary accounts never download.
- **Verified locally:** `tsc`, vitest 128/128, and every console view (all pages, billing tabs, drawer tabs, dialog, organisation page for owner, member and no organisation) in a throw-away mocked-API harness in light and dark at 1100 px and 400 px wide: axe WCAG 2.1 AA 0 violations, visual review done. The harness is not committed. This is browser output only, not evidence from Windows or a phone.
- **CI evidence:** the first push (4c46a57) failed only the web workflow (9 Playwright failures; the other six workflows were green). Root causes, both real: (1) the console overlay class `.scrim` collided with the Explorer's global `.scrim { display: none }`, so drawers and the reason dialog never appeared (fixed by renaming to `.overlay`); (2) the generic console kit styles (`.toolbar`, `.empty`, `.hint`) were loaded for every customer and restyled the Explorer toolbar, which broke the mobile-webkit "Icons view" preview test (fixed by scoping all kit rules under `.kit`; the preview test was not touched). The admin spec's old "(" assertion on the PC figure was replaced by `^\d+ of \d+$` for the new "N of M" display. No assertions were weakened or skipped, and none of this was a flake. Final code SHA d6d40fa: all workflows green (web 5bf169a also run once: only the preview test failed, same cause). The site workflow only runs on site changes, so it was green on 4c46a57 and not re-triggered afterwards.
- **Console usability fix (branch feature/console-usability, from main 638886d):** the console could not be scrolled because `.win .window-body` is `overflow:hidden` and clipped it; `.con-box` is now the scroller. The Administration window opens full size, the overview notices are shorter, Settings has a Security section explaining the operator second factor, and `apps/web/e2e/admin.spec.mjs` checks that the console scrolls. Web only; the gateway and relay are unchanged. First push 6ac4a09: contracts, security, desktop, gateway and desktop-ui green; web failed on mobile WebKit (no mouse wheel there, so the new scroll check now asserts the scroller overflow there and uses a real wheel elsewhere; plus one axe contrast report on the account drawer, taken while it was still sliding in or drawn inside the scroller) and integration failed once on Windows in cost-limits (relayBytesToday read before the relay metered the download; no gateway change in this branch). Drawers and dialogs are now drawn on the page, not inside the scrolling console, so they cannot scroll away. Results of the final SHA are in the report to the owner. Browser output here is not Windows or phone evidence.
- **Next, if the owner agrees:** desktop (WPF) design pass with the same principles; it is a separate phase and has not been started.
- Services page no longer says organisation admin tools are missing.
