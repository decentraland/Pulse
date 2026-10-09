# Peer clustering over Pulse AoI

Pulse derives realm-scoped clusters from the same spatial boards used by avatar interest management. A cluster is a proximity-connected group, with no size cap or transport endpoint. "Island" remains the legacy wire vocabulary (`engine.islands`, `IslandStatusMessage`, LiveKit room names).

This branch implements clustering and assignment recovery. Iteration-2 presence/HTTP work has a separate migration plan; its proposed endpoints are not part of this contract.

## Components and algorithm

`ClusterTracker` runs a dedicated background pass, outside worker simulation. It reads `RealmSpatialGrids`, `SnapshotBoard` and `IdentityBoard`, then atomically publishes an immutable `ClusterPass` to `ClusterBoard`. Working buffers are reused; published passes are fresh because readers retain them. `RunPass` is the deterministic test/benchmark seam.

Each pass:

1. Enumerate occupied cells per realm. Include only registered peers that remain their wallet's current binding; listeners and realm-less peers contribute no grid placement.
2. Union each cell with its eight occupied neighbors within that realm. Each connected component becomes a cluster; compute count, XZ centroid and radius.
3. Assign sticky IDs, apply publication debounce, publish topology, expose current recovery assignments, then emit assignment changes. Recovery assignments use the post-debounce room, while topology describes the computed components.

Cost is O(N + C), with C occupied cells. Cell adjacency is transitive: distant chain ends can share a cluster while outside each other's AoI. Consumers must treat clusters as room groupings, not visibility sets.

Retained cell occupant sets can outlive movement or a realm change. A pass attributes each peer
to the first grid that collects it; concurrent teleport or slot reuse can temporarily preserve
its previous cell/realm assignment until a stable pass. Interest management separately checks
the accepted snapshot's realm.

## Stability

A component inherits the previous computed cluster with greatest member overlap; ties prefer older IDs. Competing components claim an ID by greatest overlap, with exact ties resolved by discovery order. Unmatched components receive `{IdPrefix}{bootEpoch}-{n}`.

Keep computed and published assignments separate: inheritance from a debounce-held publication can mint a new candidate every pass and prevent the dwell streak completing. Publish a reassignment after `DwellPasses` agreeing passes, except first assignment, teleport, realm change or deletion of the old cluster. Realm changes publish even if the sticky ID survives. Teleport bypass is best-effort because later movement can replace its latest-snapshot marker.

Immutable identity registrations reset recycled slots, including identical-wallet/session reconnects between passes. The current assignment map and its remaining read races are defined in [assignment recovery](cluster-assignment-recovery.md).

## Geometry and measured limits

Resolve current settings from `appsettings.json` and `SpatialHashAreaOfInterestOptions`; their defaults differ. At the inspected 100-unit cell size, adjacent-cell peers can be 0–283 units apart. A dense realm can become one large component: `CeilingUniform` measured 1,904 occupied cells and a largest cluster of 4,091/4,095 peers. Room-size or diameter bounds, if needed, belong in Pulse's tracker; gatekeeper maps one cluster directly to `island-{clusterId}`.

`ClusterTrackerBenchmarks` measured about 395 us per cold capacity pass (321 us warm), with roughly 230 KB allocated for the immutable output. Cold cost is pass-plus-churn minus churn-only, a difference of means whose uncertainty includes both rows. These measurements describe tracker cost, not AoI throughput or LiveKit capacity.

The [dense](img/island-clustering-illustration.png), [chain](img/island-clustering-chain-illustration.png) and [sparse](img/island-clustering-sporadic-illustration.png) illustrations use an older 50-unit grid. Reproduce current topology with `ClusterScenario`; the capacity-density result above is the relevant warning at 100 units.

## Feed

Pulse owns assignments, retained cleanup and admission readiness. Gatekeeper executes removal,
checks bans and mints credentials only for a ready plan; WS Connector delivers them to the
authenticated session. Pulse publishes no LiveKit tokens or client cluster messages.

