# OrbitDrive

Browse folders you explicitly share from your Windows PC, and send files to your phone, through a
Windows-11-inspired web workspace. One repository: Windows agent/console, web app, gateway/relay and shared contracts.

**Status: in development.** See [docs/IMPLEMENTATION_STATE.md](docs/IMPLEMENTATION_STATE.md) for exactly what is
built and verified, and [docs/evidence/REQUIREMENTS_MATRIX.md](docs/evidence/REQUIREMENTS_MATRIX.md) for per-requirement status.
Nothing here is a production release. The specification is [docs/plans/OrbitDrive_Complete_Build_Plan.md](docs/plans/OrbitDrive_Complete_Build_Plan.md).

## What it is (and is not)

- The PC owner chooses which folders are shared. Access is **read-only** by default. Windows permissions still apply.
- The PC connects **outbound** over HTTPS/WSS to a relay. No router port forwarding, no FTP, no remote shell.
- The PC must be on, awake and running the agent. There is no cloud copy of your files.
- The relay terminates TLS and can process file bytes in transit. Nothing is stored there, but this is
  **not end-to-end encryption**.
- A file "handed to your browser" is not the same as "saved on your phone"; the browser owns the download.

## Layout

| Path | Component |
|---|---|
| `apps/desktop/OrbitDrive.Core` | Local filesystem policy: approved roots, safe path/handle resolution, revisions |
| `apps/desktop/OrbitDrive.Agent` | Per-user background agent (outbound control channel + byte uplink) |
| `apps/desktop/OrbitDrive.Desktop` | WPF owner console (currently a feasibility window) |
| `apps/web` | React + TypeScript + Vite web app (static, Firebase Hosting ready) |
| `services/gateway` | ASP.NET Core gateway and byte relay (separate origin from Hosting) |
| `packages/protocol` | Canonical schema; generated C# and TypeScript models |
| `tests/` | Contract, unit, real-process integration and browser tests |
| `infra/` | Firebase Hosting/rules config and relay container (drafts, not deployed) |
| `docs/` | Plan, architecture, threat model, evidence, continuation state |

## Run it locally (development identities only)

Requires the .NET 10 SDK and Node 22.

```sh
dotnet build OrbitDrive.slnx -c Release
node --test tests/integration/slice.test.mjs          # real gateway + agent: pairing, ranges, revocation, large file

# Manual run
export ASPNETCORE_ENVIRONMENT=Development OrbitDrive__DevIdentity__Enabled=true \
       OrbitDrive__DevIdentity__Secret=$(openssl rand -hex 32) OrbitDrive__AllowedWebOrigins__0=http://localhost:5173
(cd services/gateway/OrbitDrive.Gateway && dotnet run -c Release -- --urls http://127.0.0.1:5199) &
dotnet run -c Release --project apps/desktop/OrbitDrive.Agent -- roots add "My Share" /path/to/folder
dotnet run -c Release --project apps/desktop/OrbitDrive.Agent -- run --gateway http://127.0.0.1:5199 --web-origin http://localhost:5173
#   then type:  pair   (prints the pairing link and code)   approve | deny | pause | resume | quit
(cd apps/web && npm ci && VITE_GATEWAY_URL=http://127.0.0.1:5199 VITE_AUTH_MODE=dev npx vite --mode e2e)
```

On Windows PowerShell use `$env:NAME = "value"` instead of `export`.

Or, for the gateway and the web app together (development identity, `127.0.0.1` only): `node scripts/dev-start.mjs`, then start the agent as it prints.
