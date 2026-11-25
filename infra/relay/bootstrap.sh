#!/usr/bin/env bash
# One-time (idempotent) setup of an OrbitDrive relay host. Ubuntu 24.04, x86-64 or ARM64, 1 GB RAM is enough.
# After this, the host maintains itself: releases are pulled and health-checked, the gateway is supervised,
# certificates renew, security updates install, logs rotate, backups run. No inbound SSH is needed for routine work.
#   sudo bash bootstrap.sh --host <public-hostname> --firebase-project <id> --web-origin <https://origin> [--channel dev]
#   --web-origin may be repeated (every origin may call the relay from a browser); --also-host may be repeated (more names this relay answers to,
#   e.g. the previous hostname during a move, so installed apps and open sites keep working).
#   --operator-uid <firebase uid> (repeatable) lists the accounts that may use the owner console; it is written to gateway.env as OrbitDrive__OperatorUids__N.
#   Without it, any operator lines already in gateway.env are kept (running this script again never removes the operator).
#   --require-operator-second-factor makes the relay refuse operator calls whose sign-in did not use a second factor. Turn on two-step sign-in for
#   the operator account in Firebase FIRST (docs/operations/relay.md); like the operator lines, an existing setting is kept when the flag is not given.
set -euo pipefail
HOST=""; PROJECT=""; ORIGIN=""; CHANNEL=dev; ORIGINS=(); ALSO_HOSTS=(); USE_FIRESTORE=0; CHECK_REVOCATION=0; OPERATORS=(); REQUIRE_2FA=0
REPO_RAW=${ORBIT_REPO_RAW:-https://raw.githubusercontent.com/nisar87/OrbitDrive/main}
while [ $# -gt 0 ]; do case "$1" in
  --host) HOST=$2; shift 2;; --firebase-project) PROJECT=$2; shift 2;; --web-origin) ORIGINS+=("$2"); shift 2;; --also-host) ALSO_HOSTS+=("$2"); shift 2;; --channel) CHANNEL=$2; shift 2;; --use-firestore) USE_FIRESTORE=1; shift;; --check-revocation) CHECK_REVOCATION=1; shift;; --operator-uid) OPERATORS+=("$2"); shift 2;; --require-operator-second-factor) REQUIRE_2FA=1; shift;;
  *) echo "unknown option $1" >&2; exit 2;; esac; done
