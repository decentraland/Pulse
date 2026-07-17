#!/usr/bin/env bash
# Unit tests for the canvas line rendering functions in update-canvas.sh.
set -euo pipefail
cd "$(dirname "$0")"

export ENVIRONMENT=dev STATE=success \
  REF=refs/heads/main SHA=725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f \
  DOCKER_IMAGE=quay.io/decentraland/pulse-server:725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f \
  TIMESTAMP=2026-07-17T14:13:53Z \
  TARGET_URL=https://dcl.tools/ops/services-pipeline/-/pipelines/59715 \
  REPO_URL=https://github.com/decentraland/Pulse

source ../update-canvas.sh

fail() { printf 'FAIL: %s\n  expected: %s\n  actual:   %s\n' "$1" "$2" "$3"; exit 1; }
assert_eq() { [[ "$2" == "$3" ]] || fail "$1" "$2" "$3"; }

assert_eq "running line" \
  '**DEV running:** `main` @ [725ed44d](https://github.com/decentraland/Pulse/commit/725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f) · `pulse-server:725ed44d0e4c…` · since 2026-07-17 14:13 UTC' \
  "$(render_running_line)"

assert_eq "last deploy line with pipeline link" \
  '**DEV last deploy:** ✅ success · `main` @ [725ed44d](https://github.com/decentraland/Pulse/commit/725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f) · 2026-07-17 14:13 UTC · [pipeline](https://dcl.tools/ops/services-pipeline/-/pipelines/59715)' \
  "$(render_last_deploy_line)"

STATE=failure TARGET_URL=""
assert_eq "failure line without pipeline link" \
  '**DEV last deploy:** ❌ failure · `main` @ [725ed44d](https://github.com/decentraland/Pulse/commit/725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f) · 2026-07-17 14:13 UTC' \
  "$(render_last_deploy_line)"

STATE=in_progress
assert_eq "in_progress emoji" "🔄" "$(state_emoji)"

ENVIRONMENT=prd REF=refs/tags/v0.9.2
assert_eq "tag ref shortened" "v0.9.2" "$(short_ref)"
assert_eq "prd running marker" "PRD running:" "$(running_marker)"
assert_eq "prd last deploy marker" "PRD last deploy:" "$(last_deploy_marker)"

DOCKER_IMAGE=quay.io/decentraland/pulse-server:short
assert_eq "short tag not truncated" "pulse-server:short" "$(short_image)"

DOCKER_IMAGE=""
assert_eq "running line omits empty image" \
  '**PRD running:** `v0.9.2` @ [725ed44d](https://github.com/decentraland/Pulse/commit/725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f) · since 2026-07-17 14:13 UTC' \
  "$(render_running_line)"

echo "render tests passed"
