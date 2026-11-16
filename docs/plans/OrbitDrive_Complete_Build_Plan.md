# OrbitDrive - complete research and build pack

Target repository: https://github.com/nisar87/OrbitDrive

Prepared 17 November 2026. Contains two coordinated implementation plans, 26 phases, 60 tracked requirements, a Claude execution prompt and 30 primary references. This is a development specification, not implemented code or executed test evidence.

Read sections in order. The desktop and web plans are separate workstreams within one repository.

---

## Document 1: 00_START_HERE_AND_CLAUDE_PROMPT.md

## OrbitDrive - architecture and Claude execution instructions

Research date: 17 November 2026 | Owner: Nisar | Version: 1.0

**Target repository: https://github.com/nisar87/OrbitDrive**

The repository was accessible and empty when reviewed [S30]. Reinspect it before implementation because this can change. This pack is a researched specification, not an implemented application or evidence of executed GitHub jobs.

### 1. One product, one repository, separate deployable components

Keep the Windows agent, browser client, operator admin area, backend and shared contracts in ONE repository. Admin is a role-protected feature of the web app, not another repository. The Windows application is an owner console and file agent, not the platform administrator application.

Recommended names: OrbitDrive for Windows, OrbitDrive Web, OrbitDrive Admin, OrbitDrive.Agent and OrbitDrive.Gateway. These are working product names, not domain or trademark clearance.

```text
OrbitDrive/
  apps/
    desktop/
      OrbitDrive.Desktop/          # WPF owner console and tray
      OrbitDrive.Agent/            # Per-user background worker
      OrbitDrive.Core/             # Local policy, filesystem and transfer logic
      OrbitDrive.ShellExtension/   # Minimal native Explorer integration
      packaging/
    web/
      src/features/
        auth/
        desktop-shell/
        explorer/
        pairing/
        devices/
        inbox/
        transfers/
        settings/
        admin/
  services/gateway/                # ASP.NET Core API, control and byte relay
  packages/protocol/               # Canonical contracts; generated C#/TS models
  packages/design-tokens/
  tests/
    contracts/
    desktop/
    web/
    gateway/
    integration/
    security/
    fixtures/
  infra/firebase/                  # Hosting, rules, indexes, emulator settings
  infra/relay/                     # Container and reverse-proxy configuration
  docs/plans/
  docs/architecture/
  docs/evidence/
  docs/operations/
  .github/workflows/
  CLAUDE.md
  README.md
  SECURITY.md
```

Independent deployment does not require independent repositories. Use directory-scoped workflows and separate web, gateway and installer release artifacts. Split repositories only when ownership or release governance genuinely requires it.

### 2. Read these files together

1. `00_START_HERE_AND_CLAUDE_PROMPT.md`: architecture, dependencies and execution policy.
2. `01_DESKTOP_IMPLEMENTATION_PLAN.md`: 13 Windows phases, D0-D12.
3. `02_WEB_AND_BACKEND_IMPLEMENTATION_PLAN.md`: 13 web/backend phases, W0-W12.
4. `03_REQUIREMENTS_TESTS_AND_RELEASE_GATES.md`: requirement traceability, test matrix, visual gates and acceptance scenarios.
5. `04_RESEARCH_AND_ARCHITECTURE_NOTES.md`: verified constraints, tradeoffs and primary sources.

Source identifiers such as [S01] refer to file 04. Both implementation plans include extension roadmaps. Required release phases and optional extensions are deliberately separate to prevent endless scope expansion.

### 3. Product contract

The Windows owner explicitly selects shareable folders or drives. A signed-in mobile browser scans a one-time QR, the PC confirms pairing, and a revocable device grant enables subsequent access. The web interface feels like a Windows-11-inspired desktop and File Explorer, but is a web application, not an operating system or remote desktop session.

Users browse real authorized folders, preview supported content and download real files across different internet connections. In real Windows Explorer, `Send to my phone via OrbitDrive` creates a transfer invitation for a paired receiver. The phone receives an Inbox item and, when supported and permitted, a notification. It then starts a user-authorized download.

Do NOT promise silent universal background saving in mobile browsers. Do NOT say a file is on the phone merely because an invitation was sent. A PC must be awake, connected and running the agent to supply unstaged files. No file synchronization or permanent cloud copy is implied.

Use HTTPS/WSS and an authenticated filesystem agent, not ordinary FTP. The chosen relay architecture requires no inbound home-router port forwarding or public PC IP. Networks blocking the service can still prevent access. Local Windows ACLs and explicit roots remain authoritative; 'any file' never means bypassing OS permissions.

Read-only access is the default. Later required phases add uploads into a specifically enabled destination. Remote deletion, public sharing, native Android, WebRTC, E2EE, offline caching and AI search have explicit extension plans rather than being silently assumed.

### 4. Recommended architecture

```text
Windows owner console <-> per-user Agent
                               |
                        outbound HTTPS/WSS
                               |
                   ASP.NET Core Gateway/Relay
                  initially ONE always-on instance
                               |
                      authenticated HTTP streams
                               |
                 Mobile browser / installed PWA

Firebase Hosting: static React/TypeScript application
Firebase Authentication: account identity
Cloud Firestore: minimal durable control metadata
Firebase Cloud Messaging: optional push notifications
```

Default stack: .NET 10 LTS on Windows and gateway; WPF owner UI; minimal native C++ shell component; React + TypeScript + Vite for the static web application. Pin supported patches and compatible packages at phase 0 [S07, S08]. Use canonical OpenAPI/event schemas and generated C#/TypeScript models, not drifting handwritten protocol copies.

Firebase Hosting serves the web assets. The long-running gateway/relay needs its own HTTPS endpoint. Firebase Hosting rewrites have a 60-second timeout; they are NOT the large-file transfer route [S01]. Initially deploy the relay container to a small always-on Linux VM/VPS, optionally in Google Cloud. Prepare configuration but do not provision paid services without approval.

Cloud Run is an alternative after a capability/cost spike, not an assumed free always-on relay. Its WebSockets have finite request lifetimes, reconnect requirements and instance-routing considerations [S02]. Adding Firestore or a message backplane alone does not move a live byte stream to the instance receiving the browser download. The initial single-instance design deliberately avoids this complexity.

Firestore contains account/device/grant/invitation metadata, not file bodies, entire directory indexes or per-packet progress. Initially deny direct client database access and route sensitive operations through the gateway. Server authorization is mandatory even with Firestore rules [S11]. Firebase Storage is not required for the initial on-demand product.

The initial relay terminates TLS and can process plaintext. 'No deliberate persistent cloud file storage' is not the same as E2EE or zero knowledge. Product text must disclose this. Do not make privacy claims the architecture cannot support.

### 5. Shared domain and protocol decisions

Define User, PcDevice, BrowserDevice, BrowserSession, PairingRequest, SharedRoot, FileEntry, FileRevision, TransferInvitation, TransferSession, UploadSession and AuditEvent. Every object has ownership and scope. IDs are opaque; an absolute client-supplied filesystem path is not authorization.

Control actions include ListRoots, ListDirectory, StatEntry, OpenRead, CancelStream, PrepareArchive, SearchNames and, after the write gate, CreateUpload/CommitUpload. Each message has protocol version, request/correlation ID, device/session scope, deadline and typed payload. Reject unknown commands. There is no ExecuteCommand, arbitrary URL proxy or remote shell.

Agree version negotiation, capability flags, typed errors, exact 64-bit size encoding and transfer states before parallel development. Distinguish offered, accepted, preparing, transferring, interrupted, cancelled, expired, revoked, sent-to-browser and confirmed-saved where observable. Ordinary browser downloads may not reveal final save completion.

### 6. Build vertical slices in this dependency order

| Milestone | Coordinated phases | Exit result |
|---|---|---|
| M0 Foundations | W0 + D0 | Contracts, threat model, CI and feasibility spikes |
| M1 Account and trust | W1 + D1 -> W2 + D2 | Real account, QR pairing and revocation |
| M2 First remote file | W3 + D3 -> D4 + W4 -> D5 + W5 | Real directory browsing and bounded-memory download |
| M3 Signature workflow | D6 + W6 | Explorer send -> phone Inbox -> download |
| M4 Polished workspace | D7 + W7 -> D8 + W8 | Previews, archives, search and multi-PC navigation |
| M5 Operational product | D9 + W9 -> D10 + W10 | Opt-in uploads, packaging and administration |
| M6 Release candidate | D11 + W11 -> D12 + W12 | Security/reliability gates and deployment handoff |

Within each pair, define contracts, implement both sides, then run the integrated test. Do not finish an isolated desktop application and only later discover the web client cannot consume it. Mocks assist development; they do not satisfy integration gates.

### 7. Mandatory phase loop

For every phase: inspect its dependencies and existing code; implement; add positive, negative and regression tests; run focused local tests; build affected components; inspect real UI output; commit/push; run GitHub Actions for that exact SHA; inspect completed jobs and artifacts; fix failures; rerun; record evidence; then immediately continue to the next ready phase.

Do not stop after a plan, skeleton, demo or the first green test. Do not ask for routine confirmation between phases. Do not weaken assertions, disable critical tests, use blanket retries/continue-on-error, fabricate screenshots or count a skipped test as passed.

Real blockers include missing credentials, cloud approval, signing identity, an interactive Windows session or physical phone evidence. Record the exact blocker, attempted workaround and independent work that can continue. Do not bypass dependent gates or pretend the environment can execute indefinitely. At session boundaries persist `docs/IMPLEMENTATION_STATE.md` with verified commit, next phase, failures, blockers and reproducible continuation steps.

### 8. GitHub, source-control and deployment policy

Use a working branch such as `feature/orbitdrive-foundation`, phase-sized commits and a continuously updated draft PR. For the currently empty repository, create only the necessary initial default-branch baseline if the environment requires it, then develop on the feature branch. Keep unrelated repositories untouched. Do not change repository visibility without permission.

Create contract, Windows, web, gateway, security, integration and release workflows. Pin trusted actions; use least privilege, job timeouts, bounded matrices and short artifact retention for sensitive traces. Never run untrusted pull-request code with deployment/signing credentials.

GitHub's x64 Windows runner does not establish Windows 11 Explorer compatibility. Hosted Windows machines also run with elevated defaults, so standard-user behavior needs separate evidence [S18]. Playwright mobile emulation is not physical-phone certification [S19]. Use actual WPF UI Automation where supported, plus real Windows 11 and real Android/iOS checks for the claims being made [S20].

The user will deploy the web version later. Prepare Firebase config, staging instructions, containers and gated deployment workflows, but do not deploy live, change DNS, provision paid resources or publish production installers automatically. Separate development/staging/production projects; a Hosting preview URL alone is not backend isolation. Prefer short-lived GitHub OIDC/Workload Identity Federation credentials [S22, S23].

### 9. Main Claude implementation prompt

