# NATS use for Pulse

Reviewed October 8, 2026. Scope: one Pulse instance serves all users; one active gatekeeper.

## Recommendation

Keep NATS for change notifications, recovery hints and observability. Preserve the existing positive assignment lookup, with failures treated as unknown. For future authoritative read APIs, prefer evaluating direct HTTP or gRPC rather than expanding NATS into a chain of credential requests and delivery acknowledgements. This is an architectural preference to reduce coordination, not proof that NATS request-reply is inherently unsuitable.

**The supported no-go is using silence as an authoritative answer or Core NATS as durable workflow storage.** NATS explicitly supports request-reply. Its transport guarantees do not establish that an unanswered wallet lookup means the wallet has no owner. [Official request-reply documentation](https://docs.nats.io/learn/core-nats/request-reply).

## What the broker proves

| Observation | Meaning |
| --- | --- |
| Valid application reply | The responder supplied that result. A current-state read can become stale after it replies. |
| Request timeout | No reply arrived before the deadline. The handler may be slow, the reply lost, or the application deliberately silent. |
| `503` / no responders | The broker sees no matching subscriber for the request. It says nothing about whether the requested wallet exists. |
| Core publish succeeds | The client accepted the write. A subsequent broker round trip confirms broker processing, not connector handling or client receipt. |
| Client reconnects | Subscriptions resume. Missed inbound Core messages are not replayed. |
| JetStream publish acknowledgement | The stream stored the message; it does not prove a consumer processed it. |

Core NATS is at-most-once and has no persistence. Request-reply uses two Core publications; neither becomes durable because the API waits for an answer. [Core delivery](https://docs.nats.io/learn/core-nats/), [request timeouts and no responders](https://docs.nats.io/learn/core-nats/request-reply).

Client reconnection restores subscriptions and may buffer outbound publications, subject to client limits. It cannot recover messages missed by an offline subscriber. [Reconnection](https://docs.nats.io/learn/resilient-clients/reconnection).

## The actual application gap

Pulse's current assignment responder sends a `PeerClusterChange` only for a matching active session. It also stays silent for malformed requests, unknown sessions, exhausted request budget and failed replies. The v1 response has no explicit negative or overloaded result. [Pulse responder](https://github.com/decentraland/Pulse/blob/13e1ce0d4d8bd3470b63e9a2ecfb8390c04656a3/src/DCLPulse/Clusters/NatsPublisher.cs#L666).

The reviewed Gatekeeper base maps a timeout to `no_reply`, then maps `no_reply` to assignment `absent`. Its stale-takeover cleanup can proceed when authority is unknown or absent, provided its local mint-history guards permit it. An existing test explicitly expects eviction when authority is unavailable. [Request adapter](https://github.com/decentraland/comms-gatekeeper/blob/cdd006cd1568c9ee146308b0f6062ee3a366547c/src/adapters/nats/component.ts#L332), [authority and cleanup](https://github.com/decentraland/comms-gatekeeper/blob/cdd006cd1568c9ee146308b0f6062ee3a366547c/src/logic/cluster-subscriber/component.ts#L258), [existing test](https://github.com/decentraland/comms-gatekeeper/blob/cdd006cd1568c9ee146308b0f6062ee3a366547c/test/unit/cluster-subscriber/component.spec.ts#L825).

A counterexample requires no speculative broker defect: an active session exists, Pulse drops its lookup because the budget is exhausted, and gatekeeper receives a timeout. The same result in that base also represents genuine absence. Therefore that result cannot safely authorize eviction. Lost or late replies create the same ambiguity.

**Fix the inference and fail closed for security side effects.** Keep assignment authority unknown after timeout, no responders, malformed replies or disconnection. Destructive cleanup requires fresh affirmative authority evidence and the existing session, ownership and post-await guards. Local mint history alone is not a current ownership answer.

Ordinary missed credential delivery already has a later recovery trigger through the periodic hints. That recovery is conditional on healthy services and LiveKit checks; it is not a strict 30-second delivery guarantee. Keep the current hints while hardening the real gaps. See [assignment recovery](cluster-assignment-recovery.md) for the current contract and its limits.

## Rules for future changes

- Use Core events when the next snapshot or reconciliation can repair a missed message. Treat hints as triggers to read current state.
- Use the existing read-only assignment request for a positive result. With no usable reply, defer the action and use a bounded retry or the next hint.
- Give new authoritative APIs explicit application outcomes: found, absent and unavailable. Transport errors always mean unavailable; a negative answer requires the ready authority to state it.
- Keep retries bounded, add backoff and limit concurrency. Retrying a read is safe; retrying token minting, revocation or other side effects needs application idempotency. [Request-reply resilience](https://docs.nats.io/learn/resilient-clients/request-reply-resilience).
- Scope broker access to the request subjects, each requester's inbox subscriptions and responder permissions. A session selector is not authentication. Missing permissions can resemble a timeout. [Authorization](https://docs.nats.io/learn/security/authorization).

## Alternatives and their limits

| Option | Suitable use | Remaining responsibility |
| --- | --- | --- |
| Existing NATS lookup plus hints | Current assignment recovery with small, repeatable reads | Fail closed on unknown authority; maintain session guards and recovery coverage. |
| Direct HTTP or gRPC | A new explicit read API to the single Pulse authority | Define readiness and negative responses. Deadlines and lost responses still mean unknown. Add service routing and access control. |
| JetStream durable consumer | Work that must survive a consumer outage and be replayed, such as a separately justified cleanup obligation | Configure storage/retention, tolerate redelivery, revalidate authority and make external side effects idempotent. |

JetStream adds storage and acknowledgement/redelivery. Those guarantees end at the broker/consumer contract; they do not make LiveKit revocation or client receipt transactional. Adopt it for a demonstrated durability requirement, not as a replacement for a current-state read. [Publish acknowledgement](https://docs.nats.io/learn/jetstream/publishing), [consumer acknowledgement](https://docs.nats.io/learn/jetstream/delivery-and-acknowledgment).

This review establishes contract limits and a code-level unsafe inference. It does not attribute a production incident to NATS or claim a broker fault test was performed.
