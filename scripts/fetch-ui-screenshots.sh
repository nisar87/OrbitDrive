#!/usr/bin/env bash
# Extracts the owner-console screenshots that the desktop-ui workflow prints into its job log.
# Usage: scripts/fetch-ui-screenshots.sh <commit-sha> <output-dir>
set -euo pipefail
sha=$1; out=$2; mkdir -p "$out"
run=$(gh api "repos/nisar87/OrbitDrive/actions/runs?head_sha=$sha&per_page=30" --jq '[.workflow_runs[] | select(.name=="desktop-ui")][0].id')
job=$(gh api "repos/nisar87/OrbitDrive/actions/runs/$run/jobs" --jq '.jobs[0].id')
gh api "repos/nisar87/OrbitDrive/actions/jobs/$job/logs" | tr -d '\r' | sed 's/^[0-9T:.Z-]* //' | awk -v out="$out" '
  /^@@PNG-BEGIN /{file=out "/" $2 ".b64"; printf "" > file; next}
  /^@@PNG-END /{close(file); file=""; next}
  file!="" && /^@@/{print substr($0,3) >> file}'
for f in "$out"/*.b64; do base64 -d "$f" > "${f%.b64}"; rm "$f"; done
ls -la "$out"