Work in **https://github.com/nisar87/OrbitDrive**. This is the only target repository. Read all five OrbitDrive build-pack files completely before coding, then inspect the actual repository state. Preserve any changes added since this research.

You are responsible for implementing OrbitDrive, not merely rewriting the plan. Build the Windows owner console/agent, Windows Explorer command, Windows-11-inspired mobile-friendly web desktop, Firebase integration and secure gateway/relay as one coordinated product.

Follow the milestone order and every required D0-D12 / W0-W12 phase. For each phase implement, verify, run appropriate GitHub cloud tests, inspect actual results for the commit, fix bugs, rerun, save evidence and continue without routine approval pauses. Maintain the requirement-to-test matrix. Keep functional, security, visual, physical-device and deployment status separate.

The two signature outcomes are: browse an owner-approved Windows folder and retrieve a real large file from a mobile browser on another network; and right-click a file in real Windows Explorer, choose a paired phone, receive an Inbox notification and download it after the phone's user action. Neither workflow may be simulated with canned files in production.

Keep read-only defaults, safe path resolution, explicit upload permissions, revocable pairing, bounded-memory streams and truthful status. No exposed FTP, arbitrary remote execution, whole-file memory buffering for large downloads or false E2EE claims. Deliver a premium visual experience and iterate against screenshots and the scoring rubric, not self-awarded scores without evidence.

Use one repository and independent component workflows. Do not stop after the first milestone; proceed until the required release-candidate scope is complete or a genuine external blocker prevents a dependent step. Continue independent work around blockers. Persist exact continuation state when the session ends rather than promising invisible background work.

Do not deploy production Firebase, create billable services, change DNS, publish unsigned installers as production-ready, merge to main or publish a release without explicit authorization or an existing clearly documented standing policy. Prepare deployment so Nisar can perform it later. Start now with phase 0 and a verified CI baseline, then implement the first real vertical slice.

---

## Document 2: 01_DESKTOP_IMPLEMENTATION_PLAN.md

## Plan 1 - OrbitDrive for Windows

Repository: https://github.com/nisar87/OrbitDrive
Research date: 17 November 2026 | Required phases: D0-D12

### Mission, platform and execution contract

Build the local owner console, secure file-access worker and real Windows Explorer 'Send to my phone' command. Only explicitly approved files may leave the PC. Use outbound authenticated HTTPS/WSS; do not expose ordinary FTP, a remote command shell or a publicly reachable local file server.

Use .NET 10 LTS, WPF and a separately testable per-user worker. Windows 11 x64 is the primary target. Validate any Windows 10 edition against current runtime/OS support; do not advertise every Windows 10 installation as supported [S08, S29]. ARM64 is an independently verified optional package. The worker runs as the logged-in user, not LocalSystem. A native, minimal Explorer component delegates to the worker; it does not host the application's business logic.

Read the master instructions, web plan and requirement matrix. Execute in the coordinated milestone order. Each phase requires local tests, relevant GitHub cloud jobs for the actual commit, artifact review, fixes, reruns and recorded evidence. Compilation alone is never the exit criterion.

### D0 - Architecture, scaffold and early feasibility

**Dependencies:** shared architecture/W0. **Requirements:** R01, R02, R07, R23, R48, R50.

Create the solution/projects, nullable and analyzer configuration, pinned SDK, test assemblies and core interfaces. Separate filesystem policy, device identity, local settings, transport, transfer sessions, previews and IPC from WPF controls. Record component and threat-boundary decisions.

Run small executable spikes before building the full product: launch/capture an actual WPF window on the selected Windows CI runner; stream a generated large file with bounded memory; register a minimal modern Explorer command on a suitable Windows 11 environment; reconnect an outbound session without duplicates; and prove the proposed packaging/app-identity route. Identify certificate, interactive-session and hardware prerequisites early.

**Tests and exit gate:** clean-checkout Windows builds and basic tests pass. Capture exact OS, architecture and tool versions. No production keys or personal paths in fixtures. Unsupported UI automation or Windows shell validation is a named blocker, not a fabricated pass.

### D1 - Owner console, tray and user-session lifecycle

**Dependencies:** D0; coordinates with W1. **Requirements:** R03, R11, R14, R34, R38, R45, R54.

Implement Overview, Shared Folders, Devices, Transfers, Activity, Settings and Diagnostics. Show real state and useful empty states. Overview contains a prominent Pause Remote Access control, current connection/account, approved roots and transfers.

Implement one instance per Windows user, tray operation, reopen settings, explicit Quit and optional start at sign-in. Closing settings may leave the worker running, but explain this. Quit must end access. No auto-sharing of familiar folders and no unsolicited elevation.

Use typed, bounded IPC with user-SID ACLs and validated callers. A second Windows user must not control the first user's worker. Never hand arbitrary shell command strings to the worker. Recover from crashes with bounded restart attempts; prevent launch loops.

Define locked, logged-out, sleeping and shut-down behavior. While minimized/locked, access can continue according to a visible setting; logout/sleep normally makes the PC unavailable. Never silently disable sleep. Any keep-awake-during-transfer option needs consent and automatic cleanup.

Provide light/dark/high-contrast support, keyboard navigation, accessible focus and DPI scaling. Distinguish disconnected, connecting, online, paused, account-action-needed and update-required.

**Tests and exit gate:** lifecycle/settings tests and actual WPF UI Automation cover navigation, pause and quit. Verify no orphan processes, duplicate instances or privileged worker. Review real screenshots at multiple DPI levels and exercise standard-user operation outside an elevated hosted-runner default.

### D2 - Persistent identity, one-time QR and revocation

**Dependencies:** D1 + W2. **Requirements:** R04-R07, R51, R53.

Generate a device identity and protect credentials using OS facilities and restrictive local ACLs; DPAPI CurrentUser is suitable for protected local secrets [S17]. Prefer OS-managed non-exportable keys where practical. Do not claim this prevents malware running as the same user from abusing the application.

The agent requests an expiring pairing challenge tied to its identity and nonce. Display the trusted app origin, one-use QR and a rate-limited manual code fallback. The QR contains a bootstrap reference, not a reusable login token, private key or Windows path. The signed-in phone claims it; the PC shows account, receiver name and matching confirmation code; the owner approves on the PC. Only then bind ownership and device access atomically.

Use reviewed cross-device authorization patterns and established cryptography rather than inventing a protocol [S12, S13]. A proposed QR lifetime is two minutes, configurable after usability testing. Limit attempts and polling; reject replay/concurrent claims; bind approval to the correct account/request. A QR does not prove physical proximity. Separate first-PC enrollment from adding another browser to an existing PC.

Persist registration so routine restarts do not require rescanning. Add revoke-browser, disconnect-PC, sign-out and emergency pause. Revocation cancels active streams and pending offers, not just future logins. Document a propagation target and fail closed on invalid credentials.

**Tests and exit gate:** the real PC/web/gateway pair successfully. Expired/reused/malformed/stolen QRs, wrong accounts, replay, race claims, clock skew and restarts are covered. Secrets do not enter logs or screenshots. Device revocation is demonstrated during an active transfer.

### D3 - Outbound connectivity, control protocol and resilience

**Dependencies:** D2 + W3. **Requirements:** R12-R14, R27, R41, R50, R54.

Connect outward to the approved gateway using HTTPS/WSS. SignalR carries small typed control messages; binary file data uses a separate bounded stream. No router reconfiguration, UPnP, inbound file listener or disabled certificate checks.

Implement heartbeat/lease presence, deadlines, credential renewal, exponential-backoff reconnect with jitter, cancellation and graceful shutdown. Permit one active connection generation per PC; superseded sessions cannot execute requests. Reconnect must not duplicate invitations or upload commits.

Handle internet loss, Wi-Fi changes, sleep/resume, gateway restart, TLS/proxy failure and time skew. Presence and permission are separate: a reconnected device still needs valid grants. Bound outstanding requests, message sizes and retry queues. Negotiate protocol versions/capabilities and provide meaningful update-required messages.

Apply local root/grant policy for every instruction even after gateway authorization. Unknown actions fail; there is no generic remote execution operation.

**Tests and exit gate:** fault-injection tests terminate sockets/processes and replay old events. The worker reconnects without duplicate ownership or stale permissions. An external-host integration demonstrates outbound-only connectivity; it is not claimed to prove every possible NAT/firewall.

### D4 - Approved roots and secure filesystem browsing

**Dependencies:** D3 + W4. **Requirements:** R08-R10, R15-R17, R38, R54-R56.

Owners select roots locally, set friendly names and see read-only permissions. Full-drive selection requires an explicit warning and never overrides OS ACLs. Remote clients cannot add roots. Bind roots to volume identity so a different removable disk using the same drive letter does not inherit access.

Implement paged listing, breadcrumbs, file properties, hidden-file preference and accurate metadata. Client entry IDs are opaque and scoped to root/user/device. An arbitrary absolute path from the browser is never sufficient authority.

Protect against traversal, encoded separator tricks, unexpected absolute/UNC/device paths, alternate data streams, long/ambiguous names and case normalization problems. Reject unapproved junction/symlink traversal by default. Validate actual opened handles and final paths/volume identity, then serve the same validated handle; do not check one path and reopen another [S15, S16]. Test ancestor-swap and reparse races. A string-prefix check is not a complete access boundary.

Specify a hard-link policy because reparse checks alone do not establish unique file origin. Initially reject ambiguous multiple-link cases or implement a reviewed/tested alternative. `.lnk` files are files, not permission to follow their targets. Access-denied, EFS/protected files, locked files and unsupported network shares must fail without elevation.

Handle Unicode/Urdu/emoji names, deep trees, large directories, removable devices and missing roots. Detect cloud placeholders and do not silently hydrate them without the configured policy. Use useful error messages; never silently expose another folder as a fallback.

**Tests and exit gate:** adversarial fixtures prove root escape and cross-device access are denied. A 10,000-entry fixture remains usable. Revoking a root stops listings and active streams. Missing disks/placeholders and locked files produce truthful states.

### D5 - Large downloads, stable revisions and resume

**Dependencies:** D4 + W5. **Requirements:** R18-R21, R39, R42, R45, R59.

Implement agent-to-relay rendezvous: receive a scoped OpenRead request; recheck permission; resolve/validate the file handle; return metadata; and open an outbound byte stream for that transfer and requested range. Uplink credentials cannot be reused across owners, devices or sessions.

Use fixed pooled buffers, bounded queues, backpressure and cancellation. No complete-file buffering, base64 file messages or giant SignalR payloads. Use exact 64-bit offsets/lengths with overflow checks. Slow receivers throttle disk reads rather than grow memory indefinitely.

Define a strong source-revision strategy. Size plus modified time is not automatically a strong validator. Use a content-verified revision or immutable local snapshot where necessary. A safe option is hashing under a stable read handle and clearly showing preparation; account for locked files and extra disk I/O. Never append a new source revision to an old partial download. If safe resume cannot be established, disable it for that item and explain.

