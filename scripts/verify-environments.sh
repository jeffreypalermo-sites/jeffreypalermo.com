#!/usr/bin/env bash
# Replays the URL contract against running sites: scripts/verify-environments.sh <base-url>...
# For each base URL it waits for /_health/ready (an environment that scaled to zero has a cold start), prints the
# version the site reports, and runs `tools/UrlContract verify`. It checks every URL given and exits 1 if any failed.
# READY_TIMEOUT: seconds to wait for readiness (default 600).
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$#" -eq 0 ]; then
  echo "usage: scripts/verify-environments.sh <base-url>..." >&2
  exit 2
fi

report() {
  echo "$1"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $1" >> "$GITHUB_STEP_SUMMARY"; fi
}

failed=0
for url in "$@"; do
  base="${url%/}"
  echo "==> $base"
  ready=""
  deadline=$((SECONDS + ${READY_TIMEOUT:-600}))
  until ready="$(curl --silent --fail --max-time 20 "$base/_health/ready")"; do
    if [ "$SECONDS" -ge "$deadline" ]; then break; fi
    sleep 5
  done
  if [ -z "$ready" ]; then
    report "FAIL $base: /_health/ready did not answer 200 within ${READY_TIMEOUT:-600} seconds"
    failed=1
    continue
  fi

  if dotnet run --project "$root/tools/UrlContract" --configuration Release -- \
       verify "$base/" "$root/tests/contract/url-contract.tsv" "$root/tests/contract/exceptions.tsv"; then
    report "PASS $base ($ready)"
  else
    report "FAIL $base ($ready): the URL contract is broken; the violations are in the log"
    failed=1
  fi
done
exit "$failed"
