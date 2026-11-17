# Evidence log

All run IDs are real GitHub Actions runs in `nisar87/OrbitDrive`; open `https://github.com/nisar87/OrbitDrive/actions/runs/<id>`.

## Commit `9592d39` — first fully green run (branch `feature/orbitdrive-foundation`)

| Workflow | Run ID | Runner | Result | What actually ran |
|---|---|---|---|---|
| contracts | 43951326845 | ubuntu-24.04 (image 20261111.127.1), .NET SDK 10.0.404, Node 22.23.4 | success | Schema drift check; xunit 17/17; vitest 25/25 on the same fixtures |
| gateway | 43951326851 | ubuntu-24.04 | success | Release build (warnings as errors); xunit 31/31 |
| desktop | 43951326822 | windows-2025 (Windows Server 2025, 10.0.26100, image 20261103.298.1) | success | Agent + Core + WPF build; xunit 42/42 Core policy tests on NTFS (symlinks, locked file, 10,000 entries) |
| desktop-ui | 43951326738 | windows-2025 | success | Real WPF window launched; 4 elements read through Windows UI Automation; 720x360 screen capture with 266 distinct sampled colours (artifact `wpf-spike`) |
| integration | 43951327071 | windows-2025 **and** ubuntu-24.04 | success | Real gateway + agent processes. Windows 36/36, 0 skipped. Linux 35 passed, 1 skipped (Windows-only hard-link policy) |
| web | 43951327095 | ubuntu-24.04, Playwright 1.65 | success | tsc, vitest 25/25, production-build guard, 10/10 browser tests: Chromium, Firefox, WebKit desktop + emulated Pixel 7 and iPhone 15, against real gateway + agent, real downloads hash-checked |
| security | 43951326854 | ubuntu-24.04 | success | Tracked-file secret scan, action pinning check, npm audit (0 high+), NuGet vulnerability audit |

### 1 GiB transfer measurements (run 43951327071; generated fixture, SHA-256 compared end to end; receiver stalled 4 s mid-stream)

| Host | Hash at prepare | Transfer | Throughput (loopback) | Gateway RSS growth | Agent RSS growth |
|---|---|---|---|---|---|
| windows-2025 | 0.88 s | 5.66 s | 181 MiB/s | 7.4 MiB | 2.0 MiB |
| ubuntu-24.04 | 0.70 s | 1.42 s | 721 MiB/s | 8.9 MiB | 2.1 MiB |

These are loopback numbers on CI VMs. They show memory does not grow with file size; they say nothing about internet speed.

### Defects found by CI and fixed

| Commit | Run | Defect | Fix | Regression proof |
|---|---|---|---|---|
| a5ce542 | integration 43950581534 (Windows), desktop 43950581272 | `BY_HANDLE_FILE_INFORMATION` declared FILETIMEs as 64-bit fields; padding shifted `NumberOfLinks`, so every file on Windows was refused as hard-linked | `Pack = 4`, DWORD pairs (9592d39) | Windows integration 36/36 incl. real hard-link refusal test; Core xunit 42/42 |
| a5ce542 | security 43950581538 | Privileged-step grep matched its own pattern | `--exclude=security.yml` | security 43951326854 green |

## Commit `ea8337d` — head of the session

All seven workflows green again: contracts 43952452561, gateway 43952452336, desktop 43952452583, desktop-ui 43952452536,
integration 43952452314, web 43952452370, security 43952452437. Adds resource-limit and reconnect regression tests
(integration: Windows 37/37, Linux 36 passed + 1 Windows-only skip). Runs for the docs-only commit `ae6c112` were
partly cancelled by the next push (concurrency group) and are not evidence.

## Commit `c23e4ae` — D1 owner console

All seven workflows green: contracts 43961529396, gateway 43961529361, desktop 43961529459, desktop-ui 43961529507,
integration 43961529432, web 43961529400, security 43961529697.

- **desktop-ui 43961529507** (windows-2025, Windows Server 2025, elevated): `scripts/desktop-ui-test.ps1` drove the real
  `OrbitDrive.exe` through Windows UI Automation against a real agent and gateway — 29/29 checks: not-set-up state, single
  console instance, exactly one agent, nothing shared by default, missing folder refused, folder shared read-only, non-HTTPS
  relay refused, Online, pairing code, approval prompt with account/browser/matching code, no access before approval, browser
  then sees exactly that folder and its real file, Pause cuts access (503) and Resume restores it, Quit leaves no orphan agent
  and the PC goes offline, settings/identity/grant persist across restart, close-to-tray keeps access, an externally stopped
  agent is not relaunched. 9 real screen captures in artifact `owner-console-ui` (**not visually reviewed**).
- **integration 43961529432**: `ipc.test.mjs` 8/8 on Windows and Linux (closed command set, malformed/oversized input,
  HTTPS-only relay, second agent exits with code 3, pair/approve/pause/quit over the pipe, restart persistence).
