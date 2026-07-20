#!/usr/bin/env bash
# End-to-end tests for update-canvas.sh against a mock GitHub API + Slack webhook.
set -euo pipefail
cd "$(dirname "$0")"

PORT=18465
PYTHON="$(command -v python || command -v python3)"

export GITHUB_API_BASE="http://127.0.0.1:${PORT}"
export SLACK_WEBHOOK_URL="http://127.0.0.1:${PORT}/webhook"
export REPO=decentraland/Pulse
export REPO_URL=https://github.com/decentraland/Pulse
export GITHUB_TOKEN=test-token

MOCK_LOG="$(mktemp)"
ERR_FILE="$(mktemp)"
SERVER_PID=""

cleanup() {
  [[ -n "$SERVER_PID" ]] && kill "$SERVER_PID" 2>/dev/null || true
  rm -f "$MOCK_LOG" "$ERR_FILE"
}
trap cleanup EXIT

fail() { echo "FAIL: $1"; exit 1; }

payload_field() {
  jq -r --arg k "$1" 'select(.path == "/webhook") | .body[$k]' "$MOCK_LOG"
}

start_server() {
  local scenario="$1" i
  : > "$MOCK_LOG"
  MOCK_SCENARIO="$scenario" MOCK_LOG="$MOCK_LOG" "$PYTHON" mock_api.py "$PORT" &
  SERVER_PID=$!
  # Bounded readiness poll: the || true keeps a failed probe from tripping set -e,
  # and a never-ready server just falls through so the first real request fails loudly.
  for i in $(seq 1 50); do curl -s "${GITHUB_API_BASE}/ping" >/dev/null 2>&1 && break || true; sleep 0.1; done
}

stop_server() {
  kill "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=""
}

# Scenario 1: normal — dev's newest deployment succeeded; prd's newest failed
# while an older one succeeded, so prd running and last-deploy differ.
start_server normal
bash ../update-canvas.sh
stop_server

[[ "$(payload_field dev_running)" == 'main @ aaaabbbb · pulse-server:aaaabbbbcccc… · since 2026-07-18 10:00 UTC' ]] \
  || fail "dev_running mismatch: $(payload_field dev_running)"
[[ "$(payload_field dev_last_deploy)" == '✅ success · main @ aaaabbbb · 2026-07-18 10:00 UTC' ]] \
  || fail "dev_last_deploy mismatch: $(payload_field dev_last_deploy)"
[[ "$(payload_field prd_running)" == 'v0.9.2 @ 12345678 · pulse-server:59791d9 · since 2026-07-15 17:11 UTC' ]] \
  || fail "prd_running mismatch: $(payload_field prd_running)"
[[ "$(payload_field prd_last_deploy)" == '❌ failure · v0.9.3 @ 99998888 · 2026-07-19 08:30 UTC' ]] \
  || fail "prd_last_deploy mismatch: $(payload_field prd_last_deploy)"
[[ "$(payload_field dev_running_commit_url)" == 'https://github.com/decentraland/Pulse/commit/aaaabbbbccccddddeeeeffff0000111122223333' ]] \
  || fail "dev_running_commit_url mismatch: $(payload_field dev_running_commit_url)"
[[ "$(payload_field prd_running_commit_url)" == 'https://github.com/decentraland/Pulse/commit/1234567890abcdef1234567890abcdef12345678' ]] \
  || fail "prd_running_commit_url mismatch: $(payload_field prd_running_commit_url)"
[[ "$(payload_field prd_last_deploy_commit_url)" == 'https://github.com/decentraland/Pulse/commit/9999888877776666555544443333222211110000' ]] \
  || fail "prd_last_deploy_commit_url mismatch: $(payload_field prd_last_deploy_commit_url)"
[[ "$(payload_field updated_at)" == *" UTC" ]] || fail "updated_at missing/malformed: $(payload_field updated_at)"
grep -q 'environment=dev' "$MOCK_LOG" || fail "expected dev deployments query"
grep -q 'environment=prd' "$MOCK_LOG" || fail "expected prd deployments query"
grep -q 'task=dcl/container-deployment' "$MOCK_LOG" || fail "expected task filter in deployments query"

# Scenario 2: no deployments at all -> placeholder lines, empty commit urls.
start_server empty
bash ../update-canvas.sh
stop_server
[[ "$(payload_field dev_running)" == "—" ]] || fail "expected placeholder dev_running, got: $(payload_field dev_running)"
[[ "$(payload_field prd_last_deploy)" == "—" ]] || fail "expected placeholder prd_last_deploy, got: $(payload_field prd_last_deploy)"
[[ "$(payload_field dev_running_commit_url)" == "" ]] || fail "expected empty dev_running_commit_url"

# Scenario 3: GitHub API errors -> loud non-zero exit with the response body,
# webhook never called.
start_server gh_error
if bash ../update-canvas.sh 2>"$ERR_FILE"; then
  fail "expected non-zero exit when the GitHub API fails"
fi
stop_server
grep -q '500' "$ERR_FILE" || fail "expected HTTP 500 in stderr, got: $(cat "$ERR_FILE")"
grep -q 'boom' "$ERR_FILE" || fail "expected error body in stderr, got: $(cat "$ERR_FILE")"
! grep -q '"/webhook"' "$MOCK_LOG" || fail "webhook must not be called when the GitHub API fails"

# Scenario 4: webhook rejects -> loud non-zero exit with the response body.
start_server webhook_fail
if bash ../update-canvas.sh 2>"$ERR_FILE"; then
  fail "expected non-zero exit when the webhook rejects"
fi
stop_server
grep -q '"/webhook"' "$MOCK_LOG" || fail "expected webhook request to be recorded"
grep -q '500' "$ERR_FILE" || fail "expected HTTP 500 in stderr, got: $(cat "$ERR_FILE")"
grep -q 'trigger_failed' "$ERR_FILE" || fail "expected webhook body in stderr, got: $(cat "$ERR_FILE")"

# Scenario 5: degraded — dev's newest deployment has no statuses yet (skipped for
# the last-deploy line), the next one failed, none succeeded; prd is empty.
start_server degraded
bash ../update-canvas.sh
stop_server
[[ "$(payload_field dev_last_deploy)" == '❌ failure · main @ bbbbcccc · 2026-07-19 09:00 UTC' ]] \
  || fail "degraded dev_last_deploy mismatch: $(payload_field dev_last_deploy)"
[[ "$(payload_field dev_running)" == '⚠️ no recent success (last 2 deploys)' ]] \
  || fail "degraded dev_running mismatch: $(payload_field dev_running)"
[[ "$(payload_field dev_running_commit_url)" == "" ]] || fail "expected empty degraded dev_running_commit_url"
[[ "$(payload_field dev_last_deploy_commit_url)" == 'https://github.com/decentraland/Pulse/commit/bbbbccccddddeeeeffff0000111122223333aaaa' ]] \
  || fail "degraded dev_last_deploy_commit_url mismatch: $(payload_field dev_last_deploy_commit_url)"
[[ "$(payload_field prd_running)" == "—" ]] || fail "expected placeholder prd_running in degraded scenario"

echo "flow tests passed"
