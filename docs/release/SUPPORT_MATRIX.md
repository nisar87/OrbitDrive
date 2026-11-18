# Support matrix (what is claimed, and the evidence behind it)

Words used: **Automated** = passes in GitHub Actions on the named runner/browser (CI is not a phone and not a Windows 11 client). **Device** = a person ran it on real hardware. **Not tested** = no evidence; do not rely on it.

## Windows PC agent
| Platform | Claim | Evidence |
|---|---|---|
| Windows Server 2025 (GitHub `windows-2025` runner) | builds, unit/integration tests, WPF UI Automation | Automated (see `docs/evidence/phase-evidence.md`) |
| Windows 11, standard (non-admin) user | intended primary target | **Not tested** on a real machine |
| Windows 10 | not a target for this candidate | Not tested |
| ARM64 Windows | not built | Not supported |
| Explorer "Send with OrbitDrive" (modern Windows 11 menu) | native command built and COM-tested | Automated only; real Explorer needs the signed package identity: **awaiting signing** |
| Classic "Send to" shortcuts | built | Automated; real Explorer: Not tested |

## Browsers (web app)
| Browser | Claim | Evidence |
|---|---|---|
| Chromium, Firefox (desktop), WebKit, emulated Android Chrome, emulated iPhone Safari | UI, pairing, browse, download, preview, upload (test engines) | Automated (Playwright) |
| WebKit save-to-file | skipped: its automated sessions refuse page file writes | Not tested |
| Real Android phone (Chrome) | intended | **Device evidence missing** |
| Real iPhone (Safari) | intended | **Device evidence missing** |
Not advertised until the device evidence exists: iOS and Android support, 1 GiB phone downloads, camera QR scanning on phones, push notifications.

## When can the phone reach the PC?
The PC keeps one outbound connection to the relay. The relay cannot wake a PC.
| PC state | Phone sees | Basis |
|---|---|---|
| Running, window minimized or closed to tray | online | design + automated tests |
| Screen locked | online, unless you switched on "pause while locked" in the console | design + automated tests; **real lock/unlock not tested on Windows** |
| Asleep or hibernating | **offline** (the connection ends within the 30 s lease); returns shortly after wake | design; **real sleep/wake not tested** |
| Signed out of Windows, or shut down | **offline** (the agent runs as your user, not as a service) | design |
| "Pause remote access" in the tray | offline by choice; running transfers stop | automated tests |
| Network switch (Wi-Fi to mobile data) on the phone | an in-flight download may stop; resume continues from the saved offset | automated fault injection; **real network switching not tested** |

## Service
| Part | Claim | Evidence |
|---|---|---|
| Relay on Oracle Always Free (development) | live, TLS, version reported, monitored | Automated probes (see state file); capacity 1 GB RAM: loopback load only |
| Firebase Hosting (development project) | live, CSP/headers verified in a real browser, keyless auto-deploy | Real (development project only) |
| Firestore, FCM push, staging, production | **not created**; need owner decisions (region, billing) | none |

## Known limits you should not be surprised by
Read-only by default; uploads are add-only and per folder; no sharing links; no end-to-end encryption claim (the relay can see traffic); no remote execution; PC must be on; a very large transfer depends on the slowest hop (loopback soak is not an internet speed claim).
