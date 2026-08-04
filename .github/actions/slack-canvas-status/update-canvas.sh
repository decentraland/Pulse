#!/usr/bin/env bash
# Renders the current deployment status of every environment from the GitHub
# Deployments API and POSTs it to a Slack Workflow Builder webhook, whose
# "Update a canvas" step replaces the Pulse channel canvas content.
#
# Stateless: each run reconstructs both environments from the API, so any
# qualifying deployment event (or a manual dispatch) produces a full,
# self-consistent canvas.
#
# Required env: REPO (owner/name), REPO_URL, GITHUB_TOKEN, SLACK_WEBHOOK_URL.
# Optional env: GITHUB_API_BASE (defaults to https://api.github.com; tests
#               point it at a mock).
set -euo pipefail
# Command substitutions must inherit errexit, or a failure inside
# $(render_*)/$(jq …) assignments would be silently swallowed.
shopt -s inherit_errexit

GITHUB_API_BASE="${GITHUB_API_BASE:-https://api.github.com}"
DEPLOY_TASK="dcl/container-deployment"
# One page of deployments per environment. Sized so a realistic streak of
# consecutive failures still keeps the actually-running deployment on the page.
MAX_DEPLOYMENTS=30
EMPTY_LINE="—"

short_ref() {
  local ref="$1"
  ref="${ref#refs/heads/}"
  ref="${ref#refs/tags/}"
  echo "$ref"
}

short_sha() {
  echo "${1:0:8}"
}

short_image() {
  local image="${1#quay.io/decentraland/}"
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
  date -u -d "$1" +"%Y-%m-%d %H:%M UTC"
}

state_emoji() {
  case "$1" in
    in_progress|queued|pending) echo "🔄" ;;
    success)                    echo "✅" ;;
    failure|error)              echo "❌" ;;
    *)                          echo "❔" ;;
  esac
}

# Webhook variables are interpolated into the Slack canvas as plain text, so
# the lines carry no markdown — labels and formatting live in the Workflow
# Builder step's rich-text editor.
render_running_line() {
  local ref="$1" sha="$2" image="$3" time="$4" line
  line="$(short_ref "$ref") @ $(short_sha "$sha")"
  if [[ -n "$image" ]]; then
    line+=" · $(short_image "$image")"
  fi
  line+=" · since $(fmt_time "$time")"
  echo "$line"
}

render_last_deploy_line() {
  local state="$1" ref="$2" sha="$3" time="$4" ts
  # Assigned separately so a fmt_time failure propagates (with inherit_errexit)
  # instead of being swallowed inside echo's argument list.
  ts="$(fmt_time "$time")"
  echo "$(state_emoji "$state") ${state} · $(short_ref "$ref") @ $(short_sha "$sha") · ${ts}"
}

gh_get() {
  curl -sS --fail-with-body --max-time 30 \
    -H "Authorization: Bearer ${GITHUB_TOKEN}" \
    -H "Accept: application/vnd.github+json" \
    "${GITHUB_API_BASE}$1"
}

# Prints "state<TAB>created_at" of a deployment's newest status; empty if none.
# The statuses endpoint returns newest-first, so per_page=1 yields the latest.
latest_status() {
  local response
  response="$(gh_get "/repos/${REPO}/deployments/$1/statuses?per_page=1")" || {
    echo "::error::GitHub API status query failed: ${response}" >&2
    exit 1
  }
  jq -r '.[0] | select(. != null) | "\(.state)\t\(.created_at)"' <<<"$response"
}