- Defects found on the way: UI test list handling and gateway start path (test script only); a Windows fixture-deletion
  race in the slice suite, now an explicit handle-release check.

## Commit `83f84b1` — pairing QR and device management (D2/W2 part)

All seven workflows green: contracts 43965532440, gateway 43965532600, desktop 43965532583, desktop-ui 43965532374,
integration 43965532417, web 43965532389, security 43965532437.

- QR encoder (`OrbitDrive.Core.QrCode`): 11/11 on Windows and Linux — every module equals the `qrcode` reference encoder
  for 9 texts across versions 1-10, all 8 masks decode with `jsQR`, oversized text refused. No camera scan has been done.
- Devices: 5/5 — lists are per account / per PC, one PC cannot revoke another PC's device, PC-side revoke cuts a live
  transfer, browser-side revoke, disconnect PC releases ownership.
- Console UI Automation 33/33 (adds: QR image shown, reattach without a second agent, paired browser listed, revoke on the
  PC ends that browser's access). Defect found and fixed: a console that attached to a running agent could relaunch it once
  after an external quit (desktop-ui 43964769582 failed → fixed in 83f84b1).

## Commit `23b7d3f` — Firebase ID-token verification (W1, gateway side)

All seven workflows green: contracts 43969406278, gateway 43969406301, desktop 43969406291, desktop-ui 43969406306,
integration 43969406281, web 43969406325, security 43969406602. `firebase-auth.test.mjs`: 22 cases with real RS256 tokens
signed by a test key (served as a JWK set). **Not** evidence about the live `orbitdrive-dev` project: no real Firebase
sign-in has been performed yet.

## Commit `899e0ff` — Firebase account flows in the web app (W1, web side)

All seven workflows green: contracts 43971788317, gateway 43971788430, desktop 43971788387, desktop-ui 43971788475,
integration 43971788213, web 43971788364, security 43971788480. The web job ran the new `firebase.spec.mjs` in Chromium,
Firefox, WebKit and the two emulated phones: create account → gateway refuses the unverified account → verification link
→ pair → hash-checked download → sign out (device credential cleared and revoked) → duplicate sign-up refused → sign in
→ password reset with identical answer for unknown emails. Identity provider was the **local Firebase Auth emulator**
(`demo-orbitdrive`); no email was really sent and the live project was not used.

Dependency note: the `firebase` umbrella package was rejected because it pulls `@grpc/grpc-js` with a high-severity
advisory; only `@firebase/app` and `@firebase/auth` ship. `firebase-tools` (emulator) lives in `tests/emulators`, is never
shipped, and its own advisories are reported as a warning by the security workflow rather than failing it.

## Commit `8390f81` — web Devices page (W2; code from `258ebc3`)

All seven workflows green on `8390f81`: contracts 43974825117, gateway 43974825202, desktop 43974825184, desktop-ui
43974825160, integration 43974825234, web 43974825166, security 43974825223. (On `258ebc3` itself six were green and the
security run 43973939668 was cancelled by the next push, so `258ebc3` is not counted as verified.) New browser test (5 projects): approved browsers listed,
removing another browser needs a second confirmation and then locks it out (403), disconnecting the PC empties the list.

## Commit `6eb6295` — relay automation live on the development relay

- `tests/relay/updater.test.mjs` 10/10 in the gateway workflow (real scripts, real gateway builds).
- `relay-probe` (GitHub network → relay): before the fix, ports 80/443 answered "no route to host"; after the owner ran
  `fix-firewall.sh`: valid Let's Encrypt certificate (CN 139-185-60-75.sslip.io, to 2027-02-15), TLS 1.3 verification OK,
  HTTP 200 JSON with `"version":"0.1.1"`.
- `relay-release` run 43995476025 on `main`: published `relay-dev-0.1.2`; verify-deployment saw the relay report 0.1.1
  for 15 polls, then 0.1.2 — a real pull-based, health-gated deployment with no inbound access.
- Host output supplied by the owner: all five units active, deploy log `deployed 0.1.1`, 670 MB memory available.

## Local-only observations (not CI evidence)

- Development sandbox: Ubuntu 24.04, .NET SDK 10.0.115, Node 22.23.4. nuget.org was unreachable, so xunit projects were
  compiled and executed locally only through a throwaway stand-in runner; the authoritative xunit results are the CI runs above.
- Playwright ran locally with installed Chrome (desktop + emulated Pixel 7): 4/4.
- Web screenshots were reviewed from the local run (pairing, confirm, roots, explorer, offline, error; desktop + mobile emulation).
  One defect found and fixed: RTL filenames flipped the row icon (`dir=auto` on the button → `<bdi>` on the name).

## Not evidenced

- The WPF screenshot artifact could not be downloaded from the build sandbox (artifact storage host blocked), so it was
  **not visually reviewed**; only the script's own assertions (UIA text, non-blank pixels) are known.
- No Windows 11 desktop, standard-user, physical Android/iOS, mobile-data, or internet-relay run exists yet.
- The relay Dockerfile and `firebase.json` have never been built/validated.
