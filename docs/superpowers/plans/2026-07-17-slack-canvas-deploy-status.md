# Slack Canvas Deployment Status Implementation Plan

> **SUPERSEDED (2026-07-20):** this plan describes the v1 Slack-app design (bot token,
> marker upsert via `canvases.*` / `conversations.*`). It was executed and then replaced
> by the v2 webhook/Workflow Builder design — see the spec's v2 banner
> (`docs/superpowers/specs/2026-07-17-slack-canvas-deploy-status-design.md`). Do not
> implement from this document.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Pulse Slack channel canvas always shows, per environment (dev/prd), what is currently deployed and the outcome of the latest deploy attempt, updated automatically from GitHub Deployment status changes.

**Architecture:** One new workflow (`.github/workflows/slack-canvas.yml`) triggers on `deployment_status` (plus `workflow_dispatch` for manual testing), filters to real container deployments, and calls a composite action (`.github/actions/slack-canvas-status/`) whose bash script upserts four marker-identified paragraph lines on the channel canvas via the Slack Web API. No existing workflow is modified.

**Tech Stack:** GitHub Actions (composite action), bash + curl + jq, Slack Web API (`conversations.info`, `conversations.canvases.create`, `canvases.sections.lookup`, `canvases.edit`), Python stdlib mock server for tests.

**Spec:** `docs/superpowers/specs/2026-07-17-slack-canvas-deploy-status-design.md`

## Global Constraints

- Repo secrets used by the workflow: `SLACK_BOT_TOKEN`, `SLACK_PULSE_CHANNEL_ID`. Never print their values; never commit real tokens or channel ids — docs use placeholders only.
- Managed line markers (exact strings): `DEV running:`, `DEV last deploy:`, `PRD running:`, `PRD last deploy:`.
- State emoji: 🔄 `in_progress`, ✅ `success`, ❌ `failure`/`error`.
- Only deployments with `task == "dcl/container-deployment"` and states `in_progress|success|failure|error` update the canvas.
- The *running* line updates only on `success`; the *last deploy* line updates on every processed state.
- Shell scripts are LF (already enforced by `.gitattributes` `*.sh text eol=lf`); always invoke them via `bash script.sh` (no reliance on the executable bit).
- Local test runs happen in Git Bash on Windows: `python` (3.14), `jq`, `curl`, `docker` are all on PATH. On ubuntu runners the same commands exist (`python` aliases python3).
- Work happens on branch `feat/slack-canvas-deploy-status`.

---

### Task 1: Canvas line rendering (`update-canvas.sh` rendering functions)

**Files:**
- Create: `.github/actions/slack-canvas-status/update-canvas.sh`
- Test: `.github/actions/slack-canvas-status/test/test-render.sh`

**Interfaces:**
- Produces (consumed by Task 2 within the same script, and by its tests via `source`):
  - `running_marker` / `last_deploy_marker` — print marker strings, e.g. `DEV running:`.
  - `render_running_line` / `render_last_deploy_line` — print one markdown paragraph each.
  - `short_ref`, `short_image`, `commit_link`, `fmt_time`, `state_emoji`, `env_label` — helpers.
  - All read env vars: `ENVIRONMENT`, `STATE`, `REF`, `SHA`, `DOCKER_IMAGE`, `TIMESTAMP`, `TARGET_URL`, `REPO_URL`.

- [ ] **Step 1: Write the failing test**

Create `.github/actions/slack-canvas-status/test/test-render.sh`:

```bash
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash .github/actions/slack-canvas-status/test/test-render.sh`
Expected: FAIL — `../update-canvas.sh: No such file or directory`

- [ ] **Step 3: Write the rendering implementation**

Create `.github/actions/slack-canvas-status/update-canvas.sh`:

