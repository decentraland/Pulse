# Slack Canvas Deployment Status

`.github/workflows/slack-canvas.yml` keeps the Pulse Slack channel's canvas in sync with
deployments — **without a Slack app**: the delivery side is a Slack Workflow Builder
workflow that any member can create (no admin approval), triggered by a webhook.

How it works:

1. The GitHub workflow listens to `deployment_status` events (every deploy flow ends in
   `dcl-deploy-action`, which creates a GitHub Deployment whose status `decentraland-bot`
   drives through the real container rollout).
2. On each qualifying event it re-renders the status of **both** environments from the
   GitHub Deployments API — stateless, so any single event produces a complete, correct
   picture:
   - *running* — the newest deployment whose latest status is `success`;
   - *last deploy* — the newest deployment regardless of outcome
     (🔄 in progress, ✅ success, ❌ failure/error).
3. It POSTs the rendered lines as webhook variables to the Slack workflow, whose
   *Update a canvas* step **replaces the entire canvas content**.

> **The canvas is CI-owned.** Because the Slack step replaces the whole canvas on every
> deploy, anything written on it by hand is wiped on the next update. Keep human notes in
> the channel or another canvas.

## Webhook variables

All values are plain text (Workflow Builder interpolates variables literally — no
markdown), so visual formatting lives in the Slack workflow's rich-text editor:

| Variable | Example |
|---|---|
| `dev_running` | `main @ 725ed44d · pulse-server:725ed44d0e4c… · since 2026-07-17 14:13 UTC` |
| `dev_last_deploy` | `✅ success · main @ 725ed44d · 2026-07-17 14:13 UTC` |
| `dev_running_commit_url` | `https://github.com/decentraland/Pulse/commit/<full sha>` |
| `dev_last_deploy_commit_url` | `https://github.com/decentraland/Pulse/commit/<full sha>` |
| `prd_running` / `prd_last_deploy` / `prd_running_commit_url` / `prd_last_deploy_commit_url` | same shape for prd |
| `updated_at` | `2026-07-20 10:02 UTC` |

Environments with no deployment yet render as `—` with empty commit URLs; an environment
whose recent deploys all failed renders `⚠️ no recent success (last N deploys)` as its
running line. (If Slack ever rejects the empty-string URL variables — only verifiable
live, via the post-setup dispatch test — switch the empty URLs to `—` in
`update-canvas.sh`.)

## One-time Slack workflow setup (no admin needed)

Requires a paid Slack plan and that your workspace hasn't restricted Workflow Builder.

1. In Slack, open **Automations** (left sidebar; or workspace name → *Tools & settings*
   → *Workflow Builder*) → **New Workflow** → **Build Workflow**.
2. **Trigger:** choose **From a webhook**. Under *Set up variables*, add each variable
   from the table above (all **Text** type, names must match exactly):
   `dev_running`, `dev_last_deploy`, `dev_running_commit_url`,
   `dev_last_deploy_commit_url`, `prd_running`, `prd_last_deploy`,
   `prd_running_commit_url`, `prd_last_deploy_commit_url`, `updated_at`.
3. **Step:** add **Canvas → Update a canvas**.
   - *Select a canvas:* pick the Pulse channel's canvas. (If the channel has no canvas
     yet, open the channel and click the canvas icon once so it exists.)
   - *Update type:* **Replace** — not append/prepend.
   - *Content:* compose the layout in the rich-text editor, inserting the variables via
     *Insert a variable*. Suggested layout (bold labels are editor formatting, `{…}` are
     variables):

     ```
     🚀 Deployments

     DEV
     Running:      {dev_running}
     {dev_running_commit_url}
     Last deploy:  {dev_last_deploy}
     {dev_last_deploy_commit_url}

     PRD
     Running:      {prd_running}
     {prd_running_commit_url}
     Last deploy:  {prd_last_deploy}
     {prd_last_deploy_commit_url}

     Last updated: {updated_at}
     ```

     The `*_commit_url` lines are optional — include them if you want clickable links to
     the deployed commits (Slack auto-links URLs).
4. **Publish** the workflow, then copy the **webhook URL** from the trigger settings.
   Treat it as a credential: anyone holding it can rewrite the canvas.
5. Add it as a repo secret (repo → Settings → Secrets and variables → Actions, or
   `gh secret set SLACK_CANVAS_WEBHOOK_URL --repo decentraland/Pulse`):
   - `SLACK_CANVAS_WEBHOOK_URL` — the webhook URL.

## Testing

`deployment_status` only fires for the workflow version on `main`, so after merging test
it manually: Actions → *Slack Canvas Deploy Status* → *Run workflow* (no inputs — it
re-renders everything from the Deployments API). Failures are loud in the step log:

- HTTP `404` from the webhook — workflow unpublished or wrong URL.
- HTTP `400` — a variable name in Slack doesn't match the payload (check spelling).
- GitHub API errors — the job's `deployments: read` permission or `repo` input.

The action's logic is covered by local tests (no Slack account needed):

    bash .github/actions/slack-canvas-status/test/test-render.sh
    bash .github/actions/slack-canvas-status/test/test-flow.sh

They also run in CI via the `slack-canvas-tests` job in `test.yml`.

## Known limitations

- Variables render as plain text in the canvas; the four `*_commit_url` variables exist
  because a commit hash can't be turned into a hyperlink through a webhook variable.
- The canvas step updates the canvas as the workflow's creator; if that person leaves the
  workspace, reassign the workflow's ownership or recreate it.
