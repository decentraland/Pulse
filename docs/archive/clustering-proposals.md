# Deferred clustering proposals

Historical design notes recovered from
[`clustering-on-aoi.md` at `986d8a2`](https://github.com/decentraland/Pulse/blob/986d8a24064bf16918456326a4b949ebffe03fd1/docs/clustering-on-aoi.md).
These proposals were removed when the active guide was shortened. They remain deferred, outside
the current takeover recovery work. Component names and numeric examples below describe that design
snapshot; verify them when resuming a proposal. Current behavior and rollout belong to
[clustering](../clustering-on-aoi.md) and [assignment recovery](../cluster-assignment-recovery.md).

## Crowd ghost avatars

Removing the cluster size cap also removes the old bound on co-located crowds. The proposal
keeps excess visible avatars as moving ghosts with nameplates, skipping wearable downloads,
instantiation and skinning. It targets client GPU/memory cost; it leaves Pulse's visibility and
room composition intact.

The original integration points were `AvatarBase.GhostGameObject`, `GhostHologram` and
`AvatarGhostSystem` for the existing loading reveal; `IPerformanceBudget` and
`AvatarInstantiatorSystem` for resource budgets; and `QualitySettingsAsset` /
`IQualityLevelController` for quality tiers. The existing ghost used one renderer and a
per-avatar material tinted by profile color.

- **Ranking:** `AvatarCrowdBudgetSystem`, before `AvatarInstantiatorSystem` in `AvatarGroup`,
  would rank visible avatars by camera distance/AoI tier and grant full rendering to the top K.
  The rest would remain in a steady ghost state instead of completing their loading reveal.
- **Budget:** quality-tier bounds would anchor K, adjusted slowly by an additive-increase,
  multiplicative-decrease controller. The original examples were Low 20, Mid 50 and High 100,
  illustrative rather than accepted limits. The proposed signal was `Profiler.LastGpuFrameTimeValueNs`
  (`ProfilerRecorder` GPU Frame Time), with `PerformanceBottleneckDetector` /
  `FrameTimingManager` restricting demotions to GPU-bound frames: GPU time includes the scene,
  not just avatars. CPU timing plus fixed tier caps was the fallback where GPU timing is unavailable.
- **Stability:** the proposed controller ran around 1 Hz with an exponential moving average,
  dead band and cooldown at least as long as a rendering transition. Timing samples lag and
  demotion releases resources late. Rank margin and dwell hysteresis would prevent boundary
  avatars from repeatedly changing representation.
- **Transitions:** promotion would reuse the ghost-to-full reveal. Demotion needs a reverse
  reveal and wearable release through the existing cleanup/pool path, with emote props/audio
  suppressed while ghosted.
  Pool churn remains a risk; ghosts themselves are skinned meshes, so a second threshold may
  need to hide avatars entirely.

## WS Connector retirement and token exchange

The proposal moves island credential delivery from Connector to a Pulse client message,
`ClusterChanged { cluster_id, realm }` on reliable channel 0. The client exchanges that
assignment at Gatekeeper for a LiveKit token. Gatekeeper remains the token issuer; the proposed
pull flow replaces its cluster-feed subscription and Connector's credential forwarding.

Prerequisites were Pulse transport support in every client, including WebTransport for browsers,
and Pulse endpoint advertisement by `realm-provider` and `lamb2`. The original sketch did not
define how a token exchange proves current assignment authority or handles session takeover.
Resuming it requires a contract that preserves the current authority and recovery guarantees.

## Ban enforcement and endpoint advertisement

Connector retirement would remove its handshake ban check and periodic sweep. Pulse would
retain connection rejection and mid-session kicks through `BansPollingHttpService` /
`BanEnforcer`; Gatekeeper would retain ban origination and checks at token issuance. The client's
status screen would remain. This consolidation depended on retirement, not on disabling checks
while Connector still serves clients.

`realm-provider` and `lamb2` would replace Connector `/status` checks and the
`archipelago:wss://.../ws` adapter advertisement with Pulse status and endpoint information.
One Pulse hosts multiple realms, so its peer capacity applies to their combined population.
Capacity routing and future instance sharding remained open questions in that proposal.
