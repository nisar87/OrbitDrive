# Requirement matrix (live status)

Statuses: NOT_STARTED, IN_PROGRESS, AUTOMATED_VERIFIED (automated proof exists for the implemented scope; hardware/device proof still required), COMPLETE, BLOCKED.
**No requirement is COMPLETE.** Last code-verified commit: `d21dffa` (runs in `phase-evidence.md`).

| ID | Status | Evidence / gap |
|---|---|---|
| R01 | IN_PROGRESS | One repo, generated C#/TS models, drift check + shared fixtures green (contracts 43951326845). Reproducible toolchain pinned via global.json/lockfile |
| R02 | IN_PROGRESS | Loop followed for the slice; evidence in phase-evidence.md |
| R03 | IN_PROGRESS | Web: slice UI only, W4 shell not built. WPF console: functional 7-page console, unstyled beyond basics, screenshots unreviewed. No rubric score claimed. **D1 pass:** start at sign-in (optional, HKCU), background start, show-existing-window, bounded crash recovery, lock policy — UIA 63/63 on windows-2025; real lock/sleep and standard-user evidence missing |
| R04 | AUTOMATED_VERIFIED | Sign-up, email verification, login, reset, logout in 5 browser projects against the Auth emulator; gateway token checks incl. wrong-project/expired/forged/unverified (web 43971788364, integration firebase-auth). Awaiting a real sign-in against the live project |
| R05 | IN_PROGRESS | One-use expiring code + PC approval implemented and tested (integration: expiry, reuse, lockout, race in StoreTests). QR image rendered on the PC and verified by reference encoder + decoder (integration qr suite); **Added:** in-site QR scanner (camera API → native detector or bundled jsQR; Chromium fake-camera e2e using the PC encoder's QR; foreign-site codes refused; camera-denied fallback); attempt limits per client address and per account with 429 + Retry-After, forged X-Forwarded-For ignored (`pairing-limits.test.mjs`); device rename (API, web, visible on PC, durable). Still missing: scan with a physical phone camera, Firefox/Safari scanner evidence |
| R06 | IN_PROGRESS | Device grant persists across agent restart; unapproved/revoked browser denied (integration). Grants, ownership and revocations survive a gateway restart (file store, `restart.test.mjs`); Firestore not built |
| R07 | IN_PROGRESS | ECDSA identity, DPAPI CurrentUser via P/Invoke on Windows; restart keeps pcId (Windows integration). Rotation not built |
| R08 | IN_PROGRESS | Roots added only by local owner action (console or CLI); remote side sees exactly those (desktop-ui, integration). Drive-root warning exists but is untested. **D4:** roots bound to disk identity; another disk at the same path is refused (unit + integration, real volume serials) |
| R09 | IN_PROGRESS | Traversal/ADS/reserved/symlink/junction/ancestor-swap/hard-link denied on Windows CI. Volume identity, placeholder, race fuzzing outstanding. **D4:** listing race closed (enumeration through a validated handle), encoded-separator, hard-link and ancestor-swap tests on Windows and Linux |
| R10 | IN_PROGRESS | No write path exists at all; every root reports readOnly. Upload enablement is D9/W9 |
| R11 | IN_PROGRESS | Tray, close-to-tray, Quit with no orphan agent, single instance verified by UI Automation (desktop-ui 43961529507). Start-at-sign-in, standard-user run outstanding |
| R12 | IN_PROGRESS | Agent is outbound-only by construction. Real agents on GitHub's Windows and Linux runners connect to the live Oracle relay over the internet and obtain pairing codes (relay-release 44000402541). No browse/transfer through the live relay yet; no mobile-data trial |
| R13 | IN_PROGRESS | Backoff+jitter reconnect; gateway-restart tests; fault-injection suite (`faults.test.mjs`: silent network loss, agent crash, relay crash mid-download with byte-exact ranged resume, graceful shutdown). Idempotency keys for retried mutations and bounded browser-side retry not built |
| R14 | IN_PROGRESS | Not-set-up/connecting/online/paused/offline states shown truthfully in console and web (desktop-ui, integration, web e2e). Lease/heartbeat expiry: a silent or frozen agent goes offline within the lease and requests fail fast with `pc_offline` (faults suite). Sleep/lock states on real Windows not evidenced |
| R15 | AUTOMATED_VERIFIED | Listing compared with the real folder on Windows + Linux, through the web UI in 3 engines (integration, web 43951327095). Awaiting real-device use. **W4:** tree, history, breadcrumbs, details/list/icon views, exact sort, filter, selection, context menu and properties in the web Explorer (Playwright ×5 projects) |
| R16 | IN_PROGRESS | Details view, click to open/download. No sort/selection/context menu/icon view yet. **D4:** hidden items only by owner opt-in per folder; system files never |
| R17 | IN_PROGRESS | Breadcrumbs, Unicode names, real size/mtime tested. Back/forward/up, properties panel outstanding. **D4/W4:** 10,000-entry folder: agent paging plus windowed web list, timings recorded for five browser projects |
| R18 | AUTOMATED_VERIFIED | 1 GiB with stalled receiver: <9 MiB RSS growth on Windows and Linux (integration 43951327071). Awaiting real network/phone. **D5/W5 (feature branch `ef4b025`, not on main):** save-to-file streaming with resume verified byte-exact in Chromium and Firefox; page memory flat while 24 MB flows **Update `98e29a4` (on main):** 1 GiB save-to-file with slow receiver, resume, same-size/same-time source rewrite refusal, cancel and disk-full state automated on CI; real picker, phones and Safari remain AWAITING_DEVICE_TEST. |
| R19 | IN_PROGRESS | Changed source → 409, If-Range mismatch → full body (integration). Interrupted-then-resumed end-to-end and timestamp-restoring rewrite outstanding. **D5/W5:** change-stamp validator catches a same-size rewrite with the date restored; stale validators never produce a 206; a changed source is never appended in the browser **Update `98e29a4` (on main):** 1 GiB save-to-file with slow receiver, resume, same-size/same-time source rewrite refusal, cancel and disk-full state automated on CI; real picker, phones and Safari remain AWAITING_DEVICE_TEST. |
| R20 | AUTOMATED_VERIFIED | SHA-256 source vs received (1 GiB, small, zero-byte); UI says 'handed to your browser', asserted not to say saved/complete. **D5/W5:** relay-observed states and PC-side bytes per transfer; browser downloads never described as saved **Update `98e29a4` (on main):** 1 GiB save-to-file with slow receiver, resume, same-size/same-time source rewrite refusal, cancel and disk-full state automated on CI; real picker, phones and Safari remain AWAITING_DEVICE_TEST. |
| R21 | IN_PROGRESS | Capability scoped to revision+device; cross-user, revoked, expired, guessed denied (integration). Per-capability abuse limits outstanding. **D5/W5:** cancel from the browser cuts the stream, kills the link and stops the PC reading; per-browser limit on prepared downloads **Update `98e29a4` (on main):** 1 GiB save-to-file with slow receiver, resume, same-size/same-time source rewrite refusal, cancel and disk-full state automated on CI; real picker, phones and Safari remain AWAITING_DEVICE_TEST. |
| R22 | NOT_STARTED | W6 |
| R23 | NOT_STARTED | D6. Needs a real Windows 11 machine + package identity; GitHub runners are Windows Server (blocker recorded) |
| R24 | NOT_STARTED | D6/W6 |
| R25 | NOT_STARTED | W6 |
| R26 | NOT_STARTED | D6/D8/W8 |
| R27 | IN_PROGRESS | Offline PC → truthful 503/offline state, no cloud copy (integration, web e2e). Invitations not built |
| R28 | NOT_STARTED | D7/W7 |
| R29 | NOT_STARTED | D7/W7 |
| R30 | NOT_STARTED | D8/W8 |
| R31 | NOT_STARTED | D8/W8 |
| R32 | IN_PROGRESS | Store test: same-named PCs with different owners cannot cross-authorise. Multi-PC UI untested with two agents |
| R33 | IN_PROGRESS | Emulated Pixel 7 / iPhone 15 layouts pass; no physical phone, no landscape/tablet work. **W4:** windowed desktop shell with taskbar, launcher, themes; phones get one maximised window (emulation only) |
| R34 | IN_PROGRESS | Semantic buttons/labels, focus-visible, reduced-motion, forced-colors CSS. No automated a11y audit or keyboard review yet |
| R35 | NOT_STARTED | W6 |
| R36 | NOT_STARTED | D9/W9 |
| R37 | NOT_STARTED | W10 |
| R38 | IN_PROGRESS | Logs asserted free of pairing/device secrets, tokens, capabilities (integration). No support bundle/cache yet |
| R39 | AUTOMATED_VERIFIED | /v1/meta and web taskbar state TLS-via-relay, not E2EE (integration test); README/SECURITY agree |
| R40 | IN_PROGRESS | Static build + production guard tested; firebase.json has no backend rewrite. Not validated with Firebase CLI |
| R41 | IN_PROGRESS | File-backed registration store (atomic snapshot, hashes only, corrupt store grants nothing); restart tests on Windows+Linux. Firestore, leases and multi-instance not built |
| R42 | IN_PROGRESS | Per-owner stream cap (429 + recovery), 64 KiB body cap (413) and control-message cap tested (integration); no relay-byte quotas yet |
| R43 | NOT_STARTED | D10 |
| R44 | NOT_STARTED | D10 (signing identity required) |
| R45 | IN_PROGRESS | Transfer metrics recorded per run; 10,000-entry listing test. Budgets beyond transfer memory not set. **D1 pass:** Fluent light/dark verified by reviewed screenshots; keyboard reachability on all pages; contrast measured. High contrast, DPI scaling and screen-reader pass not evidenced |
| R46 | IN_PROGRESS | /healthz only |
| R47 | IN_PROGRESS | Adversarial cases run in every integration/Core run; no dedicated corpus/fuzzing yet |
| R48 | IN_PROGRESS | Runner OS/tool versions recorded; emulation labelled; device evidence listed as missing |
| R49 | NOT_STARTED | D12/W12 |
| R50 | IN_PROGRESS | Version negotiation in handshake; unsupported agent version rejected with update message (integration). No old/new matrix |
| R51 | IN_PROGRESS | Revoke cuts an active stream and later access; sign-out revokes the browser (integration, web e2e). Account removal, PC removal outstanding |
| R52 | NOT_STARTED | No service worker exists yet (W6) |
| R53 | IN_PROGRESS | Account/request/PC binding, substituted PC, second account refused (integration, StoreTests). Origin display + anti-phishing copy on the PC is D2 |
| R54 | IN_PROGRESS | Empty/offline/error/paused states in web slice and owner console (asserted text; console screenshots unreviewed) |
| R55 | NOT_STARTED | D6/W6 |
| R56 | IN_PROGRESS | Locked file → truthful 'in use' error on Windows (Core test). Removable/placeholder/EFS not tested |
| R57 | IN_PROGRESS | Gateway refuses Production with dev identity; web build guard (tests). No Firebase projects exist |
| R58 | IN_PROGRESS | No deploy workflow exists; security job fails if a deploy/privileged step appears in ordinary workflows |
| R59 | AUTOMATED_VERIFIED | HEAD, full, explicit/open/suffix ranges, 416, If-Range, malformed/multi-range fallback, expiry (integration both OSes + RangeParser tests). Awaiting real browser download-manager behaviour |
| R60 | NOT_STARTED | Extensions deliberately out of scope |
