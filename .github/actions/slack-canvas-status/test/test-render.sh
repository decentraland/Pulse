#!/usr/bin/env bash
# Unit tests for the line rendering helpers in update-canvas.sh.
set -euo pipefail
cd "$(dirname "$0")"

source ../update-canvas.sh

fail() { printf 'FAIL: %s\n  expected: %s\n  actual:   %s\n' "$1" "$2" "$3"; exit 1; }
assert_eq() { [[ "$2" == "$3" ]] || fail "$1" "$2" "$3"; }

SHA=aaaabbbbccccddddeeeeffff0000111122223333
IMAGE="quay.io/decentraland/pulse-server:${SHA}"

assert_eq "running line" \
  'main @ aaaabbbb · pulse-server:aaaabbbbcccc… · since 2026-07-17 14:13 UTC' \
  "$(render_running_line refs/heads/main "$SHA" "$IMAGE" 2026-07-17T14:13:53Z)"

assert_eq "running line omits empty image" \
  'v0.9.2 @ aaaabbbb · since 2026-07-17 14:13 UTC' \
  "$(render_running_line refs/tags/v0.9.2 "$SHA" "" 2026-07-17T14:13:53Z)"

assert_eq "last deploy success" \
  '✅ success · main @ aaaabbbb · 2026-07-17 14:13 UTC' \
  "$(render_last_deploy_line success refs/heads/main "$SHA" 2026-07-17T14:13:53Z)"

assert_eq "last deploy failure" \
  '❌ failure · v0.9.3 @ aaaabbbb · 2026-07-17 14:13 UTC' \
  "$(render_last_deploy_line failure refs/tags/v0.9.3 "$SHA" 2026-07-17T14:13:53Z)"

assert_eq "error emoji" "❌" "$(state_emoji error)"
assert_eq "in_progress emoji" "🔄" "$(state_emoji in_progress)"
assert_eq "queued emoji" "🔄" "$(state_emoji queued)"
assert_eq "unknown state emoji" "❔" "$(state_emoji superseded)"

assert_eq "short tag not truncated" "pulse-server:short" "$(short_image quay.io/decentraland/pulse-server:short)"
assert_eq "untagged image passthrough" "pulse-server" "$(short_image quay.io/decentraland/pulse-server)"
assert_eq "branch ref shortened" "main" "$(short_ref refs/heads/main)"
assert_eq "plain ref unchanged" "main" "$(short_ref main)"

echo "render tests passed"
