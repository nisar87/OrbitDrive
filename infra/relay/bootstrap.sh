#!/usr/bin/env bash
# One-time (idempotent) setup of an OrbitDrive relay host. Ubuntu 24.04, x86-64 or ARM64, 1 GB RAM is enough.
# After this, the host maintains itself: releases are pulled and health-checked, the gateway is supervised,
# certificates renew, security updates install, logs rotate, backups run. No inbound SSH is needed for routine work.
#   sudo bash bootstrap.sh --host <public-hostname> --firebase-project <id> --web-origin <https://origin> [--channel dev]
set -euo pipefail
HOST=""; PROJECT=""; ORIGIN=""; CHANNEL=dev
REPO_RAW=${ORBIT_REPO_RAW:-https://raw.githubusercontent.com/nisar87/OrbitDrive/main}
while [ $# -gt 0 ]; do case "$1" in
  --host) HOST=$2; shift 2;; --firebase-project) PROJECT=$2; shift 2;; --web-origin) ORIGIN=$2; shift 2;; --channel) CHANNEL=$2; shift 2;;
  *) echo "unknown option $1" >&2; exit 2;; esac; done
[[ $HOST =~ ^[a-z0-9.-]+$ && $PROJECT =~ ^[a-z0-9-]{4,40}$ && $ORIGIN =~ ^https://[a-z0-9.-]+$ && $CHANNEL =~ ^[a-z]+$ ]] || { echo "usage: bootstrap.sh --host H --firebase-project P --web-origin https://O" >&2; exit 2; }
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }

# Swap first: package installs and the .NET runtime need headroom on a 1 GB host.
if ! swapon --show | grep -q /swapfile; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >>/etc/fstab
fi
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q aspnetcore-runtime-10.0 caddy unattended-upgrades jq curl

id orbitdrive >/dev/null 2>&1 || useradd --system --home /opt/orbitdrive --shell /usr/sbin/nologin orbitdrive
install -d -o root -g root -m 0755 /opt/orbitdrive /opt/orbitdrive/releases /var/log/orbitdrive
install -d -o root -g orbitdrive -m 0750 /etc/orbitdrive
install -d -o root -g root -m 0755 /var/lib/orbitdrive /var/lib/orbitdrive/public
for tool in orbitdrive-update orbitdrive-monitor orbitdrive-backup; do
  curl -fsSL -o "/usr/local/bin/$tool" "$REPO_RAW/infra/relay/bin/$tool" && chmod 0755 "/usr/local/bin/$tool"
done

# Gateway configuration. No secrets: Firebase tokens are verified against Google's public keys.
cat >/etc/orbitdrive/gateway.env <<ENV
ASPNETCORE_ENVIRONMENT=Production
OrbitDrive__Firebase__ProjectId=$PROJECT
OrbitDrive__AllowedWebOrigins__0=$ORIGIN
OrbitDrive__DataDirectory=/var/lib/orbitdrive-gateway
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
  printf '[Unit]\nDescription=%s\n[Service]\nType=oneshot\nEnvironmentFile=-/etc/orbitdrive/updater.env\nExecStart=%s\n' "$desc" "$cmd" >"/etc/systemd/system/$name.service"
  { printf '[Unit]\nDescription=%s (timer)\n[Timer]\n' "$desc"; printf '%s\n' "$@"; printf 'Persistent=true\n[Install]\nWantedBy=timers.target\n'; } >"/etc/systemd/system/$name.timer"
}
unit orbitdrive-update "OrbitDrive release updater" /usr/local/bin/orbitdrive-update "OnBootSec=1min" "OnUnitActiveSec=5min" "RandomizedDelaySec=30"
unit orbitdrive-monitor "OrbitDrive host monitor" /usr/local/bin/orbitdrive-monitor "OnBootSec=2min" "OnUnitActiveSec=5min"
unit orbitdrive-backup "OrbitDrive configuration backup" /usr/local/bin/orbitdrive-backup "OnCalendar=daily" "RandomizedDelaySec=1h"

# HTTPS: Caddy obtains and renews the certificate automatically. Responses are streamed, never buffered.
cat >/etc/caddy/Caddyfile <<CADDY
$HOST {
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
