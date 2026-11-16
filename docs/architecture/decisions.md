# Decisions and deviations from the build pack

| ID | Decision | Why | Revisit |
|---|---|---|---|
| ADR-1 | Agent control channel is a plain WebSocket carrying the versioned typed envelope, **not SignalR** (the plan's D3 suggestion) | The envelope/correlation/deadline protocol is defined in the canonical schema either way; SignalR's client is an extra dependency in the per-user agent, and this build environment cannot restore NuGet packages to verify it locally. Transport sits behind `AgentConnection`/`ControlClient` | D3. **Owner decision welcome** — switching is contained to two classes |
| ADR-2 | Agent byte uplink is `POST /v1/agent-streams/{id}` with a streamed body rather than a binary WebSocket | HTTP request-body streaming gives end-to-end backpressure with no framing code; one-use bearer token per stream | D5 if resumable uplinks need it |
| ADR-3 | Gateway and agent production code use only the .NET shared framework (no third-party packages) so far; DPAPI and Win32 file APIs via P/Invoke | Smaller attack surface; locally verifiable | W1 adds the official Firebase Admin SDK as planned |
| ADR-4 | Durable metadata is an in-memory `Store` in this slice | Firestore needs a project/emulator (W3). A restart forgets grants (fails closed) | W3 |
| ADR-5 | Identity is a development HMAC verifier behind `IIdentityVerifier`; Production refuses to start | Firebase project/config not available; W1 | W1 |
| ADR-6 | 64-bit integers are decimal strings on the wire, rejected if sent as JSON numbers | JavaScript cannot represent sizes above 2^53 exactly | — |
| ADR-7 | Multi-range requests are answered with the full representation (200) | Permitted by RFC 9110; avoids multipart complexity and abuse | W5 |
| ADR-8 | Hard-linked files are refused on Windows | Reparse checks alone do not establish unique origin | D4 |
| ADR-9 | Workflows have no path filters yet | Avoids required checks that never start; revisit when CI minutes matter | W11 |
| ADR-10 | `main` is the default branch and only ever fast-forwards to commits with all seven workflows green; work lands on `feature/orbitdrive-foundation` first | Owner authorised `main` on 2026-11-17 on condition of proper testing | — |
