# Pulse architecture

One Pulse instance serves the entire current userbase, with realm-scoped grids and clusters inside that process. Clients also connect to LiveKit for voice and other comms; Pulse neither simulates scene entities nor mints LiveKit credentials. ENet uses UDP behind an AWS NLB; WebTransport shares the peer allocator and simulation. Cluster membership does not imply mutual visibility.

## Transport and authentication

Channel 0 carries reliable control/events; channel 1 carries unreliable sequenced movement/deltas. ENet packet flags enforce these semantics. Channel 2 is declared unsequenced and currently unused.

Authentication validates the Decentraland ECDSA chain locally: signer/delegation, ephemeral expiry, connection signature, timestamp window and server ID. The wallet identifies the user; the final signing address identifies the session (wallet itself without delegation). A duplicate wallet evicts its incumbent connection.

`PENDING_AUTH` becomes `AUTHENTICATED` after validation. Server-requested disconnects pass through `PENDING_DISCONNECT` until the transport event establishes `DISCONNECTING`; inbound packets are skipped meanwhile. Auth failures flush the rejection before disconnect, while auth timeout disconnects immediately. Limits and rejection reasons live in [hardening](hardening.md).

Optional `HandshakeRequest.PlayerInitialState` is validated before authentication completes. It must supply a valid realm; omitting the seed leaves the peer invisible until its first teleport sets one. A resumed emote backdates `StartTick` by its elapsed offset, clamped against underflow.

## Shared state and lifetime

- `IdentityBoard` atomically publishes one immutable wallet/session registration per slot and keeps the wallet's current binding. Registration changes on every reconnect, even with identical wallet/session. Value-checked cleanup preserves a replacement's binding.
- `SnapshotBoard` is a single-writer, seqlock-protected ring per subject. Nullable realm/emote ledger fields inherit prior state; stop markers last only their event snapshot. `PeerSnapshotPublisher` coordinates handler publications with spatial-index updates, removing the old realm placement before a realm-changing publish.
- `RealmSpatialGrids` holds one grid per occupied realm. Copy-on-write cell sets supply candidates; [interest snapshot consistency](interest-snapshot-consistency.md) defines eligibility, deduplication and target resolution.
- `ProfileBoard` holds profile versions. Slot cleanup clears the boards before allocator release.

`PeerIndex` is a recycled server slot with a transport routing tag, not a persistent player identity. Allocation -> pending recycle -> cleanup -> release gives observers a grace window. Visibility teardown clears active state and grid membership on the owning worker's disconnect event; identity/profile cleanup and slot release follow later.

Stale views expire by simulation ticks, while recycle grace and transport timeouts use wall time. The nominal sweep bound is `(VIEW_STALE_TICKS + SWEEP_CHECK_INTERVAL) * BaseTickMs` (about four seconds at the inspected defaults); sustained tick overruns can exceed recycle grace. Registration checks protect aliasing. Same-wallet reconnects can briefly leave old and new views until stale retirement. Silent disconnects also wait for transport timeout. A restart drops live connections; there is no graceful drain. Dense AoI fan-out remains O(N²), with no shedding.

## Synchronization

Simulation resolves the interest-approved target, scans history only through that target, collapses each discrete event type to its latest occurrence, then sends events and state. An emote started and stopped within a batch is invisible to the observer. Teleport and emote start suppress that tick's unreliable delta; emote stop permits it. `EmoteCompleter` publishes one-shot completion on the subject's worker.

Deltas compare against the observer's last sent snapshot, without unreliable ACK tracking. Clients request resync on a sequence gap. Full state is the default response; optional targeted deltas require a retained baseline earlier than the resolved target. Equal, future or evicted baselines receive full state. Self mirror bypasses spatial collection but still requires a realm.

`PlayerJoined` announces identity, profile, full state and realm. A different accepted realm
retires/rejoins the subject; an observer changing realm retires and reseeds all views. An A → B → A
round trip before view expiry may retain the A view and deliver a teleport, or a delta if the
markers were overwritten. Subjects outside interest follow stale-view grace, except for the
immediate rejection path defined in the consistency contract.

Reliable emote events carry full state. `server_tick` is monotonic milliseconds, shared by movement and events; ENet RTT/2 estimates one-way latency. Delta state flags are always present because boolean transitions drive animation.

## Scene listeners

A listener authenticates with one entry per announced realm and inclusive parcel rectangles. It receives positional/emote events, omits profile announcements, and never registers as a visible subject or cluster member. Only resync and listener-update messages remain accepted.

`SceneListener:MaxParcels` is one cumulative budget: per-realm charges plus nominal rectangle areas. Whitelisted source IPs bypass this budget; realm uniqueness/length and rectangle validation still apply. A valid update replaces the complete set on the next tick and rechecks the live whitelist. Updates use the discrete-event bucket; malformed updates disconnect without partial application. Newly eligible subjects join; removed subjects follow stale-view grace.

## Protocol and operations

Message envelopes and fields are authoritative in the sibling protocol repository's `decentraland/pulse/*.proto`. Standard protobuf `optional` fields encode delta presence. Quantized values are uint32 varints; `protoc-gen-bitwise` adds float accessors and step constants, including power-law velocity encoding.

For cluster/feed behavior read [clustering](clustering-on-aoi.md) and [assignment recovery](cluster-assignment-recovery.md). Use [metrics](metrics.md) for instruments/export, [feature flags](feature-flags.md) for runtime IP configuration, and [README](../README.md) for builds, bots and deployments. Code rules live in [CLAUDE.md](../CLAUDE.md).