| Subject | Payload | Trigger |
| --- | --- | --- |
| `peer.{lowercase-wallet}.cluster_change` | `PeerClusterChange` | Published assignment changes |
| `engine.islands` | `IslandStatusMessage` | Each pass |
| `engine.discovery` | `ServiceDiscoveryMessage` | Discovery timer |
| `peer.{lowercase-wallet}.cluster_snapshot` | `PeerClusterChange` | Recovery timer |
| `peer.{wallet}.cluster_assignment` | Raw session request -> `PeerClusterChange` reply | Session-scoped lookup |

`PeerClusterChange` carries cluster, realm, authenticated session and a room-recovery plan.
The session is the lowercase final signing address, or wallet without delegation. Legacy
`displaced_session` and `displaced_cluster_id` fields retain their bounded event-history behavior;
the independent recovery ledger preserves unfinished room obligations without expiry. Changes,
replies and hints expose that plan. Completion subjects, authority rules and rollout are defined
in [assignment recovery](cluster-assignment-recovery.md).

`engine.islands` reports `max_peers = 0` for uncapped clusters. Discovery timestamps use uint64 epoch milliseconds; uint32 overflow breaks legacy health checks. Generated contracts come from `archipelago.proto` and `pulse_clusters.proto` in the protocol repository.

### Delivery

`NatsPublisher` keeps topology in one latest-wins slot and assignments in a bounded outbox, one latest value per wallet. Different wallets cannot supersede each other. Exceeding `ChannelCapacity` distinct pending wallets evicts the longest-admitted entry; benign same-wallet superseding is counted separately. Discovery bypasses the outbox. Topology is queued before change events, but separate subscribers cannot rely on cross-subject delivery order.

Core NATS is at-most-once. Broker failure preserves pending state and leaves tracker/simulation running; reconnect and supervision loops restore the publisher/responder. Auth-error abort is disabled so rotated credentials can recover. Current-assignment queries plus periodic hints repair missed changes for stationary peers.

## Configuration and metrics

`Clusters:Enabled` controls the tracker; `Nats:Url` enables broker publication and recovery independently. The checked-in configuration runs clustering with an empty broker URL. `Nats__Url` takes precedence over the `NATS_URL` alias. Clearing a URL disables the feed; it is not a coordinated migration rollback.

Use `ClusterOptions`, `NatsOptions` and runtime configuration for defaults. `PassIntervalMs` controls pass cadence; `DwellPasses` controls reassignment delay; `SessionRetentionPasses=0` disables legacy event history, while room-recovery obligations remain retained. Recovery timer/request-budget settings are documented once in the recovery contract.

[Metrics](metrics.md#cluster-metrics) defines tracker and NATS counters. Watch connected state, dropped assignments and publish failures separately: outbox capacity addresses distinct-wallet eviction, not failed broker writes. Superseding is expected.

## Migration

The original clustering cutover replaces archipelago-core with Pulse as cluster author and gatekeeper as token issuer; WS Connector retains session delivery. Stats/client heartbeat retirement belongs to iteration 2, with `/hot-scenes` owned by gatekeeper's Catalyst integration.

For iteration 2 use the workspace `archipelago-workers/Claude outputs/iteration-2-implementation-plan.md` (revision 4), its `iteration-2-direct-cutover-rev4.md` task matrix, and the [release tree](https://app.notion.com/p/3db5f41146a58116aaf2c6e738f9cdaf). Those supersede historical flag/parking instructions. Release removes legacy producers only after their consumers migrate; rollback coordinates previous images, compatible clients and routing. This document supplies no deployment authorization.

For the additive PR #51 recovery rollout, use [assignment recovery](cluster-assignment-recovery.md#rollout).

## Open questions

Boot-scoped IDs prevent room-name reuse after restart. The recovery ledger remains in memory,
so each new epoch requires controlled bootstrap. Replicas and overlapping ownership would need
distributed fencing; the current contract assumes one Pulse and one active Gatekeeper.

Shadow measurements should determine whether cell resolution, exact-distance refinement, cluster size or diameter needs bounding. Scene listeners already contribute no snapshots or cluster members. Broader endpoint advertisement and WS Connector retirement remain separate work.
