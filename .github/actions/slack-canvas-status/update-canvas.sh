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

slack_get() {
  local method="$1" query="$2"
  curl -sS --fail-with-body -H "Authorization: Bearer ${SLACK_BOT_TOKEN}" \
    "${SLACK_API_BASE}/${method}?${query}"
}

slack_post() {
  local method="$1" body="$2"
  printf '%s' "$body" | curl -sS --fail-with-body -H "Authorization: Bearer ${SLACK_BOT_TOKEN}" \
    -H "Content-Type: application/json; charset=utf-8" \
    --data-binary @- "${SLACK_API_BASE}/${method}"
}

require_ok() {
  local response="$1" context="$2"
  if [[ "$(jq -r '.ok' <<<"$response")" != "true" ]]; then
    echo "::error::Slack ${context} failed: $(jq -r '.error // "unknown"' <<<"$response")" >&2
    exit 1
  fi
}

get_canvas_id() {
  local response
  response="$(slack_get conversations.info "channel=${SLACK_CHANNEL_ID}")"
  require_ok "$response" "conversations.info"
  jq -r '.channel.properties.canvas.file_id // empty' <<<"$response"
}

create_canvas() {
  local markdown="$1" body
  body="$(jq -n --arg channel "$SLACK_CHANNEL_ID" --arg md "$markdown" \
    '{channel_id: $channel, document_content: {type: "markdown", markdown: $md}}')"
  require_ok "$(slack_post conversations.canvases.create "$body")" "conversations.canvases.create"
}

lookup_section() {
  local canvas_id="$1" marker="$2" response
  response="$(slack_post canvases.sections.lookup \
    "$(jq -n --arg id "$canvas_id" --arg text "$marker" '{canvas_id: $id, criteria: {contains_text: $text}}')")"
  require_ok "$response" "canvases.sections.lookup"
  jq -r '.sections[0].id // empty' <<<"$response"
}

upsert_line() {
  local canvas_id="$1" marker="$2" content="$3" section_id change body
  section_id="$(lookup_section "$canvas_id" "$marker")"
  if [[ -n "$section_id" ]]; then
    change="$(jq -n --arg sid "$section_id" --arg md "$content" \
      '{operation: "replace", section_id: $sid, document_content: {type: "markdown", markdown: $md}}')"
  else
    change="$(jq -n --arg md "$content" \
      '{operation: "insert_at_end", document_content: {type: "markdown", markdown: $md}}')"
  fi
  body="$(jq -n --arg id "$canvas_id" --argjson change "$change" '{canvas_id: $id, changes: [$change]}')"
  require_ok "$(slack_post canvases.edit "$body")" "canvases.edit"
}

main() {
  : "${ENVIRONMENT:?}" "${STATE:?}" "${REF:?}" "${SHA:?}" "${REPO_URL:?}"
  : "${SLACK_BOT_TOKEN:?}" "${SLACK_CHANNEL_ID:?}"
  TIMESTAMP="${TIMESTAMP:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

  local lines=()
  if [[ "$STATE" == "success" ]]; then
    lines+=("$(running_marker)|$(render_running_line)")
  fi
  lines+=("$(last_deploy_marker)|$(render_last_deploy_line)")

  local canvas_id
  canvas_id="$(get_canvas_id)"

  local entry
  if [[ -z "$canvas_id" ]]; then
    local markdown="# 🚀 Deployments"$'\n\n'
    for entry in "${lines[@]}"; do
      markdown+="${entry#*|}"$'\n\n'
    done
    create_canvas "$markdown"
    echo "Created channel canvas with initial deployment status."
    return
  fi

  for entry in "${lines[@]}"; do
    upsert_line "$canvas_id" "${entry%%|*}" "${entry#*|}"
  done
  echo "Canvas updated: ${STATE} on ${ENVIRONMENT}."
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi
