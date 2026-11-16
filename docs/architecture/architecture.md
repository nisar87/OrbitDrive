# Architecture (as implemented)

```text
Windows owner console (WPF, D1) ─ local IPC (D1) ─ OrbitDrive.Agent ── OrbitDrive.Core (filesystem policy)
                                                        │  outbound only
                                 WSS /v1/agent-control  │  HTTPS POST /v1/agent-streams/{id}
                                                        ▼
                                     OrbitDrive.Gateway (ONE instance; control + byte relay)
                                                        ▲
                        HTTPS JSON API + GET/HEAD /v1/downloads/{capability}
                                                        │
                                  apps/web (static; Firebase Hosting) / phone browser
```

## Trust boundaries

1. **Agent local policy is final.** Every instruction is re-checked against the owner's approved roots on the PC,
   whatever the gateway authorised. The action set is closed (`packages/protocol/schema`); there is no execute,
   shell or URL-proxy action.
2. **Gateway authorises people and devices.** Each browser call needs a verified account *and* an approved,
   unrevoked browser-device credential holding a grant for that PC. A client-supplied UID or path is never authority.
3. **The relay sees plaintext.** TLS terminates at the gateway. Nothing is persisted, but this is not E2EE and the
   product must say so (`GET /v1/meta` reports `endToEndEncrypted:false`; the web taskbar states it).
4. **Firebase Hosting serves static assets only.** Transfers go straight to the relay origin (Hosting rewrites time
   out after 60 s). `infra/firebase/firebase.json` contains no API rewrites.

## Identifiers

- `pcId` = base64url(SHA-256(agent public key)); the agent proves possession by signing the gateway's nonce (ECDSA P-256).
- `entryId`, `readToken`, cursors = AES-256-GCM tokens sealed by the agent. The gateway and browser cannot read
  the path inside or forge one. Even a genuinely sealed token is re-resolved through `PathPolicy` + handle checks.
- Download capability = 256-bit random bearer value, stored hashed, scoped to one file revision + one device grant,
  reusable for HEAD/ranges until expiry or revocation. It is a bearer capability, not cryptographically phone-bound.

## File read path (D4/D5 core)

`PathPolicy.Resolve` validates each component (no separators, `..`, ADS `:`, reserved device names, trailing dot/space),
refuses reparse points on every component, then `FileAccessService.OpenValidated` opens the file and checks the
**opened handle**: final path (GetFinalPathNameByHandleW) must be under the root's final path, the handle must not be
a reparse point, and the hard-link count must be 1. Bytes are served from that same handle.

Revision = SHA-256 computed under a share-read handle at prepare time. Every range read re-opens, re-validates and
compares size + last-write time with the prepared revision; any difference is `revision_changed` (HTTP 409).
Known limit: a same-size rewrite that also restores the timestamp between prepare and read is not detected (D5 item).

## Transfer path (W5 core)

Browser `GET /v1/downloads/{cap}` → gateway parses a single RFC 9110 range → `open_read` to the agent with a one-use
uplink token → agent POSTs the range body → gateway copies with one pooled 64 KiB buffer, awaiting each write
(backpressure through Kestrel/TCP to the agent's disk reads). A short uplink aborts the browser connection so a
truncated body never looks complete. Min-data-rate limits are lifted for these two requests so slow phones are
throttled, not dropped.

Measured buffers: see `docs/evidence/phase-evidence.md`. Budget adopted: < 96 MiB RSS growth per process during a
1 GiB transfer with a stalled receiver (observed single-digit MiB).

## Not built yet (do not assume)

Firebase Auth verification (W1), Firestore persistence (W3; state is in memory and lost on gateway restart),
QR rendering/scanning (D2/W2), WPF owner console and IPC (D1), Explorer shell extension (D6), Inbox/push (W6),
previews, archives, search, uploads, admin, packaging, deployment workflows.
