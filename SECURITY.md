# Security

MyOrbitDrive is pre-release software. A development relay and web app are live for testing (real email sign-in; the Windows app is an unsigned preview).
Do not expose a gateway that has development identities switched on: they are not real authentication, and a production gateway refuses to start with them.

- Report vulnerabilities privately to the repository owner (GitHub security advisory on this repository).
- Threat model: `docs/architecture/threat-model.md`. Known gaps are listed there and in `docs/IMPLEMENTATION_STATE.md`.
- A review of 2026-11-25 was AI-assisted, not an independent audit. An independent security review is still a next step; see `docs/security/accepted-risks.md` for what is knowingly open.
- The relay terminates TLS and can process file contents in transit. OrbitDrive does not claim end-to-end encryption.
- Never commit service-account keys, signing certificates or `.env` files. `VITE_*` values are public by design.

## Dependency advisories and accepted risks

The `security` workflow audits npm and NuGet dependencies on every push. Shipped (production) dependencies must
have no high or critical advisory, without exception. For build and test tooling, an advisory may be accepted only
with the owner's authorisation, a written justification and an expiry of at most 30 days; the gate
(`scripts/audit-check.mjs`) enforces those limits and prints accepted advisories as warnings on every run.
Currently accepted (not fixed): see `docs/security/accepted-risks.md`.
