#!/usr/bin/env bash
# One-off repair for a relay host whose web-port rules were placed after the image's REJECT rule.
# Changes only the order of the two ACCEPT rules for TCP 80/443, restarts Caddy so it retries the certificate,
# then prints diagnostics. Does not touch SSH rules, TLS settings or the gateway. Safe to run more than once.
set -uo pipefail
[ "$(id -u)" = 0 ] || { echo "run with sudo" >&2; exit 1; }
echo "== firewall before"; iptables -L INPUT --line-numbers -n
for port in 80 443; do
  rule=(INPUT -m state --state NEW -p tcp --dport "$port" -j ACCEPT)
  while iptables -C "${rule[@]}" 2>/dev/null; do iptables -D "${rule[@]}"; done
  reject=$(iptables -L INPUT --line-numbers -n | awk '$2=="REJECT" || $2=="DROP" {print $1; exit}')
  if [ -n "$reject" ]; then iptables -I INPUT "$reject" "${rule[@]:1}"; else iptables -A "${rule[@]}"; fi
done
command -v netfilter-persistent >/dev/null && netfilter-persistent save >/dev/null 2>&1
echo "== firewall after"; iptables -L INPUT --line-numbers -n
systemctl restart caddy
echo "== waiting 60 s for the certificate"; sleep 60
host=$(awk 'NR==1 {print $1}' /etc/caddy/Caddyfile)
echo "== services"; systemctl is-active orbitdrive-gateway caddy orbitdrive-update.timer orbitdrive-monitor.timer orbitdrive-backup.timer
echo "== listeners"; ss -tlnp | grep -E ':(80|443|8080)\s'
echo "== deploy log"; cat /var/log/orbitdrive/deploy.log 2>/dev/null || echo "(none)"
echo "== gateway (local)"; curl -sS -m 10 http://127.0.0.1:8080/v1/meta; echo
echo "== gateway (public HTTPS, certificate verified)"; curl -sS -m 20 "https://$host/v1/meta"; echo
echo "== caddy log (last 25 lines)"; journalctl -u caddy -n 25 --no-pager | cut -c1-300
echo "== memory"; free -m
