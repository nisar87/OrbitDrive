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

## Recovery when automation fails

- SSH from the owner's IP still works (`ubuntu@139.185.60.75`); the Oracle console can change the SSH rule if the IP changes.
- `sudo systemctl status orbitdrive-gateway caddy`, `sudo journalctl -u orbitdrive-gateway -n 100`, `cat /var/log/orbitdrive/deploy.log`.
- Force an update check: `sudo systemctl start orbitdrive-update`. Retry a rolled-back version: remove it from `/opt/orbitdrive/bad-versions`.
- Manual rollback: `sudo ln -sfn /opt/orbitdrive/releases/<version> /opt/orbitdrive/current && sudo systemctl restart orbitdrive-gateway`.
- Restore configuration: `sudo tar -xzf /var/backups/orbitdrive/<file> --absolute-names`.
- Total loss: create a new instance and run the bootstrap again; no state on the host is irreplaceable today (gateway
  metadata is still in memory — W3).

## What has and has not been proven

Proven in CI (`tests/relay/updater.test.mjs`, real scripts + real gateway builds): install, version-verified health
check, checksum refusal, automatic rollback, bad-version memory, malformed/malicious release refusal, disk guard,
pruning, monitor output and alerts, backup + restore check. **Not yet proven on the Oracle host:** `bootstrap.sh` end
to end (apt packages, systemd units, Caddy certificate issuance via sslip.io, journald/logrotate/unattended-upgrades
settings), behaviour within 1 GB under load, and the two GitHub workflows against the live relay.
