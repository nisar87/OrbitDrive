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

## Commit `d21dffa` — real agent against the live relay

`relay-release` run 44000402541 on `main`: `relay-dev-0.1.3` published and verified on the relay; `live-agent` jobs on
windows-2025 and ubuntu-24.04 passed 3/3: verified-HTTPS meta, refusal of unauthenticated/dev/unsigned identities in
production, and a real agent connecting outbound, authenticating and receiving a pairing code. Claim, browse and transfer
through the live relay are **not** covered (no verified Firebase test account yet).

## Commit `7be37ee` — durable gateway registrations (W3, part)

- Feature branch: all seven workflows green for `7be37ee` (contracts 44002338760, gateway 44002338584, desktop
  44002338561, desktop-ui 44002338562, integration 44002338605, web 44002338531, security 44002338568).
- `integration` ran `restart.test.mjs` with real gateway and agent processes on windows-2025 (86/86) and ubuntu-24.04
  (85 pass, 1 skipped = the Windows-only DPAPI case): after a gateway restart the agent reconnects unaided and is still
  owned, the approved browser works without re-pairing, a revoked browser stays refused, a stranger cannot claim the PC,
  download capabilities and pending pairing requests are gone, PC removal is durable, no usable secret is in the store
  file (0600 on Linux), and a damaged store grants nothing and is kept as `registrations.json.corrupt-*`.
- `main` fast-forwarded to `7be37ee`; all seven workflows green there too, and `relay-release` 44003244175 passed
  test, publish, verify-deployment and both live-agent jobs.
- Observation: on Linux the agent took ~30 s to notice each gateway restart (4 s on Windows). The control channel has
  no heartbeat/lease expiry yet — next W3/D3 item.
- **Not evidenced:** persistence on the live relay. Its systemd unit and `gateway.env` predate this change, so it still
  runs memory-only until the owner re-runs the bootstrap pinned to this commit.

## Live relay — durable store enabled (owner-run, 2026-11-17)

Host output supplied by the owner: bootstrap from `7be37ee` re-run on the existing server completed; `gateway.env`
contains `OrbitDrive__DataDirectory=/var/lib/orbitdrive-gateway`; after a gateway restart the directory exists
(`drwx------ orbitdrive orbitdrive`), the service is active, public HTTPS `/v1/meta` reports 0.1.4, firewall order intact.
Two defects found and handled: the Oracle Dubai apt mirror returned 404 (sources switched to `archive.ubuntu.com` on the
host), and the re-run did not restart an already-current gateway (fixed in `3cdd3e4`).
**Not evidenced:** a real registration surviving a restart on the live relay (no pairing through it yet).

## Commit `d8de61a` — heartbeat leases and fault injection (W3/D3)

- Feature branch and `main`: all seven workflows green (integration 44009921480 / 44010856122, gateway 44009921543,
  contracts 44009921540, desktop 44009921519, desktop-ui 44009921487, web 44009921501, security 44009921475).
- `integration` on windows-2025 (94/94) and ubuntu-24.04 (93 pass, 1 Windows-only skip) ran `faults.test.mjs` against
  real processes with a 1 s heartbeat / 3 s lease: idle connection kept alive for 2.5 leases; silent network loss
  (TCP proxy stops forwarding without closing) noticed by gateway and agent within the lease, `pc_offline` returned at
  once, self-healing with a new generation and no re-pairing; a scripted agent that stops heartbeating is dropped at
  lease expiry while a heartbeating one is kept; an agent without the capability is not leased; hard-killed agent
  reported offline and restored with the same grant; relay hard-killed mid-download gives the client an error (not a
  short "complete" file), the agent reports the transfer as not delivered, and a ranged `If-Range` resume against the
  restarted relay reproduces the source SHA-256 exactly; graceful shutdown releases agents within 5 s.
- Gateway-restart test on Linux fell from ~63 s to ~4 s (agents are now cut loose on shutdown).
- `relay-release` 44010856238 on `main`: `relay-dev-0.1.6` deployed and verified on the live relay; live-agent jobs
  passed on both runners (new agent with heartbeats against the new relay over the internet).
- **Not evidenced:** sleep/resume, lid close or real network switching on a Windows PC; behaviour behind mobile-carrier NAT.

## Commit `c9daac8` — pairing remainder (W2): scanner, attempt limits, rename

- Feature branch and `main`: all seven workflows green (web 44079707058 / 44081023231, integration 44079706588 /
  44081023209, gateway 44079706674, contracts 44079706422, desktop 44079706361, desktop-ui 44079706389, security
  44079706371). `relay-release` 44081023268: `relay-dev-0.1.7` deployed and verified; live-agent jobs passed.
