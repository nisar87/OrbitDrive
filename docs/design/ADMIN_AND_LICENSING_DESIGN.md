# Admin, licensing and plan limits: research and design

Status: PROPOSAL, owner decisions recorded 25 Nov 2026 (section 11). Nothing here is built. The program and the live website are unchanged.
Related notes: `docs/branding/PRICING_DECISIONS.md` (agreed plans and prices), `docs/architecture/decisions.md` (ADR-4, 18, 21, 22).

## 1. What already exists (checked in the code, 25 Nov 2026)

- **Operator admin area** (ADR-21): operators are named in deployment configuration (`OrbitDrive:OperatorUids`), every `/v1/admin/*` call re-checks the list and needs a sign-in at most 15 minutes old. Endpoints today: overview, users, suspend / unsuspend, revoke-all browsers, reset relay allowance, settings (oldest allowed PC version), append-only audit trail. No endpoint lists files or acts as a user.
- **Daily byte counter per account** (`Operations.cs`): in memory only, resets on restart, used for an alert (`RelayBytesDailyAlert`), not for enforcement.
- **Durable state**: a Firestore snapshot backend exists (`FirestoreSnapshotBackend`) alongside the file-backed store; one gateway instance only.
- **Uploads** already have owner-set per-folder limits and quotas (ADR-19). Account deletion exists (ADR-22). One browser device identity spans all PCs of an account (ADR-18).
- **Not present**: plans, entitlements, licences, coupons, payments, organisations, members, per-user limits, a customer "Account / usage" page.

So the work is to extend an existing, tested operator surface, not to start from nothing.

## 2. How comparable products do it (vendor documentation, not hands-on testing)

| Area | Pattern | Seen in |
|---|---|---|
| Roles | Owner / admin / member; finer roles (billing, IT, auditor) at larger scale | Splashtop, Tailscale |
| Devices | List, rename, admin approval of new devices, revoke, per-member device limits, remote wipe of a lost device | Tailscale, Dropbox, Synology |
| Groups and policies | Apply settings to groups of users or computers instead of one by one | Splashtop, TeamViewer |
| Activity | Who connected, when, how long; filterable log; CSV export; configuration audit log that cannot be turned off | TeamViewer, Dropbox, Tailscale |
| Quotas | Per-user and per-group quotas and transfer-rate limits | Synology |
| Entitlements | Plan maps to a set of features/limits; access granted on purchase, revoked on cancel/expiry, changes applied at a defined time | Stripe Billing, Keygen |
| Licence types | Perpetual, timed/subscription, node-locked, floating, feature, trial; expiry with optional grace | Keygen, Revenera, Thales |
| Coupons | Percent or flat; expiry; overall redemption limit; restricted to certain products; one-time or recurring | Stripe, Paddle, Lemon Squeezy |
| Admin security | Administrative interfaces use multi-factor authentication; least privilege; attributable audit logs | OWASP ASVS |

## 3. Recommended licence model

MyOrbitDrive is account-based (Firebase sign-in, server-side gateway). Offline licence keys, hardware locks and floating licence servers add complexity that this product does not need. Use **server-side entitlements**: the account (or later the organisation) holds a *licence*; a licence points to a *plan*; the plan defines limits. The gateway checks limits; the PC app is never the gatekeeper for files (the PC keeps its own folder policy, ADR trust boundary 1).

Licence types:

| Type | Subject | Duration | Notes |
|---|---|---|---|
| Free | account | no end | 1 PC, 1 device, 5 files/day, 100 MB/file, 300 MB/day. Always the fallback. |
| Pass (Plus) | account | 1, 3 or 12 months, no auto-renew | Extends from the later of now and the current end date. |
| Pass (Office) | organisation | 1, 3 or 12 months | Seats are PCs. Devices unlimited under fair use. |
| Trial | - | - | Dropped (owner decision): Free is the trial. |
| Grant | account or organisation | any | Issued by the owner (support goodwill, partners, testers). Always logged. |

Rules:
- **Expiry never locks anyone out of their own files.** After the end date plus a short grace (suggest 3 days) the account falls back to Free limits. Extra PCs/devices beyond Free are paused, not deleted, and come back on renewal.
- **Upgrades** (Plus to Office) start now; remaining Plus days become credit days the owner applies by hand in v1. No automatic proration in v1.
- **Stacking**: two passes extend the end date; they do not run in parallel.
- **Currency and tax**: prices in PKR. Sales tax on digital services is a provincial matter in Pakistan and rates differ by province; sources disagree on details, so the owner should ask an accountant before charging. Receipts should carry an order number.

## 4. Data model (Firestore, behind the existing snapshot/backend layer)

- `plans/{planId}`: name, prices per period (PKR), limits `{pcs, devices, filesPerDay, bytesPerFile, bytesPerDay, historyDays}`, `active`, `version`.
- `licences/{id}`: subject (`acct:{uid}` or `org:{orgId}`), planId, startsAt, endsAt, source (`manual`, `gateway`, `coupon`, `grant`, `trial`), createdBy, note.
- `orders/{id}`: subject, planId, period, couponCode, priceDue, status (`requested`, `proof_submitted`, `verified`, `rejected`, `expired`), payment reference and method, verifiedBy, verifiedAt. Proof images, if any, go to private storage never publicly readable.
- `coupons/{code}`: kind (`percent`, `fixed`, `free_days`), value, appliesTo plans, validFrom/To, maxRedemptions (overall), maxPerAccount, stackable=false, active. Codes compared case-insensitively. `redemptions/{id}` record each use.
- `usage/{subject}/days/{yyyymmdd}`: files, bytes (a document per subject per day).
- `orgs/{orgId}`, `orgs/{orgId}/members/{uid}` (role, caps), later `groups`.
- Audit: extend the existing append-only trail (never file or folder names).

Counter design: keep counters in memory for the hot path and flush to Firestore in batches every few seconds and on shutdown. Firestore sustains about one write per second per document, so one document per subject per day, written in batches, is enough at this scale; sharded counters only if volume grows. If the counter store is unreachable, **fail open with an alert** for cost limits (do not block people from their own files because our database hiccuped); fail closed for administrative actions.

Day boundary: Pakistan midnight (Asia/Karachi, UTC+5, no daylight saving). The current counter's day definition must be checked and aligned.

## 5. Enforcement points (gateway)

| Limit | Checked when | If exceeded |
|---|---|---|
| PCs | A PC is claimed/paired | Clear error with upgrade hint |
| Devices | A browser/phone is claimed | Same |
| Max file size | A download or send starts (size known) | Refuse before streaming |
| Files per day | A transfer is started; counted when it completes | Refuse; failed or cancelled transfers do not count |
| Bytes per day | Start, and while streaming | Stop cleanly at a boundary with a clear message |
| Org pool and per-user caps | Same points, strictest wins | Warn at 80%, block at 100% |

Responses carry the remaining allowance so the app can show "2 of 5 files left today, resets at midnight Pakistan time".

## 6. Owner admin panel (extends ADR-21)

Sections, in build order:
1. **Customers**: search, plan and end date, usage today, PCs and devices, notes. Actions: grant, extend, cancel, suspend (exists), revoke browsers (exists), reset today's allowance (exists).
2. **Orders and payments**: queue of requested orders, proof review, verify/reject, one button "activate". This single *activate* function is the seam where an online payment gateway later plugs in (a webhook calls the same function).
3. **Plans and prices**: edit limits and PKR prices with versioning; changes apply to new purchases, existing passes keep the terms they were bought under.
4. **Coupons**: create, restrict, expire, see redemptions; free-days coupons suit the manual flow best.
5. **Settings**: announcement date for when limits switch on (feature flag per plan), maintenance banner, minimum PC version (exists), support contact.
6. **Reports**: revenue by month, active passes, expiring in 7 days, usage outliers for fair use, CSV export.
7. **Audit**: all of the above, filterable.

Security (OWASP ASVS says administrative interfaces use multi-factor authentication): keep operators in deployment config; keep the 15-minute recent-sign-in rule for money- or access-changing actions; require multi-factor sign-in for operator accounts (check what Firebase Authentication offers for this project before promising it); rate-limit admin calls; no impersonation; every change attributable and logged; proof images in private storage.

**Price display problem (needs a decision).** The marketing site is static with a strict Content-Security-Policy and `connect-src 'none'`. If prices are editable in the panel, the site must read them from somewhere:
- A. Prices live in a versioned repo file used by both site and gateway; changing a price is a reviewed commit and deploy. Safest, least flexible.
- B. The gateway serves a public read-only `/v1/plans`; the site fetches it and falls back to baked-in values; the site CSP allows connecting to the relay origin only. Flexible; one new public endpoint and a CSP change.
- C. Panel edits trigger a deploy that regenerates the site. Most moving parts.
Decision: B (see section 11). Either way Terms and Refund text must not quote prices that can drift; they point to the Services page.

## 7. Individual users: Account page (free and paid)

- Plan, end date, "renews: no, one-time pass", and a Buy / Extend button (opens the order request in v1).
- Usage today: files used of limit, data used of limit, reset time.
- PCs and devices: list with names and last seen, rename, remove, sign out all browsers.
- Security: recent sign-in time, delete account (exists, ADR-22).
- Preferences (exist in part): view mode, sort order, notification choices.
- Paid extras: more PCs/devices, higher limits, longer activity history. No feature should be locked that the Free plan advertises.
- Orders and receipts list.
- Honest-limit wording near usage: "files pass through our relay over HTTPS and are not stored".

## 8. Organisation admin panel

Entity: an organisation with an owner; members join by emailed invitation; **seats are PCs** (Office: 10), devices unlimited under fair use. Entitlement subject for members' PCs is the organisation.
- **Stage 1**: members list (invite, remove), PC and device list with owner/name/last seen/revoke, usage today, plan and end date, billing state (owner activates), audit of admin actions.
- **Stage 2**: roles (owner, admin, member), admin approval of new PCs/devices, groups of PCs, per-person and per-group limits (files/day, bytes/day, max file size) inside the plan ceiling, org shared pool with an optional per-person cap, warnings at 80%, CSV usage export, idle sign-out setting.
- **Stage 3, on demand**: single sign-on, office-hours and file-type policies, remote disconnect of a lost PC, auditor role, API.
- **Privacy rule (decided)**: organisation views and logs hold counts, sizes, times and device names (optionally IPs with a retention period), never file or folder names. Staff must be told that usage is monitored; the Privacy page needs an organisation section and a lawyer's review.
- Limits resolve strictest-first: member cap, group cap, organisation pool, plan ceiling.

## 9. Build order (each step: tests, CI green, then merge; the live site changes only when the matching code is live)

1. Plan data, licence record and an *effective limits* function with unit tests (free fallback, expiry, grace, stacking).
2. Persistent daily counters (batched Firestore writes, restart test, Pakistan-midnight reset test).
3. Enforcement at pair, claim, download, upload with negative and security tests (no bypass by retry, no counting of failed transfers).
4. Account page with usage and allowance.
5. Owner admin: customers, orders, activate/extend/cancel, audit; manual payment flow end to end.
6. Coupons and plan/price editing.
7. Switch limits on: announcement date, update the site wording, publish.
8. Online payment gateway connection (after approval) calling the same *activate* function.
9. Organisations stage 1, then stage 2 as customers ask.

## 10. Risks and open decisions

- Payment approval is not needed for steps 1 to 7.
- The limits are cost protection, not security; the PC's own folder policy remains final.
- Bandwidth cost per GB is unknown; daily caps are guesses until measured.
- Decisions for the owner: trial yes/no; grace length; price display option A or B; whether Free gets a "send to phone" count separate from downloads; refund handling of coupons; invoice/receipt format and sales-tax registration (accountant).
- Multi-factor sign-in for operators must be confirmed against the Firebase project's capabilities.
- End-to-end encryption is out of scope here and the product must keep saying it is not end-to-end encrypted.

## Sources

- Tailscale: https://tailscale.com/kb/1350/manage, https://tailscale.com/kb/1196/security-hardening, https://tailscale.com/blog/config-audit-logging
- Splashtop: https://support-splashtopbusiness.splashtop.com/hc/articles/212725143, https://www.splashtop.com/en-gb/features/user-computer-management-grouping
- TeamViewer: https://www.teamviewer.com/en-us/global/support/knowledge-base/teamviewer-remote/get-started/management-console-integrated-into-teamviewer-s-admin-settings/
- Dropbox: https://help.dropbox.com/guide/admin/how-to-get-started, https://help.dropbox.com/teams-admins/admin/device-approvals, https://help.dropbox.com/security/customize-security
- Synology: https://kb.synology.com/th-th/DSM/help/SynologyDrive/drive_admin_console?version=7, https://www.synology.com/en-global/dsm/7.2/software_spec/dsm
- Keygen: https://keygen.sh/docs/choosing-a-licensing-model/floating-licenses/, https://keygen.sh/docs/choosing-a-licensing-model/feature-licenses
- Stripe Billing: https://docs.stripe.com/billing/entitlements, https://docs.stripe.com/billing/lifecycle
- Paddle discounts: https://developer.paddle.com/build/products/offer-discounts-promotions-coupons/; Lemon Squeezy: https://docs.lemonsqueezy.com/help/orders/creating-discount-codes
- Licence types: https://www.revenera.com/blog/software-monetization/software-licensing-models-types/, https://cpl.thalesgroup.com/software-monetization/software-licensing-models-guide
- Firestore counters: https://firebase.google.com/docs/firestore/solutions/counters
- OWASP ASVS access control (4.3.1 MFA for administrative interfaces): https://github.com/OWASP/ASVS/blob/v4.0.2/4.0/en/0x11-V2-Authentication.md
- Pakistan sales tax on digital services (secondary sources, verify with an accountant): https://trykintsugi.com/sales-tax-guides/apac/pakistan, https://payproglobal.com/saas-sales-tax/pakistan/
- Manual payment practice in Pakistan (JazzCash, Easypaisa, bank transfer, proof review): https://wordpress.org/plugins/cardraabta-manual-payments-for-woocommerce/

## 11. Owner decisions (25 Nov 2026)

- **No trial.** The Free plan already lets people try the product.
- **Grace period: yes, 3 days** after a pass ends, then fall back to Free (PCs/devices beyond Free are paused, not deleted).
- **Price display: option B** (revised after the owner asked to change prices from the admin panel). The panel is the source of truth; the gateway serves a public read-only `/v1/plans`; the site fetches it with baked-in fallback values; the site CSP `connect-src` allows only the gateway origin. Option A (repo file) is rejected because panel edits would not reach the site.
- **Free plan: 5 files/day, 100 MB per file** (300 MB/day), as published.
- **Sales tax: postponed.** Revisit with an accountant before charging anyone.
- **Currencies (owner, 25 Nov 2026): never PKR only.** Every price is shown in USD with the PKR equivalent. Each plan stores `priceUsd` and `pricePkr` per period; the panel can set PKR directly or derive it from an exchange rate (rounded). Orders record the currency actually paid. Existing passes keep their purchase price. Collection today is PKR only (local rails); an international card option is an open item and no provider has been researched or promised. Indicative USD at an assumed PKR 280/USD, to be confirmed by the owner: Plus $3.50 / $9 / $29, Office $11 / $27 / $86 (1 / 3 / 12 months).
