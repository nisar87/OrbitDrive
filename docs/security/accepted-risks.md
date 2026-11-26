# Accepted security risks

An entry here is a known, **unfixed** issue that the repository owner has accepted for a limited time. It is not a
fix. Machine-readable source: `security/audit-exceptions.json`; enforcement: `scripts/audit-check.mjs` (run by the
`security` workflow, which prints every accepted advisory as a warning on each run).

## AR-1 — GHSA-fc5q-39xc-vjpf, `picomatch` (ReDoS, CVSS 7.5, CWE-1333) — OPEN, accepted until 2026-12-17

| | |
|---|---|
| Authorised by | Repository owner (nisar87), written instruction of 2026-11-18 (owner local time): evaluate and, only if justified by documented evidence, implement a narrowly scoped temporary exception for this exact advisory |
| Accepted | 2026-11-17 (UTC) |
| Expires | 2026-12-17 (30 days), or earlier — see "Removal" |
| Scope | `apps/web`, package `picomatch` 4.0.7, reached only through `vite`, `vitest`, `tinyglobby`, `fdir`, `@vitejs/plugin-react` (build and test tooling) |
| Status | Not fixed. No release outside the advisory range (`<=4.0.7`) existed on 2026-11-17; `latest` was 4.0.7 |

### What was investigated (2026-11-17)

1. **Is there a fixed version?** No. The npm registry lists 4.0.7 as the newest release and the advisory range
   is `<=4.0.7`. The advisory web page itself could not be opened from the build sandbox (the request needed an
   approval nobody was present to give); title, range, severity, CWE and CVSS come from the advisory data that
   `npm audit` returns. Re-read the page when re-reviewing.
2. **Where does it come from?** `npm ls picomatch --all` in `apps/web`: `vite@8.3.8 → picomatch@4.0.7`,
   `vite → tinyglobby@0.2.17 → picomatch` and `→ fdir@6.5.0 → picomatch` (peer), `vitest@5.1.0 → picomatch`.
   All are `devDependencies`. `tests/integration` does not contain it. `tests/emulators` contains it through
   `firebase-tools → … → node-gyp → tinyglobby`; that directory is emulator tooling used only by tests and was
   already report-only before this advisory (unchanged).
3. **Is it shipped?** No. `npm audit --omit=dev` reports 0. A production build was made and inspected: it is built
   only from `@firebase/app`, `@firebase/auth`, `@firebase/component`, `@firebase/logger`, `@firebase/util`, `idb`,
   `jsqr`, `pdfjs-dist` (added in W7 for PDF preview; Apache-2.0; `npm audit --omit=dev` = 0 on 2026-11-18; run with
   scripting, forms and annotation layers off), `react`, `react-dom`, `scheduler` plus application source, and contains no picomatch code. The gateway,
   agent, console and relay release are .NET artifacts with no npm content. This is now checked on every run by
   `scripts/bundle-check.mjs`.
4. **What triggers it?** Measured with the installed 4.0.7: cost grows steeply with the number of repeated
   extended-glob groups in the **pattern** (10 groups ≈ 1 ms, 26 groups ≈ 150 ms, rising). The patterns this
   project actually supplies (tool defaults and `src/**/*.test.ts`) matched against hostile 20–50 kB strings took
   at most 0.6 ms. So an attacker needs to choose the glob pattern; hostile file names or request paths (matched
   text) are not enough.
5. **Can untrusted input choose a pattern here?** Patterns come from files in the repository (`vite.config.ts`,
   tool defaults); the source does not use `import.meta.glob`; no configuration contains extended-glob syntax
   (`tests/relay/picomatch-exposure.test.mjs` keeps checking this and the timing above). Changing a pattern means
   changing repository code, and whoever can do that can already run arbitrary code in that same job.
6. **CI trust boundaries.** The tooling runs in the `web`, `contracts` and `security` workflows: `contents: read`,
   no secrets, no `pull_request_target`. A pull request from a fork runs with the same read-only token and no
   secrets; picomatch gives it nothing it does not already have. The privileged jobs (`relay-release` publish
   with `contents: write`; `relay-monitor` with `issues: write`) do not install or run anything from `apps/web`.
   Worst realistic outcome: a slow or timed-out unprivileged CI job (all jobs have `timeout-minutes`).
7. **The Vite development server** uses picomatch for its file-serving deny list, with fixed default patterns.
   In CI it listens on the runner's loopback address only. Developers should not expose a dev server to a network
   (general Vite guidance, unrelated to this advisory).
