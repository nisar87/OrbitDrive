# Working in this repository

Read `docs/IMPLEMENTATION_STATE.md` first, then `docs/plans/OrbitDrive_Complete_Build_Plan.md` (the specification).

## Rules
- Follow the milestone order in the plan. Each phase: implement → positive/negative/security/regression tests →
  local run → push to `feature/orbitdrive-foundation` → inspect GitHub Actions for that exact SHA → fix → record
  evidence in `docs/evidence/` and update `REQUIREMENTS_MATRIX.md` and `IMPLEMENTATION_STATE.md`.
- Never weaken assertions, skip critical tests, fake CI results or present browser/emulator output as Windows or phone evidence.
- Do not deploy Firebase, create paid resources, change DNS, publish installers or merge to `main` without the owner's explicit say-so.
- Read-only by default; no remote execution, FTP, whole-file buffering or E2EE claims.

## Commands
- Build: `dotnet build OrbitDrive.slnx -c Release` (WPF project builds on Windows only: `dotnet build apps/desktop/OrbitDrive.Desktop`)
- Protocol: edit `packages/protocol/schema/protocol.schema.json`, then `node packages/protocol/generate.mjs` (CI runs `--check`)
- Integration (real processes): `node --test tests/integration/slice.test.mjs` (`ORBIT_LARGE_MIB=1024` for the 1 GiB case)
- C# unit tests: `dotnet test tests/<area>/<project>` (needs nuget.org)
- Web: `cd apps/web && npm ci && npx tsc --noEmit && npx vitest run && npx playwright test`
- CI inspection: `gh api "repos/nisar87/OrbitDrive/actions/runs?per_page=20"`; job logs via `gh api repos/nisar87/OrbitDrive/actions/jobs/<id>/logs`