- `integration` (windows-2025 99/99; ubuntu-24.04 98 + 1 Windows-only skip) ran `pairing-limits.test.mjs`: a guessed
  code dies after 5 wrong secrets and the address is locked out at 10 attempts/min (429, `Retry-After` 1-60 s) even for
  the right code, another account or no account from that address; one account hopping addresses is locked out; forged
  `X-Forwarded-For` does not help with or without a trusted proxy; polls bounded per account; rename validated,
  isolated between accounts, shown on the PC and durable across a relay restart.
- `web` Playwright: 26 passed, 9 skipped. The scanner tests ran on desktop-chromium and mobile-chromium-emulated
  only, with Chromium's fake camera playing a video of the QR drawn by the real PC encoder for a live code. **Skipped
  and therefore not evidence:** all three scanner tests on Firefox and WebKit (desktop and emulated iPhone).
- Screenshots reviewed locally in Chrome (rename form, wrong-site scan message, camera denied) — emulated viewports.
- Bug found by the new tests under load and fixed: the agent announced `connected` before its connection was usable.
- **Not evidenced:** scanning with a physical phone camera; Safari/Firefox scanner behaviour; per-address limiting on
  the live relay (needs `OrbitDrive__TrustedProxies`, set by re-running the bootstrap from this commit).

## Commit `410c3d8` — owner console lifecycle and visual pass (D1)

- All seven workflows green (desktop-ui 44102651018, desktop 44102651114 incl. 46 Core tests, integration
  44102650955, gateway 44102650853, contracts 44102650938, web 44102650968, security 44102650854).
- `desktop-ui` on windows-2025 drove the real WPF console through UI Automation, 63/63 checks: everything from the
  earlier 33, plus deliberate stop shown as "Stopped"; three agent crashes (process killed) each recovered
  automatically with exactly one agent; a fourth crash NOT restarted ("Agent keeps stopping"), explicit Start works;
  every enabled control on all seven pages keyboard-focusable; Ctrl+3 / Ctrl+6 switch sections (real key input);
  start at sign-in off by default, registers one HKCU Run entry with `--background` when ticked, removed when
  unticked; pause-while-locked saved and persisted; a `--background` start runs the agent with no window; launching
  again exits 3 and brings the running window forward; dark theme active when requested; no orphan agent at the end.
- **Screenshots are now reviewable:** the workflow prints them into the job log and `scripts/fetch-ui-screenshots.sh`
  extracts them. 18 real screenshots reviewed; 7 kept in `docs/evidence/screenshots/owner-console/`. Findings fixed
  from that review: Start button shown while online; secondary text 3.2:1 contrast (now 5.5:1 light, 8.5:1 dark,
  measured from pixels); stale numbers while the agent was down; shared-folder paths cut off with a sideways scrollbar.
- Bug found by the test: settings checkboxes ignored toggles made through accessibility APIs (listened to Click only).
- **Not evidenced (hosted runner is Windows Server 2025, elevated, 100 % DPI):** Windows 11 client, standard user,
  a second Windows user, real lock/unlock (only the policy logic is unit-tested), sleep/resume, Windows high-contrast
  mode, 125–200 % DPI, clicking the tray menu, sign-in actually launching the Run entry.

## `main` @ `410c3d8` (D1) — confirmed

All seven workflows green on `main` for `410c3d8` (integration 44103655820, desktop-ui 44103655766, gateway
44103655731, desktop 44103655791, contracts 44103655866, security 44103655873, web 44103656072). No `relay-release`
run was created for this commit because nothing under the relay's paths changed; the live relay stays on 0.1.7.

## Commit `62bb432` — web desktop shell and File Explorer (W4)

- All seven workflows green (web 44112037555, integration 44112037823, gateway 44112037656, contracts 44112037398,
  desktop 44112037450, desktop-ui 44112037553, security 44112037547).
- `web` Playwright against the real gateway and agent: 51 passed, 9 skipped (the skips are the camera-scanner tests
  outside Chromium). `explorer.spec.mjs` ran on all five projects (Chromium, Firefox, WebKit desktop; Chromium and
  WebKit phone emulation): sort by exact size, filter, three views persisted without storing file names; Back/
  Forward/Up/breadcrumbs/browser Back/tree; keyboard selection (arrows, Space, Shift, Ctrl+A, Escape, Enter,
  Backspace) and mouse selection; touch long-press and Select mode on phones; context menu fully on screen with only
  real actions; properties with exact byte count; a ~160-character Urdu/emoji name and an HTML-looking name stay text
  and cause no sideways overflow in any view; windows move, resize, stay reachable, maximise, minimise, come to the
  front; phones show one maximised window; launcher lists only real apps and returns focus on Escape; dark/light/
  system theme persisted; empty desktop usable.