8. **Can it be removed or replaced?** The only automatic remedy npm offers is older tooling (vite 6.2.7,
   vitest 3.1.4, plugin-react 5.2.0). Tried in a scratch copy: picomatch 4.0.7 is still present (through vitest →
   tinyglobby) and the downgrade adds 2 critical and 3 further high advisories (tinypool, vite dev-server issues).
   Rejected. Replacing Vite/Vitest is out of proportion to a build-time, pattern-controlled weakness.

### Conditions enforced by the gate (it fails again if any stops holding)

- only this advisory id, this package, this directory;
- not after 2026-12-17, and no entry may run longer than 30 days;
- picomatch must not be reachable through any package other than the five reviewed ones;
- the registry's newest picomatch must still be 4.0.7 — any new release forces a re-review;
- production dependencies must be free of high/critical advisories (no exception mechanism exists for them);
- every other high/critical advisory in any dependency still fails the workflow.

### Removal

Delete the entry from `security/audit-exceptions.json` and this section's OPEN status when a picomatch release
outside the advisory range exists (update the lock file), or when vite/vitest no longer depend on it. Re-review
earlier if dependencies, CI permissions/triggers or glob usage change.

## AR-2 — F-023, the browser's device secret is kept in `localStorage` — OPEN, accepted until 2026-12-25 (placeholder)

This entry is documentation only. It is not in `security/audit-exceptions.json`, because that file gates npm advisories and this is a design
decision. The expiry below is a **placeholder** set 30 days after the entry was written; the owner confirms or changes it.

| | |
|---|---|
| Source | AI-assisted review of 2026-11-25 (not an independent audit), finding F-023 |
| Written | 2026-11-25 (UTC) |
| Expires | 2026-12-25 (30 days), placeholder until the owner confirms |
| Scope | `apps/web/src/api.ts`: the approved-browser id and secret, stored per account in the browser's `localStorage` |
| Status | Not fixed |

### What is accepted

Each approved browser holds a secret that proves "this is the browser my owner approved". It is kept in `localStorage`, so script running in the web
app's own page could read it. The browser also needs the signed-in account's token to use any route, so the secret alone opens nothing.

### Why the risk is limited

- The app's content policy forbids inline script, and the source contains no `innerHTML`, `eval` or third-party script (checked by review and by the web tests).
  The usual way such a secret is stolen is script injection, which that policy blocks.
- The secret is useless without a valid account token; both are needed on every request. Revoking the browser (sign-out with "remove the pairing", "Sign out
  everywhere", or removal from the PC) stops it at once.
- Since 2026-11-25 sign-out asks whether to remove the pairing from this browser. The default keeps it; on a shared or public computer the person should
  choose to remove it.

### What would remove the risk

Bind the approval to a non-exportable key (WebAuthn or a WebCrypto key stored as non-extractable in IndexedDB) so the browser proves possession without
ever exposing a secret. That is a larger change to pairing and is not scheduled.

### Review

Re-review at the expiry, and earlier if the web app gains any way to render text from another person as markup, or loads any third-party script.

## AR-3 — R65, operators see account emails; audit entries and backups keep them after deletion — OPEN, accepted by the owner (2026-11-26)

Documentation only (not in `security/audit-exceptions.json`).

| | |
|---|---|
| Source | Owner request of 2026-11-26: the operator console must show whom each account is |
| Scope | Gateway: `account/<uid>` billing entry, `/v1/admin/*` responses, audit entries (`operatorEmail`, `targetEmail`), daily backups of the data directory |
| Status | Implemented; the privacy page, the account page and `docs/design/ADMIN_CONSOLE_REDESIGN.md` say so |

### What is accepted

- Anyone on the operator list (a recent sign-in, optionally a second factor) can read every known account's email address and display name. That is personal data
  the console did not show before. It is account identity only: no file or folder name, path or content.
- The audit log keeps the email as it was when a change was made, without an end date, so it **stays after the account is deleted** (the owner asked for entries to
  stay readable). The privacy page says so and offers removal on request; there is no console feature to edit the log.
- The billing record held the email until the account was deleted, and seven days of local backups (and the optional off-host copy, once configured) may hold it
  until they are replaced.

### Why the risk is limited

- Only `/v1/admin/*` returns it, and each call re-checks the operator list and a recent sign-in. A test signs in as a plain member, a stranger and an
  organisation admin and checks that no ordinary endpoint returns another account's email.
- It is never put in a URL or a log line. The organisation panel for customers is unchanged (it shows the label a member chose, never their email).
- Deleting an account removes the billing copy (unit, emulator and integration tests).

### What would reduce it

Turn on the operator second factor (F-005), keep the operator list short, and consider hashing or truncating the email in audit entries after a retention period.

