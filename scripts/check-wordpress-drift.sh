#!/usr/bin/env bash
# Checks that the WordPress site has not changed since it was frozen (ADR-0010):
#   scripts/check-wordpress-drift.sh [freeze-file]
# The freeze file (content/archive/wordpress-freeze.json) records, for posts, pages and comments, how many the
# site's REST API listed on the day of the freeze and when the newest one last changed. This script asks the API
# for the same two facts and exits 1 when one differs, or when the API cannot be asked: content/ is edited in git
# now, so a change on the WordPress site would be lost unless someone carries it over by hand.
# The API is WordPress.com's own address for the site, which keeps answering after jeffreypalermo.com moves. It
# lists media only to a signed-in caller, so uploads are not checked: one shows on the site only through a post.
# REQUEST_PAUSE: seconds between requests (default 2; WordPress.com refuses bursts).
# RETRIES: how often a request that failed is tried again, 5 seconds apart (default 3).
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
freeze="${1:-$root/content/archive/wordpress-freeze.json}"
if [ ! -f "$freeze" ]; then
  echo "usage: scripts/check-wordpress-drift.sh [freeze-file]; there is no $freeze" >&2
  exit 2
fi

report() {
  echo "$1"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $1" >> "$GITHUB_STEP_SUMMARY"; fi
}

api="$(jq --raw-output '.api' "$freeze")"
frozen_on="$(jq --raw-output '.frozenOn' "$freeze")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failed=0
first=1
for resource in $(jq --raw-output '.resources | keys_unsorted[]' "$freeze"); do
  if [ "$first" -eq 0 ]; then sleep "${REQUEST_PAUSE:-2}"; fi
  first=0
  expected_total="$(jq --raw-output ".resources.$resource.total" "$freeze")"
  expected_newest="$(jq --raw-output ".resources.$resource.newest" "$freeze")"
  # A comment has no "modified": the newest by date stands for it.
  if [ "$resource" = "comments" ]; then order="date_gmt"; else order="modified"; fi

  if ! curl --silent --show-error --fail --max-time 30 --retry "${RETRIES:-3}" --retry-delay 5 --retry-all-errors \
       --user-agent "jeffreypalermo.com-drift-check/1.0" \
       --dump-header "$work/headers" --output "$work/body" \
       "$api/$resource?per_page=1&orderby=$order&order=desc&_fields=id,date_gmt,modified_gmt"; then
    report "FAIL $resource: $api/$resource could not be asked"
    failed=1
    continue
  fi

  total="$(grep --ignore-case '^x-wp-total:' "$work/headers" | tr --delete '\r' | cut --delimiter=' ' --fields=2)"
  newest="$(jq --raw-output '.[0] | (.modified_gmt // .date_gmt) // "none"' "$work/body")"
  if [ "$total" = "$expected_total" ] && [ "$newest" = "$expected_newest" ]; then
    report "PASS $resource: $total, the newest from $newest, as on $frozen_on"
  else
    report "FAIL $resource: the site has ${total:-no count}, the newest from $newest; frozen on $frozen_on with $expected_total, the newest from $expected_newest"
    failed=1
  fi
done
exit "$failed"
