# MyOrbitDrive: authentication and anti-abuse audit (Phase F, scoped)

Scope: the existing gateway, the new static marketing site, and the sign-in path. Method: read the code and the existing tests, then decide what, if anything, to change. No paid service, DNS change or Firebase console change was made. **No gateway code was changed in this phase**, because the audit found no defect that justified a change (see section 4).

## 1. What already exists (code and test evidence)

| Risk | Control | Where | Tested by |
|---|---|---|---|
| Pairing-code guessing | Each code dies after 5 wrong secrets; codes live 120 s; every claim attempt (right or wrong) is also throttled per client address and per account, 10/min by default, 429 with Retry-After | `Store.ClaimPairing`, `Program.cs` claim endpoint, `GatewayOptions.PairingClaimsPerMinute` | `tests/integration/pairing-limits.test.mjs`, `GatewayTests.cs` |
| Polling abuse | Status polls throttled per account (90/min) | `Program.cs` | pairing-limits |
| Spoofed client address | `X-Forwarded-For` believed only when the TCP peer is a configured trusted proxy, and only its right-most entry; relay host trusts 127.0.0.1 and ::1 (Caddy) | `ClientAddress.Of`, `infra/relay/bootstrap.sh` | `GatewayTests.cs` |
| Account enumeration | Pairing errors do not reveal whether an account or PC exists beyond "belongs to a different account" (shown only after a correct secret) | `Store.ClaimPairing` | hardening sweep |
| Unauthenticated access | Every route needs a sign-in unless on a short reviewed anonymous list; garbage credentials get 401; paired-device and operator checks sit on top | `Program.cs` | `hardening.test.mjs` (route sweep, 401, device and admin checks) |
| Anonymous PC streams | Refuse unknown callers and never answer 2xx | download and stream routes | `hardening.test.mjs` |
| Agent socket flooding | Challenge first; unanswered connections dropped in 10 s; limited number waiting at once with Retry-After; forged or oversized hello ends the connection; browser-origin requests refused | `AgentHub`, `Program.cs` | `hardening.test.mjs` |
| Heavy-endpoint abuse | Per-device and per-owner throttles on archives, search, uploads, download starts and admin | `Program.cs` (Throttle calls) | `cost-limits.test.mjs` |
| Cross-site | CORS answers only the configured web origin, never `*`, never credentials; look-alike and null origins get nothing | `UseCors` | `hardening.test.mjs` |
| Response headers | no-store, nosniff, no-referrer, CSP `default-src 'none'; frame-ancestors 'none'; sandbox`, X-Frame-Options DENY, CORP same-origin, Permissions-Policy; HSTS from Caddy | `Program.cs`, Caddyfile in `bootstrap.sh` | `hardening.test.mjs` ("full set") |
| Malformed input | Bad, huge, nested or odd JSON, paths and headers give clean 4xx, never 5xx | JSON limits | `hardening.test.mjs`, fuzz tests |
| Server holds authority | Permissions, roots and approvals are decided on the PC and the gateway, not by the browser | architecture | multi-PC isolation and device tests |

## 2. Third-party bot protection (evaluated, not enabled)

| Option | Fits? | Cost and effect | Recommendation |
|---|---|---|---|
| Firebase App Check (reCAPTCHA provider) | Sign-in is Firebase Auth, which is where account-creation abuse would land | Enabling is a Firebase console change and adds a Google script and token to the web app; enforcement can lock out real users if misconfigured | **Owner decision.** Reasonable later for the staging/production project; not changed here. |
| Cloudflare Turnstile | Would need Cloudflare in front of a form | The marketing site has **no form**, so there is nothing to protect; adding it would add a third-party script and a privacy cost | Not needed now. |
| reCAPTCHA Enterprise | Heavier than App Check, may need billing | Paid-tier risk | Not recommended. |
| More gateway throttles | Sign-in verification is a local signature check, so garbage tokens are cheap to reject | Extra per-address limit adds complexity with no demonstrated problem | Not added; revisit if the relay monitor ever shows 401/429 floods. |

## 3. The marketing site's own surface
Static files only: no form, no API call, no storage, no cookies, no third-party request (a test fails if any appears). A strict meta CSP (`default-src 'none'`, scripts, styles and images from self only, no connections, no forms) is in the page and the test run would fail on any violation; inline styles were removed for this reason. `apps/site/headers.hosting.json` holds the full response-header set (HSTS, nosniff, no-referrer, frame DENY, permissions policy, CSP with `frame-ancestors`, cache rules) to apply **if and when** the owner approves hosting; it is not wired to any deployment.

## 4. Findings
1. **No exploitable gap found in the existing controls** relevant to this assignment. No change made, deliberately: changing working auth code without a defect risks the live relay (see CLAUDE.md: preserve what works).
2. Account-creation abuse is governed by Firebase Auth settings outside this repository (owner console): review Identity Platform quotas and consider App Check there.
3. The relay monitor already surfaces 5xx, error and restart counts; adding a 429 and 401 count to the digest would make abuse visible. Small, safe, optional follow-up (not done here to keep this branch free of product changes).
4. Not tested here: a live abuse run against the deployed relay (the sandbox cannot reach it, and load against production was not requested).

## 5. Regression and abuse tests run in this phase
Site: `apps/site/tests/site.spec.mjs` (16 tests across desktop and phone). Gateway and integration suites run in CI on this branch's SHA (no gateway files changed).