Implement the agent half of byte-range/conditional reads [S14]. Revalidate resumed reads. Cover full/partial/open-ended/suffix requests, zero-byte files, HEAD metadata and unsatisfiable ranges. Document unsupported multi-range behavior and abuse limits. User cancellation, access pause and revocation stop reading promptly.

Track agent bytes read separately from relay bytes sent and receiver confirmation. Add concurrency/bandwidth controls and metered-network warnings. Protocol resume is not a universal guarantee about a mobile browser's native download manager.

**Tests and exit gate:** hash-verified zero-byte, small and 1-GiB fixtures pass. Optional nightly 5-GiB tests use bounded CI resources. Interrupt, resume, mutate the source, throttle the receiver and revoke access. Record memory/throughput metrics; no corruption or false saved-on-phone status.

### D6 - Real Windows Explorer send-to-phone integration

**Dependencies:** D2, D5 + W6; app-identity spike from D0. **Requirements:** R23-R27, R55.

Use a minimal native IExplorerCommand component with appropriate app identity for the modern menu [S07]. It captures selected shell items, delegates through secure local IPC and returns promptly. No hashing, network calls, uploads or business logic during menu construction. Provide an appropriate legacy menu only on separately tested editions.

Display 'Send to my phone via OrbitDrive' and a named receiver picker. Receiver names refer to browser registrations, not an assumed hardware identity. Show last-seen state without promising notification delivery. Validate single/multiple selection, spaces, quotes, Unicode and malicious-looking filenames. Safely start the worker when needed; never inject selected paths into a shell command line.

An explicit owner-selected file outside a permanent root may create a short-lived file-specific read grant for the chosen receiver. This must not reveal the parent directory or create a permanent root. Bind it to file identity/revision, target and expiry. Folder selections become supported after D8; until then show a truthful unsupported state.

Create a durable invitation, not a claim that the transfer is complete. The original PC must be available when accepted unless an explicitly enabled future cloud copy exists. Support cancel/revoke/resend and idempotency. Changed/moved/deleted sources produce recoverable failures.

**Tests and exit gate:** invoke the actual modern menu in real Windows 11 Explorer with the package identity installed. A classic 'Show more options' entry alone does not pass the modern-menu gate. The invitation appears in W6 and the phone can download on another network after user action.

### D7 - Safe thumbnails and media preparation

**Dependencies:** D5 + W7. **Requirements:** R28, R29, R38, R45, R47.

Generate thumbnails locally with maintained decoders and pixel/memory/time/concurrency limits. Isolate risky parsing in a restricted helper where feasible. Never execute a document or invoke arbitrary file associations to preview it.

Use a quota-bounded private per-user cache keyed by source revision and transform parameters, with expiry and clear/purge controls. Permission changes invalidate access to derived content. Do not place private thumbnails in public folders; originals remain unchanged.

Support approved image and small text previews. The web side uses a maintained isolated PDF renderer. Active HTML/SVG content is forced to attachment or safely transformed, not inserted into the authenticated app. Range-stream supported audio/video originals; universal codecs or silent heavy transcoding are not promised.

**Tests and exit gate:** malformed media, image bombs, excessive dimensions, corrupted documents and hostile metadata fail within resource limits. Unsupported types have a download fallback. Cache access/revocation and cleanup are verified.

### D8 - Folder archives, filename search and multi-PC readiness

**Dependencies:** D6-D7 + W8. **Requirements:** R26, R30-R32, R45.

Implement folder/multiselection ZIPs with safe relative names. Exclude out-of-scope content and unapproved links. Define changed/locked-file behavior: cancel or report omissions explicitly; never silently call a partial archive complete.

Distinguish bounded streaming ZIP output, which need not support arbitrary resume, from a locally staged immutable archive that can resume. Staging requires free-space checks, private temporary storage, size/TTL quotas and crash/cancel cleanup. Validate ZIP64/large archive support using the chosen library. Never modify originals.

Add cancellable filename search limited to allowed roots. Start with bounded traversal; introduce a local filename index when justified. Indexing follows exclusions and permissions, reconciles watcher overflow, reports freshness and stops on root removal. Semantic/content indexing is deferred unless explicitly enabled later.

Support multiple PC identities without conflating file IDs, roots or permissions. Two PCs may have identical names/paths but remain distinct devices.

**Tests and exit gate:** extract archives and compare authorized contents; verify safe names and resumable staged output. Search never returns unshared data. Revoke a root during archive/search work. Duplicate names and unavailable devices remain unambiguous.

### D9 - Explicitly authorized uploads from mobile

**Dependencies:** D5, D8 + W9. **Requirements:** R10, R36, R47, R54.

Add write access only for a locally approved destination. The browser cannot name an arbitrary Windows path. Create bounded upload sessions with declared size, destination, collision policy and expiry.

Write to a private temporary file on the destination volume. Check available disk, quotas, filenames, reserved Windows names, case collisions, length, chunk order and digest. Commit atomically only after validation. Default to no overwrite; alternative renamed copies must be deterministic and visible.

Never execute/open/install received files or automatically unpack archives. Apply appropriate download-origin security treatment where supported. Do not claim antivirus protection unless implemented and tested. Expired/cancelled partial files are cleaned up and never presented as completed files.

**Tests and exit gate:** disk-full, interruption, duplicated/out-of-order chunks, checksum mismatch, oversized upload, collision, traversal and revocation are covered. Crash recovery leaves no corrupt final file. Every write endpoint rejects read-only roots.

### D10 - Packaging, signing, identity and updates

**Dependencies:** D6, D9 + W10 release metadata. **Requirements:** R43, R44, R50.

Choose one supported route: signed MSIX/App Installer where suitable, or an installer plus sparse package identity if required. Do not install conflicting shell registrations. Match native extension architecture to Explorer. Test installation, repair, upgrade, migration, uninstall and rollback.

Use legitimate code signing for public releases. Development self-signed packages are restricted to disposable/test environments and clearly labelled. Never ask users to disable Windows security globally. Missing signing credentials are an explicit public-release blocker, not a reason to label unsigned artifacts production-ready.

Updates require verified signed metadata/packages, safe interruption and version/protocol compatibility. Do not execute arbitrary update URLs. Pause/drain active work before replacing processes. Preserve credentials/settings safely across migrations; support an explicit reset. Keep signing secrets out of ordinary PR jobs.

Uninstall removes only OrbitDrive-owned startup entries, registrations and caches. Shared folders and user files are never deleted by uninstall.

**Tests and exit gate:** clean install/upgrade/uninstall and package validation pass. Actual Windows 11 confirms modern-menu registration/removal. Test multiple user profiles and standard-user operation. Verify interrupted updates recover and unsigned/tampered packages are rejected.

### D11 - Security, resilience, performance and diagnostics

**Dependencies:** D0-D10 + W11. **Requirements:** R02, R38, R42, R45-R48, R51, R54, R59.

Run the shared adversarial corpus: root/reparse/hard-link races, wrong-user IPC, forged sessions, malformed messages, replay, revoked grants, unbounded queues, hostile files and upload races. Review dependencies/native interop and resolve exploitable security defects.

Run bounded scheduled/manual soak jobs, including prolonged connections, gateway restarts, slow receivers, concurrent browsing/transfers and temporary network failures. Real sleep/wake, lock/unlock and network switching require real-machine evidence. Do not use a server VM to pretend these mobile/desktop behaviors were tested.

Measure idle CPU/RAM, active overhead, transfer throughput, cancellation latency, listing/search responsiveness and cache growth. Set budgets on reproducible hardware and report results rather than claiming universal internet speed. Redact telemetry: no secrets, file bodies, raw private paths or filenames by default. Provide owner-visible activity history, rotating logs and previewable opt-in support bundles.

**Tests and exit gate:** no unresolved critical/high security issue, corruption, authorization bypass or sustained unbounded memory. Evidence includes exact commit/run IDs. Investigate flaky tests rather than hiding them with retries.

### D12 - Release candidate and owner handoff

**Dependencies:** D11 + W12. **Requirements:** R48-R50, R57, R58.

Prepare versioned Windows artifacts, checksums, signature information, third-party notices, compatibility matrix, installation/update/uninstall instructions and troubleshooting. Document minimized/locked/sleeping/logged-out/shut-down behavior accurately.

Perform the end-to-end acceptance script from the QA plan with owner-controlled files: install; approve a root; pair a phone; browse on mobile data; download and hash-verify 1 GiB; interrupt/resume through a supported path; send through real Explorer; receive and accept; revoke and prove access stops. Include physical Android and iOS evidence for whichever platforms are advertised.

**Tests and exit gate:** Required automated and real-device checks pass for every advertised platform, and artifacts match the verified commit. Missing physical-device/signing evidence means 'release candidate awaiting X', not 'fully tested release'. Prepare but do not publish production installers or deploy the live Firebase site without authorization.

### Extension phases - after the required release scope

**Shared requirement: R60.** These are planned extensions, not unfinished mandatory release phases.

**E-D1 Native Android receiving:** companion app, account/device pairing, explicit auto-receive preferences, foreground transfer notifications, system storage integration and retries. Test OS battery/background restrictions. Keep the browser usable without installation.

**E-D2 Direct transfer:** maintained native WebRTC transport, authenticated signaling, STUN/TURN, bounded data-channel backpressure, reliable relay fallback and network-change recovery [S24]. Direct connection success is not guaranteed.

**E-D3 End-to-end encryption:** reviewed key agreement/verification, recipient-bound encryption, rotation/revocation, chunk integrity and encrypted resume. Test browser save/memory implications before claiming it supports every download path.

**E-D4 Safe file operations:** separately approved rename/move/delete/recycle, conflict detection, operation-level permissions, audit and recovery. Never enable destructive operations merely to imitate Windows UI.

**E-D5 Advanced services:** opt-in local semantic search, isolated format conversion, automated backup with a native phone client, separately designed wake infrastructure and optional compatibility protocols. Every extension gets threat/cost analysis and its own evidence gate.

---

## Document 3: 02_WEB_AND_BACKEND_IMPLEMENTATION_PLAN.md

## Plan 2 - OrbitDrive Web, backend and Firebase deployment readiness

Repository: https://github.com/nisar87/OrbitDrive
Research date: 17 November 2026 | Required phases: W0-W12

### Mission, stack and execution contract

Build a premium Windows-11-inspired workspace in the browser, usable on phones, tablets and desktop screens. It shows real authorized files from paired PCs and receives invitations initiated from real Windows Explorer. It is not a remote Windows session and must not pretend to launch Windows executables.

Use a static React/TypeScript/Vite application suitable for Firebase Hosting. Firebase Authentication handles account identity; Firestore holds limited durable control metadata; FCM supplies optional push notifications. A separate .NET 10 ASP.NET Core gateway/relay supplies outbound agent connectivity and streamed file transfer. There is no mandatory Firebase Storage component in the initial release.

