#!/usr/bin/env bash
# Upserts per-environment deployment status lines on a Slack channel canvas.
#
# Required env: ENVIRONMENT, STATE, REF, SHA, REPO_URL, SLACK_BOT_TOKEN, SLACK_CHANNEL_ID.
# Optional env: DOCKER_IMAGE, TIMESTAMP (defaults to now), TARGET_URL,
#               SLACK_API_BASE (defaults to https://slack.com/api; tests point it at a mock).
set -euo pipefail

SLACK_API_BASE="${SLACK_API_BASE:-https://slack.com/api}"

env_label() {
  echo "${ENVIRONMENT^^}"
}

short_ref() {
  local ref="$REF"
  ref="${ref#refs/heads/}"
  ref="${ref#refs/tags/}"
  echo "$ref"
}

commit_link() {
  echo "[${SHA:0:8}](${REPO_URL}/commit/${SHA})"
}

short_image() {
  local image="${DOCKER_IMAGE#quay.io/decentraland/}"
  local name="${image%%:*}"
  local tag="${image#*:}"
  if [[ "$tag" == "$image" ]]; then
    echo "$image"
    return
  fi
  if (( ${#tag} > 12 )); then
    tag="${tag:0:12}…"
  fi
  echo "${name}:${tag}"
}

fmt_time() {
  date -u -d "$TIMESTAMP" +"%Y-%m-%d %H:%M UTC"
}

state_emoji() {
  case "$STATE" in
    in_progress) echo "🔄" ;;
    success)     echo "✅" ;;
    *)           echo "❌" ;;
  esac
}

running_marker() {
  echo "$(env_label) running:"
}

last_deploy_marker() {
  echo "$(env_label) last deploy:"
}

render_running_line() {
  local line="**$(running_marker)** \`$(short_ref)\` @ $(commit_link)"
  if [[ -n "${DOCKER_IMAGE:-}" ]]; then
    line+=" · \`$(short_image)\`"
  fi
  line+=" · since $(fmt_time)"
  echo "$line"
}

render_last_deploy_line() {
  local line="**$(last_deploy_marker)** $(state_emoji) ${STATE} · \`$(short_ref)\` @ $(commit_link) · $(fmt_time)"
  if [[ -n "${TARGET_URL:-}" ]]; then
    line+=" · [pipeline](${TARGET_URL})"
  fi
  echo "$line"
}