[[ $HOST =~ ^[a-z0-9.-]+$ && $PROJECT =~ ^[a-z0-9-]{4,40}$ && $CHANNEL =~ ^[a-z]+$ ]] || { echo "usage: bootstrap.sh --host H --firebase-project P --web-origin https://O" >&2; exit 2; }
ORIGIN=${ORIGINS[0]:-}
[[ $ORIGIN =~ ^https://[a-z0-9.-]+$ ]] || { echo "need at least one valid --web-origin https://..." >&2; exit 2; }
for o in "${ORIGINS[@]}"; do [[ $o =~ ^https://[a-z0-9.-]+$ ]] || { echo "bad --web-origin $o" >&2; exit 2; }; done
for h in "${ALSO_HOSTS[@]}"; do [[ $h =~ ^[a-z0-9.-]+$ ]] || { echo "bad --also-host $h" >&2; exit 2; }; done
for u in "${OPERATORS[@]}"; do [[ $u =~ ^[A-Za-z0-9._:-]{1,128}$ ]] || { echo "bad --operator-uid (a Firebase user id: letters, digits, . _ : -)" >&2; exit 2; }; done
SITES=$HOST; for h in "${ALSO_HOSTS[@]}"; do SITES="$SITES, $h"; done
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }

# Swap first: package installs and the .NET runtime need headroom on a 1 GB host.
if ! swapon --show | grep -q /swapfile; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >>/etc/fstab
fi
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q aspnetcore-runtime-10.0 caddy unattended-upgrades jq curl gnupg openssl

id orbitdrive >/dev/null 2>&1 || useradd --system --home /opt/orbitdrive --shell /usr/sbin/nologin orbitdrive
install -d -o root -g root -m 0755 /opt/orbitdrive /opt/orbitdrive/releases /var/log/orbitdrive
install -d -o root -g orbitdrive -m 0750 /etc/orbitdrive
install -d -o root -g root -m 0755 /var/lib/orbitdrive /var/lib/orbitdrive/public
for tool in orbitdrive-update orbitdrive-monitor orbitdrive-backup orbitdrive-restore-verify; do
  curl -fsSL -o "/usr/local/bin/$tool" "$REPO_RAW/infra/relay/bin/$tool" && chmod 0755 "/usr/local/bin/$tool"
done

# Optional: durable control metadata in Firestore instead of the local file. The service-account key is NOT handled here: the owner copies it to
# /etc/orbitdrive/firebase-key.json (mode 0640, group orbitdrive) and this refuses to switch until it is there. The previous file store is imported once.
STORE_ENV="OrbitDrive__DataDirectory=/var/lib/orbitdrive-gateway"
KEY=/etc/orbitdrive/firebase-key.json
REVOCATION_ENV=""
if [ "$CHECK_REVOCATION" = 1 ]; then
  # Asks Firebase whether an account is disabled/deleted or had its sessions revoked (about a minute of delay at most). Needs the same key as Firestore.
  [ -s "$KEY" ] || { echo "FATAL: --check-revocation needs the service-account key at $KEY." >&2; exit 1; }
  chgrp orbitdrive "$KEY"; chmod 0640 "$KEY"
  REVOCATION_ENV="OrbitDrive__Firebase__CheckRevocation=true
GOOGLE_APPLICATION_CREDENTIALS=$KEY"
fi
if [ "$USE_FIRESTORE" = 1 ]; then
  [ -s "$KEY" ] || { echo "FATAL: --use-firestore needs the service-account key at $KEY (copy it there first; never paste it into a chat)." >&2; exit 1; }
  chgrp orbitdrive "$KEY"; chmod 0640 "$KEY"
  STORE_ENV="OrbitDrive__Firestore__ProjectId=$PROJECT
OrbitDrive__Firestore__ImportFromDataDirectory=/var/lib/orbitdrive-gateway"
  [ "$CHECK_REVOCATION" = 1 ] || STORE_ENV="$STORE_ENV
GOOGLE_APPLICATION_CREDENTIALS=$KEY"
fi

# Operator accounts and the second-factor switch. Given on the command line, they are written; otherwise what the file already holds is kept, so
# running this script again (an update, a move to Firestore) never silently removes the operator or turns a protection off.
OPERATOR_ENV=""
if [ ${#OPERATORS[@]} -gt 0 ]; then
  i=0; for u in "${OPERATORS[@]}"; do OPERATOR_ENV="$OPERATOR_ENV
OrbitDrive__OperatorUids__$i=$u"; i=$((i+1)); done
elif [ -f /etc/orbitdrive/gateway.env ]; then
  OPERATOR_ENV=$(grep -E '^OrbitDrive__OperatorUids__[0-9]+=' /etc/orbitdrive/gateway.env || true)
fi
SECOND_FACTOR_ENV=""
if [ "$REQUIRE_2FA" = 1 ]; then SECOND_FACTOR_ENV="OrbitDrive__RequireOperatorSecondFactor=true"
elif [ -f /etc/orbitdrive/gateway.env ]; then SECOND_FACTOR_ENV=$(grep -E '^OrbitDrive__RequireOperatorSecondFactor=' /etc/orbitdrive/gateway.env || true); fi

# Gateway configuration. No secrets: Firebase tokens are verified against Google's public keys.
cat >/etc/orbitdrive/gateway.env <<ENV
ASPNETCORE_ENVIRONMENT=Production
OrbitDrive__Firebase__ProjectId=$PROJECT
$(i=0; for o in "${ORIGINS[@]}"; do echo "OrbitDrive__AllowedWebOrigins__$i=$o"; i=$((i+1)); done)
OrbitDrive__TrustedProxies__0=127.0.0.1
OrbitDrive__TrustedProxies__1=::1
$STORE_ENV
$REVOCATION_ENV
$OPERATOR_ENV
$SECOND_FACTOR_ENV
DOTNET_gcServer=0
DOTNET_GCHeapHardLimit=0x18000000
ENV
chmod 0640 /etc/orbitdrive/gateway.env; chgrp orbitdrive /etc/orbitdrive/gateway.env
echo "ORBIT_CHANNEL=$CHANNEL" >/etc/orbitdrive/updater.env

cat >/etc/systemd/system/orbitdrive-gateway.service <<'UNIT'
[Unit]
Description=OrbitDrive gateway and relay
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=0
[Service]
User=orbitdrive
EnvironmentFile=/etc/orbitdrive/gateway.env
WorkingDirectory=/opt/orbitdrive/current
StateDirectory=orbitdrive-gateway
StateDirectoryMode=0700
ExecStart=/usr/bin/dotnet /opt/orbitdrive/current/OrbitDrive.Gateway.dll --urls http://127.0.0.1:8080
Restart=always
RestartSec=3
MemoryHigh=560M
MemoryMax=680M
LimitNOFILE=16384
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
[Install]
WantedBy=multi-user.target
UNIT
unit() { # name, description, command, schedule lines...
  local name=$1 desc=$2 cmd=$3; shift 3
  printf '[Unit]\nDescription=%s\n[Service]\nType=oneshot\nEnvironmentFile=-/etc/orbitdrive/updater.env\nEnvironmentFile=-/etc/orbitdrive/backup.env\nExecStart=%s\n' "$desc" "$cmd" >"/etc/systemd/system/$name.service"
  { printf '[Unit]\nDescription=%s (timer)\n[Timer]\n' "$desc"; printf '%s\n' "$@"; printf 'Persistent=true\n[Install]\nWantedBy=timers.target\n'; } >"/etc/systemd/system/$name.timer"
}
unit orbitdrive-update "OrbitDrive release updater" /usr/local/bin/orbitdrive-update "OnBootSec=1min" "OnUnitActiveSec=5min" "RandomizedDelaySec=30"
unit orbitdrive-monitor "OrbitDrive host monitor" /usr/local/bin/orbitdrive-monitor "OnBootSec=2min" "OnUnitActiveSec=5min"
unit orbitdrive-backup "OrbitDrive configuration backup" /usr/local/bin/orbitdrive-backup "OnCalendar=daily" "RandomizedDelaySec=1h"

# HTTPS: Caddy obtains and renews the certificate automatically. Responses are streamed, never buffered.
cat >/etc/caddy/Caddyfile <<CADDY
$SITES {
	header Strict-Transport-Security "max-age=31536000"
	handle /relay-status.json {
		root * /var/lib/orbitdrive/public
		rewrite * /status.json
		header Cache-Control "no-store"
		file_server
	}
	handle {
		reverse_proxy 127.0.0.1:8080 {
			flush_interval -1
		}
	}
}
CADDY

# Logs: bounded journal, rotated deploy log. Security updates: automatic, with a night-time reboot when required.
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=200M\nMaxRetentionSec=30day\n' >/etc/systemd/journald.conf.d/orbitdrive.conf
printf '/var/log/orbitdrive/*.log {\n  weekly\n  rotate 12\n  compress\n  missingok\n  notifempty\n  copytruncate\n}\n' >/etc/logrotate.d/orbitdrive
printf 'APT::Periodic::Update-Package-Lists "1";\nAPT::Periodic::Unattended-Upgrade "1";\n' >/etc/apt/apt.conf.d/20auto-upgrades
printf 'Unattended-Upgrade::Automatic-Reboot "true";\nUnattended-Upgrade::Automatic-Reboot-Time "22:00";\n' >/etc/apt/apt.conf.d/52orbitdrive-reboot

# Host firewall: Oracle's Ubuntu images end the INPUT chain with a REJECT rule and allow only SSH before it.
# The web ports must be accepted BEFORE that rule; a rule placed after it is never reached (this was the cause of
# the first TLS failure on the development relay: the ACME challenge could not get in, so no certificate was issued).
open_port() {
  local rule=(INPUT -m state --state NEW -p tcp --dport "$1" -j ACCEPT)
  while iptables -C "${rule[@]}" 2>/dev/null; do iptables -D "${rule[@]}"; done   # drop any misplaced copies
  local reject; reject=$(iptables -L INPUT --line-numbers -n | awk '$2=="REJECT" || $2=="DROP" {print $1; exit}')
  if [ -n "$reject" ]; then iptables -I INPUT "$reject" "${rule[@]:1}"; else iptables -A "${rule[@]}"; fi
}
open_port 80; open_port 443
command -v netfilter-persistent >/dev/null && netfilter-persistent save >/dev/null

systemctl daemon-reload
systemctl restart systemd-journald
systemctl enable --now orbitdrive-update.timer orbitdrive-monitor.timer orbitdrive-backup.timer
systemctl enable orbitdrive-gateway.service
systemctl start orbitdrive-update.service || true   # installs the newest release and starts the gateway
systemctl try-restart orbitdrive-gateway.service    # a re-run must apply a changed unit/env even when no new release was installed
systemctl enable --now caddy && systemctl reload caddy
echo "bootstrap complete: https://$HOST/healthz (channel $CHANNEL)"