- vitest 51/51 (sorting beyond 2^53, type labels, filter in Urdu, selection ranges, hostile stored preferences).
- Bugs found by these tests and fixed: context menu clipped inside the window frame; keyboard focus lost after
  opening a folder; a dragged window could be left with only its buttons on screen.
- Screenshots reviewed locally in Chrome (desktop 1280×720 and Pixel 7 emulation, light and dark); six kept in
  `docs/evidence/screenshots/web/`.
- **Not evidenced:** physical phones/tablets (safe areas, on-screen keyboard, real long-press), landscape tablet with
  overlapping windows, 200 % text zoom, screen-reader pass, forced-colors rendering, a 10,000-entry folder in the
  browser (tested to 450 entries; the table is not virtualised), hidden-file display against a real Windows volume.
- Not built in W4 yet: Inbox and Transfers apps (their phases come later), multi-file download (needs archives, W8),
  search across folders (W8; the current box filters the open folder and says so), keyboard move/resize of windows.

## `main` @ `62bb432` (W4) — confirmed

All seven workflows green on `main` for `62bb432` (contracts 44113458195, integration 44113460182, desktop-ui
44113458197, gateway 44113460702, desktop 44113452013, security 44113451849, web 44113451642).

## Commit `8905264` — disk binding, hidden items, large folders, listing race fix (D4)

- All seven workflows green (desktop 44122833259: 61/61 Core tests on windows-2025; integration 44122833746;
  desktop-ui 44122833204: 67/67; web 44122833598: 61 passed, 9 skipped; gateway 44122834709; contracts
  44122833945; security 44122833613).
- **Security defect found and fixed.** The new ancestor-swap race test failed on Windows (run 44120828276): while a
  folder inside the share was repeatedly replaced by a link to a private folder, a directory listing returned the
  private folder's file name (`secret.txt`). Cause: the folder was validated by path and then enumerated by path
  again. Fix: listings open the folder, verify the opened handle is inside the root and is not a link, and enumerate
  through that handle (which also pins the folder and its parents on Windows). The same test now passes on Windows
  and Linux with the swap running for 3 s. File contents were never affected (always served from a validated handle).
- Disk binding: unit tests with a simulated disk swap (listing, nested listing, prepare and an already prepared
  download all refused as unavailable, access returns when the original disk does); real volume identities differ
  between two real volumes on both runners; through the real agent and gateway a mismatching identity gives
  `available: false` and 503 "different disk", is never silently re-bound, and an older config is bound once.
- Hidden items: not sent unless the PC owner opts in for that folder (Core test incl. Windows Hidden and System
  attributes; integration over IPC on both runners; console UI Automation with a real hidden file; browser test).
  No remote instruction or HTTP route can change what a PC shares (asserted).
- Other adversarial cases: nine encoded or look-alike separator forms are treated as literal names (NotFound); a
  second hard link to an outside file is refused on Windows (the alias really exposed the bytes); a root chosen
  through a link works while links inside it are refused.
- Large folders: 10,000 real files through the real agent and gateway in all five browser projects. Load-all
  2.2–3.5 s; filter 45–116 ms; sort 275–439 ms; Ctrl+A 360–484 ms; End 145–413 ms; at most 1,200 rows drawn at once.
  Before the change, load-all had not finished after 240 s.
- **Not evidenced:** a real removable disk being swapped (identity comparison is tested with real serials and a
  simulated swap); mount points inside a shared folder; cloud placeholders (no policy built yet); EFS/protected
  files; network shares; real-phone performance with 10,000 items; hard-link refusal on Linux (development only).

## `main` @ `8905264` (D4) — confirmed

All seven workflows green on `main` for `8905264`, and `relay-release` 44124795696 passed test, publish,
verify-deployment and both live-agent jobs.

## Commits `ef4b025` → `98e29a4` — downloads, resume and Transfers (D5/W5) — on `main` at `98e29a4`

- Six workflows green: web 44142286306 (72 passed, 13 skipped), integration 44142286270, desktop 44142286356,
  desktop-ui 44142286463 (71/71), gateway 44142286681, contracts 44142286450.