This is the second implementation plan, not a second repository. The protected admin area lives inside the web codebase. Read the desktop plan, master execution policy and QA matrix. Build each paired milestone, verify it in GitHub, fix failures and continue. Prepare deployment but leave live deployment to the owner unless explicitly authorized.

### W0 - Contracts, threat model and browser feasibility

**Dependencies:** shared architecture; coordinates with D0. **Requirements:** R01, R02, R18-R21, R39, R40, R48, R50, R57, R58.

Create the application, gateway, generated protocol models, deterministic fixtures, emulator configuration and CI skeleton. Provide local startup instructions/scripts on Windows and Linux. Production builds must reject emulator flags, fake identities and fixture adapters. Pin compatible dependencies and check licenses/maintenance.

Define ownership, entity schemas, events, errors, pagination, transfer states and version negotiation before parallel implementation. C# and TypeScript must agree on exact large-file size/offset serialization; avoid silently rounding integers outside JavaScript's safe range. Add generation-drift and cross-language serialization tests.

Threat-model browser, PC, shell helper, relay, identity service, database, notification provider and operator. Cover credential theft, malicious QR relay, cross-account access, unsafe paths, hostile previews, replay, abuse and operator visibility. Begin with owner/invite-only registration rather than an unprotected public file relay.

Immediately prove large browser downloads with a generated 1-GiB stream on representative Android and iOS environments. Test native download navigation, user interaction, supported file-save APIs, interruption and backgrounding. Record real-device evidence separately from emulation. Do not make an unverified browser API the only receiving mechanism.

**Tests and exit gate:** clean-checkout builds, contract tests and CI baseline pass. The static-hosting/relay boundary is explicit. Major phone/Windows capability risks have actual spike evidence or named blockers. No automatic cloud purchases or production deployment.

### W1 - Accounts, authentication and approved browser sessions

**Dependencies:** W0; coordinates with D1. **Requirements:** R04, R06, R21, R37, R38, R51.

Implement sign-in, sign-out, password reset, verification and useful failure states using supported Firebase providers. Start with verified email/password and Google sign-in where configured. Make persistent versus temporary login explicit for shared phones. Do not collect Firebase passwords in the Windows program.

Verify Firebase ID tokens at the gateway with the official Admin SDK and appropriate issuer/audience/expiry/revocation checks [S09, S10]. A client-supplied UID is never authority. Authorization checks ownership plus current PC/browser/root/session grants on every operation, including previews, ranges and uploads.

Browser-device approval is distinct from login and notification registration. A new logged-in browser cannot silently inherit access to all PCs. Define a reviewed device/session credential design with expiry, rotation and revocation; use platform keys and established libraries where justified, not a novel crypto protocol. A random localStorage device name is not proof of device trust. Clearing storage may require reapproval, and an authorized session compromised by XSS remains a threat.

Logout invalidates the relevant receiver session, cancels active application transfers as applicable, clears private caches and removes its notification association. Provide revoke-one-device and revoke-all. Choose supported provider MFA/step-up capabilities for sensitive/admin operations; do not invent passkey APIs or assume pricing/features without checking.

**Tests and exit gate:** expired, malformed, wrong-project, revoked and cross-account tokens fail. Direct API requests cannot bypass UI guards. Test account switching, refresh, session replay, cleared storage and revoked browsers. Emulator success does not prove live identity-provider configuration.

### W2 - QR pairing, confirmation and device management

**Dependencies:** W1 + D2. **Requirements:** R05-R07, R32, R51, R53.

Implement issue/claim/confirm/revoke states with cryptographic randomness, hashed bootstrap secrets, explicit expiry, attempt limits and atomic transitions. Database TTL cleanup is not the authorization check. A QR request cannot be claimed twice or substituted during login.

Support both the phone's system camera opening the trusted pairing URL and scanning inside the website over HTTPS. Detect camera/decoder support at runtime; provide a maintained QR decoder fallback and manual-code entry. Camera frames remain local and are not retained. Permission denial has a usable fallback.

Keep bootstrap secrets out of logs/referrers/analytics and scrub pairing URL history after claim. Bind request, account, PC identity and browser. Show matching confirmation information and require the PC approval described in D2. Scanning is not a proof of proximity; defend against attacker-originated QR flows [S12, S13].

Create a Devices page distinguishing PCs and browser receivers, with name, capability/version, connection status, last seen, rename and revoke. Last seen is not online. Removing a PC/browser revokes associated invitations and streams; it must not leave usable download capabilities behind.

**Tests and exit gate:** real PC/web/backend pairing works. Test expired/reused QR, concurrent claims, account switch, missing desktop approval, malicious replacement, denied camera, manual-code guessing and reload. No pairing secrets appear in telemetry.

### W3 - Gateway, connection ownership and durable metadata

**Dependencies:** W2 + D3. **Requirements:** R12-R14, R27, R39-R42, R46, R50, R54.

Implement authenticated control channels, leases, deadlines, typed request correlation, version/capability negotiation and recoverable offline states. Keep live connections separate from durable registrations. Establish one authoritative connection generation per PC and reject stale session messages.

Initially use one gateway/relay instance. Browser HTTP downloads and agent uplinks must rendezvous there. Firestore stores durable metadata; byte streams use bounded memory. Restarts interrupt/recover sessions explicitly. Do not promise horizontal scale or zero-downtime rollouts before connection/stream routing is solved.

Suggested collections: users, PC devices, browser devices, pairing requests, grants, invitations, transfer/upload sessions and audit events. Store owner IDs, states, expiry and idempotency keys. Do not store entire folder trees, file bodies or every progress tick. Bound heartbeat/progress write frequency to control cost.

Initially deny direct browser Firestore access. The gateway enforces ownership independently because server libraries bypass Security Rules [S11]. Use least-privilege service credentials and only necessary indexes/TTL policies. Do not mistake a permissive development database rule for production authorization.

Containerize the service. Configure TLS, trusted forwarded headers, allowed hosts, WebSocket upgrade, streaming/proxy buffering, timeouts, size/concurrency limits, health/readiness and graceful shutdown. The service is not an arbitrary TCP/URL proxy. No caller may direct it to fetch internal URLs or execute PC commands.

Document privacy accurately: TLS ends at the relay, which can process plaintext. No deliberate persistent file storage does not equal E2EE. Disable accidental payload logging, public caches and proxy spill-to-disk where the design promises streaming.

**Tests and exit gate:** cross-user lookups fail; leases/restarts/duplicate sessions recover; slow clients do not grow buffers indefinitely. Staging uses the direct relay origin, not a Firebase Hosting rewrite [S01]. Missing infrastructure is recorded rather than represented by an invented live URL.

### W4 - Windows-inspired shell and actual File Explorer

**Dependencies:** W3 + D4. **Requirements:** R03, R15-R17, R32-R34, R54.

Build OrbitDrive's own visual identity around familiar Windows-style interactions. Use licensed/original icons/backgrounds and system-font fallbacks; do not redistribute proprietary Windows fonts or wallpapers.

Required shell: desktop surface, launcher, taskbar/dock, active-app indicators, window title bars, focus/minimize/maximize/close, desktop/tablet drag-resize, notification area and settings. Launch only implemented features: Explorer, Inbox, Transfers, Devices, Settings. No fake terminal, Windows program launcher or destructive Recycle Bin.

Required Explorer: PC/root chooser, navigation tree, breadcrumbs, back/forward/up/refresh, icon/list/details views, sort, selection/multiselect, properties, contextual actions and search entry. Show real metadata, permission failures and offline/empty states. Do not fabricate drive capacity or canned folders.

On phones, preserve the visual personality while maximizing the active window in portrait, collapsing the tree and using comfortable touch targets. Landscape/tablet can offer overlapping windows. Mobile tap opens; long-press shows actions; explicit selection supports multiple items. Desktop users get mouse context menus, double-click and keyboard shortcuts. Keep scrolling, browser back, zoom and text selection sensible. Handle mobile keyboard and safe areas.

Provide accessible semantics, focus trapping/restoration, Escape, contrast, reduced motion and screen-reader labels. Do not implement the entire interface as inaccessible fixed-position painted elements. Save layout preferences without retaining private file content.

**Tests and exit gate:** real agent-backed navigation works; Playwright covers interactions and responsive layouts. Review light/dark, portrait/landscape, large text and long Unicode filenames. No offscreen dialogs, clipped labels, nonfunctional menus or tiny unusable controls.

### W5 - Streaming downloads, resume and honest progress

**Dependencies:** W4 + D5. **Requirements:** R18-R21, R39, R42, R45, R59.

Download flow: authenticated approved browser requests a scoped entry/revision; gateway verifies current grants and quota; agent validates and prepares the file; gateway pairs an authorized HTTP download with the matching outbound agent stream; bounded binary buffers forward bytes with backpressure; cancellation/revocation closes both sides. Keep invitation, preparation, transfer and result separate.

Use capability-based receiving. The baseline is a user-triggered native browser download with proper Content-Disposition, not downloading a huge Blob into JavaScript memory. Where tested save-to-file APIs exist, optionally offer managed streaming with richer progress/resume. Feature-detect and test actual devices; browser APIs require permissions/user gestures and differ in availability [S05].

Native navigation cannot just attach a Firebase Authorization header. Implement a dedicated, narrowly scoped download capability, never a Firebase/account token embedded in a URL. Scope it to one revision, allowed methods, grant and limited lifetime. Use high entropy, log redaction, no-referrer behavior, revocation and replay/abuse limits.

An invitation is one-use; a transfer session may need HEAD, retries and multiple ranges. Define redemption/resume semantics explicitly. Do not consume the only credential on a preliminary HEAD request. A native-download URL is a bearer capability: do not claim it is cryptographically phone-bound if possession is sufficient. Managed authenticated modes can impose stronger proof. Avoid IP binding that breaks Wi-Fi/mobile switching.

Implement correct full/partial response headers and conditional-range behavior [S14]. Test Content-Length, UTF-8 filenames, content type, Accept-Ranges, Content-Range, strong validators, If-Range, 206 and 416. No byte-altering compression on range streams. Bound malformed/overflowing/tiny/concurrent ranges. CORS allows specific approved web origins; it is not authentication. Cross-origin download behavior must be proven with actual headers, not the HTML download attribute alone.

Follow D5 source-revision rules. Never append changed-file bytes to a stale partial file. If no safe validator exists, explain that resume is unavailable. Recover interrupted/expired sessions through reauthorization or restart. Native mobile download-manager behavior remains browser-dependent.

Show bytes/speed/time estimates only where observable. Distinguish 'handed to browser' and 'sent to browser' from 'saved on phone'. Background Fetch/Service Worker behavior is not a universal mobile background-download guarantee [S06]. Do not hide a growing full-file buffer behind a progress animation.

