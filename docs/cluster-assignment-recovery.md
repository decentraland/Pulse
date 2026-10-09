# Cluster assignment recovery

A missed Core NATS change or lost Gatekeeper mirror can strand a stationary peer without
credentials. Pulse exposes the last completed pass's assignment and retained room obligations
independently of the change feed. For the design and failure cases, read the
[Pulse-owned recovery plan](demand-driven-recovery-protocol-plan.md); for field definitions, read
the [protocol contract](https://github.com/decentraland/protocol/blob/feat/pulse-room-recovery/docs/pulse-room-recovery.md).

## Broker contract

| Operation | Contract |
| --- | --- |
| Request `peer.{wallet}.cluster_assignment` | Body: raw UTF-8 session address, exactly 42 bytes. Wallet/session comparisons accept any casing. Active plans require the captured live registration still to match. Retained departures use their last session selector and reply only while the wallet has no live registration. Unknown wallets, malformed or mismatched sessions receive no reply; callers enforce a bounded timeout. |
| Reply | `PeerClusterChange` with desired assignment and additive `room_recovery` state. Cleanup-only replies have empty cluster/realm. Sent only to `_INBOX.` reply subjects; other subjects are rejected before spending request budget. |
| Hint `peer.{lowercase-wallet}.cluster_snapshot` | Same current-state payload for active assignments and retained departures, including unchanged ones. A delayed hint can be stale: query before acting. |
| Completion `peer.{wallet}.room_cleanup_completed` | Gatekeeper reports confirmed removal or observation of a completed departure. The tracker accepts only the current epoch/revision and matching operation. |
| Bootstrap `pulse.room_recovery.bootstrap_completed` | An operator confirms controlled recovery for an exact Pulse epoch. Every new epoch starts blocked. |

Consumers query on connect and before applying changes or hints. Only an exact ready plan
permits island credentials. Pending operations survive notification loss and coalescing; legacy
displaced-session fields are not the recovery ledger. Producer feed semantics live in
[clustering](clustering-on-aoi.md#feed). The session address is a selector, not a credential.

| Setting | Default | Non-positive value |
| --- | --- | --- |
| `Nats__AssignmentRefreshIntervalMs` | 30000 | Disables hints; lookup remains available. |
| `Nats__MaxAssignmentRequestsPerSecond` | 5000 | Disables the request cap. |

The cap uses fixed one-second windows, counting drops immediately and logging their total when the next window opens. Over-budget requests receive no reply. The subscription has no queue group: every connected instance spends budget before checking ownership, so with multiple responders the limit covers all requested wallets, not just locally owned peers. Both operations require configured NATS and the cluster tracker.

### Broker permissions

Scope recovery subjects to Pulse and Gatekeeper. Only Gatekeeper publishes wallet completions;
only the operator publishes bootstrap confirmation. Connector/client identities receive neither
permission. Pulse needs response permissions (`allow_responses`, one response per request), and
Gatekeeper subscribes to `_INBOX.>` for replies. `engine.islands` already exposes cluster rosters,
so withholding the session from hints would not provide broker isolation.

## Assignment consistency

The tracker reconciles desired assignments before applying completion messages, then publishes
an immutable recovery snapshot. A stale completion cannot clear a newer plan. Lookup and hints
also check the live registration, deferring during takeover lag or recycled-slot mismatch.
An unchanged reconnect keeps its revision after the tracker adopts the new registration.

The active assignment map remains separate from retained recovery state. Departures stay
queryable until cleanup is confirmed, Gatekeeper has observed retirement and the last cutoff
has passed. Unfinished work never expires. Capacity exhaustion blocks admission rather than
discarding room obligations. Topology and recovery publication are separate; a lookup can
briefly name a cluster absent from the latest topology.

## Rollout

This hardening builds on [Pulse #51](https://github.com/decentraland/Pulse/pull/51),
[Gatekeeper #300](https://github.com/decentraland/comms-gatekeeper/pull/300) and
[connector #136](https://github.com/decentraland/archipelago-workers/pull/136). Additive protobuf
compatibility does not make old Gatekeepers safe: they ignore the admission barrier. Activate
the backend pair under closed admission with the journal migration and restricted broker
permissions in place. Follow the
[bootstrap procedure](https://github.com/decentraland/comms-gatekeeper/blob/fix/pulse-owned-room-recovery/docs/room-recovery-operations.md).

After Pulse confirms readiness, Gatekeeper preserves existing room membership and can recover
credentials for an absent wallet. Unknown authority defers work. Connector deduplication permits
replacement credentials for the same room; Explorer keeps its existing metadata and token
contract. A Pulse connection is still required for an active assignment.

## Limits

- **Pulse restart:** room IDs include the boot epoch, but the ledger is in memory. Every restart
  requires controlled old-room recovery and explicit bootstrap; seamless restart is additional scope.
- **Uncertain removal:** Gatekeeper's durable dispatch journal blocks the affected wallet until
  operator reconciliation. A timer, broker flush or membership absence cannot prove revocation.
- **Deployment topology:** one Pulse and one active Gatekeeper. There is no distributed fence
  for overlapping instances. Actual Cloud revocation, clock bounds and future-token admission
  remain controlled acceptance requirements.

## Verification

Tracker/resolver tests cover assignment consistency, takeover chains, movement, capacity,
bootstrap and retained departures. `ClusterAssignmentRecoveryIntegrationTests` uses a real
broker through `NATS_TEST_URL`, including completion and retirement observation through the
actual tracker. These tests do not establish LiveKit Cloud enforcement.

Reply/hint publish successes and failures use existing NATS counters. Intentional silence for a non-owned session is not a publish failure; [metrics](metrics.md#cluster-metrics) defines the counting scopes.
