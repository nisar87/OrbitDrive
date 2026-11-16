# Security

OrbitDrive is pre-release software. Do not expose a development gateway to the internet: development identities
are not real authentication.

- Report vulnerabilities privately to the repository owner (GitHub security advisory on this repository).
- Threat model: `docs/architecture/threat-model.md`. Known gaps are listed there and in `docs/IMPLEMENTATION_STATE.md`.
- The relay terminates TLS and can process file contents in transit. OrbitDrive does not claim end-to-end encryption.
- Never commit service-account keys, signing certificates or `.env` files. `VITE_*` values are public by design.