**Tests and exit gate:** real 1-GiB streaming, hash validation, source mutation, cancellation, revoked/expired ticket, HEAD-before-GET, repeated ranges and memory limits pass. Test direct origin, forbidden origin, backgrounding, network interruption and fallback when save APIs are unavailable.

### W6 - Incoming files, push notifications and PWA lifecycle

**Dependencies:** W5 + D6. **Requirements:** R22, R24-R27, R35, R51, R52.

Inbox displays sender PC, permitted file details, size, invitation expiry, source availability, Accept/Decline/Download and cancellation. Reconnect reconstructs pending invitations without duplicates. Offline source means waiting, not a fictitious cloud copy.

Use in-app real-time notices while open. Offer opt-in FCM notifications using the currently supported SDK registration/installation APIs; handle registration changes, stale receivers and cleanup on logout/unpair [S03]. A provider send response is not proof of delivery/display. Default lock-screen text is generic and contains no download secret or sensitive filename.

Ask notification permission only after a user action. Unsupported/denied notifications leave Inbox functional. Document/test the applicable iOS Home Screen app requirement rather than promising arbitrary Safari tabs receive background push [S04]. Opening a notification still leads to an authorized, user-initiated receive flow.

Create manifest, icons, install guidance and one coordinated service-worker strategy. Cache static shell assets only by default. Never broadly cache authentication, pairing, private directory listings or file payloads. Purge private state on logout/account switch. Offer safe updates/reloads instead of replacing the service worker during an active managed transfer.

The website does not guarantee silent automatic saving while closed. A later Android companion may improve receiving, but must not become a hidden prerequisite for the browser product.

**Tests and exit gate:** test lost/duplicate events, expiry, revocation, stale notification registration, denied permission and account switching. Real phones verify foreground/background/closed-app notifications and download interactions. CI mocks are not reported as delivered pushes.

### W7 - Safe image, PDF, text and media previews

**Dependencies:** W5-W6 + D7. **Requirements:** R28, R29, R34, R38, R47.

Add thumbnail-first image gallery, image zoom/navigation, small text preview, isolated PDF viewer and browser-supported audio/video playback. Originals download only on explicit request. Publish format/size limits and retain a download fallback.

Treat filenames, document metadata and content as hostile. Escape text. Never insert remote HTML/SVG as active authenticated-origin markup. Use a maintained PDF renderer with restrictive settings, scripting disabled and appropriate isolation. Untrusted previews must not access application credentials. Restrict unsafe external links; do not execute files or install preview helpers automatically.

Range-based media needs narrowly scoped preview authorization valid for legitimate repeated requests. Stop access on revocation/source change. Do not claim every codec plays on every device or silently transcode expensive media on the PC.

**Tests and exit gate:** hostile filenames/XSS strings, malformed PDFs, image bombs, missing thumbnails, huge text and revoked previews are covered. Loading is lazy/bounded, unsupported media is understandable and originals are not altered.

### W8 - Archives, search, favorites and multiple PCs

**Dependencies:** W6-W7 + D8. **Requirements:** R26, R30-R33, R45.

Implement multiselection/folder ZIP preparation, progress and cancellation. Explain whether an archive is streamed without arbitrary resume or staged for reliable resume. Make omissions/changed-file failures explicit; do not label a partial folder archive fully successful.

Add filename search by selected root/PC or multiple available PCs. Search is cancellable, limited and permission-aware. Show partial results/offline PCs/index freshness rather than presenting unavailable PCs as having zero matches. Semantic/content search is deferred.

Favorites/recents use scoped opaque IDs and do not bypass permission. Stale/deleted/unshared entries show an unavailable state and can be removed. A virtual combined root view groups PCs clearly; it is not one actual Windows drive.

Switching PCs must clear incompatible selections and reject foreign entry IDs. Long lists use pagination/virtualization with correct selection and keyboard/accessibility behavior. Persist safe view/layout preferences across screen sizes.

**Tests and exit gate:** no cross-user/PC or revoked data leaks through search/recents. Duplicate filenames remain distinguishable. ZIP downloads cover Unicode/deep paths/changed sources. Large lists remain usable without losing selection correctness.

### W9 - Uploads from phone into approved destinations

**Dependencies:** W5-W8 + D9. **Requirements:** R10, R36, R47, R54.

Expose Upload only for owner-enabled destinations. Use the system file/photo picker rather than requesting all phone storage. Show destination, size/quota and collision policy before sending.

Stream upload bodies without whole-file server model-binding buffers. Use bounded sessions, cancellation, integrity checks and idempotent final commit. Add resumable/chunked upload only with explicit tested sequencing and expiry. The server/agent grant determines the path, never arbitrary client input.

Report network progress separately from final verified PC commit. Handle phone backgrounding, PC offline, disk full, duplicate names, expired permission and picker errors. No success before commit, no auto-execution/extraction. Folder selection support is capability-dependent and not promised on every browser.

**Tests and exit gate:** checksum/atomic commit and interruption recovery pass. Reject replay, oversized payload, wrong chunk order and unauthorized destinations. A hidden endpoint cannot write a read-only root. Revocation leaves no corrupt final file.

### W10 - Operator administration and owner controls

**Dependencies:** W1-W9; coordinates with D10. **Requirements:** R37, R38, R42, R46, R51, R58.

Implement a small lazy-loaded admin area in the same app. Server-enforce roles and suitable recent/strong authentication on every privileged endpoint. A hidden route is not security. Bootstrap the first operator through a trusted explicit operation, never 'first registrant becomes admin'.

Admin scope: aggregate health, users/devices, failed transfers, relay bytes, quotas, minimum supported versions, abuse suspension and audited revocation. No default private file browser, raw path viewer or user impersonation. This is an application permission boundary, not a claim that a TLS-terminating operator cannot see traffic.

Owner controls include paired devices, active sessions, root summaries, transfer history retention, notification preferences, revoke-all, diagnostic consent and account export/deletion. Deletion revokes access and cleans cloud metadata; it never deletes the owner's PC files. Document any legally/operationally retained minimal audit data rather than promising instant removal of every backup.

Enforce application quotas for streams, queued requests, archive preparation, upload sizes and relay bytes. Monitor compute, network egress, database operations and CI costs. Alert-only billing budgets are not automatic caps; check current service-specific spend-cap support separately [S25]. Never provision billing without permission.

**Tests and exit gate:** ordinary users cannot call admin APIs; role changes and revocation are enforced. Account deletion and quota recovery have integration tests. Dashboards display genuine metrics, not fictional charts.

### W11 - Security, visual quality and reliability hardening

**Dependencies:** W0-W10 + D11. **Requirements:** R02, R03, R34, R38, R42, R45-R48, R51, R52, R59.

Run API/authorization, session/QR replay, XSS, CSRF where cookie credentials apply, CORS/origin, token leak, capability guessing, unsafe preview, upload and abuse tests. Add a restrictive deploy-compatible CSP, security headers, controlled dependencies and secret scanning. An 'allow all' development configuration cannot ship.

Test slow/unstable networks, gateway restarts, background tabs, concurrent transfers, source changes, offline devices, notification denial and stale service workers. Bounded staging soak/load jobs use safe synthetic fixtures and resource quotas, not private files. All retry queues and caches need limits and cleanup.

Run Chromium/Firefox/WebKit Playwright tests, mobile/tablet/desktop screenshots and accessibility checks. Mobile emulation does not certify a real phone [S19]. Follow the shared visual rubric, targeting 9.5/10 as a design judgement with evidence. Correct real defects; do not inflate scores or automatically accept altered screenshot baselines.

Keep core state/transport independent of animation. Inspect busy, offline, empty, error, long-name, large-text and translated-content resilience, not just a polished empty desktop. Pin performance budgets from reproducible fixtures and report results honestly.

**Tests and exit gate:** no unresolved critical/high security failure, data corruption, blocking UX problem or unbounded transfer memory. All required jobs have actual run evidence. Remaining real-device/signing/configuration blockers are separately listed and cannot be called passed.

### W12 - Firebase-ready release and deployment handoff

**Dependencies:** W11 + D12. **Requirements:** R40, R48-R50, R57, R58.

Prepare separate development/staging/production Firebase projects and environment templates, but do not create/deploy paid resources without approval. Project IDs and domain names remain configurable; do not pretend proposed names are available. Reuse the single repository, not a separate admin repo.

Provide configuration/checklists for Hosting build output and SPA routing; correct static asset/cache headers; service-worker update policy; authorized auth domains/providers; email verification/reset; Firestore deny-by-default rules/indexes; gateway IAM/credentials; current FCM setup; relay TLS/DNS; explicit CORS/CSP origins; health/readiness; quotas; backups/retention and logs. Client VITE-prefixed values are public; service-account/private signing secrets must never enter the web bundle.

Never route large downloads through Hosting rewrites [S01]. The browser calls the dedicated gateway/transfer origin. Configure exact preview origins rather than permitting arbitrary unrelated hosted sites. A Hosting preview channel is not a separate identity/database environment by itself; pair previews with staging resources [S22].

Create gated CI deployment commands using short-lived credentials where supported [S23]. Prepare installer/gateway/web version compatibility, rollback and incident runbooks. Test rollback on staging, including metadata migrations and reconnect behavior. Maintenance mode must not silently corrupt active transfers.

Deliver build artifacts, checksums, test/evidence index, truthful support matrix, deployment prerequisites, source links and a step-by-step owner handoff. The owner explicitly deploys the web version later. A ready-to-deploy bundle is not a live verified deployment.

**Tests and exit gate:** full acceptance checklist passes for claimed platforms; secret scan and production-config validation pass. Actual staging smoke tests are labelled with environment/run IDs. Missing real hardware, certificate or production configuration is clearly reported, with no fictitious URL or successful deployment claim.

### Extension phases - scoped after the required release candidate

**Shared requirement: R60.** These are planned extensions, not unfinished mandatory release phases.

**E-W1 Native Android integration:** shared device/invitation protocol, background-transfer capabilities, opt-in receive rules, durable notifications and supported storage permissions. Browser behavior remains independent.

**E-W2 Direct/P2P transfer:** WebRTC signaling, TURN credentials, transport selection, failure recovery and bounded browser receive pipeline. Measure relay savings; P2P is not guaranteed [S24].

**E-W3 E2EE mode:** authenticated recipient keys, verification, rotation, encrypted chunks/resume and an explicit supported save-mode matrix. Audit before claiming relay confidentiality.

**E-W4 Public expiring share links:** separate guest capability, single-file scope, expiry/password/optional count semantics, online-PC warning, abuse controls and revocation. Define whether counts refer to starts or completed streams; never expose a directory by accident.

**E-W5 Offline pocket/cloud staging:** deliberate file selection, storage quota, encryption, retention/deletion, local-cache visibility and cost controls. Offline availability requires an actual saved copy; never imply otherwise.

