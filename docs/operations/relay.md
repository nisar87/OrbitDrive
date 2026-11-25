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
| Health history | The host keeps its OWN rolling 24 hours (`last24h` in `/relay-status.json`: totals, worst values and every alert raised, one counts-only sample per 5-minute run), because GitHub runs the outside check only a few times a day. Each outside check also prints the status into its run log and saves a rolling `relay-history` artifact. Neither needs write access to the repository, which the security workflow forbids for ordinary workflows | `orbitdrive-monitor`, `relay-monitor.yml` |
| Daily review | A scheduled Claude task reads the newest `last24h` and `recentAlerts` from the outside check's run logs each morning, looks for trends and anomalies, and opens or updates a `[relay-review]` GitHub issue (read-only; it changes nothing on the relay) | scheduled task |
| Notification | `relay-monitor.yml` probes from outside every 30 min; problems open/update a GitHub issue (emailed to the owner), recovery closes it | `.github/workflows/relay-monitor.yml` |
| Logs and disk | journald capped at 200 MB / 30 days; deploy log rotated weekly; installs refused above 90 % disk; three releases kept | `bootstrap.sh`, updater |
| Backup | Daily verified archive of configuration/state, seven kept, restore-tested on every run. Invitation records are left out. An encrypted copy can be sent off the host (**ready but off** until configured, see "Off-host backup and the restore drill") | `infra/relay/bin/orbitdrive-backup`, `orbitdrive-restore-verify` |
| Audit trail | Git history + GitHub releases and workflow runs + `/var/log/orbitdrive/deploy.log` (JSON lines) on the host | — |

Trust model: anyone who can publish a `relay-dev-*` release in this repository can deploy to the development relay.
Releases are created only by `relay-release.yml` on `main` after tests. The updater refuses a release whose version is lower than the running one (**enforced**), and verifies a detached signature when the host has a signing key (**ready but off** until the owner supplies a key, see "Signed releases"). Build-provenance attestation is not implemented. Production has no automatic path: a `prod` channel host would
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


## What is enforced and what is ready but off

Honest status of the controls added after the AI-assisted review of 2026-11-25 (it was an AI-assisted review, not an independent audit). "Enforced" means the code does it on every run. "Ready but off" means the code exists and is tested, but does nothing until the owner supplies something that only the owner can supply.

| Control | State | What switches it on |
|---|---|---|
| Unpaired PCs are never saved; a cap and an idle clean-up apply; the agent version text is capped at 32 characters (F-001) | Enforced | nothing |
| Waiting control connections are limited per client address and in total, and the wait is 4 seconds (F-002) | Enforced | nothing |
| Operator changes need a reason of 3 to 200 characters and the audit record is written before the change (F-006) | Enforced | nothing |
| A damaged `billing.json` is set aside, the relay starts, plan limits and ordering stay off, and the console and monitor say so (F-009) | Enforced | nothing |
| Two relay processes cannot share one data directory (F-019) | Enforced | nothing |
| The relay keeps no file names: an invitation holds an id, size and expiry, and a name sealed by the PC (F-003) | Enforced | nothing |
| Ordering is hidden and refused until the start date (one switch, "plan limits", in the console) | Enforced | the owner opens ordering by switching plan limits on |
| The updater refuses a lower version (F-011) | Enforced | nothing |
| Operator second factor (F-005) | **Ready but off** | see "Operator second factor" |
| Encrypted off-host backup (F-010) | **Ready but off** | see "Off-host backup and the restore drill" |
| Signed relay releases (F-011) | **Ready but off** | see "Signed releases" |

## Operators (`--operator-uid`)

The people who may use the owner console are listed in `gateway.env`. Give each Firebase user id once on the bootstrap command line (repeat the flag for more than one):

```sh
sudo bash bootstrap.sh <the same arguments as before> --operator-uid <firebase-uid>
```

Find the id in the Firebase console (Authentication, Users, "User UID"). Running the bootstrap again without the flag keeps the operator lines that are already in `gateway.env`; it never removes the operator by accident.

## Operator second factor (F-005, ready but off)

The relay can refuse operator calls whose sign-in did not use a second factor. It is **off by default so the owner cannot be locked out**; the console overview shows a notice while it is off. To turn it on:

