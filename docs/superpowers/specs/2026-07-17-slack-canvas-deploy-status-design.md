# Slack Canvas Deployment Status — Design

**Date:** 2026-07-17
**Status:** Approved

## Goal

The Pulse Slack channel's canvas always shows, per environment (dev and prd), what is
currently deployed (branch/tag, commit, docker image, since when) and the outcome of the
most recent deploy attempt — updated automatically by CI.

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
superseded deployments `inactive`. Statuses carry a `target_url` pointing at the ops
pipeline.

A second, empty-payload deployment record with `task: "deploy"` is created by GitHub's
`environment:` declaration in some workflows; it duplicates the same rollout and must be
ignored.

## Architecture

One new workflow listening to deployment status changes + one composite action holding the
Slack logic. **No existing workflow is modified.** Anything that changes the repo's
Deployments section — including future flows — is covered automatically, and the canvas
reflects the actual rollout outcome, not merely that CI requested a deploy.

### Workflow: `.github/workflows/slack-canvas.yml`

- Triggers:
  - `deployment_status` — the production path.
  - `workflow_dispatch` with inputs (`environment`, `state`, `ref`, `sha`, `docker-image`,
    `url`) — manual end-to-end testing, since `deployment_status` only fires once the
    workflow file exists on `main`. The dispatch path stamps the current UTC time as the
    timestamp.
- Job condition (deployment_status path):
  - `github.event.deployment.task == 'dcl/container-deployment'`
  - `github.event.deployment_status.state` ∈ {`in_progress`, `success`, `failure`, `error`}
    (skip `queued`, `pending`, `inactive`).
- `concurrency: slack-canvas-<environment>` (no cancel-in-progress) serializes canvas
  edits per environment. Cross-environment edits touch disjoint sections, so they may run
  concurrently.
- Steps: checkout (for the composite action) → invoke the composite action with values
  from the event (or dispatch inputs).
- Duplicate `in_progress` events (the bot emits two) produce an identical replace — a
  visual no-op. A late-running `in_progress` job racing a `success` job is possible but
  self-heals on the next event; accepted.

### Composite action: `.github/actions/slack-canvas-status/action.yml`

Inputs: `environment`, `state`, `ref`, `sha`, `docker-image`, `timestamp`, `url`,
`repo-url`, `slack-token`, `channel-id`. Implementation: bash + `curl` + `jq` against the
Slack Web API.

Flow:

1. `conversations.info` → read the channel's canvas file id
   (`.channel.properties.canvas.file_id`).
2. No canvas → `conversations.canvases.create` with a skeleton
   (`# 🚀 Deployments` heading + the managed lines for this event) and stop.
3. Compute which managed lines this event updates:
   - `success` → both the *running* line and the *last deploy* line for the environment.
   - `in_progress`, `failure`, `error` → only the *last deploy* line.
4. Per line: `canvases.sections.lookup` with `contains_text: <marker>` → found:
   `canvases.edit` `replace` on that `section_id`; not found: `canvases.edit`
   `insert_at_end`.

Line formats (one paragraph each — atomic replace, immune to the undocumented semantics
of replacing heading sections, and never touches human-authored canvas content):

```
**DEV running:** `main` @ [725ed44](https://github.com/decentraland/Pulse/commit/725ed44…) · `pulse-server:725ed44…` · since 2026-07-17 14:13 UTC
**DEV last deploy:** ✅ success · `main` @ [725ed44](https://github.com/decentraland/Pulse/commit/725ed44…) · 2026-07-17 14:13 UTC · [pipeline](https://…)
```

- Markers are the stable prefixes `DEV running:`, `DEV last deploy:`, `PRD running:`,
  `PRD last deploy:`.
- State emoji: 🔄 `in_progress`, ✅ `success`, ❌ `failure`/`error`.
- `ref` is shortened (`refs/heads/`, `refs/tags/` stripped); `sha` is shown as 8 chars
  linking to the commit on GitHub (`<repo-url>/commit/<full-sha>`, with `repo-url` passed
  in from `github.server_url`/`github.repository`); docker image shown without the
  `quay.io/decentraland/` prefix and with its tag truncated to 12 chars.
- Timestamp comes from `deployment_status.created_at` (UTC), not the runner clock.
- `url` is the status's `target_url` (ops pipeline); omitted when empty.

## Configuration

Repo secrets (new):

- `SLACK_BOT_TOKEN` — Slack app bot token. Scopes: `canvases:read`, `canvases:write`,
  `channels:read` (public channel) / `groups:read` (private channel). The bot must be a
  member of the channel.
- `SLACK_PULSE_CHANNEL_ID` — the Pulse channel id.

`docs/slack-canvas.md` documents the Slack app setup (create app, add scopes, install,
invite bot to channel, add secrets) and the manual-dispatch test procedure.

## Error handling

- Slack API failures (`ok: false`, HTTP errors) fail the step with the Slack error name
  in the log. The workflow is independent of the deploy flows and can never block or fail
  a deploy.
- The action never deletes sections; a malformed canvas at worst gains duplicate lines.
  Lookup+replace only ever rewrites the *first* section matching a marker, so any stale
  twins are left in place and must be deleted by hand — they are not reconciled
  automatically.

## Testing

- Composite action logic is plain bash; validated end-to-end via the `workflow_dispatch`
  trigger against the real channel once secrets are configured (dev-shaped test payload).
- No .NET code is touched; no unit tests apply.

## Out of scope

- Chat notifications (messages to the channel) — canvas only.
- Deploy history on the canvas — only *running* + *last deploy* per environment.
- Changes to `platform-actions` or the deploy actions themselves.