```bash
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `bash .github/actions/slack-canvas-status/test/test-render.sh`
Expected: `render tests passed`

- [ ] **Step 5: Commit**

```bash
git add .github/actions/slack-canvas-status
git commit -m "feat: canvas status line rendering for Slack deploy status"
```

---

### Task 2: Slack API orchestration + mock-server flow tests + CI test job

**Files:**
- Modify: `.github/actions/slack-canvas-status/update-canvas.sh` (append below the rendering functions from Task 1)
- Create: `.github/actions/slack-canvas-status/test/mock_slack.py`
- Create: `.github/actions/slack-canvas-status/test/test-flow.sh`
- Modify: `.github/workflows/test.yml` (add job)

**Interfaces:**
- Consumes: rendering functions and markers from Task 1 (`render_running_line`, `render_last_deploy_line`, `running_marker`, `last_deploy_marker`).
- Produces: `main` entrypoint — running `bash update-canvas.sh` with the env vars listed in Task 1 plus `SLACK_BOT_TOKEN`, `SLACK_CHANNEL_ID` performs the full canvas upsert. Task 3's composite action invokes exactly this.

- [ ] **Step 1: Write the mock Slack server**

Create `.github/actions/slack-canvas-status/test/mock_slack.py`:

```python
#!/usr/bin/env python3
"""Mock Slack Web API for update-canvas.sh flow tests.

Scenario is selected with the MOCK_SCENARIO env var:
  has_canvas  - channel has a canvas, every section lookup matches.
  no_canvas   - channel has no canvas yet.
  no_sections - channel has a canvas, no section lookup matches.
Every request is appended to MOCK_LOG as one JSON line: {"path": ..., "body": ...}.
"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

SCENARIO = os.environ.get("MOCK_SCENARIO", "has_canvas")
LOG_PATH = os.environ["MOCK_LOG"]


class Handler(BaseHTTPRequestHandler):
    def _respond(self, payload):
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _record(self, path, body):
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": path, "body": body}, ensure_ascii=False) + "\n")

    def do_GET(self):
        path = self.path.split("?")[0]
        self._record(path, self.path)
        if path == "/conversations.info":
            if SCENARIO == "no_canvas":
                self._respond({"ok": True, "channel": {"id": "C123", "properties": {}}})
            else:
                self._respond({"ok": True, "channel": {"id": "C123", "properties": {"canvas": {"file_id": "F999"}}}})
        else:
            self._respond({"ok": False, "error": "unknown_method"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")
        path = self.path.split("?")[0]
        self._record(path, body)
        if path == "/canvases.sections.lookup":
            if SCENARIO == "no_sections":
                self._respond({"ok": True, "sections": []})
            else:
                marker = body["criteria"]["contains_text"]
                section_id = "sec-" + marker.replace(" ", "-").rstrip(":")
                self._respond({"ok": True, "sections": [{"id": section_id}]})
        elif path in ("/canvases.edit", "/conversations.canvases.create"):
            self._respond({"ok": True, "canvas_id": "F999"})
        else:
            self._respond({"ok": False, "error": "unknown_method"})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
```

- [ ] **Step 2: Write the failing flow test**

Create `.github/actions/slack-canvas-status/test/test-flow.sh`:

```bash
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `bash .github/actions/slack-canvas-status/test/test-flow.sh`
Expected: FAIL — `update-canvas.sh` has no `main` yet, so it exits without calling the mock; `requests.jsonl` is never written and scenario 1 fails with `expected 2 edits, got ` (empty count).

- [ ] **Step 4: Append the API orchestration to `update-canvas.sh`**

Append to `.github/actions/slack-canvas-status/update-canvas.sh` (after `render_last_deploy_line`):

```bash
slack_get() {
  local method="$1" query="$2"
  curl -sS --fail-with-body -H "Authorization: Bearer ${SLACK_BOT_TOKEN}" \
    "${SLACK_API_BASE}/${method}?${query}"
}

slack_post() {
  local method="$1" body="$2"
  curl -sS --fail-with-body -H "Authorization: Bearer ${SLACK_BOT_TOKEN}" \
    -H "Content-Type: application/json; charset=utf-8" \
    -d "$body" "${SLACK_API_BASE}/${method}"
}

require_ok() {
  local response="$1" context="$2"
  if [[ "$(jq -r '.ok' <<<"$response")" != "true" ]]; then
    echo "::error::Slack ${context} failed: $(jq -r '.error // "unknown"' <<<"$response")"
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
```

- [ ] **Step 5: Run both tests to verify they pass**

Run: `bash .github/actions/slack-canvas-status/test/test-render.sh && bash .github/actions/slack-canvas-status/test/test-flow.sh`
Expected: `render tests passed` then `flow tests passed`

- [ ] **Step 6: Add the test job to CI**

In `.github/workflows/test.yml`, add after the `test` job (same indentation level):

```yaml
  slack-canvas-tests:
    name: Slack canvas action tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Render tests
        run: bash .github/actions/slack-canvas-status/test/test-render.sh

      - name: Flow tests
        run: bash .github/actions/slack-canvas-status/test/test-flow.sh
```

- [ ] **Step 7: Verify the workflow YAML with actionlint**

Run (Git Bash): `docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color`
Expected: no output (exit 0). Fix anything it reports before committing.

- [ ] **Step 8: Commit**

```bash
git add .github/actions/slack-canvas-status .github/workflows/test.yml
git commit -m "feat: Slack canvas upsert flow with mock-server tests"
```

---

### Task 3: Composite action definition

**Files:**
- Create: `.github/actions/slack-canvas-status/action.yml`

**Interfaces:**
- Consumes: `bash "${GITHUB_ACTION_PATH}/update-canvas.sh"` with the env contract from Tasks 1–2.
- Produces: composite action `./.github/actions/slack-canvas-status` with inputs `environment`, `state`, `ref`, `sha`, `docker-image`, `timestamp`, `url`, `repo-url`, `slack-token`, `channel-id` — used by Task 4's workflow.

- [ ] **Step 1: Write `action.yml`**

Create `.github/actions/slack-canvas-status/action.yml`:

```yaml
name: Slack canvas deploy status
description: Upserts per-environment deployment status lines on the Pulse Slack channel canvas.

inputs:
  environment:
    description: Deployment environment (dev or prd).
    required: true
  state:
    description: Deployment state (in_progress, success, failure or error).
    required: true
  ref:
    description: Deployed git ref (branch or tag; long refs/... form is shortened).
    required: true
  sha:
    description: Deployed commit SHA.
    required: true
  docker-image:
    description: Deployed docker image.
    required: false
    default: ""
  timestamp:
    description: ISO8601 time of the status change; defaults to now when empty.
    required: false
    default: ""
  url:
    description: Link to the deployment pipeline.
    required: false
    default: ""
  repo-url:
    description: Repository web URL used for commit links.
    required: true
  slack-token:
    description: Slack bot token (canvases:read, canvases:write, channels:read scopes).
    required: true
  channel-id:
    description: Slack channel whose canvas is updated.
    required: true

runs:
  using: composite
  steps:
    - name: Update canvas
      shell: bash
      env:
        ENVIRONMENT: ${{ inputs.environment }}
        STATE: ${{ inputs.state }}
        REF: ${{ inputs.ref }}
        SHA: ${{ inputs.sha }}
        DOCKER_IMAGE: ${{ inputs.docker-image }}
        TIMESTAMP: ${{ inputs.timestamp }}
        TARGET_URL: ${{ inputs.url }}
        REPO_URL: ${{ inputs.repo-url }}
        SLACK_BOT_TOKEN: ${{ inputs.slack-token }}
        SLACK_CHANNEL_ID: ${{ inputs.channel-id }}
      run: bash "${GITHUB_ACTION_PATH}/update-canvas.sh"
```

- [ ] **Step 2: Validate the YAML parses**

Run: `python -c "import yaml" 2>/dev/null || python -m pip install --user --quiet pyyaml; python -c "import yaml; yaml.safe_load(open('.github/actions/slack-canvas-status/action.yml')); print('action.yml OK')"`
Expected: `action.yml OK`

- [ ] **Step 3: Commit**

```bash
git add .github/actions/slack-canvas-status/action.yml
git commit -m "feat: composite action for Slack canvas deploy status"
```

---

### Task 4: `slack-canvas.yml` workflow

**Files:**
- Create: `.github/workflows/slack-canvas.yml`

**Interfaces:**
- Consumes: the composite action from Task 3 (all ten inputs), repo secrets `SLACK_BOT_TOKEN` and `SLACK_PULSE_CHANNEL_ID`.
- Produces: the production trigger. Nothing else depends on it.

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/slack-canvas.yml`:

```yaml
name: Slack Canvas Deploy Status

on:
  deployment_status:
  workflow_dispatch:
    inputs:
      environment:
        required: true
        type: choice
        options:
          - dev
          - prd
        default: dev
        description: Environment
      state:
        required: true
        type: choice
        options:
          - in_progress
          - success
          - failure
          - error
        default: success
        description: Deployment state to report
      ref:
        required: false
        type: string
        default: main
        description: Deployed branch or tag
      sha:
        required: false
        type: string
        default: ""
        description: Deployed commit SHA (defaults to this workflow's commit)
      docker-image:
        required: false
        type: string
        default: quay.io/decentraland/pulse-server:manual-test
        description: Deployed docker image
      url:
        required: false
        type: string
        default: ""
        description: Pipeline link

permissions:
  contents: read

jobs:
  update-canvas:
    # Only real container deployments (skips the empty-payload records GitHub's
    # environment: declaration creates) in states worth showing.
    if: >-
      github.event_name == 'workflow_dispatch' ||
      (github.event.deployment.task == 'dcl/container-deployment' &&
       contains(fromJSON('["in_progress", "success", "failure", "error"]'), github.event.deployment_status.state))
    runs-on: ubuntu-latest
    timeout-minutes: 5
    concurrency:
      group: slack-canvas-${{ github.event_name == 'workflow_dispatch' && inputs.environment || github.event.deployment.environment }}
      cancel-in-progress: false
    steps:
      # The action version from main runs regardless of which ref was deployed.
      - uses: actions/checkout@v4
        with:
          ref: main

      - name: Update Slack canvas
        uses: ./.github/actions/slack-canvas-status
        with:
          environment: ${{ github.event_name == 'workflow_dispatch' && inputs.environment || github.event.deployment.environment }}
          state: ${{ github.event_name == 'workflow_dispatch' && inputs.state || github.event.deployment_status.state }}
          ref: ${{ github.event_name == 'workflow_dispatch' && inputs.ref || github.event.deployment.ref }}
          sha: ${{ github.event_name == 'workflow_dispatch' && (inputs.sha || github.sha) || github.event.deployment.sha }}
          docker-image: ${{ github.event_name == 'workflow_dispatch' && inputs.docker-image || github.event.deployment.payload.dockerImage }}
          timestamp: ${{ github.event.deployment_status.created_at }}
          url: ${{ github.event_name == 'workflow_dispatch' && inputs.url || github.event.deployment_status.target_url }}
          repo-url: ${{ github.server_url }}/${{ github.repository }}
          slack-token: ${{ secrets.SLACK_BOT_TOKEN }}
          channel-id: ${{ secrets.SLACK_PULSE_CHANNEL_ID }}
```

- [ ] **Step 2: Verify with actionlint**

Run (Git Bash): `docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color`
Expected: no output (exit 0). Fix anything it reports.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/slack-canvas.yml
git commit -m "feat: update Slack channel canvas on deployment status changes"
```

---

### Task 5: Setup documentation

**Files:**
- Create: `docs/slack-canvas.md`
- Modify: `CLAUDE.md` (Docker — Deployment & Debugging section, after the "Deploy pipeline:" paragraph)

**Interfaces:**
- Consumes: secret names, scopes, markers, and the dispatch test from Tasks 3–4.
- Produces: operator documentation only.

- [ ] **Step 1: Write `docs/slack-canvas.md`**

```markdown
# Slack Canvas Deployment Status

`.github/workflows/slack-canvas.yml` keeps the Pulse Slack channel's canvas in sync with
deployments. It listens to GitHub `deployment_status` events (every deploy flow ends in
`dcl-deploy-action`, which creates a GitHub Deployment whose status `decentraland-bot`
drives through the real container rollout) and upserts four managed lines on the canvas:

- `DEV running:` / `PRD running:` — what is currently deployed. Updated only on `success`.
- `DEV last deploy:` / `PRD last deploy:` — outcome of the latest attempt
  (🔄 in_progress, ✅ success, ❌ failure/error). Updated on every state.

Lines are found by those marker prefixes and replaced atomically; everything else on the
canvas is left untouched. If the channel has no canvas, one is created with a
`# 🚀 Deployments` heading. Don't rename the markers by hand — the workflow would then
append fresh lines instead of updating the old ones (deleting the stale lines fixes that).

## One-time Slack app setup

1. Create a Slack app (https://api.slack.com/apps → *Create New App* → *From scratch*)
   in the Decentraland workspace, e.g. named `Pulse Deployments`.
2. Under *OAuth & Permissions* add the bot token scopes: `canvases:read`,
   `canvases:write`, `channels:read` (public channel) or `groups:read` (private channel).
3. Install the app to the workspace and copy the bot token (`xoxb-…`).
4. In the Pulse channel run `/invite @Pulse Deployments` (the bot must be a member).
5. Copy the channel id (channel name → *View channel details* — the id is at the bottom
   of the About tab, `C…`).
6. Add the repo secrets (repo → Settings → Secrets and variables → Actions):
   - `SLACK_BOT_TOKEN` — the bot token.
   - `SLACK_PULSE_CHANNEL_ID` — the channel id.

## Testing

`deployment_status` only fires for the workflow version on `main`, so after merging test
it manually: Actions → *Slack Canvas Deploy Status* → *Run workflow* with a dev-shaped
payload. The run fails with the Slack error name in the log if something is off
(`missing_scope`, `not_in_channel`, `channel_not_found` are the usual suspects).

The action's logic is covered by local tests (no Slack account needed):

    bash .github/actions/slack-canvas-status/test/test-render.sh
    bash .github/actions/slack-canvas-status/test/test-flow.sh

They also run in CI via the `slack-canvas-tests` job in `test.yml`.

## Known limitation

`canvases.sections.lookup` is filtered by `contains_text` only. If Slack ever requires a
`section_types` filter (paragraph sections are not a filterable type), the lookup call
fails loudly with `invalid_arguments`; the fallback is to render the managed lines as
`###` headings so `section_types: ["h3"]` can be used. The manual dispatch test after the
first merge confirms which behavior is live.
```

- [ ] **Step 2: Add the CLAUDE.md pointer**

In `CLAUDE.md`, in the **Docker — Deployment & Debugging** section, right after the paragraph starting with `Deploy pipeline:`, add:

```markdown
Every deployment also updates the Pulse Slack channel canvas (per-environment "running" /
"last deploy" lines) via `.github/workflows/slack-canvas.yml`; Slack app setup and
troubleshooting live in [docs/slack-canvas.md](docs/slack-canvas.md).
```

- [ ] **Step 3: Commit**

```bash
git add docs/slack-canvas.md CLAUDE.md
git commit -m "docs: Slack canvas deploy status setup guide"
```

---

## Post-merge validation (user-facing, not a task)

1. Configure the two secrets per `docs/slack-canvas.md`.
2. Merge the PR; run the manual dispatch test (dev, success).
3. Confirm the canvas shows the DEV lines; then let the next real deploy update it.
