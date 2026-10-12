# Retained takeover recovery

12 October 2026. Implemented locally; local checks passed. Replaces the broader room-recovery plan.
One Pulse serves all users and one Gatekeeper executes room operations.

## Problem and scope

A takeover notification can be lost while the old session remains in LiveKit. Existing
recovery knows the new assignment but checks only whether the wallet occupies its room.
That can mistake the old session for the new one and suppress replacement credentials.

**Pulse retains unfinished takeovers in the assignment state.** Existing change events,
periodic hints and positive assignment lookups carry that state. Gatekeeper finishes the
retained cleanup before issuing replacement credentials. Explorer remains unchanged.

This change repairs lost notifications and completions during normal service operation.
It does not add a general room lifecycle controller or promise crash-safe recovery of an
unknown LiveKit operation.

## Protocol

| Message | Contents |
| --- | --- |
| Existing `PeerClusterChange` | Existing assignment fields; `pending_takeovers`; optional `token_not_before` |
| Embedded `PendingTakeover` | Unique `takeover_id`, `displaced_session`, `displaced_cluster_id` |
| New `TakeoverCompleted` | `takeover_id`, confirmed `revoke_before` timestamp |

`TakeoverCompleted` is the only new NATS event, published by Gatekeeper on
`peer.{wallet}.takeover_completed`. Broker permissions restrict that publisher to Gatekeeper.
The existing assignment request/reply exchange stays unchanged.

The timestamp is in whole Unix seconds. Present zero means a supported first assignment;
absence cannot authorize credentials. An empty pending list permits credentials only when
that timestamp is present and the assignment is still current. This prevents an old Pulse
response from silently bypassing the new behavior.

Published draft field 6 is reserved rather than reused. Fields 1–5 keep their wire meaning.
The [protocol contract](https://github.com/decentraland/protocol/blob/feat/pulse-room-recovery/docs/pulse-room-recovery.md)
owns field definitions and validation.

## Communication

```text
Pulse: retain takeover A -> B with a unique ID
  -> existing change event, or a later periodic hint
Gatekeeper -> Pulse: existing assignment lookup for B
Pulse -> Gatekeeper: B's assignment + unfinished takeover
Gatekeeper -> LiveKit: remove wallet from displaced room; revoke old tokens
LiveKit -> Gatekeeper: confirmed result
Gatekeeper -> Pulse: TakeoverCompleted(ID, revocation cutoff)
Pulse: remove that pending ID; retain the confirmed token floor
Gatekeeper -> Pulse: read the current assignment again
Pulse -> Gatekeeper: current assignment, no pending takeover, token floor
Gatekeeper -> Connector -> Explorer: existing island_changed credentials
```

Gatekeeper waits until Pulse applies completion before issuing credentials. A successful
completion publish alone is insufficient. This prevents a later hint from removing the winner.

## Rules that make retries safe

- **Retain until completion.** Outbox replacement, disconnects and later takeovers cannot erase
  unfinished cleanup. Snapshot publication copies state; it does not consume it.
- **Identify the operation.** A new takeover gets a globally unique ID, including A → B → A
  after B became eligible. Duplicate or unknown completion IDs do nothing. Pulse accepts a
  cutoff only above the operation's prior token floor and never lowers the confirmed floor.
- **Preserve earlier work.** If B never became eligible for credentials, A → B → C retains A's
  cleanup and changes the desired winner to C. It does not replace A with an unadmitted B.
- **Avoid repeated removal.** Gatekeeper caches confirmed results in bounded process-local
  memory and re-sends completion after message loss. A fresh authoritative read proving that
  an ID is no longer pending allows its confirmed cache entry to be removed.
- **Check before acting.** Serialize work per wallet. Re-read authority before removal and
  around credential issuance. Lookup failure defers work. Every credential path observes the
  pending list and token floor.
- **Bound memory.** At capacity, withhold credential eligibility and retain existing work.
  Unresolved operations are never evicted merely to admit another user.

## Limits and rollout

An uncertain LiveKit result blocks the affected wallet in the running Gatekeeper. It is not
blindly retried: an old removal may still execute against a replacement. The block and receipt
cache are in memory. A Gatekeeper crash during such a call requires operational reconciliation;
this smaller change does not provide a durable execution journal.

Pulse's takeover state is also in memory. Recovery after Pulse state loss, general cleanup of
ordinary room movement/departure, and overlapping Gatekeepers remain separate requirements.
There is no bootstrap message, epoch/revision protocol, admission enum, database migration,
or retirement-observation handshake. Deployment must preserve the single-active-service
assumption and explicitly reconcile uncertain operations or lost Pulse state.

Publish the protocol artifact, then activate the matching Pulse/Gatekeeper pair. The existing
Connector PR is still needed to forward changed credentials for the same room. No Explorer
change is required. Cloud revocation and ordinary Explorer reconnect behavior require
controlled acceptance before production rollout.

## Implementation and verification

1. Replace the draft protocol and generated bindings; prove fields 1–5 remain compatible and
   present-zero differs from an absent token floor.
2. Simplify Pulse's ledger, inbox and lookup/hint publication; test message loss, chains, ABA,
   stale completion, registration changes and capacity without dropping pending work.
3. Simplify Gatekeeper and remove SQL journal/bootstrap machinery; test duplicate hints,
   lost completion, uncertain results, stale asynchronous work and credential recovery.
4. Run affected suites and real broker integration, verify the published dependency artifact,
   and update existing PRs with the narrower guarantees and remaining acceptance gates.
