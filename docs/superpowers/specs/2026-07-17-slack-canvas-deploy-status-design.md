# Slack Canvas Deployment Status — Design

**Date:** 2026-07-17 (v2: 2026-07-20)
**Status:** Approved

> **v2 (2026-07-20):** switched delivery from a Slack app (Web API canvas upsert) to a
> Slack Workflow Builder webhook, so no app install / workspace-admin approval is needed.
> The GitHub side became stateless: every run re-renders both environments from the
> GitHub Deployments API and the Slack workflow replaces the whole canvas.

## Goal

The Pulse Slack channel's canvas always shows, per environment (dev and prd), what is
currently deployed (branch/tag, commit, docker image, since when) and the outcome of the
most recent deploy attempt — updated automatically by CI, with no Slack app and no
workspace-admin involvement.

## Background

Four flows deploy pulse-server today:

| Workflow | Trigger | Environment |
|---|---|---|
| `docker-next.yml` | push to `main` | dev |
| `docker-release.yml` | release created | prd |
| `manual-deploy.yml` | `workflow_dispatch` | dev or prd |
| `deploy-dev-debug.yml` | `workflow_dispatch` | dev |

All of them (directly or via `decentraland/platform-actions` reusable workflows) end in
`dcl-deploy-action`, which creates a **GitHub Deployment** in this repo with
`task: "dcl/container-deployment"`, `environment`, `ref` (branch or tag), `sha`, and
`payload.dockerImage`. `decentraland-bot` then drives the deployment's status through the
real container rollout: `queued → in_progress → success` (or `failure`/`error`), and marks
superseded deployments `inactive`.

A second, empty-payload deployment record with `task: "deploy"` is created by GitHub's
`environment:` declaration in some workflows; it duplicates the same rollout and must be
ignored.

## Architecture

One workflow listening to deployment status changes + one composite action. **No existing
deploy workflow is modified.** The Slack side is a Workflow Builder workflow (created by
any member, webhook-triggered) whose *Update a canvas* step **replaces the entire channel
canvas content** on every call — therefore the canvas is CI-owned and human edits to it do
not survive.

### Workflow: `.github/workflows/slack-canvas.yml`

- Triggers:
  - `deployment_status` — the production path.
  - `workflow_dispatch` with no inputs — manual re-render / post-setup validation, since
    `deployment_status` only fires once the workflow file exists on `main`.
- Job condition (deployment_status path):
  - `github.event.deployment.task == 'dcl/container-deployment'`
  - `github.event.deployment_status.state` ∈ {`in_progress`, `success`, `failure`, `error`}
    (skip `queued`, `pending`, `inactive`).
- Permissions: `contents: read` (checkout), `deployments: read` (render source).
- Concurrency: one global group (`slack-canvas`) with `cancel-in-progress: true` — every
  run replaces the whole canvas, so only the newest render matters and superseded runs
  are cancelled.
- Steps: checkout `main` (the action version from main runs regardless of which ref was
  deployed) → invoke the composite action with the webhook secret, `github.token`,
  `github.repository`, and the repo web URL.

### Composite action: `.github/actions/slack-canvas-status/action.yml`

Inputs: `webhook-url`, `github-token`, `repo`, `repo-url`. Implementation: bash + `curl`
+ `jq`.

Flow (stateless — no values are taken from the triggering event):

1. For each environment (`dev`, `prd`): `GET
   /repos/{repo}/deployments?environment=…&task=dcl/container-deployment&per_page=30`,
   then walk newest-first, fetching each deployment's latest status
   (`GET …/deployments/{id}/statuses?per_page=1`):
   - the newest deployment (with any status) → the *last deploy* line;
   - the newest deployment whose latest status is `success` → the *running* line
     (superseded deployments are `inactive`, so the active one is the only recent
     `success`); stop there.
   - No deployments → `—` placeholder lines and empty commit URLs.
   - Deployments exist but none on the page succeeded → the running line renders
     `⚠️ no recent success (last N deploys)` so `—` unambiguously means "nothing
     deployed".
2. POST one JSON payload to the Slack webhook; non-2xx fails the step loudly.

Payload variables (all plain text — Workflow Builder interpolates variables literally,
so markdown would render as garbage; labels/formatting live in the Slack workflow's
rich-text editor):

```
dev_running:                 main @ 725ed44d · pulse-server:725ed44d0e4c… · since 2026-07-17 14:13 UTC
dev_last_deploy:             ✅ success · main @ 725ed44d · 2026-07-17 14:13 UTC
dev_running_commit_url:      <repo-url>/commit/<full sha>
dev_last_deploy_commit_url:  <repo-url>/commit/<full sha>
prd_* (same four)
updated_at:                  2026-07-20 10:02 UTC
```

- State emoji: 🔄 `in_progress`/`queued`/`pending`, ✅ `success`, ❌ `failure`/`error`,
  ❔ anything else.
- `ref` is shortened (`refs/heads/`, `refs/tags/` stripped); `sha` shown as 8 chars; the
  commit URLs exist because a webhook variable cannot carry a hyperlinked hash; docker
  image shown without the `quay.io/decentraland/` prefix and with its tag truncated to
  12 chars.
- Timestamps come from the deployment status' `created_at` (UTC); `updated_at` is the
  render time.

## Configuration

Repo secret (new):

- `SLACK_CANVAS_WEBHOOK_URL` — the Workflow Builder webhook URL. Treated as a credential:
  anyone holding it can rewrite the canvas.

The Slack-side workflow (webhook trigger with 9 text variables + *Update a canvas* step
in Replace mode) is created once by any workspace member; `docs/slack-canvas.md` has the
click path, the variable list, and a suggested canvas layout.

## Error handling

- GitHub API or webhook failures (non-2xx) fail the step with the HTTP error visible in
  the log. The workflow is independent of the deploy flows and can never block or fail a
  deploy.
- The render is stateless and idempotent: any subsequent run fully repairs the canvas, so
  transient failures self-heal on the next deployment event (or a manual dispatch).

## Testing

- Local tests, no Slack account needed: `test-render.sh` (line rendering helpers) and
  `test-flow.sh` (mock GitHub API + webhook covering five scenarios: normal render
  including a failed-newest/succeeded-older environment; degraded — a statusless newest
  deployment plus an all-failures page; no deployments; GitHub API error; webhook
  rejection). Both run in CI (`slack-canvas-tests` job in `test.yml`).
- End-to-end: the `workflow_dispatch` trigger after merge + Slack-side setup.
- No .NET code is touched.

## Out of scope

- Chat notifications (messages to the channel) — canvas only.
- Deploy history on the canvas — only *running* + *last deploy* per environment.
- Human-authored content on the channel canvas — the Replace-mode step wipes it; notes
  belong elsewhere.
- Changes to `platform-actions` or the deploy actions themselves.