**E-W6 Advanced workspace:** safely authorized write operations, local semantic search, isolated conversion, phone backup scheduling, activity timelines and optional wake infrastructure. Each gets a separate threat model, acceptance tests and explicit permission surface.

---

## Document 4: 03_REQUIREMENTS_TESTS_AND_RELEASE_GATES.md

## OrbitDrive - requirements, tests and release gates

Repository: https://github.com/nisar87/OrbitDrive
Version 1.0 | 17 November 2026

This is a planned acceptance matrix. No item below is claimed implemented or passed. Claude must add concrete test IDs, commit hashes, completed workflow-run references and evidence as implementation proceeds. A phase is not complete merely because a checkbox is ticked.

### 1. Requirement traceability

| ID | Required outcome | Phase ownership | Minimum proof |
|---|---|---|---|
| R01 | One repository, shared contracts, reproducible toolchain | D0/W0 | Clean checkout; C#/TS schema compatibility and drift check |
| R02 | Implement/test/fix/advance loop in GitHub | All phases | Completed jobs for the exact phase commit; reviewed artifacts |
| R03 | Premium Windows-inspired visuals, not a generic dashboard | D1/W4/W11 | Actual screenshots, rubric and functional controls |
| R04 | Verified account login/reset/logout | D2/W1 | Positive and invalid/revoked/wrong-project auth tests |
| R05 | Expiring, one-use QR with PC approval | D2/W2 | Real pairing plus expiry/reuse/race denial |
| R06 | Remembered, revocable browser trust | D2/W1/W2 | Restart persistence; unapproved browser denied; revoke works |
| R07 | Protected persistent PC identity | D0/D2/W2 | Safe storage, key/credential rotation, no leaked secrets |
| R08 | Explicit owner-selected roots/drives | D4/W4 | No automatic root; unauthorized roots unavailable |
| R09 | Robust Windows path/handle authorization | D4/W11 | Traversal/reparse/hard-link/race corpus denied |
| R10 | Read-only defaults; separate write enablement | D4/D9/W9 | Every unauthorized write path rejected |
| R11 | Tray, startup, quit and session lifecycle | D1/D3 | Standard-user lifecycle and no orphan worker |
| R12 | Different-network access without inbound router ports | D3/W3 | External integration and real mobile-data trial |
| R13 | Reconnection and retry without duplicate work | D3/W3/W11 | Forced disconnect/restart, idempotency and bounded retry |
| R14 | Accurate online/offline/paused presence | D1/D3/W3 | Lease expiry and stale-connection tests |
| R15 | Real authorized filesystem browsing | D4/W4 | Listing compared with actual test folder |
| R16 | Views, sorting, selection and contextual actions | D4/W4 | Mouse/keyboard/touch interaction tests |
| R17 | Breadcrumbs, navigation and accurate properties | D4/W4 | Unicode/long paths, back/up and real metadata checks |
| R18 | Large files with bounded memory/backpressure | D5/W5 | 1-GiB transfer; memory profile under slow receiver |
| R19 | Safe resume where supported | D5/W5 | Interrupt/resume; changed revision cannot be appended |
| R20 | File integrity and truthful completion | D5/W5 | Hash match; distinguish sent versus saved |
| R21 | Scoped download authorization | D5/W1/W5 | Cross-user/revoked/expired/stolen-scope attempts denied |
| R22 | Opt-in notifications with fallback | W6 | Real foreground/background tests; denied permission works |
| R23 | Real modern Windows 11 Explorer command | D0/D6/D10 | Installed native menu invoked on Windows 11 |
| R24 | Choose a particular paired receiver | D6/W6 | Correct receiver; no delivery to another browser |
| R25 | Durable Inbox and acceptance states | W6 | Refresh/reconnect, accept/decline/expire/cancel |
| R26 | Multiple files/folders with explicit support state | D6/D8/W8 | Correct selection and authorized archive contents |
| R27 | Offline PC/phone and pending invitation behavior | D3/D6/W3/W6 | No fictitious cloud copy; recoverable waiting state |
| R28 | Safe images/text/PDF previews | D7/W7 | Corrupt/hostile/oversized fixtures and isolated rendering |
| R29 | Supported media streaming with fallback | D7/W7 | Tested codec/range matrix; unsupported format fallback |
| R30 | Folder ZIP with clear resume/staging policy | D8/W8 | ZIP extraction comparison, cancellation and disk limits |
| R31 | Permission-aware filename search/favorites | D8/W8 | Revoked items excluded; stale entries handled |
| R32 | Multiple PCs without identity confusion | D8/W2/W8 | Same filenames/paths across PCs cannot cross-authorize |
| R33 | Touch-first portrait and desktop-like landscape | W4/W8 | Real phone plus responsive/gesture tests |
| R34 | Keyboard, focus, contrast, zoom and reduced motion | D1/W4/W7/W11 | Accessibility checks and manual keyboard review |
| R35 | Installable PWA with safe lifecycle | W6 | Manifest/install/update tests on supported platforms |
| R36 | Opt-in mobile uploads with atomic commit | D9/W9 | Checksum, partial cleanup, collision and disk-full tests |
| R37 | Server-enforced operator administration | W1/W10 | User cannot self-promote or access admin endpoints |
| R38 | Minimal/redacted metadata and privacy controls | D1/D7/D11/W10 | Logs/cache/support bundle inspected for sensitive data |
| R39 | Accurate TLS versus E2EE claims | D5/W0/W3/W5 | Architecture and UI/privacy text agree |
| R40 | Firebase-ready static web and separate relay | W0/W12 | Build/config validation; no long-transfer Hosting rewrite |
| R41 | Durable metadata, crash and restart recovery | D3/W3 | Gateway restart reconciles rather than invents state |
| R42 | Cost/resource/concurrency limits | D5/D11/W3/W10 | Quota/abuse tests; no unbounded buffers or DB writes |
| R43 | Install, repair, upgrade and uninstall | D10 | Clean and upgraded systems; no user files deleted |
| R44 | Signed, verified updates and recovery | D10 | Tampered update rejected; interrupted update recovers |
| R45 | Measured performance and bounded caches | D5/D8/D11/W11 | Reproducible metrics, large-list and transfer profiles |
| R46 | Useful diagnostics and operational health | D11/W3/W10 | Redaction, rotation, correlation and health checks |
| R47 | Security regression suite | D9/D11/W11 | No unresolved exploitable critical/high defects |
| R48 | Honest CI versus physical-device evidence | D0/D12/W0/W12 | Runner/browser versions and hardware evidence distinguished |
| R49 | Release/operation/troubleshooting handoff | D12/W12 | Complete artifact/evidence/config inventory |
| R50 | Protocol compatibility and upgrade behavior | D0/D3/D10/W0 | Old/new capability and minimum-version tests |
| R51 | Logout/revoke/account removal actually cuts access | D2/D11/W1/W10 | Active streams/offers/caches become unavailable |
| R52 | No unintended service-worker/private content cache | W6/W11 | Account switch/logout and cache inspection |
| R53 | Anti-phishing and replay-safe cross-device approval | D2/W2 | Wrong-origin/account/substituted request denied |
| R54 | Helpful empty/offline/error/permission states | D1-D12/W1-W12 | Adverse-state screenshots and recovery tests |
| R55 | One-shot send outside roots does not expose parents | D6/W6 | Only selected revision/receiver authorized |
| R56 | Locked, removable, placeholder and protected files | D4/D5 | Explicit supported/denied behavior without elevation |
| R57 | Separate dev/staging/prod and safe previews | W0/W12 | Preview cannot use production grants/data accidentally |
| R58 | No unapproved spending or production deployment | D12/W12 | Protected workflows; no implicit live deployment |
| R59 | Correct HTTP range/session semantics | D5/W5/W11 | HEAD, full/range, If-Range, 206/416 and expiry tests |
| R60 | Advanced features remain explicitly scoped | Extension phases | Each has permissions, threat model and acceptance gate |

### 2. Required GitHub workflow families

These are proposed workflow names to implement, not existing workflows.

| Workflow | Environment | Responsibility |
|---|---|---|
| contracts.yml | Linux | Schema generation, breaking-change/serialization checks |
| desktop.yml | Pinned Windows x64 image | Restore/build/analyzers/unit/filesystem/IPC/package validation |
| desktop-ui.yml | Interactive Windows where supported | Actual WPF UI Automation, screenshots, focus/DPI checks |
| web.yml | Linux with Playwright | Type/lint/unit/component and Chromium/Firefox/WebKit tests |
| gateway.yml | Linux + emulator dependencies | Auth, ownership, invitations, streaming/ranges, quotas |
| integration.yml | Windows agent + gateway + browser | Real component handoffs, checksum and fault-injection tests |
| security.yml | Appropriate OS matrix | Secrets/dependency/static analysis, adversarial regression |
| nightly-soak.yml | Trusted/manual/scheduled environment | Large files, long reconnect/latency/load tests with limits |
| release-candidate.yml | Protected environment | Signed packages, provenance/checksums, compatibility and evidence bundle |
| deploy-staging.yml | Explicit authorized environment | Staging only; credential and spending gates |
| deploy-production.yml | Explicit protected approval | Prepared for owner use; never automatic from ordinary phase commits |

Pin actions/toolchain versions and record runner image details. Use path filters carefully: changing shared contracts must trigger both client suites. Required status checks must not become permanently pending because a filtered workflow never starts. Set timeouts, concurrency controls and artifact retention. Do not upload private test files or live tokens.

Untrusted pull requests cannot receive deployment/signing secrets or privileged self-hosted runners. GitHub-hosted Windows elevated defaults are not evidence of least-privilege compatibility [S18]. Test standard-user behavior separately. Never emulate a WPF screenshot using a browser-rendered mock.

Use Firebase Auth/Firestore emulators for repeatable CI [S21]. Live identity configuration, FCM delivery and genuine mobile saves require separate tests. A local workflow simulator is not an actual GitHub Actions run.

Suggested inspection commands after workflows exist:

```sh
gh run list --repo nisar87/OrbitDrive --branch feature/orbitdrive-foundation
gh run view <RUN_ID> --repo nisar87/OrbitDrive
gh run watch <RUN_ID> --repo nisar87/OrbitDrive --exit-status
gh run download <RUN_ID> --repo nisar87/OrbitDrive
```

Use the real run IDs returned by GitHub. Compare each run's head SHA with the phase commit. Never infer success from workflow YAML alone.

### 3. Test data and difficult cases

Generate deterministic fixtures during tests rather than committing multi-gigabyte files. Include zero-byte, tiny, 32-MiB, 1-GiB and scheduled 5-GiB streams; Unicode/Urdu/emoji/RTL names; reserved/long names; deep trees; 10,000-entry directories; same names across PCs; locked files; removable roots; cloud placeholders; reparse/hard-link fixtures; malformed images/PDFs and harmless XSS test strings.

Compare source and received SHA-256 where bytes are observable. Validate file size and content, not just HTTP 200 or a green progress bar. Never read private personal files for automated tests.

Exercise expired/reused QR, substituted account/device, revoked browser, suspended owner, expired download capability, preliminary HEAD, repeated/suffix/invalid ranges, source mutation, stale versions and duplicate requests. Confirm unauthenticated/other-user requests cannot list, preview, stream, search, archive or upload.

Network tests include slow receiver, dropped connection, latency, gateway restart, agent restart, Wi-Fi/mobile transition, background tab, locked screen and source-PC sleep. Distinguish simulated faults from actual network/hardware evidence.

### 4. Browser and operating-system evidence matrix

| Target | Automated evidence | Additional evidence required for a support claim |
|---|---|---|
| Windows 11 x64 agent/Explorer | Core and packaging tests; UI where available | Real Explorer modern menu, standard user, install/upgrade, lock/sleep |
| Supported Windows 10 edition | Compatibility build/tests if pursued | Exact edition/runtime support, classic-menu and standard-user checks |
| Android Chrome | Chromium mobile viewport and API tests | Actual phone QR, native download, notifications and network switch |
| Android Samsung Internet | Shared web tests plus capability handling | Actual Samsung phone browser testing; do not infer from Chrome |
| iPhone Safari/Home Screen app | WebKit emulation | Actual install/push/QR/save/background tests for advertised behavior |
| Desktop Chrome/Edge | Chromium tests | Large download and keyboard smoke test |
| Desktop Firefox | Firefox tests | Native download/fallback behavior |
| iPad/tablet | Responsive layouts | Touch/keyboard/safe-area checks where advertised |

Playwright emulates aspects such as viewport and touch; it is not a physical mobile OS [S19]. Do not make a broad support claim for untested devices. The first release can explicitly support tested Android behavior while marking iOS gaps, rather than pretending all platforms passed.

### 5. Performance budgets and release invariants

Set numerical performance targets in phase 0 based on reproducible hardware, then track regressions. Suggested engineering targets, not measured facts: stable idle CPU/RAM; no transfer-size-proportional memory growth; bounded per-stream buffers; cancellation within a few seconds under the test network; responsive paged listing of 10,000 entries; no unlimited thumbnail/temp-file growth.

Agree the actual buffer/concurrency budgets before implementation. For example, a relay may target at most several MiB of additional buffering per active stream, separate from runtime baseline memory. Measure with a throttled receiver. Never confuse low memory on a tiny file with streaming proof.

Hard release invariants: no cross-owner access, no path escape, no changed-revision resume corruption, no unauthorized write, no plaintext account credentials in URLs/logs, no hidden private cache, and no false saved-on-phone confirmation. Security/correctness cannot be traded away for a higher design score.

### 6. Visual quality gate

Target 9.5/10 (95/100) as a documented design judgement, not a mathematically proven fact. Score actual rendered application screenshots across states:

| Dimension | Weight |
|---|---:|
| Composition, spacing and window coherence | 20 |
| Typography, density and legibility | 15 |
| Mobile touch ergonomics/responsiveness | 20 |
| Familiar desktop interactions and visual hierarchy | 15 |
| Complete empty/loading/error/busy states | 15 |
| Accessibility, contrast, focus and reduced motion | 15 |

Iterate build -> capture -> inspect -> identify defects -> fix -> recapture. Store examples of defects and their corrections. Do not automatically approve changed screenshot baselines. A reviewer should explain the score and weaknesses; owner review or independent review remains useful. If quality remains below target, record it as incomplete rather than endlessly raising the score without changing the UI.

Mandatory views: login, pairing, initial empty workspace, Explorer icon/details views, long paths, multi-selection, context menu, properties, offline PC, interrupted transfer, Inbox, upload permission failure, Devices, Settings and protected admin. Include portrait/landscape, light/dark and large-text scenarios. Desktop screenshots must come from the actual WPF app.

### 7. Final acceptance demonstration

1. On a clean standard-user Windows 11 machine, install the candidate and start the agent. Select a test folder; confirm unrelated folders are not exposed.
2. On a real phone, open the web app, sign in, scan the expiring QR and confirm on the PC. Restart both sides and verify the approved registration persists without rescanning.
3. Turn off phone Wi-Fi, use mobile data and browse actual files, Unicode names, properties and nested folders. Confirm the PC only initiates outbound connectivity.
4. Download a generated 1-GiB file. Check integrity using an available device-supported method or an explicit controlled transfer of the received test artifact for hashing; do not claim a hash was checked when only the UI completed.
5. Interrupt a supported managed/native resume path, reconnect and verify correct continuation or the documented safe restart. Change the source and prove stale bytes cannot be appended.
6. In real Windows Explorer right-click a file and choose the paired receiver. Verify Inbox and permitted notification. Accept/download. Repeat outside a permanent shared root and prove no parent directory was exposed.
7. Exercise offline phone and offline PC cases, invitation expiry, folder ZIP, previews, search and multiple-PC identity separation.
8. Enable upload for one destination and verify a phone file commits intact. Prove another read-only root cannot be written and an interrupted upload leaves no completed corrupt file.
9. Revoke the phone during a transfer. Verify access stops, future requests fail and reconnect does not restore the revoked grant.
10. Exercise update/rollback/uninstall and confirm personal source files remain untouched. Review actual evidence and list anything unverified.

No release is called complete with missing critical gates. Physical-device limitations may be recorded as support limitations only when the advertised release scope is correspondingly restricted and approved.

### 8. Evidence record and continuation state

For every phase record: phase ID, status, owner component, requirement IDs, exact commit SHA, local commands/results, GitHub run IDs/URLs, test counts including skipped tests, screenshots/traces, performance measurements, security findings, fixes, manual-device outcomes and remaining blockers.

Use statuses such as NOT_STARTED, IN_PROGRESS, AUTOMATED_VERIFIED, AWAITING_DEVICE_TEST, COMPLETE and BLOCKED. AUTOMATED_VERIFIED is not COMPLETE when a required hardware test remains. Fix defects with a regression test and fresh run evidence.

Maintain `docs/IMPLEMENTATION_STATE.md`: last verified commit, current phase, next dependency-ready work, failed jobs, unresolved blockers, exact environment/tool versions, needed owner actions and continuation commands. Continue independent work without repeated approval requests, but never conceal a blocker or claim work will run in the background without a real execution mechanism.

### 9. Operational incident checks

Document and test emergency remote-access pause, account/session revocation, compromised signing credential response, vulnerable version block, gateway rollback, TLS certificate renewal, relay exhaustion, database unavailability, billing/quota alerts and service-worker rollback. Back up necessary configuration/metadata, not secretly copied personal files. A restored database must not revive revoked device grants without deliberate reconciliation.

### 10. Proposed API boundary to finalize in phase 0

These routes are a starting contract, not implemented endpoints. Claude may refine names but must preserve ownership and trust boundaries, generate bindings and version breaking changes.

| Operation | Suggested route | Required boundary |
|---|---|---|
| Request pairing | POST /v1/pairing-requests | Rate-limited device bootstrap; no file permission yet |
| Claim pairing | POST /v1/pairing-requests/{id}/claim | Authenticated account and intended receiver binding |
| Confirm pairing | POST /v1/pairing-requests/{id}/confirm | Correct PC proof plus explicit local approval |
| List own PCs/roots | GET /v1/pcs; GET /v1/pcs/{id}/roots | Current owner and approved browser grant |
| List directory | POST /v1/pcs/{id}/directory-queries | Opaque root/entry, pagination and local agent recheck |
| Offer a file | POST /v1/transfer-invitations | Authenticated agent and explicit scoped local action |
| Read/accept Inbox | GET /v1/inbox; POST /v1/invitations/{id}/accept | Correct approved recipient; atomic state transition |
| Prepare download | POST /v1/download-sessions | Current scoped read grant, revision and quota |
| Download/HEAD/Range | GET or HEAD /v1/downloads/{capability} | Narrow expiring bearer capability; redacted access logs |
| Agent uplink | WSS /v1/agent-streams | One-use scoped rendezvous authorization; binary bounds |
| Prepare/commit upload | POST /v1/upload-sessions; POST /v1/upload-sessions/{id}/commit | Explicit destination write grant; complete verified content |
| Revoke browser/PC | DELETE /v1/browser-devices/{id}; DELETE /v1/pcs/{id} | Owner plus appropriate step-up; terminate related access |
| Operator overview | GET /v1/admin/health-summary | Server-enforced operator role; no private file browsing |

Define shared errors, deadlines, idempotency keys, capability lifetime, range/session reuse, cancellation and version headers. Test websocket Origin where relevant, body limits and credential renewal. Authentication on one control channel does not automatically authorize a separate byte-stream connection.

---

## Document 5: 04_RESEARCH_AND_ARCHITECTURE_NOTES.md

## OrbitDrive - research findings and architecture decisions

Repository: https://github.com/nisar87/OrbitDrive
Research checked: 17 November 2026

These notes distinguish verified platform facts from proposed OrbitDrive engineering decisions. Sources were reviewed on the research date. Dependency versions, supported OS editions, cloud pricing and SDK details must be rechecked during implementation. Research is not an external security audit, a trademark clearance or an executed product test.

### 1. Feasibility and product differentiation

The proposed experience is technically feasible: a web-built desktop interface, a least-privileged Windows filesystem agent, outbound connectivity, explicit pairing, browser downloads and native shell integration. It is not necessary to stream the entire real Windows desktop to make a familiar workspace.

Remote file access and device-to-device transfer are not new categories. Taildrop sends files between a user's devices and requires Tailscale on both ends [S26]. PairDrop already offers browser transfers, persistent pairing and internet connectivity [S27]. MeshCentral combines agent-based browser management with file-management capability [S28].

OrbitDrive's proposed differentiation is the combination of a polished Windows-like mobile workspace, unattended access to explicitly shared PC folders while the agent is running, a focused send-to-phone menu, and low-configuration owner-controlled access. This is a product positioning proposal, not a claim that no competitor has a similar feature.

Do not indiscriminately copy another project's code/assets or inherit its full architecture. Review dependencies and licenses before reuse. The goal is a focused file product, not a general remote-administration suite.

### 2. Firebase Hosting is the interface host, not the complete data plane

Firebase documents a 60-second timeout for Hosting requests routed to dynamic backends [S01]. Therefore large transfers must use a separate relay origin. This does not prevent the website itself from being deployed to Firebase Hosting.

Recommended initial arrangement: Firebase static hosting/authentication/control metadata/notifications, plus one always-on .NET gateway container on a small Linux VM/VPS. The deployment provider, region and budget remain owner decisions. A VM in Google Cloud is an option; a separate repository is not necessary.

Cloud Run supports WebSockets but imposes request-lifetime/reconnection constraints and best-effort affinity; open WebSockets also affect billing [S02]. For this design, cloud autoscaling adds a routing problem: an agent uplink and browser download must reach matching stream state. Firestore metadata or a signaling backplane does not itself carry that stream. Start with one relay instance and explicit restart recovery; introduce distributed stream routing only after measurement justifies it.

