# Relay operations (development relay)

Host: Oracle Cloud `orbitdrive-relay`, UAE East (Dubai), VM.Standard.E2.1.Micro (Always Free, 1 GB), Ubuntu 24.04,
public IP 139.185.60.75 (ephemeral), hostname `139-185-60-75.sslip.io` (free wildcard DNS that maps to the IP; a
third-party convenience for development until the owner has a domain).

## How it runs itself

| Need | Mechanism | Where |
|---|---|---|
| Deploy | The host pulls: a timer runs `orbitdrive-update` every 5 minutes, reads this repo's public releases for its channel (`relay-dev-*`), verifies the SHA-256, installs, restarts | `infra/relay/bin/orbitdrive-update` |
| No inbound admin access | Nothing connects *to* the host to deploy. No SSH keys, tokens or cloud credentials exist in GitHub or on the host for deployment | — |
| Health-gated release | A release is kept only if the gateway then reports exactly that version on `/v1/meta`; otherwise the previous release is restored and the bad version is never retried | updater |
| GitHub verifies reality | `relay-release.yml` waits until the live relay reports the new version, else fails and opens an issue | `.github/workflows/relay-release.yml` |
| Supervision | systemd `Restart=always`, memory ceilings (560 MB soft / 680 MB hard), sandboxed unprivileged user | `bootstrap.sh` |
| HTTPS | Caddy obtains and renews certificates automatically; streams without buffering | `bootstrap.sh` |
| Security updates | unattended-upgrades, reboot at 22:00 UTC when required | `bootstrap.sh` |
| Monitoring | `orbitdrive-monitor` every 5 min writes `/relay-status.json` (health, memory, swap, disk, monthly egress, alerts) | `infra/relay/bin/orbitdrive-monitor` |
| Notification | `relay-monitor.yml` probes from outside every 30 min; problems open/update a GitHub issue (emailed to the owner), recovery closes it | `.github/workflows/relay-monitor.yml` |
| Logs and disk | journald capped at 200 MB / 30 days; deploy log rotated weekly; installs refused above 90 % disk; three releases kept | `bootstrap.sh`, updater |
| Backup | Daily verified archive of configuration/state, seven kept, restore-tested on every run | `infra/relay/bin/orbitdrive-backup` |
| Audit trail | Git history + GitHub releases and workflow runs + `/var/log/orbitdrive/deploy.log` (JSON lines) on the host | — |

Trust model: anyone who can publish a `relay-dev-*` release in this repository can deploy to the development relay.
Releases are created only by `relay-release.yml` on `main` after tests. Build-provenance attestation and off-host
backups (Oracle Object Storage) are not implemented yet. Production has no automatic path: a `prod` channel host would
only follow `relay-prod-*` releases, which nothing creates today.

## One-time setup (the only routine SSH session)

```sh
curl -fsSL -o bootstrap.sh https://raw.githubusercontent.com/nisar87/OrbitDrive/main/infra/relay/bootstrap.sh
sudo bash bootstrap.sh --host 139-185-60-75.sslip.io --firebase-project orbitdrive-dev --web-origin https://orbitdrive-dev.web.app
```

Then set `"enabled": true` in `infra/relay/environments/dev.json` so deployment verification and monitoring start.

## Moving to the real domain (myorbitdrive.app)

DNS: `relay.myorbitdrive.app` A record to the relay's IP (Namecheap, done 2026-11-20); `app.myorbitdrive.app` CNAME to `orbitdrive-dev.web.app` (Firebase Hosting custom domain, plus `app.myorbitdrive.app` under Authentication, Authorized domains). Then, once on the host (the script is idempotent; the old names keep working because they are kept as extra hosts and origins):

```sh
curl -fsSL -o bootstrap.sh https://raw.githubusercontent.com/nisar87/OrbitDrive/main/infra/relay/bootstrap.sh
sudo bash bootstrap.sh --host relay.myorbitdrive.app --also-host 139-185-60-75.sslip.io --firebase-project orbitdrive-dev \
  --web-origin https://app.myorbitdrive.app --web-origin https://orbitdrive-dev.web.app
```

Caddy then obtains a certificate for the new name on the first request (it needs ports 80 and 443 open and the DNS record to resolve). Check `https://relay.myorbitdrive.app/relay-status.json`.

## Recovery when automation fails

- SSH from the owner's IP still works (`ubuntu@139.185.60.75`); the Oracle console can change the SSH rule if the IP changes.
- `sudo systemctl status orbitdrive-gateway caddy`, `sudo journalctl -u orbitdrive-gateway -n 100`, `cat /var/log/orbitdrive/deploy.log`.
- Force an update check: `sudo systemctl start orbitdrive-update`. Retry a rolled-back version: remove it from `/opt/orbitdrive/bad-versions`.
- Manual rollback: `sudo ln -sfn /opt/orbitdrive/releases/<version> /opt/orbitdrive/current && sudo systemctl restart orbitdrive-gateway`.
- Restore configuration: `sudo tar -xzf /var/backups/orbitdrive/<file> --absolute-names`.
- Total loss: create a new instance and run the bootstrap again. The only state worth restoring is
  `/var/lib/orbitdrive-gateway/registrations.json` (PCs, owners, approved/revoked browsers; secrets as hashes only),
  which the local backup includes. Without it every PC must be paired again; nothing else is lost.
- Durable registrations need the unit file and `gateway.env` written by the bootstrap from this commit or later
  (`StateDirectory=orbitdrive-gateway`, `OrbitDrive__DataDirectory`). A host bootstrapped earlier keeps running in
  memory-only mode until the bootstrap is re-run; the updater replaces the gateway binary only.

## What has and has not been proven

Proven in CI (`tests/relay/updater.test.mjs`, real scripts + real gateway builds): install, version-verified health
check, checksum refusal, automatic rollback, bad-version memory, malformed/malicious release refusal, disk guard,
pruning, monitor output and alerts, backup + restore check. **Not yet proven on the Oracle host:** `bootstrap.sh` end
to end (apt packages, systemd units, Caddy certificate issuance via sslip.io, journald/logrotate/unattended-upgrades
settings), behaviour within 1 GB under load, and the two GitHub workflows against the live relay.
- If `apt-get update` fails with 404 on `*.clouds.archive.ubuntu.com` (Oracle regional mirror), replace that host with
  `archive.ubuntu.com` in `/etc/apt/sources.list.d/ubuntu.sources`; the bootstrap stops before changing anything.
- Pairing attempt limits are per client address. The gateway only believes `X-Forwarded-For` from `OrbitDrive__TrustedProxies`
  (the bootstrap sets `127.0.0.1` and `::1`, i.e. Caddy on the same host). Without it every visitor shares Caddy's
  address and the 10-per-minute pairing limit becomes a single global bucket.
