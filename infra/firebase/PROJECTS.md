# Firebase projects

| Environment | Project ID | Created | Status |
|---|---|---|---|
| Development | `orbitdrive-dev` (project number 821727049510) | 2026-11-17, by Claude in the owner's console with the owner's approval | Spark (free) plan. Google Analytics off, Gemini off. Authentication on with **Email/Password** only. Web app "OrbitDrive Web" registered (`1:821727049510:web:ffdbc2b2c43bdfe47d73e7`). Hosting, Firestore, Cloud Messaging not enabled. Nothing deployed. |
| Staging | — | not created | |
| Production | — | not created | |

Web config values (`authDomain` `orbitdrive-dev.firebaseapp.com`, `projectId`, `appId`, and the web API key) are public by
design but are **not committed**: copy them from Project settings → General → Your apps → Config into a local
`apps/web/.env.local` (git-ignored) or CI variables. The gateway needs only `OrbitDrive__Firebase__ProjectId=orbitdrive-dev`.

No service-account key has been created. One will be needed for Firestore and token-revocation checks; the owner should
create it and store it as a GitHub Actions secret / relay host secret — never in the repository.