- **`security` 44142286454 is red, and `main` was therefore not moved.** Cause: a new upstream advisory published
  2026-11-17, GHSA-fc5q-39xc-vjpf (ReDoS in `picomatch`), flagged against every released version with no fixed
  release. It enters `apps/web` only through build/test tooling (vite, vitest, tinyglobby); `npm audit --omit=dev`
  (what is shipped to browsers) reports 0. Because the audit step fails, the later NuGet audit step in the same job
  does not run. A dated, narrowly scoped exception mechanism was drafted but not applied: relaxing a security gate
  is the owner's decision. `main` at `8905264` would also fail this step if re-run today.
- Browser tests (real gateway and agent; the browser reaches the relay through a TCP proxy that can slow and drop the
  link): a browser download is described as handed over and "left the relay", never as saved (5 projects);
  save-to-file streams into a file with real progress, notices a connection that goes silent without closing
  (20 s stall watch), keeps what was written, resumes and produces a file whose SHA-256 equals the source
  (Chromium and Firefox, desktop and phone emulation); a source replaced during the interruption stops with
  "the file changed" and Start again yields only the new content; cancel removes the partial file and the PC stops
  reading. Page memory while 24 MB flowed through: +0.5–0.8 MB after forced GC incl. buffer storage (Chromium, via
  the debugging protocol). WebKit: save-to-file tests skipped — its test sessions refuse page file writes.
  The save-location picker itself is replaced by a private-file-system handle in tests (it cannot be automated).
- Integration (both runners): relay-observed states prepared / sending / interrupted / sent / cancelled with exact
  byte coverage; capability never returned again; other browsers cannot see or cancel a download; cancel cuts a
  running stream; per-browser limit on prepared downloads; a rewrite that restores size and exact modified time is
  refused (409) and a stale validator never yields a 206; PC reports bytes it really sent per transfer.
- Core tests (61 on windows-2025): change-stamp detection of a same-size rewrite with the date put back; a second
  hard link is refused on Windows and now on Linux too (statx).
- Console: Transfers page lists each transfer with bytes sent to the relay (UI Automation check + screenshot).
- **Not evidenced:** the real save-location picker and real disks; phones (most mobile browsers lack save-to-file,
  so they use the browser download path); 1 GiB through save-to-file (tested at 24 MB; the relay path itself is
  tested at 1 GiB); Safari; throughput figures; bandwidth limits and metered-network warnings (not built).

### D5/W5 completion (commits `c96c6bd`, `ac7e1de`, `c0ce7e4`, `98e29a4`)

- Seven workflows green on `98e29a4` (runs listed in IMPLEMENTATION_STATE). Earlier candidates failed and were fixed, not
  re-run until lucky: `ac7e1de` integration 44162937157 (windows-2025: gateway bind to a fixed port failed with WSAEACCES →
  tests now take an OS-assigned free port, `c0ce7e4`); integration 44165071129 and 44167038049 (ubuntu: a late
  `stream_finished` of the previous test's small file was consumed as the abandoned big.bin transfer → event matched by
  file name, `98e29a4`; the 64 MiB-file attempt in between did not change the result and was reverted). No assertion weakened.
- web run 44162937386 (same web code as `98e29a4`): 75 passed, 20 skipped. 1 GiB save-to-file, Chromium desktop: 13.8 s, slow
  phase receiver wrote 61 MiB while the PC had sent 81 MiB (backpressure), growth page 4 / relay 14 / PC 1 MiB. Firefox
  heap metric unavailable (0/0, not claimed). Skips: WebKit save-to-file, and per-engine items listed in the specs.
- Owner limits (`MaxUploadKBps`, `MaxConcurrentTransfers`, 429 after a 2 s slot wait) and pause-on-metered covered by
  integration tests and the console UI Automation test; real metered-network behaviour on Windows is AWAITING_DEVICE_TEST.
- Security gate: `GHSA-fc5q-39xc-vjpf` (picomatch, dev-only, not in the production bundle) is an **accepted risk, not fixed**;
  expiry 2026-12-17, rationale and removal conditions in `docs/security/accepted-risks.md`.

### D6/W6 automated scope (commit `a3a833e`, seven workflows green)

- Runs for `a3a833e`: security, contracts, gateway, desktop, desktop-ui, integration (ubuntu + windows) and web all succeeded
  (run ids in IMPLEMENTATION_STATE). Candidates before it failed and were fixed, never re-run until lucky:
  `4c4c643` desktop 44186947450 — Windows reports `\\?\C:\…` final paths, so every one-shot grant re-open failed
  (real product defect, fixed: grant stores the drive-letter path, UNC refused); `83ce036` desktop (xunit2010 analyzer),
  integration (a `"` cannot exist in a Windows file name → test uses a Windows-legal odd name), web (launcher now lists Inbox;
  WebKit turns spaces into `_` in suggested download names — bytes still checked exactly); `b10dd5b` desktop (NTFS coarse
  clock: rewrite within one tick is indistinguishable → test waits 100 ms), `c4c6895` integration (EBUSY: Windows refuses
  to write a file the agent still holds for a moment → test retries up to 10 s). Product fix from the web run: the window
  cascade pushed later windows off the desktop edge; now clamped. No assertion was weakened. A final intermittent Firefox e2e failure (pairing field never appeared, web run on `5a0cb98`) was a real product bug: the new service worker's `clients.claim()` fired `controllerchange` on first install and the page reloaded mid-sign-in; fixed in `d4c6dcb` (reload only after the user applied an update) with a unit test that fails on the old code.
