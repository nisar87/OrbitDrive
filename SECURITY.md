# Security

OrbitDrive is pre-release software. Do not expose a development gateway to the internet: development identities
are not real authentication.

- Report vulnerabilities privately to the repository owner (GitHub security advisory on this repository).
- Threat model: `docs/architecture/threat-model.md`. Known gaps are listed there and in `docs/IMPLEMENTATION_STATE.md`.
- The relay terminates TLS and can process file contents in transit. OrbitDrive does not claim end-to-end encryption.
- Never commit service-account keys, signing certificates or `.env` files. `VITE_*` values are public by design.

## Dependency advisories and accepted risks

The `security` workflow audits npm and NuGet dependencies on every push. Shipped (production) dependencies must
have no high or critical advisory, without exception. For build and test tooling, an advisory may be accepted only
with the owner's authorisation, a written justification and an expiry of at most 30 days; the gate
(`scripts/audit-check.mjs`) enforces those limits and prints accepted advisories as warnings on every run.
Currently accepted (not fixed): see `docs/security/accepted-risks.md`.
