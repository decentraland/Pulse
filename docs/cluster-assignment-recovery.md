# Cluster assignment recovery

The change feed is not a durable assignment store. A stationary peer may emit no further
`cluster_change` events after a missed delivery, and gatekeeper's local mirror can expire or
be lost on restart. Pulse now exposes its current assignment independently of that feed.

## Broker contract

* Request `peer.{wallet}.cluster_assignment` with the raw UTF-8 ephemeral session address as
  the body; the wallet in the subject may carry any casing. The reply is the existing protobuf
  `PeerClusterChange`, with cluster, realm and session populated. Unknown/departed wallets,
  malformed requests, requests without a session and mismatched sessions receive no reply;
  callers must enforce a bounded timeout. There is no session-less mode: an instance replies
  only when it owns the requested session, so an empty or warming instance cannot mask the
  owner during an overlapping deployment, and no broker client can read an assignment it
  cannot name the session of.
* Replies go only to the requester's inbox — a reply subject under the `_INBOX.` prefix both
  NATS clients default to. A request naming any other reply subject is ignored, so Pulse never
  publishes a `PeerClusterChange` under a subject the requester chose. Ignored requests are
  counted but spend none of the request budget, so they cannot crowd out a valid lookup.
* Each instance handles at most `Nats__MaxAssignmentRequestsPerSecond` requests per second
  (default 5000, counted over fixed one-second windows) and drops the rest unanswered, counting
  each drop as it happens and logging the window's total when the next window opens. A dropped request looks to the caller like any other timeout, and
  the next hint retries it.
* Every 30 seconds, `peer.{lowercase-wallet}.cluster_snapshot` carries the same protobuf as
  a recovery hint, session included: the consumer's hint handler keys its re-query on that
  session, and it is the same ephemeral address every `cluster_change` already carries — a
  selector, not a credential. Consumers must query the current assignment before acting on a
  hint; a hint delayed by broker backpressure may describe an older assignment. Consumers must
  also query before applying `cluster_change`, validating its session and using the current
  room while retaining the matching event's displaced-session cleanup.
* What a broker client may learn is bounded by broker permissions, not by these payloads:
  `engine.islands` already lists every wallet per cluster each pass. Scope publish and
  subscribe on `peer.*` to Pulse and gatekeeper.
* Neither reply nor hint repeats displaced-session cleanup. The original `cluster_change`
  event retains takeover semantics. Coalescing subsequent moves for the same replacement
  session preserves any displaced identity still pending in the outbox.

Assignments reflect the last completed clustering pass (normally one second), using the
post-debounce room actually published, rather than the candidate topology. Departed peers
are excluded even while the tracker retains their takeover ledger. Reads are safe against
the next pass replacing the entire map. This is not an instantaneous session registry: a
departure or takeover becomes visible on the next completed pass.
The complete assignment map is published before change events for that pass, and after the
pass's topology snapshot, so an assignment read in between can name a cluster the latest
`engine.islands` no longer lists; the next pass reconciles both. Reused peer slots are reset
using an atomically read identity registration, including reconnects with the same wallet and
ephemeral session that happen between tracker passes; a slot reused between the grid read and
that check can still be attributed to the previous occupant's cell for that one pass.

`Nats__AssignmentRefreshIntervalMs` changes the hint interval. A non-positive value disables
hints but leaves request/reply available. Both require the existing NATS and cluster tracker
configuration. No token is minted by Pulse.

## Rollout

Deploy this Pulse change before the companion gatekeeper recovery change. The new subjects
are additive and older gatekeepers ignore them. Gatekeeper then queries on every connect
and before every change event or recovery hint. Recovery checks the active session and
LiveKit membership, and mints only when the wallet is absent from its assigned room.
An unavailable authority fails closed;
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
departure, immediate slot reuse, replacement sessions, immutable snapshots, event ordering, debounce
and lower-cased assignment keys. Resolver tests cover session isolation, malformed and
session-less requests, any-cased subjects, session replacement, the reply-inbox guard and the
request budget. Broker integration tests run with `NATS_TEST_URL` set; CI supplies a local NATS
service. They cover multiple responders, publisher connection loss and resubscription, repeated
unchanged hints, a failed reply followed by recovery, a session-less request, a checksum-cased
subject and a request naming a foreign reply subject. Reply and hint publish successes/failures contribute
to the existing NATS counters; intentional silence on a non-owned session is not a failure.