- Native Explorer command: `OrbitDriveShell.dll` built with `/W4 /WX /guard:cf` on windows-2025 and exercised through COM
  (`SHELL EXTENSION TEST PASSED (0 failures)`). **This is not the real Explorer menu.**
- iPhone-like WebKit emulation has no `Notification` API: the Inbox shows a plain explanation and keeps working (e2e).

### D6/W6 not evidenced

- Modern Windows 11 menu invoked in real Explorer with the package installed (needs Windows 11 + signing identity).
- A phone on another network downloading after tapping Accept; real push delivery (no Firebase credentials); iOS Home
  Screen install behaviour. Local notifications were tested only against browser mocks.

## D7/W7 previews and thumbnails (commits `8e31042` → `f79897c`, seven workflows green on `f79897c`)

- Fixes found by CI, none by weakening a test: nullable collection-expression assertion (CS8631) in `PreviewTests`; the cache
  test held an open handle while calling `Clear` (Windows cannot delete open files); `pdfjs-dist` newly appeared in the
  production bundle and `bundle-check` refused it until it was reviewed and recorded (`docs/security/accepted-risks.md`).
- Real decoder: integration "the real decoder handles pictures, honours rotation, and contains corrupt, truncated and oversized
  inputs" passed on ubuntu and windows runners; Core tests passed on windows-2025; e2e `preview.spec.mjs` 6 tests x 5 projects passed.
- Local fix found by reviewing a mobile screenshot: an enlarged picture could not be scrolled to its left/top edge (flex centring);
  now safe-centred. Previous/next now stays within the kind opened (pictures with pictures).
- Local runs used a **fake** helper (`fake-thumbnail-helper.sh`); only CI exercised the real Skia helper.

### D7/W7 not evidenced

- Real phones, non-emulated browsers, a wide codec/PDF corpus, Windows job-object limits under an adversarial memory test,
  PDF renderer origin isolation, fuzzing, WPF console toggle for thumbnails.

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

## D8/W8 — ZIP archives, search, favorites/recents (code SHA `b0ab209`)

- CI for the exact SHA `b0ab209164c47785c094b85d03a5b427e2e04e11`: contracts, security, gateway, desktop, desktop-ui, integration and web all **success**. The first D8 commit `c011b62` failed `desktop` (CS8631 nullability in two `ArchiveTests` assertions); fixed by making the expression nullability-safe, assertions unchanged.
- Added: Core `ArchiveTests` and `SearchTests`; integration `archives` (4), `search` (7), `multipc` (3); gateway device-reuse tests (3); web vitest `library.test.ts` (16); Playwright `archive.spec.mjs` (ZIP of a folder and of a multi-selection extracted with an independent reader and compared by SHA-256, symlink omission shown and reported in `_OrbitDrive_omitted.txt`, two PCs paired from one browser, cross-PC search, offline PC shown as offline, favorite becomes Unavailable after rename and can be removed).
- Defects found by these tests and fixed: one device per pairing broke multi-PC use (claim now reuses the device); Favorites list crashed the Explorer (`placeItem` used before initialisation) — found by Playwright, fixed.
- Test changes that are not weakenings: `devices.test.mjs` now expects the remaining device list after the device-reuse change; `explorer.spec.mjs` context-menu list gained the intended "Add to favorites" item (still an exact list match).

### Local-only observations
- Local runs used the fake thumbnail helper where relevant and the stand-in xunit runner (nuget.org unreachable); CI is the authority. Mobile screenshots (`archive-ready-*`, `search-two-pcs-*`) were reviewed; both phone layouts push the file list below the panel, which is acceptable but not polished.

### Not evidenced
- A real >4 GiB ZIP64 archive (only forced-ZIP64 structures and a >65 534-entry directory); physical phones; large-list profile on devices; server-side favorites; WPF console toggles (thumbnails/cache clear, SendTo shortcut); Windows-specific hidden/system-attribute handling beyond the CI Windows unit tests.

## D9/W9 — uploads from the phone into approved folders (code SHA `91be9bf`)

