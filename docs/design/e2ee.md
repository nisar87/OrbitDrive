# End-to-end encryption (extensions E-D3 / E-W3): design proposal

**Status: proposal for owner review. No code yet. Nothing in the product may claim end-to-end encryption until every item in section 9 is done and the design has been reviewed.**

## 1. What is true today
The relay ends TLS on both sides and streams bytes between the PC agent and the browser. It does not store file contents, but it **can read them while they pass**, and it sees file
names, folder listings and sizes. The plan (lines 122, 458, 881) requires the product to say exactly that, and the privacy panel does. This document is the plan's E-D3/E-W3 extension.

## 2. Goal and non-goals
**Goal:** a relay (or anyone who can read its memory, logs or network) learns no file contents, file names or folder structure; a stolen relay database or a malicious relay operator
cannot read or silently alter what a paired browser receives.
**Not goals (will be disclosed, not hidden):** hiding that a transfer happened, its approximate size and timing, the account, the PC and device ids, or IP addresses; protecting a
browser whose own device is compromised; protecting against a malicious web app, see 8.

## 3. Threat model additions
| Attacker | Can do today | After this design |
|---|---|---|
| Relay operator / compromised relay | read and change file bytes and names; swap keys if there were any | read nothing useful; tampering is detected; cannot swap keys (4) |
| Network attacker past TLS | none (TLS) | none |
| Stolen browser device | use that device's access until revoked | same, and revocation also stops decryption (the PC forgets that device's key) |
| Malicious hosted JavaScript | read everything | **still can** (section 8) |

## 4. Keys and pairing
- Each agent has a long-term ECDH key pair (P-256: the one curve every browser's built-in WebCrypto supports; X25519 can replace it when all target browsers have it). The private key is stored in the agent's state protected with the Windows user's DPAPI.
- Each browser device generates its own key pair in WebCrypto, **non-extractable**, kept in IndexedDB. (This also replaces the device credential that lives in `localStorage` today.)
- **Authenticating the keys without trusting the relay.** The pairing link/QR already travels outside the relay (it is shown on the PC's screen). It will carry, in the URL **fragment** (never sent to any server), the hash of the agent's public key and a one-time pairing secret `s`.
  - Browser → PC: the browser sends its public key with `MAC(s, pk_browser || pk_agent)`. The agent accepts it only if the MAC verifies, so the relay cannot substitute its own key.
  - PC → browser: the browser checks the agent's public key against the hash in the fragment, so the relay cannot impersonate the PC.
  - The existing confirmation code stays as the human check that the owner is approving the request they started.
- Revoking a device deletes its public key from the agent; it can no longer derive anything.
- Re-pairing is how keys are rotated. There is no recovery: clearing the browser's storage means pairing again (stated in the UI).

## 5. Encrypting data
- Shared secret = ECDH(browser private, agent public) → HKDF-SHA-256 with context strings → a **per-transfer key**: salt = random 128-bit transfer id chosen by the agent.
- Files are cut into **64 KiB chunks**, each sealed with AES-256-GCM. Nonce = chunk index (96-bit counter); AAD = transfer id ‖ chunk index ‖ "last chunk" flag, so chunks cannot be reordered, repeated, moved between files or silently cut short.
- Encrypted length = plaintext length + 16 bytes per chunk, so **range requests and resume map onto whole chunks** and stay bounded-memory (one chunk at a time, no whole-file buffering).
- Names, folder listings, sizes shown to the user, thumbnails, upload requests and "send to phone" metadata are sealed the same way (single-chunk messages), so the relay only routes opaque blobs with ids. Search and sorting by name move to the client.
- Uploads and PC → phone transfers use the same format in the other direction.

## 6. Delivering it in the browser
- Images, video, audio and PDF preview use plain URLs today. A **service worker** intercepts the app's own download/preview URLs, fetches the encrypted ranges, decrypts chunk by chunk and answers with a normal `206` response, so `<video>` seeking and PDF.js keep working.
- Save-to-disk streams through the same worker (or the File System Access API where present). **Support matrix to be measured before any claim:** Chrome/Edge, Firefox, Safari (service worker limits), iOS Home Screen app, Android Chrome. Where a browser cannot stream-decrypt a very large file, the UI says so and offers the nearest supported path. It never falls back silently to plaintext.

## 7. Compatibility and rollout
- Protocol gains an `encryption` capability. New pairings are E2EE by default; existing pairings keep working in the current mode and show "Not end-to-end encrypted: pair again to enable" until re-paired.
- No mixed claims: a PC/browser pair shows **one** state (encrypted or not), and the privacy panel text is generated from that state.

## 8. What this does not protect (disclosed)
- **The web app itself is served by hosting we control.** A malicious or compromised deployment of the page could steal keys, whatever the cryptography. Mitigations to evaluate: stricter CSP and SRI (already in place), signed/reproducible web builds with a published hash, a desktop-only client for the highest-trust use. These reduce but do not remove the risk.
- Metadata listed in section 2.

## 9. Work plan (each step ends with tests and CI evidence)
1. Review of this document and the threat model (owner, then an independent reviewer if possible).
2. Shared crypto spec with **known-answer test vectors**, implemented in C# (agent) and TypeScript (web); both must produce identical bytes for the vectors.
3. Pairing key agreement (section 4) including a **malicious-relay test** that tries to substitute keys and must fail.
4. Encrypted file streaming: chunk format, range/resume, 1 GiB bounded-memory test, tamper/truncate/reorder/replay tests.
5. Service-worker delivery and the browser support matrix (real devices where required; CI cannot prove phones).
6. Encrypted metadata, listings and thumbnails; client-side search/sort.
7. Encrypted uploads and send-to-phone.
8. Privacy panel and docs switch (R39) **only after** step 9.
9. Independent security review, then and only then the product may describe itself as end-to-end encrypted.

## 10. Questions for the owner
1. P-256 now (works everywhere) or wait for X25519 in every target browser?
2. Is a plaintext fallback acceptable for browsers that can't stream-decrypt (shown clearly as "not encrypted"), or must those browsers be refused?
3. Is losing access when browser storage is cleared acceptable (re-pair), or do you want a recovery code?
4. Should step 9 (independent review) be a hard gate for using the words "end-to-end encrypted"? (Recommended: yes.)