# Fills RUNNING_LINE / LAST_LINE / RUNNING_COMMIT_URL / LAST_COMMIT_URL for one
# environment. "Running" is the newest deployment whose latest status is
# success (superseded deployments are marked inactive by the deploy infra);
# "last deploy" is the newest deployment regardless of outcome.
collect_env() {
  local environment="$1"
  RUNNING_LINE="$EMPTY_LINE"
  LAST_LINE="$EMPTY_LINE"
  RUNNING_COMMIT_URL=""
  LAST_COMMIT_URL=""

  local deployments count
  deployments="$(gh_get "/repos/${REPO}/deployments?environment=${environment}&task=${DEPLOY_TASK}&per_page=${MAX_DEPLOYMENTS}")" || {
    echo "::error::GitHub API deployments query failed: ${deployments}" >&2
    exit 1
  }
  count="$(jq 'length' <<<"$deployments")"
  if (( count == 0 )); then
    return
  fi

  local i id ref sha image status state created
  for (( i = 0; i < count; i++ )); do
    id="$(jq -r ".[$i].id" <<<"$deployments")"
    status="$(latest_status "$id")"
    if [[ -z "$status" ]]; then
      continue
    fi
    state="${status%%$'\t'*}"
    created="${status#*$'\t'}"
    ref="$(jq -r ".[$i].ref" <<<"$deployments")"
    sha="$(jq -r ".[$i].sha" <<<"$deployments")"
    image="$(jq -r ".[$i].payload.dockerImage // \"\"" <<<"$deployments")"

    if [[ "$LAST_LINE" == "$EMPTY_LINE" ]]; then
      LAST_LINE="$(render_last_deploy_line "$state" "$ref" "$sha" "$created")"
      LAST_COMMIT_URL="${REPO_URL}/commit/${sha}"
    fi
    if [[ "$state" == "success" ]]; then
      RUNNING_LINE="$(render_running_line "$ref" "$sha" "$image" "$created")"
      RUNNING_COMMIT_URL="${REPO_URL}/commit/${sha}"
      return
    fi
  done

  # Deployments exist but none of the recent ones succeeded — say so instead of
  # rendering the ambiguous "nothing deployed" placeholder. Skipped when every
  # deployment was statusless (no last-deploy line either): both placeholders
  # stay "—" rather than contradicting each other.
  if [[ "$LAST_LINE" != "$EMPTY_LINE" ]]; then
    RUNNING_LINE="⚠️ no recent success (last ${count} deploys)"
  fi
}

main() {
  : "${REPO:?}" "${REPO_URL:?}" "${GITHUB_TOKEN:?}" "${SLACK_WEBHOOK_URL:?}"

  collect_env dev
  local dev_running="$RUNNING_LINE" dev_last="$LAST_LINE"
  local dev_running_url="$RUNNING_COMMIT_URL" dev_last_url="$LAST_COMMIT_URL"

  collect_env prd
  local prd_running="$RUNNING_LINE" prd_last="$LAST_LINE"
  local prd_running_url="$RUNNING_COMMIT_URL" prd_last_url="$LAST_COMMIT_URL"

  local payload
  payload="$(jq -n \
    --arg dev_running "$dev_running" \
    --arg dev_last "$dev_last" \
    --arg dev_running_url "$dev_running_url" \
    --arg dev_last_url "$dev_last_url" \
    --arg prd_running "$prd_running" \
    --arg prd_last "$prd_last" \
    --arg prd_running_url "$prd_running_url" \
    --arg prd_last_url "$prd_last_url" \
    --arg updated_at "$(date -u +"%Y-%m-%d %H:%M UTC")" \
    '{
      dev_running: $dev_running,
      dev_last_deploy: $dev_last,
      dev_running_commit_url: $dev_running_url,
      dev_last_deploy_commit_url: $dev_last_url,
      prd_running: $prd_running,
      prd_last_deploy: $prd_last,
      prd_running_commit_url: $prd_running_url,
      prd_last_deploy_commit_url: $prd_last_url,
      updated_at: $updated_at
    }')"

  local response
  response="$(printf '%s' "$payload" | curl -sS --fail-with-body --max-time 30 \
    -H "Content-Type: application/json; charset=utf-8" \
    --data-binary @- "$SLACK_WEBHOOK_URL")" || {
    echo "::error::Slack webhook rejected the payload: ${response}" >&2
    exit 1
  }
  echo "Canvas content sent to the Slack workflow webhook (dev: ${dev_last}; prd: ${prd_last})."
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi
