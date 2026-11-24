# Pricing decisions (24 Nov 2026)

Status: agreed in discussion with the owner and published on the website Services, Terms, Refund and Delivery pages. NOT yet enforced by the program: the gateway has no plans, limits or counters, so the site states that plans and limits start on a date to be announced.

## Plans (PKR, one-time passes that do not auto-renew)
| Plan | 1 month | 3 months | 12 months | Limits |
|---|---|---|---|---|
| Free, for life | 0 | 0 | 0 | 1 PC, 1 paired device, 5 files/day, 100 MB per file, 300 MB/day |
| Plus | 1,000 | 2,500 | 8,000 | 3 PCs, 5 devices, 50 files/day, 1 GB per file, about 20 GB/day (fair use) |
| Office | 3,000 | 7,500 | 24,000 | 10 PCs, unlimited devices (hidden cap about 100), unlimited files, 2 GB per file (fair use, about 100 GB/day) |

Who set what: the owner set the Free structure (1 PC, 1 device, 100 MB per file), Plus at 1,000 / 2,500 for 1 / 3 months with 3 PCs and 5 devices and 1 GB per file, and Office at 10 PCs, unlimited devices, 2 GB per file and unlimited files under fair use. Claude proposed the 12-month prices (ladder 1x / 2.5x / 8x), Office prices on the same ladder, Free 5 files/day with a 300 MB/day cap (owner had said 3 files, then asked whether more was fair), Plus 50 files/day, and the daily data caps. The 5-file choice was taken from Claude's last suggestion; the owner can revert it to 3.

## Rules
- A file counts when a download or a send to phone finishes. Browsing and previews do not count. Failed or cancelled transfers do not count. The day resets at midnight Pakistan time.
- Organisation logs hold counts, sizes, times, device names and optionally IP addresses, never file or folder names (matches today's activity history, which never reads names).
- The service is not end-to-end encrypted; the relay sees traffic in transit but does not store or log it. Say so plainly.

## Office admin panel (not built, not promised)
Ideas listed for later: organisation owner/admin and roles, PC and device list with revoke, shared-folder overview, usage report (no file names), IP and sign-in records with a retention period, policies, billing, two-factor for admins. Needs privacy wording for monitoring staff and legal review. Suggested first step: a small "Team v1" after real offices confirm demand.

## Open
- Paid launch date and how payment is collected (FastPay requirements for an individual unconfirmed; manual confirmation is the fallback).
- Real relay cost per GB to confirm the daily caps.
- Support email: support@myorbitdrive.app does not exist yet (needs forwarding to an address the owner chooses).
- Free trial period.

## Engineering work before charging anyone
Plan and end date on each account; checks for PCs, devices, files per day, size per file and daily data; counters that survive a gateway restart (state is in memory today); clear in-app message and a "used today" counter; a manual way to activate or extend a plan; tests for each limit.
