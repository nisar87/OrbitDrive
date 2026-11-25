# Completion audit against the master document (2026-11-24)

Every requirement (R01 to R60) was checked against the code, tests and CI. This page separates **what is built and proven by automation** from **what only a person with a device, a certificate or a decision can finish**. Nothing buildable is left in the second group.

Labels used honestly: CI runs on GitHub-hosted **Windows Server 2025 and Ubuntu**, in emulated browsers. That is never presented as Windows 11, a physical phone, or a real network.

## A. Built and proven by automated tests (no further work possible without a device)

Pairing and identity (R04-R07, R53; PC key rotation added today), read-only browsing and listing (R08-R10, R16-R18 incl. back/forward/properties/sort/icons), downloads, ranges, resume and capabilities (R19-R22, R59), uploads into opted-in folders (R24, R55), archives and search (R26, R30), previews and thumbnails (R27-R29, console toggle exists), multi-PC (R32, incl. isolation test), connectivity, leases, idempotency and fault tolerance (R12-R14, R41 single-writer lease), revocation (R51), admin and cost limits (R37, R42), logs, diagnostics and support bundle (R38, R46 incl. request ids, host log digest and daily review), updates (R44, signed feed, rollback), release gates (R48, R49, R57, R58), security fuzzing (R47 incl. archive writer), PWA and caching (R35, R52), console and tray (R03, R11, R54, R56).

## B. Built, but the final proof needs a real device or environment (owner-side)

| Area | Why automation cannot finish it |
|---|---|
| Windows 11 look and behaviour (R03, R45): Mica, DPI scaling, high contrast, lock/unlock, sleep/resume, toast and tray flyout, real Explorer "Send to" and modern menu (R23) | Needs a Windows 11 machine; CI is Windows Server |
| Real phones (R05, R19-R21, R33, R35): camera QR scan, 1 GiB save to file, PWA install, Safari/Firefox scanner, mobile data | Needs physical phones |
| Standard-user run and installer (R11, R43) | Needs a Windows 11 machine and a signing identity |
| Push notifications (R25) | Needs the Firebase service key (FCM) |
| Live Firebase identity paths (R37, R51): token revocation at the provider, `auth_time` | Needs the owner's Firebase project credentials |
| Staging and production projects (R50, R57) | Do not exist; creating them is the owner's decision (cost) |
| Real OneDrive, network and removable drives (R09, R56) | None on CI; documented as unsupported or skipped by design |
| Independent security review | A person outside this project |
| Real >4 GiB archive (R26) | Test written; runs only in the manual `nightly-soak` workflow (not yet run) |

## C. Decided, not built (by design, with the reason)

- **E2EE** (plan extension): no design document is written yet (an earlier version of this line cited `docs/design/e2ee.md`, which does not exist). Not built and not claimed anywhere.
- **Several relay instances behind one address** (R41 scale-out): the plan uses one relay VM; instead a single-writer lease stops a second instance from corrupting state.
- **OS-bound (non-exportable) device keys**: rotation is built; hardware binding is not.
- **Extensions** (R60): out of scope by the plan.
- **Server-side favorites sync** (R31): the plan requires permission-aware favorites with stale entries handled, which is built; favorites stay browser-local.

## D. Wording corrected in this pass

R07, R08, R09, R11, R16, R17, R32, R33, R34, R41, R42, R46, R47 and R53 described things as "not built / untested" that exist now or were already built. They are corrected in `REQUIREMENTS_MATRIX.md`.

## E. Bugs found and fixed while closing the list

1. Windows could stay partly off-screen after a tablet rotated or the browser shrank (found by the new tablet/landscape viewport test; fixed, and the test fails without the fix).
2. The first version of the relay health history needed repository write access, which the security workflow forbids; it now uses a workflow artifact.
