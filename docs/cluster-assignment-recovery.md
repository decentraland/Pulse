# Cluster assignment recovery

The change feed is not a durable assignment store. A stationary peer may emit no further
`cluster_change` events after a missed delivery, and gatekeeper's local mirror can expire or
be lost on restart. Pulse now exposes its current assignment independently of that feed.

## Broker contract

* Request `peer.{lowercase-wallet}.cluster_assignment` with the raw UTF-8 ephemeral session
  address as the body. The reply is the existing protobuf `PeerClusterChange`, with cluster,
  realm and session populated. Unknown/departed wallets, malformed requests and mismatched
  sessions receive an empty protobuf. An empty request body supports legacy connectors.
* Every 30 seconds, `peer.{lowercase-wallet}.cluster_snapshot` carries the same protobuf as
  a recovery hint. Consumers must query the current assignment before acting on a hint;
  a hint delayed by broker backpressure may describe an older assignment.
* Neither reply nor hint repeats displaced-session cleanup. The original `cluster_change`
  event retains takeover semantics. Coalescing subsequent moves for the same replacement
  session preserves any displaced identity still pending in the outbox.

Assignments reflect the last completed clustering pass (normally one second), using the
post-debounce room actually published, rather than the candidate topology. Departed peers
are excluded even while the tracker retains their takeover ledger. Reads are safe against
the next pass replacing the entire map. This is not an instantaneous session registry: a
departure or takeover becomes visible on the next completed pass.

`Nats__AssignmentRefreshIntervalMs` changes the hint interval. A non-positive value disables
hints but leaves request/reply available. Both require the existing NATS and cluster tracker
configuration. No token is minted by Pulse.

## Rollout

Deploy this Pulse change before the companion gatekeeper recovery change. The new subjects
are additive and older gatekeepers ignore them. Gatekeeper then queries on every connect
and every recovery hint, checks the active session and LiveKit membership, and mints only
when the wallet is absent from its assigned room. An unavailable authority fails closed;
the next hint retries. Deploy the connector dropped-frame recovery afterward.

This does not add an assignment producer for clients that never connect to Pulse, nor does
it persist every historical takeover during an outage. The single displaced-session field
cannot represent an arbitrary chain of takeovers. It repairs the confirmed same-session
move overwriting a pending takeover, and restores current assignments for active peers.
Gatekeeper's membership guard identifies the wallet, not the ephemeral session. If a lost
takeover leaves the displaced session occupying the same desired room, hints alone cannot
distinguish it from a healthy participant and will not evict it.

## Tests

Tracker regressions cover stationary peers beyond the former one-hour mirror lifetime,
departure, replacement sessions, immutable snapshots and debounce. Resolver tests cover
session isolation, malformed requests, legacy requests and session replacement. Broker
integration tests run with `NATS_TEST_URL` set; CI supplies a local NATS service.
