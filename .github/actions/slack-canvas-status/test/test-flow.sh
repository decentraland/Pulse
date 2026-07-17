#!/usr/bin/env bash
# End-to-end tests for update-canvas.sh against the mock Slack server.
set -euo pipefail
cd "$(dirname "$0")"

PORT=18465
PYTHON="$(command -v python || command -v python3)"

export SLACK_API_BASE="http://127.0.0.1:${PORT}"
export REPO_URL=https://github.com/decentraland/Pulse \
  SLACK_BOT_TOKEN=test-token SLACK_CHANNEL_ID=C123 \
  REF=refs/heads/main SHA=725ed44d0e4cf8fd0d4d5f67f9fb0ad85724ef1f \
  DOCKER_IMAGE=quay.io/decentraland/pulse-server:next \
  TIMESTAMP=2026-07-17T14:13:53Z TARGET_URL=

fail() { echo "FAIL: $1"; exit 1; }

run_scenario() {
  local scenario="$1" environment="$2" state="$3"
  MOCK_LOG="$PWD/requests.jsonl"
  rm -f "$MOCK_LOG"
  MOCK_SCENARIO="$scenario" MOCK_LOG="$MOCK_LOG" "$PYTHON" mock_slack.py "$PORT" &
  local server_pid=$!
  trap 'kill '"$server_pid"' 2>/dev/null || true' EXIT
  sleep 1
  ENVIRONMENT="$environment" STATE="$state" bash ../update-canvas.sh
  kill "$server_pid" 2>/dev/null || true
  wait "$server_pid" 2>/dev/null || true
}

# Scenario 1: canvas and sections exist, success -> both lines replaced.
# grep -c is wrapped in || true: with set -e a zero-match (exit 1) or missing file
# (exit 2) must reach the assertion message instead of killing the script.
run_scenario has_canvas dev success
edits="$(grep -c canvases.edit requests.jsonl || true)"
[[ "$edits" == "2" ]] || fail "expected 2 edits, got $edits"
replaces="$(grep -c '"operation": "replace"' requests.jsonl || true)"
[[ "$replaces" == "2" ]] || fail "expected 2 replace ops, got $replaces"
grep -q 'DEV running:' requests.jsonl || fail "expected running line update"
grep -q 'DEV last deploy:' requests.jsonl || fail "expected last deploy line update"
grep -q '"section_id": "sec-DEV-running"' requests.jsonl || fail "expected replace to target looked-up section"

# Scenario 2: no canvas yet -> canvas created with skeleton, no edits.
run_scenario no_canvas dev success
grep -q conversations.canvases.create requests.jsonl || fail "expected canvas create"
grep -q '🚀 Deployments' requests.jsonl || fail "expected skeleton heading"
grep -q 'DEV running:' requests.jsonl || fail "expected running line in skeleton"
grep -q 'DEV last deploy:' requests.jsonl || fail "expected last deploy line in skeleton"
! grep -q canvases.edit requests.jsonl || fail "expected no edits when creating canvas"

# Scenario 3: failure with no matching sections -> one insert_at_end, running untouched.
run_scenario no_sections prd failure
edits="$(grep -c canvases.edit requests.jsonl || true)"
[[ "$edits" == "1" ]] || fail "expected 1 edit, got $edits"
grep -q '"operation": "insert_at_end"' requests.jsonl || fail "expected insert_at_end"
grep -q 'PRD last deploy:' requests.jsonl || fail "expected PRD last deploy line"
! grep -q 'PRD running:' requests.jsonl || fail "running line must not update on failure"

echo "flow tests passed"