1. In the Firebase console: Authentication, Sign-in method, enable multi-factor authentication (SMS or TOTP; this may need the Identity Platform upgrade, which can have a cost: check before enabling). Then enrol the operator account in the web app or with the Firebase SDK.
2. Sign in as the operator and confirm the console overview shows "second factor present" (the sign-in token carries `firebase.sign_in_second_factor`).
3. Only then re-run the bootstrap with `--require-operator-second-factor`. To turn it off again, edit `/etc/orbitdrive/gateway.env` (remove the line `OrbitDrive__RequireOperatorSecondFactor=true`) and restart the gateway.

Not verified here: a real Firebase sign-in with a second factor. The relay's check is tested with a development sign-in that carries the same claim, not with Google's tokens.

## Off-host backup and the restore drill (F-010, ready but off)

The daily archive is verified locally and kept seven times on the same disk. A copy on the same disk does not protect against losing the disk. To send an encrypted copy elsewhere, create `/etc/orbitdrive/backup.env` (mode 0640, group `orbitdrive`; the bootstrap reads it into the backup service):

```sh
ORBIT_BACKUP_UPLOAD_CMD='rclone copyto "$1" remote:orbitdrive-backups/'   # any command that takes the file path as $1
ORBIT_BACKUP_PASSPHRASE_FILE=/etc/orbitdrive/backup-passphrase            # one line, mode 0600
```

What the script does with it: encrypts the archive with GnuPG (AES-256, symmetric) before it leaves the host, checks that the encrypted file opens with the passphrase, runs your command, and removes the encrypted file. It refuses to send anything unencrypted (exit 3), and a failed upload keeps the local archive and exits with an error (exit 5). Keep a copy of the passphrase away from this server, or the off-host copy cannot be opened after the server is lost. Invitation records are left out of every archive.

Nothing here chooses or pays for a storage service. Oracle Object Storage, another free tier or a drive of your own all work with a suitable command.

### Restore drill (do it once before the first paid order, then every few months)

`orbitdrive-restore-verify` unpacks an archive into a scratch directory, checks it and deletes the scratch directory. It never writes to the live paths.

```sh
# a local archive
sudo orbitdrive-restore-verify /var/backups/orbitdrive/orbitdrive-<time>.tar.gz
# an off-host copy you downloaded
sudo orbitdrive-restore-verify ./orbitdrive-<time>.tar.gz.gpg /path/to/passphrase-file
```

It must print `restore drill ok`. It fails if the archive does not decrypt, list or extract, if a JSON state file does not parse, or if invitation records are in it. A full rehearsal on a fresh server (new VM, `bootstrap.sh`, then `sudo tar -xzf <archive> --absolute-names`, restart, confirm a PC stays paired) is still a manual step that has not been done on the real host.

## Signed releases (F-011, ready but off)

Releases are checked against a checksum that travels with the release, so anyone who can publish a release can install code on the relay. A detached signature, checked against a key that is **not** on GitHub, closes that. The updater verifies it only when the host has a public key:

1. On your own computer, once: `node scripts/relay-sign.mjs generate ./keys` creates `release-signing.key` (keep it offline) and `release-signing.pub`.
2. Store the private key's text as the GitHub Actions secret `RELAY_SIGNING_KEY` (Settings, Secrets and variables, Actions). Releases made after that carry a `.sig` file; without the secret they are published as before, unsigned.
3. Copy the public key to the host as `/etc/orbitdrive/release-signing.pub` (root, mode 0644). From then on the updater refuses a release with no signature, a damaged signature or a signature by another key, and the deploy log says "signature verified". With no key on the host it logs "checksum only: no signing key on this host".

Order matters: publish one signed release first, then install the public key, or the next unsigned release is refused.

## Billing file damaged

If `billing.json` cannot be read at start, the relay moves it to `billing.json.damaged-<time>` beside it, starts with empty billing, **holds plan limits and ordering off** (so nobody is wrongly moved to the Free plan, and nobody can order), and raises `billing_data_damaged` in `/relay-status.json`. Restore the file from the latest backup (`orbitdrive-restore-verify` first, then copy `billing.json` back), restart the gateway, and remove the `.damaged-` file when satisfied.

## Switching to Firestore: what moves

`--use-firestore` imports pairings (as before) and now also imports `billing.json` once, and keeps the operator audit log, the minimum PC version and the invitation records (without names) in Firestore. The old files are left on disk, unused. Not run against real Firestore here: the Firestore code was verified in CI only where the emulator job runs it.
