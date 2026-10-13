# Cluster assignment recovery

A missed Core NATS event or lost Gatekeeper mirror can strand a stationary peer without credentials.
Pulse exposes its current assignment and retained pending takeovers independently of the change feed.
Read the [takeover recovery plan](demand-driven-recovery-protocol-plan.md) for the design and limits.

## Broker contract

| Operation | Contract |
| --- | --- |
| Request `peer.{wallet}.cluster_assignment` | Raw UTF-8 session address, exactly 42 bytes. Wallet/session comparisons accept any casing. A matching active assignment requires its captured live registration. Explicitly departed records reply only while pending takeovers remain, using the retained winner session. Unknown, completed departed, malformed or mismatched requests receive no reply; callers use bounded timeouts and treat silence as unknown. |
| Reply | `PeerClusterChange` with assignment, repeated `pending_takeovers` and optional `token_not_before`. A supported assignment includes the token floor even when zero. Its absence means unsupported or capacity blocked. Only `_INBOX.` reply subjects are accepted. |
| Hint `peer.{lowercase-wallet}.cluster_snapshot` | Same assignment payload for active registrations or explicitly departed records with pending work. A delayed hint can be stale: query before acting. |
| Completion `peer.{wallet}.takeover_completed` | `TakeoverCompleted` with a unique takeover ID and positive JavaScript-safe `revoke_before`. Only a matching pending ID for that wallet is accepted. |

Gatekeeper can issue island credentials only when the capability marker is present and pending takeovers
are empty. Every token uses the retained `token_not_before` floor. Legacy displaced-session fields remain
compatible feed annotations and are not the retained recovery state. Field 6 and `room_recovery` are reserved.

| Setting | Default | Non-positive value |
| --- | --- | --- |
| `Nats__AssignmentRefreshIntervalMs` | 30000 | Disables hints; lookup remains available. |
| `Nats__MaxAssignmentRequestsPerSecond` | 5000 | Disables the request cap. |

The request cap uses fixed one-second windows. Over-budget requests receive no reply. The subscription
has no queue group, so each connected responder spends budget before checking ownership. Lookup and
hints require configured NATS and the tracker. Core NATS delivery loss is repaired by another lookup or
hint; publication success does not prove consumer receipt.

### Broker permissions

Only Gatekeeper publishes takeover completions. Connector/client identities receive no completion
permissions. Pulse needs response permissions (`allow_responses`, one response per request), and
Gatekeeper subscribes to `_INBOX.>` for replies. The session selector is not authentication.

## Assignment consistency

The tracker adopts desired assignments before applying queued completions, then publishes an immutable
snapshot. Lookup and hints check the captured live registration, including same-session reconnects and
recycled slots. A stale completion cannot clear a fresh same-room takeover because IDs are globally unique.

A ready assignment is conservatively treated as potentially credentialed. Displacing it creates retained
pending work. An unadmitted A -> B -> C chain keeps A's pending takeover; B cannot receive credentials
while A remains pending and therefore creates no additional removal obligation. Same-session ordinary
moves create no takeover work. A later admitted same-room ABA transition receives a fresh ID.

Unfinished takeovers survive departures and never expire. Explicitly departed records remain discoverable
through hints and exact retained-session lookup only while pending work exists. After completion they
never return a ready assignment. Completed records use the existing session retention window and retain
the token floor until its cutoff and clock grace have passed. Completion cutoffs must exceed the floor
captured when that takeover was created; accepted cutoffs raise the wallet floor using their maximum.

Capacity limits are hard bounds on retained wallets and pending takeovers. At capacity, Pulse retains
existing pending work and any exposed displacement it cannot yet record. A blocked or overflow
assignment omits `token_not_before`, even when its pending list is empty, and cannot authorize credentials.

## Limits and rollout

This contract covers in-process session takeover recovery and credential recovery. Ordinary move/departure
cleanup, crash-safe durable state and automated Pulse restart recovery are outside this scope. Room IDs
retain a local boot identifier; it is not a restart recovery handshake. There is no bootstrap, revision,
retirement observation or general all-room cleanup orchestrator.

One Pulse and one active Gatekeeper are supported. Paired backend rollout requires closed admission
through the incompatible interval. Old Gatekeepers ignore the pending-work barrier. Existing Cloud
revocation, current-holder guards, token clock bounds and actual Cloud acceptance remain separate
requirements. Timeout or no responders is unknown authority and cannot authorize destructive cleanup.

## Verification

Ledger and tracker tests cover lost hints/completions, repeated pending state, unadmitted chains,
ABA, live-registration fencing, cutoffs and capacity. `ClusterAssignmentRecoveryIntegrationTests`
uses a real broker through `NATS_TEST_URL`, including the takeover completion subject. These tests
do not establish LiveKit Cloud enforcement. Reply/hint publication uses the existing NATS counters.
