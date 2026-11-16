# Requirement matrix (live status)

Statuses: NOT_STARTED, IN_PROGRESS, AUTOMATED_VERIFIED (automated proof exists for the implemented scope; hardware/device proof still required), COMPLETE, BLOCKED.
**No requirement is COMPLETE.** Last code-verified commit: `9592d39` (runs in `phase-evidence.md`).

| ID | Status | Evidence / gap |
|---|---|---|
| R01 | IN_PROGRESS | One repo, generated C#/TS models, drift check + shared fixtures green (contracts 43951326845). Reproducible toolchain pinned via global.json/lockfile |
| R02 | IN_PROGRESS | Loop followed for the slice; evidence in phase-evidence.md |
| R03 | IN_PROGRESS | Slice UI only (single window, details list). W4 shell not built; no rubric score claimed. WPF console is a D0 feasibility window |
| R04 | NOT_STARTED | Development HMAC identity only. Firebase Auth is W1 (needs project config) |
| R05 | IN_PROGRESS | One-use expiring code + PC approval implemented and tested (integration: expiry, reuse, lockout, race in StoreTests). QR image rendering/scanning not built |
| R06 | IN_PROGRESS | Device grant persists across agent restart; unapproved/revoked browser denied (integration). Lost on gateway restart until W3 |
| R07 | IN_PROGRESS | ECDSA identity, DPAPI CurrentUser via P/Invoke on Windows; restart keeps pcId (Windows integration). Rotation not built |
| R08 | IN_PROGRESS | Roots only via local CLI; only approved root listed (integration, Core tests). Owner UI is D1/D4 |
| R09 | IN_PROGRESS | Traversal/ADS/reserved/symlink/junction/ancestor-swap/hard-link denied on Windows CI. Volume identity, placeholder, race fuzzing outstanding |
| R10 | IN_PROGRESS | No write path exists at all; every root reports readOnly. Upload enablement is D9/W9 |
| R11 | NOT_STARTED | Tray/startup/quit lifecycle is D1 |
| R12 | IN_PROGRESS | Agent is outbound-only by construction (no listener). Only loopback tested; no external-network or mobile-data trial |
| R13 | IN_PROGRESS | Backoff+jitter reconnect; restart test. No fault-injection/idempotency suite yet |
| R14 | IN_PROGRESS | Online/offline/paused states tested (integration, web e2e). Lease/heartbeat expiry not built |
| R15 | AUTOMATED_VERIFIED | Listing compared with the real folder on Windows + Linux, through the web UI in 3 engines (integration, web 43951327095). Awaiting real-device use |
| R16 | IN_PROGRESS | Details view, click to open/download. No sort/selection/context menu/icon view yet |
| R17 | IN_PROGRESS | Breadcrumbs, Unicode names, real size/mtime tested. Back/forward/up, properties panel outstanding |
| R18 | AUTOMATED_VERIFIED | 1 GiB with stalled receiver: <9 MiB RSS growth on Windows and Linux (integration 43951327071). Awaiting real network/phone |
| R19 | IN_PROGRESS | Changed source → 409, If-Range mismatch → full body (integration). Interrupted-then-resumed end-to-end and timestamp-restoring rewrite outstanding |
| R20 | AUTOMATED_VERIFIED | SHA-256 source vs received (1 GiB, small, zero-byte); UI says 'handed to your browser', asserted not to say saved/complete |
| R21 | IN_PROGRESS | Capability scoped to revision+device; cross-user, revoked, expired, guessed denied (integration). Per-capability abuse limits outstanding |
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
| R33 | IN_PROGRESS | Emulated Pixel 7 / iPhone 15 layouts pass; no physical phone, no landscape/tablet work |
| R34 | IN_PROGRESS | Semantic buttons/labels, focus-visible, reduced-motion, forced-colors CSS. No automated a11y audit or keyboard review yet |
| R35 | NOT_STARTED | W6 |
| R36 | NOT_STARTED | D9/W9 |
| R37 | NOT_STARTED | W10 |
| R38 | IN_PROGRESS | Logs asserted free of pairing/device secrets, tokens, capabilities (integration). No support bundle/cache yet |
| R39 | AUTOMATED_VERIFIED | /v1/meta and web taskbar state TLS-via-relay, not E2EE (integration test); README/SECURITY agree |
| R40 | IN_PROGRESS | Static build + production guard tested; firebase.json has no backend rewrite. Not validated with Firebase CLI |
| R41 | NOT_STARTED | In-memory store; W3 |
| R42 | IN_PROGRESS | Per-owner stream cap (429 + recovery), 64 KiB body cap (413) and control-message cap tested (integration); no relay-byte quotas yet |
| R43 | NOT_STARTED | D10 |
| R44 | NOT_STARTED | D10 (signing identity required) |
| R45 | IN_PROGRESS | Transfer metrics recorded per run; 10,000-entry listing test. Budgets beyond transfer memory not set |
| R46 | IN_PROGRESS | /healthz only |
| R47 | IN_PROGRESS | Adversarial cases run in every integration/Core run; no dedicated corpus/fuzzing yet |
| R48 | IN_PROGRESS | Runner OS/tool versions recorded; emulation labelled; device evidence listed as missing |
| R49 | NOT_STARTED | D12/W12 |
| R50 | IN_PROGRESS | Version negotiation in handshake; unsupported agent version rejected with update message (integration). No old/new matrix |
| R51 | IN_PROGRESS | Revoke cuts an active stream and later access; sign-out revokes the browser (integration, web e2e). Account removal, PC removal outstanding |
| R52 | NOT_STARTED | No service worker exists yet (W6) |
| R53 | IN_PROGRESS | Account/request/PC binding, substituted PC, second account refused (integration, StoreTests). Origin display + anti-phishing copy on the PC is D2 |
| R54 | IN_PROGRESS | Empty/offline/error/paused states in web slice with screenshots; desktop states are D1 |
| R55 | NOT_STARTED | D6/W6 |
| R56 | IN_PROGRESS | Locked file → truthful 'in use' error on Windows (Core test). Removable/placeholder/EFS not tested |
| R57 | IN_PROGRESS | Gateway refuses Production with dev identity; web build guard (tests). No Firebase projects exist |
| R58 | IN_PROGRESS | No deploy workflow exists; security job fails if a deploy/privileged step appears in ordinary workflows |
| R59 | AUTOMATED_VERIFIED | HEAD, full, explicit/open/suffix ranges, 416, If-Range, malformed/multi-range fallback, expiry (integration both OSes + RangeParser tests). Awaiting real browser download-manager behaviour |
| R60 | NOT_STARTED | Extensions deliberately out of scope |
