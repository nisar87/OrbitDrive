# Threat model (phase 0 baseline; revisit each phase)

| # | Threat | Component | Current mitigation | Status / gap |
|---|---|---|---|---|
| T1 | Stolen account token used from attacker's browser | Gateway | Account token alone grants nothing; needs approved browser-device credential | Tested. Device secret is a bearer secret in localStorage — XSS on the web origin would expose it (CSP is W11; platform-key design is W1) |
| T2 | Attacker shows victim a QR for the attacker's PC, or relays a victim PC's QR | Pairing | Claim binds account+request; PC shows account, browser name and 6-digit code; owner must approve locally; PC already owned cannot be claimed by another account | Tested. QR is not proof of proximity — stated in UI copy plan |
| T3 | Pairing code guessing / replay / race | Gateway | 10-char secret from 31-symbol alphabet, hashed; 5 failed attempts burn the request; 2-minute TTL checked at claim and confirm; atomic one-use claim; new request supersedes old | Tested (incl. 64-way race). Claims are also limited to 10/min per client address and per account, polls to 90/min per account (429 + Retry-After); the client address comes from X-Forwarded-For only when the TCP peer is a configured trusted proxy, right-most entry only (tested with forged headers). Limits are in memory on the single relay instance |
| T4 | Cross-account / cross-PC access | Gateway | Owner + device grant checked on every route; foreign PC returns 404 | Tested |
| T5 | Path traversal, absolute/UNC/device paths, ADS, reserved names | Core | Sealed IDs + per-segment validation even for genuinely sealed tokens | Tested on Windows and Linux |
| T6 | Symlink/junction/ancestor-swap redirect | Core | Reparse refusal per component + opened-handle final-path check; served from same handle | Tested incl. ancestor swap. Volume-identity binding of roots is D4 |
| T7 | Hard link exposing a file outside the root | Core | Windows link count > 1 refused | Tested on Windows. Not enforced on non-Windows dev hosts |
| T8 | Resume splicing two file versions | Core/Gateway | Content-hash ETag; If-Range mismatch → full body; agent revalidates size+mtime per range | Tested. Timestamp-restoring same-size rewrite is a known gap (D5) |
| T9 | Download capability leak (logs, Referer, history) | Gateway/Web | Hashed at rest; request-path logging off; `Referrer-Policy: no-referrer`; `no-store`; expiry; revocation cancels live streams | Tested for logs/revocation/expiry. Capability is bearer: whoever has the URL can download until expiry |
| T10 | Malicious/compromised gateway instructs agent | Agent | Closed action set; local root policy; pause; only reads approved roots | A compromised relay can still read files in approved roots while the agent runs — inherent until E2EE (E-D3) |
| T11 | Rogue agent impersonates a PC | Gateway | pcId is the key hash; nonce signature; older connection generation cut off | Tested |
| T12 | Browser-origin WebSocket to agent channel | Gateway | Requests with an `Origin` header are refused | Tested |
| T13 | Memory/connection exhaustion | Gateway/Agent | 64 KiB JSON body cap, 256 KiB control message cap, per-owner stream cap (4), agent stream cap (4), pending-request caps | Partly tested. No global rate limiting/quotas yet (W10) |
| T14 | Local malware as the same Windows user | Agent | DPAPI CurrentUser for keys | Not a defence against same-user malware (stated limitation) |
| T15 | Dev identity reaching production | Gateway/Web | Gateway refuses to start in Production; web production build guard | Tested |
| T16 | Operator visibility | Relay | No payload logging or storage | Operator with relay access can observe traffic; disclosed |
| T17 | Hostile filenames (XSS, RTL, control chars) | Web | React text rendering only; `bdi` isolation; names with control chars are never listed | Preview-related risks are W7 |
