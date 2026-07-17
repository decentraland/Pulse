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

MOCK_LOG="$(mktemp)"
SERVER_PID=""

cleanup() {
  [[ -n "$SERVER_PID" ]] && kill "$SERVER_PID" 2>/dev/null || true
  rm -f "$MOCK_LOG"
}
trap cleanup EXIT

fail() { echo "FAIL: $1"; exit 1; }

start_server() {
  local scenario="$1" i
  : > "$MOCK_LOG"
  MOCK_SCENARIO="$scenario" MOCK_LOG="$MOCK_LOG" "$PYTHON" mock_slack.py "$PORT" &
  SERVER_PID=$!
  # Bounded readiness poll: the || true keeps a failed probe from tripping set -e,
  # and a never-ready server just falls through so the first real request fails loudly.
  for i in $(seq 1 50); do curl -s "${SLACK_API_BASE}/ping" >/dev/null 2>&1 && break || true; sleep 0.1; done
}

stop_server() {
  kill "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=""
}

run_scenario() {
  local scenario="$1" environment="$2" state="$3"
  start_server "$scenario"
  ENVIRONMENT="$environment" STATE="$state" bash ../update-canvas.sh
  stop_server
}

# Scenario 1: canvas and sections exist, success -> both lines replaced.
# grep -c is wrapped in || true: with set -e a zero-match (exit 1) or missing file
# (exit 2) must reach the assertion message instead of killing the script.
run_scenario has_canvas dev success
edits="$(grep -c canvases.edit "$MOCK_LOG" || true)"
[[ "$edits" == "2" ]] || fail "expected 2 edits, got $edits"
replaces="$(grep -c '"operation": "replace"' "$MOCK_LOG" || true)"
[[ "$replaces" == "2" ]] || fail "expected 2 replace ops, got $replaces"
grep -q 'DEV running:' "$MOCK_LOG" || fail "expected running line update"
grep -q 'DEV last deploy:' "$MOCK_LOG" || fail "expected last deploy line update"
grep -q '"section_id": "sec-DEV-running"' "$MOCK_LOG" || fail "expected replace to target looked-up section"

# Scenario 2: no canvas yet -> canvas created with skeleton, no edits.
run_scenario no_canvas dev success
grep -q conversations.canvases.create "$MOCK_LOG" || fail "expected canvas create"
grep -q '🚀 Deployments' "$MOCK_LOG" || fail "expected skeleton heading"
grep -q 'DEV running:' "$MOCK_LOG" || fail "expected running line in skeleton"
grep -q 'DEV last deploy:' "$MOCK_LOG" || fail "expected last deploy line in skeleton"
! grep -q canvases.edit "$MOCK_LOG" || fail "expected no edits when creating canvas"

# Scenario 3: failure with no matching sections -> one insert_at_end, running untouched.
run_scenario no_sections prd failure
edits="$(grep -c canvases.edit "$MOCK_LOG" || true)"
[[ "$edits" == "1" ]] || fail "expected 1 edit, got $edits"
grep -q '"operation": "insert_at_end"' "$MOCK_LOG" || fail "expected insert_at_end"
grep -q 'PRD last deploy:' "$MOCK_LOG" || fail "expected PRD last deploy line"
! grep -q 'PRD running:' "$MOCK_LOG" || fail "running line must not update on failure"

# Scenario 4: conversations.info returns ok:false -> fail loudly before any other Slack call.
start_server ok_false
err="$(mktemp)"
if ENVIRONMENT=dev STATE=success bash ../update-canvas.sh 2>"$err"; then
  fail "expected non-zero exit when conversations.info returns ok:false"
fi
stop_server
grep -q missing_scope "$err" || fail "expected missing_scope in stderr, got: $(cat "$err")"
grep -q conversations.info "$MOCK_LOG" || fail "expected conversations.info request"
! grep -q canvases.sections.lookup "$MOCK_LOG" || fail "expected no section lookup after ok:false"
! grep -q canvases.edit "$MOCK_LOG" || fail "expected no canvas edit after ok:false"
! grep -q conversations.canvases.create "$MOCK_LOG" || fail "expected no canvas create after ok:false"
rm -f "$err"

echo "flow tests passed"
