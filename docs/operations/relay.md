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
| Log digest | The same monitor reads the gateway's own log (journald, last 6 min) and adds a `logs` block to `/relay-status.json`: request count, 5xx / 429 / 401+403 counts, slowest request, warning and error lines, gateway restarts, top failing **route templates** and top **exception type names**. Counts only: no message text, ids, addresses, accounts or file names. Alerts: `http_5xx_spike`, `gateway_errors`, `rate_limit_flood`, `auth_failures_spike`, `gateway_restarted` (thresholds `ORBIT_WARN_*`) | `infra/relay/bin/orbitdrive-monitor` |
| Health history | Every 30-min check appends one JSON line (counts and health only) to a rolling `relay-history` workflow artifact (newest ~2000 checks, 90-day retention). An artifact needs no write access to the repository, which the security workflow forbids for ordinary workflows | `relay-monitor.yml` |
| Daily review | A scheduled Claude task reads that history each morning, looks for trends and anomalies, and opens or updates a `[relay-review]` GitHub issue (read-only; it changes nothing on the relay) | scheduled task |
| Notification | `relay-monitor.yml` probes from outside every 30 min; problems open/update a GitHub issue (emailed to the owner), recovery closes it | `.github/workflows/relay-monitor.yml` |
| Logs and disk | journald capped at 200 MB / 30 days; deploy log rotated weekly; installs refused above 90 % disk; three releases kept | `bootstrap.sh`, updater |
| Backup | Daily verified archive of configuration/state, seven kept, restore-tested on every run | `infra/relay/bin/orbitdrive-backup` |
| Audit trail | Git history + GitHub releases and workflow runs + `/var/log/orbitdrive/deploy.log` (JSON lines) on the host | — |

Trust model: anyone who can publish a `relay-dev-*` release in this repository can deploy to the development relay.
Releases are created only by `relay-release.yml` on `main` after tests. Build-provenance attestation and off-host
backups (Oracle Object Storage) are not implemented yet. Production has no automatic path: a `prod` channel host would
only follow `relay-prod-*` releases, which nothing creates today.

**Applying the log digest to an existing host:** the updater replaces only the gateway binary; the host scripts come from the bootstrap. Re-run the pinned bootstrap once (same arguments as before) so `/usr/local/bin/orbitdrive-monitor` is the new version. Until then `/relay-status.json` has no `logs` block and the history lines simply lack it.

**Reading logs by hand:** `sudo journalctl -u orbitdrive-gateway --since "-1h" -p warning` for problems; every request line ends in a correlation id `[abc123...]` that the browser also receives as the `X-Request-Id` response header, so a user can quote it.

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


## Switching the relay to Firestore (durable metadata)

1. In the Firebase console (project `orbitdrive-dev`): Project settings, Service accounts, Generate new private key. Never paste or upload the file anywhere.
2. From your own computer: `scp firebase-key.json <relay-user>@relay.myorbitdrive.app:/tmp/firebase-key.json`, then on the relay: `sudo install -m 0640 -o root -g orbitdrive /tmp/firebase-key.json /etc/orbitdrive/firebase-key.json && rm /tmp/firebase-key.json`. Delete the copy on your computer.
3. Re-run the pinned bootstrap with the same arguments as before plus `--use-firestore`. Existing pairings are imported once from the old file store (which is left untouched as a backup).
4. Check `journalctl -u orbitdrive-gateway` for "importing" and no Firestore errors, then confirm a PC stays paired after `sudo systemctl restart orbitdrive-gateway`.

## Firebase account checks (disabled accounts and "sign out everywhere")

Without this, the relay accepts a valid sign-in token until it expires (up to an hour), even if you disable or delete the account or revoke its sessions in Firebase. With it, the relay also asks Firebase about the account (read-only `accounts:lookup`, one account at a time, answers reused for 60 s) and refuses a disabled/deleted account or a sign-in older than the last "revoke sessions". If Firebase cannot be asked, an answer from the last 15 minutes is used; with none, the request is refused as temporarily unavailable.

Needs the service-account key at `/etc/orbitdrive/firebase-key.json` (see the Firestore section). Re-run the pinned bootstrap with the same arguments plus `--check-revocation`. To turn it off, re-run without the flag.
