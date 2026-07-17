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