No hosting design makes an offline PC's unuploaded files available. Optional cloud staging would be a separate storage feature with explicit consent, retention, encryption and cost implications.

### 3. Browser receiving: reliable baseline versus enhanced capabilities

The baseline is a user-initiated native browser download over authenticated/scoped HTTP, with proper attachment headers and a streaming server. Enhanced direct-to-file APIs require capability detection and user permission [S05]. Never buffer an entire multi-gigabyte file into a Blob merely to show a custom progress UI.

A service worker is not an indefinitely running background process. Browser Background Fetch is a specialized capability, not universal proof of reliable silent receiving [S06]. Test the exact mobile browsers and document what happens when a page closes, the screen locks or the network changes. A native Android client is the more suitable future direction for stronger background receiving, but mobile OS restrictions still apply.

FCM requires compatible browser/service-worker/HTTPS setup [S03]. Apple's documented web-push model includes Home Screen web apps and user-triggered permission [S04]. The app must still work without notifications. A notification invitation does not mean the file has arrived; browser/network completion does not always expose final OS save completion.

### 4. Windows integration and supported platforms

Modern Explorer integration is a native IExplorerCommand/app-identity feature, not simply adding an old registry verb [S07]. Keep the extension minimal and delegate work out of Explorer. Prove the real menu early because packaging and architecture are part of the feature.

.NET 10 remains an actively supported LTS choice on the research date; it is selected for a conservative baseline, not because it is the newest major release [S08]. Recheck patched SDKs and the actual Windows support matrix [S29]. A build that runs on a Windows CI server does not automatically establish support for every desktop edition.

### 5. Pairing and filesystem access are security boundaries

Use device/cross-device authorization standards and current threat guidance to inform pairing [S12, S13]. Short expiry alone is insufficient. Bind the account, PC identity, request and receiver; require explicit PC confirmation; prevent replay and rate-limit guessing. The plans do not claim Firebase directly supplies the entire custom PC-pairing flow or that a partial implementation is automatically RFC-compliant.

The gateway verifies account tokens and current device permissions [S09, S10]. The agent independently limits access to local approved roots. Firestore server SDK access bypasses Security Rules, so backend ownership checks cannot be delegated to rules alone [S11].

Windows links, volume changes and opened-handle identity require more than string-prefix path checks. Microsoft's final-path API and reparse-point documentation inform the local policy design [S15, S16]. DPAPI provides a Windows facility for protected local data, but does not turn a compromised logged-in user session into a trusted environment [S17]. Security review and adversarial tests remain necessary.

### 6. Resume and confidentiality require precise claims

HTTP defines byte ranges and conditional validators [S14]. OrbitDrive must preserve source identity across resume, implement the required responses and handle changed files safely. A source timestamp/length tuple must not be casually labelled a strong content validator.

Resumable protocol support and a particular mobile browser resuming a native download are different promises. Likewise, an expiring invitation and a reusable-within-limits download session are different objects. HEAD, retry and range requests must not consume the only valid credential accidentally.

The initial TLS-terminating relay can see plaintext. Keeping payloads out of permanent storage is valuable but not E2EE. End-to-end confidentiality requires additional authenticated endpoint keys and encrypted-transfer design. It may also change the practical browser receive/save pipeline. Public marketing must match the implemented transport, not the future roadmap.

WebRTC can reduce relay traffic where direct connectivity works; TURN remains a necessary fallback consideration [S24]. Direct transfers are a later optimization, not an excuse to omit a reliable first-release transport.

### 7. Cloud tests and actual device evidence

GitHub provides multiple Windows images/architectures, and its hosted Windows machines use elevated defaults [S18]. Choose a pinned supported image, and supplement it with real Windows 11/standard-user shell testing. UI Automation through FlaUI is a candidate for WPF testing, subject to actual session capability [S20].

Playwright is appropriate for browser interaction and responsive emulation [S19], but real Android/iOS notification, file-save and background behaviors still need phone evidence. Emulators make Firebase tests repeatable [S21]; they do not certify production identity or delivered notifications.

Deploy previews and production deliberately. Hosting supports GitHub workflows [S22]; use isolated backend environments, not merely different frontend URLs. Prefer short-lived workload credentials instead of permanent service-account keys where supported [S23].

### 8. Cost and operations model

Do not promise the product will cost nothing because the frontend is static. The operating model includes relay compute, internet egress, database operations, logs, optional storage, certificates/signing and CI usage. A source PC's upload capacity is also a throughput limit.

Illustrative arithmetic, not a price quote: 200 downloads of a 1-GB file produce about 200 GB of relay-to-client payload before retries, thumbnails and protocol overhead. Each originating PC must upload its corresponding bytes. Actual provider charges depend on region, tier and current pricing.

Set admission/concurrency limits, per-account relay-byte quotas, archive/temp-space quotas and abuse controls. Avoid persisting every progress event. Alert-only budgets monitor expenditure; check current spend-cap availability for the actual services rather than assuming an alert is a hard stop [S25]. Billing choices and resource creation require owner authorization.

Prepare incident procedures for revoked credentials, harmful updates, exhausted relay capacity, certificate expiry, stale service workers and broken migrations. Recovery must not restore revoked grants accidentally. A future self-hosted mode may reduce dependency on a shared service, but does not eliminate infrastructure work.

### 9. Decisions deliberately left for phase-0 evidence

The exact signed installer route, browser managed-save capability matrix, supported Windows 10 editions, selected VPS/region, public domain names, signing identity and numerical performance budgets must be decided using the actual development/deployment environment. These are explicit evidence-driven choices, not forgotten requirements.

Use the fixed recommended stack unless a capability spike demonstrates a real blocker. Do not spend weeks comparing every frontend framework or add a distributed architecture before the first real remote file works.

### 10. Primary sources

All references below are primary project/vendor/standards documentation. Short descriptions explain why each was used; the implementation plans are original engineering recommendations, not quotations from these sources.

#### S01 - Firebase Hosting and dynamic backends
https://firebase.google.com/docs/hosting/serverless-overview
Relevant fact: Hosting request timeout and separation from longer-running backend services.

#### S02 - Cloud Run WebSockets
https://docs.cloud.google.com/run/docs/triggering/websockets
Relevant facts: request lifetime, reconnection, instance affinity and active-connection billing.

#### S03 - Firebase Cloud Messaging for web
https://firebase.google.com/docs/cloud-messaging/web/get-started
Relevant facts: supported web setup, HTTPS/service-worker requirements and current registration guidance.

#### S04 - Apple WebKit web push
https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/
Relevant facts: Home Screen app delivery model and permission requirements.

#### S05 - Chrome File System Access API
https://developer.chrome.com/docs/capabilities/web-apis/file-system-access
Relevant facts: permission/user-gesture model and capability detection for file access.

#### S06 - Chrome Background Fetch
https://developer.chrome.com/blog/background-fetch/
Relevant facts: long background work differs from ordinary service-worker fetch handling.

#### S07 - Microsoft modern Explorer context-menu integration
https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer
Relevant facts: IExplorerCommand, native integration, app identity and keeping shell code responsive.

#### S08 - Official .NET support policy
https://dotnet.microsoft.com/en-us/platform/support/policy
Relevant fact: .NET 10 LTS is a supported baseline; patches/toolchains must remain current.

#### S09 - Firebase ID-token verification
https://firebase.google.com/docs/auth/admin/verify-id-tokens
Relevant fact: backend verification of authenticated client identity.

#### S10 - Firebase session management
https://firebase.google.com/docs/auth/admin/manage-sessions
Relevant facts: token/session invalidation and revocation handling.

#### S11 - Firestore Security Rules conditions
https://firebase.google.com/docs/firestore/security/rules-conditions
Relevant fact: server client libraries bypass the client rules layer.

#### S12 - RFC 8628: OAuth 2.0 Device Authorization Grant
https://www.rfc-editor.org/rfc/rfc8628
Relevant guidance: device authorization patterns and user confirmation concerns.

#### S13 - RFC 10027: security of cross-device flows
https://www.rfc-editor.org/rfc/rfc10027
Relevant guidance: current cross-device threat analysis; published August 2026.

#### S14 - RFC 9110: HTTP Semantics
https://www.rfc-editor.org/rfc/rfc9110
Relevant guidance: range requests, representations and conditional validators.

#### S15 - GetFinalPathNameByHandleW
https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
Relevant API: inspect the final path associated with an opened handle.

#### S16 - Windows reparse points
https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points
Relevant behavior: redirected filesystem resolution must be considered in authorization.

#### S17 - .NET ProtectedData / Windows data protection
https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
Relevant facility: protecting local credential data under Windows.

#### S18 - GitHub-hosted runner reference
https://docs.github.com/en/actions/reference/runners/github-hosted-runners
Relevant facts: available images/architectures, changing labels and administrative defaults.

#### S19 - Playwright emulation
https://playwright.dev/docs/emulation
Relevant scope: viewport, browser characteristics and touch emulation, not full physical hardware.

#### S20 - FlaUI project
https://github.com/FlaUI/FlaUI
Relevant capability: Windows UI Automation support for testing real desktop applications.

#### S21 - Firebase Local Emulator Suite
https://firebase.google.com/docs/emulator-suite/install_and_configure
Relevant capability: repeatable development/integration testing without production data.

#### S22 - Firebase Hosting GitHub integration
https://firebase.google.com/docs/hosting/github-integration
Relevant capability: preview/live workflow integration, to be configured with environment controls.

#### S23 - Google GitHub Actions authentication
https://github.com/google-github-actions/auth
Relevant capability: Workload Identity Federation and short-lived deployment authentication.

#### S24 - WebRTC TURN guidance
https://webrtc.org/getting-started/turn-server
Relevant fact: some connections require relaying rather than direct peer connectivity.

#### S25 - Google Cloud budgets
https://docs.cloud.google.com/billing/docs/how-to/budgets
Relevant distinction: alert-only budgets versus supported service-specific spend-cap options.

#### S26 - Tailscale Taildrop
https://tailscale.com/docs/features/taildrop
Relevant comparison: personal-device file transfer with Tailscale running at both ends.

#### S27 - PairDrop project
https://github.com/schlagmichdoch/PairDrop
Relevant comparison: browser transfers, persistent pairing, QR codes and internet connectivity.

#### S28 - MeshCentral project
https://github.com/Ylianst/MeshCentral
Relevant comparison: agent-based web management with remote file-management functionality.

#### S29 - .NET 10 supported operating systems
https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md
Relevant constraint: support is specific to OS versions/editions and architecture.

#### S30 - Target repository
https://github.com/nisar87/OrbitDrive
Observed on research date: accessible, empty repository. Reinspect before coding; no code audit was possible or claimed.