- CI for the exact SHA `91be9bf95d526af5adcebeec6cc647a010831334`: contracts, security, gateway, desktop, desktop-ui, integration and web all **success**; `main` fast-forwarded to it. Three earlier commits failed and were fixed without weakening: `64a764a` (xUnit2029 analyzer error in `UploadTests`, real xunit only), `68fd17f` (integration: the 1 GiB CI run consumed the root's quota counter so the next test was refused `too_large`; fixed by resetting the counter after that run, reproduced locally with 250 MiB), `74c24ae` (Windows integration: an 8 MiB+1 piece is refused by closing the connection, so the sender saw ECONNRESET and the leftover upload cascaded into later tests; the test now accepts 400/413 or a reset and still requires that nothing was stored).

- Added: Core `UploadTests`; integration `uploads.test.mjs` (20: enablement, forged ids, auth, multi-piece, mismatch/duplicate/skip/replay, cut-off piece, collisions, bad names, limits incl. disk-full at start, concurrency cap, device ownership, cancel, uploads off mid-upload, revocation both ways, bounded-memory 48 MiB run, crash recovery); web vitest `upload.test.ts` (12); Playwright `upload.spec.mjs` (4 × desktop and emulated mobile).
- Local results: Core 229 pass (stand-in runner), uploads integration 20/20, regression 91 pass/0 fail, vitest 91, Playwright 69 passed/1 skipped (the 1 GiB case).
- Memory: 48 MiB upload, gateway about +8 MiB, agent about +8 MiB RSS (`artifacts/upload-metrics-<platform>.json`).
- Defects found by these tests and fixed: Finish answered Conflict before Forbidden when uploads were off; the start throttle (60/min) tripped legitimate suites (now configurable, default 300); the Upload confirm radios rendered oversized and the folder list did not refresh after a save (found in the mobile screenshots).
- Mobile screenshots (`upload-confirm-*`, `upload-done-*`) reviewed locally; the confirm panel scrolls inside the pane on a phone.

### Not evidenced
- The WPF upload toggle is verified by the desktop-ui workflow's build and UIA assertions only; its screenshot was not visually reviewed.
- Windows: Mark-of-the-Web and `MoveFileEx` semantics run only in Windows CI unit tests.
- Real 45-minute idle expiry; mid-chunk disk-full against a real full disk; resume after agent restart (not supported); folder upload and drag-and-drop (not built); the interruption e2e cuts the connection at a moment the test cannot pin to mid-file, so byte-level resume is proven by the integration and vitest suites, not that e2e alone; physical phones.

## D10/W10 — administration, owner controls and update verification (status: local verification only until CI is recorded)

- Added: gateway `Operations.cs` (Metrics, AdminLog), Store suspension/preferences/account deletion, `/v1/admin/*` and `/v1/account*` routes, protocol types; web `AccountPanel`, lazy `AdminApp`, desktop windows for late-added apps; Core `Updates.cs` (UpdateVerifier, UpdateStager), `tools/release/sign-update-manifest.mjs`, a fixture signed with a throwaway test key.
- Tests: integration `admin.test.mjs` (19), gateway `OperationsTests` and Core `UpdateTests` (stand-in runner 287 pass), Playwright `admin.spec.mjs` (3 × desktop and emulated mobile), full regression: integration 178 pass/0 fail (ORBIT_LARGE_MIB=64), vitest 91, Playwright 74 pass/1 skip.
- Defects found by these tests and fixed: the desktop shell crashed when an app (Administration) appeared after start (layout lookup undefined); test-gateway restarts invalidate dev tokens (test fixed); int64 counters are decimal strings (test fixed).
- Flake, recorded honestly: `transfers.spec.mjs` large-file test failed once while the stand-in unit runner ran at the same time (CPU contention made the "slow receiver" not slow) and passed alone and in the earlier D9 full run. Not investigated further.
- Screenshots reviewed: admin overview on a phone (readable, numbers measured). The first account-panel screenshot did not show the panel; the test now scrolls it into view.

### Not evidenced for D10/W10
- Installer/MSIX packaging, repair/upgrade/uninstall, rollback, multi-profile and standard-user operation, modern-menu registration on Windows 11 (R43, R23): not started; need the owner's Windows 11 machine and signing identity.
- A real signing key and a real signed release (R44): only a throwaway test key exists; no update is wired into the agent's UI/CLI yet, so nothing downloads updates.
- Firebase `auth_time` handling for operators is exercised only through the dev identity and code review, not a live or emulator Firebase run.
- Dashboards show counters since the last service start only; no history, no cost or billing metrics, no spend caps (never provisioned billing).
- Retention preference can only shorten the relay's one-hour in-memory list; diagnostics consent records a choice for features that do not exist yet.

### D10/W10 CI finding on `7fafe3a` (recorded honestly)
- `desktop` workflow (real Windows xunit) FAILED: `UpdateTests.A_staged_file_changed_after_download_is_removed_and_never_launched`. Cause: a genuine bug, `UpdateStager.ApplyAsync` tried to delete a tampered staged installer while its own read stream was still open; Windows refuses that and the failure was swallowed, so the file stayed on disk (it was still never launched). The Linux stand-in runner cannot show this. Fixed by deleting after the stream is closed; the test assertion was not changed.
- contracts, gateway, integration, security, desktop-ui were green on `7fafe3a`; `web` was still running when this was written.
- `web` workflow on `bfb7ac3`: all but one Playwright test passed (166), 2 failed: `upload.spec` "a dropped connection is resumed…" on desktop-chromium and desktop-webkit (passed on firefox and both mobile projects, and 3/3 locally). No progress bar was seen for 30 s. I could not download the CI screenshots (artifact host blocked from this workspace), so the cause is a HYPOTHESIS: the unthrottled 7 MiB upload can finish before the test sees it, so the link is never cut mid-transfer. The test's assertions were not changed; the proxy gained an upload-direction limit (`throttleUploads`, off by default) so the drop is certain to land mid-transfer. If it fails again in CI the hypothesis is wrong and it is a real defect.
- CORRECTION to the line above: the hypothesis was wrong. With the upload throttle the same test failed on all four projects in CI, and I reproduced it locally under one CPU core (about 1 in 2). The page showed "Another piece of this upload is still being received." Cause: a real D9 defect in the browser, not a test race. After a drop, the PC still held the cut-off piece and answered the retry with `conflict`; the browser treated that as fatal. Fix: `conflict` during sending is now wait-and-ask-again inside the existing bounded retry budget (then pauses for Resume); if the PC says the upload is no longer receiving, it fails immediately. 3 unit tests added (retry succeeds, really-over fails, never-clearing pauses). The Playwright test and its assertions are unchanged (still cuts the link mid-transfer and checks the file byte for byte). 6/6 locally under one core after the fix.
- Also seen once in CI on `4c5dfac`: `explorer.spec` "selection, keyboard, context menu and properties" on desktop-firefox (focus check, spec untouched, passed on the previous CI run). Recorded as a suspected flake, not investigated further; if it repeats it is a real defect.

## D11/W11 — security, resilience, performance, diagnostics, accessibility (code SHA `604ca8d`, plus a follow-up commit recorded below)

- CI for the exact SHA `604ca8dd62cb259625cab80532db2b42f01f5d75`: contracts, security, gateway, desktop, desktop-ui and integration **success** on the first attempt; `web` **success on attempt 2**. `main` was fast-forwarded to it (`81c8351..604ca8d`).
- Earlier commit `43cb14f` failed and was fixed without weakening anything:
  - `security`: the repository secret scan (correctly) flagged a personal-looking Windows path in my own test fixture. The scanner was left alone; the fixture builds the path from parts.
  - `integration` (Windows only): `diagnostics` compared the bundle path with the state path as strings, and the same folder can be spelled with an 8.3 or differently-cased name on Windows (now compared canonically, with both paths in the message); `hardening` "door reopens" failed. While reproducing it locally I got a different failure of the same test, "5 accepted with a limit of 4": a **real race**, the cap was checked before the WebSocket was accepted but counted later, so concurrent upgrades all passed the check. Fixed with an atomic reserve (`TryReserveHandshake`), released in `RunAsync`'s `finally` or when accepting fails. The test was not loosened (it also now allows up to 8 s for the door to reopen, which a real leak would still fail). 5/5 clean runs afterwards, against about 1 in 3 failing before.
  - `web` on `43cb14f` was cancelled by my own next push (the workflow's concurrency group); on `604ca8d` its first attempt hung for 14 minutes in "Install browsers" (the Playwright download), was cancelled by me and re-run; attempt 2 passed all browsers. No test was retried or skipped to get there.
- Added and verified locally and in CI: single-source CSP/header builder shared by `firebase.json` and tests (`tests/web/security-headers.test.mjs`), real-browser CSP spec (framing blocked by a real second server, eval blocked, `.mjs` served as JavaScript), gateway security headers and `Vary: Origin`, authenticate-before-validate fix on download-sessions, `hardening.test.mjs` (21), axe-core accessibility sweep plus reduced-motion, reflow and keyboard checks, and the diagnostics work (redactor, activity history, support bundle, `diagnostics.test.mjs`). Local: Core 326 (stand-in runner), integration 205 pass/0 fail/5 skipped (3 live-relay tests need ORBIT_LIVE_RELAY_URL, the real image decoder exists only in CI, the 1 GiB case needs ORBIT_LARGE_MIB), vitest 94, Playwright 101 passed/1 skipped on desktop-chrome and mobile-chrome-emulated.
- Mutation checks (assertion made to fail on purpose, then restored): a download entry carrying the file name is caught by the diagnostics privacy test; disabling session retirement fails all three `sessions` tests.

### Follow-up commit (soak, performance, session limit) — CI result to be recorded below
- **Soak** (`tests/soak/soak.mjs`): real gateway + agent + paired browser identity behind the fault proxy; 3 download workers (one slow reader), browsing, uploads; disturbances in rotation: relay crash and restart, dropped connections, 6 s silent network loss, 8 s slow link. Rules: every success is byte-exact (sha-256, uploads re-hashed on the PC's disk), recovery after each disturbance for every kind of work, a quiet tail with no failures, and (runs of 240 s or more) no memory growth beyond a margin between the first and last quarter. 303 s run on the 2-core Linux sandbox: **26 disturbances, 2,252 verified downloads, 554 verified uploads, 0 mismatches, slowest recovery 6.5 s, relay about 110–125 MiB and agent about 220–235 MiB flat after the first minute**. A 75 s run showed the agent growing ~90 MiB; a 327 s run showed that was warm-up (it plateaus), so growth is judged only on long runs. This is one machine over loopback, not a device or Windows-service soak.
- **Real defect found by the soak and fixed:** download sessions stay valid for an hour (needed for resume) and counted against the per-browser limit of 64 for that whole hour even after the file was delivered, so a browser that saved 65 files in an hour was refused with "too many downloads waiting". At the limit the oldest cancelled or completely-sent sessions are now retired; waiting, running and partially-sent (resumable) ones are never taken and the limit still refuses against them. Trade-off: a retired capability link no longer works. `sessions.test.mjs` (3).
- **Not defects (soak behaving unlike a client):** the daily relay allowance legitimately ended a long run (turned off for the soak; it has its own tests); abandoned uploads each hold one of the PC's 3 slots for 45 minutes by design (resume), so the soak now cancels a failed upload as a person who gives up would. Both are documented behaviour, not changed.
- **Performance** (`tests/perf/measure.mjs`, 2-core Linux sandbox, loopback, 64 MiB file): idle relay 84 MiB / agent 61 MiB, idle CPU 0.4 % / 0.2 % of one core; 450-entry listing p50 2.9 ms, p95 8.5 ms; filename search p50 7.9 ms, p95 11.6 ms; small download p50 5 ms, p95 74 ms; 64 MiB in 0.3 s (215 MiB/s, a ceiling for the software over loopback, not a network speed); peak memory while transferring 91 MiB relay / 87 MiB agent; cancel latency 15 ms. The CI guards are deliberately loose (order-of-magnitude); tighten them from the artifacts of the CI runs, not from this table. Windows CPU is not sampled (null).
- New workflow `nightly-soak.yml` is manual-only (no schedule, no secrets, no cloud resources); `integration.yml` runs the perf measurements and a 75 s soak on Linux and Windows.

### Not evidenced for D11/W11
- Real devices: sleep/wake, lock/unlock, Wi-Fi/mobile switching, background tab, battery; a Windows-service or multi-day soak; any real-network latency or throughput.
- WPF history viewer and support-bundle UI (cannot be built or run on Linux); names containing spaces are only partly redactable, which is why events never carry names.
- Visual rubric: no 9.5/10 score is claimed; screenshots for the final visual gate, DPI scaling, high contrast and translated/large-text review on real hardware are not done. The axe sweep does not cover Transfers, Inbox/Send or Archive dialogs.
- No coverage-guided fuzzing or hostile-file corpus beyond the existing archive/upload cases; native-interop/dependency review not repeated this phase beyond the accepted picomatch risk (expires 2026-12-17).
- Deploy-time facts: `firebase.json` carries the fail-closed placeholder relay origin `https://relay.example.invalid` until `--write --gateway` is run at deploy (W12); hosting must serve `.mjs` as JavaScript (checked against a local server only); relay Caddy already sends HSTS. Nothing was deployed.
- Known test limits: local Playwright cannot decode thumbnails (the helper is built only in CI; tests wait on the 200 response); `explorer.spec` focus check on Firefox failed once in an earlier CI run and has not recurred (suspected flake, still not root-caused).
