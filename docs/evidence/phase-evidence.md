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
