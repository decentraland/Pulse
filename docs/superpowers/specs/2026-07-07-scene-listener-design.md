# Scene Listener — Design

Date: 2026-07-07
Status: Implemented

## Overview

A **scene listener** is a special client that connects to Pulse to observe player
positions inside a fixed set of parcels. It authenticates with the same
Decentraland ECDSA auth chain as a player client, announces its parcel set once
at handshake time, never sends state updates, and receives the positional
subset of the observer stream for players inside its parcels only.

Primary consumer: scene/world services that need server-side awareness of who
is standing where (e.g. scene runtimes reacting to player presence).

## Requirements

1. On handshake the client announces an array of parcels which acts as its area
   of interest and never changes without re-connection.
2. The client never sends state updates to the server.
3. The client receives positional updates for players within its parcels only.

## Decisions (resolved during brainstorming)

| Question | Decision |
|---|---|
| Authorization | Same ECDSA auth chain as players; listener mode is signaled by a dedicated handshake message. No allowlist, no special ephemeral purpose. |
| Protocol shape | Separate `SceneListenerHandshakeRequest` message (new `ClientMessage` variant), not new fields on `HandshakeRequest`. |
| Message surface | Positional only: `PlayerJoined`, `PlayerLeft`, `PlayerStateDelta`, `PlayerStateFull`, `Teleported`. Emote and profile-version messages are suppressed for listener observers. |
| Sessions | One session per wallet — the existing `DUPLICATE_SESSION` eviction applies unchanged, across listener and player sessions alike. |
| Parcel cap | Config cap `SceneListener:MaxParcels` (default 256); handshake exceeding it is rejected, never clamped. |
| Resync | `RESYNC_REQUEST` remains allowed — "never sends updates" means no state mutations. Listeners use the standard client-driven gap recovery. |
| Tiering | Fixed `TIER_0` (full detail, base 50 ms cadence) for every subject in the parcel set. No distance tiers — a parcel set has no meaningful center. |

## Architecture

The listener is a **peer variant**, not a separate subsystem. It lives in the
normal per-worker `peerStates` dict, goes through the same auth pipeline, and
drives the same `PeerSimulation` observer machinery (`observerViews`,
`PeerToPeerView`, delta diffing, resync, sweeps). Two things differ:

1. **It is never a subject.** No `SnapshotBoard.SetActive`, no snapshot
   publish, no `SpatialGrid.Set`. Players can never see a listener.
2. **Its interest set is parcel-based**, computed by a new private path in
   `PeerSimulation` instead of the radius-based `SpatialHashAreaOfInterest`.

Rejected alternatives:

- *Separate listener subsystem (own board + fan-out loop)* — duplicates the
  entire per-observer view/diff/baseline/resync machinery and violates the
  repo's "prefer extending existing boards over parallel per-peer boards" rule.
- *Synthetic snapshot at the parcel centroid, reuse radius AoI* — a radius
  around a centroid is not "within its parcels only": it over-includes
  neighbors and under-covers parcel sets wider than `MaxRadius`.

## Protocol changes

Applied in the sibling `@dcl/protocol` repo via the `modify-protocol` skill;
regenerated C# lands under `src/Protocol/Generated/`.

New client→server message in `pulse_client.proto`:

```proto
message SceneListenerHandshakeRequest {
  bytes auth_chain = 1;            // same signed-fetch headers JSON as HandshakeRequest
  string realm = 2;                // required; same rules as TeleportRequest.realm
  repeated int32 parcel_indices = 3; // ParcelEncoder-packed indices; immutable for the connection
}
```

New `ClientMessage` envelope variant: `scene_listener_handshake = 8`.

`HandshakeRequest` is untouched. No new server→client messages: the server
replies with the existing `HandshakeResponse` (`Success`/`Error`), and the
listener consumes the existing positional stream. Sent on ch0 (reliable),
like `HandshakeRequest`.

## Handshake & authorization

A new `SceneListenerHandshakeHandler` is registered for the
`SceneListenerHandshake` envelope case. It composes the same injected services
as `HandshakeHandler` — `HandshakeAttemptPolicy` (attempts counted across both
handshake types), auth-chain parse + `AuthChainValidator.Validate`, `BanList`,
replay policy — so the crypto/anti-abuse path is identical. Shared logic is
extracted rather than duplicated where the two handlers overlap.

The PENDING_AUTH packet gate (which today silently drops every non-`Handshake`
message before authentication) is widened to also admit the
`SceneListenerHandshake` case.

Listener-specific validation (`FieldValidator.ValidateSceneListenerHandshake`):

- `realm` non-empty and within `MaxRealmLength` (same rules as
  `TeleportRequest.realm`).
- `parcel_indices` non-empty; every index passes `ParcelEncoder.IsValidIndex`.
- After in-place dedup, count ≤ `SceneListener:MaxParcels`.
- Any failure → `INVALID_HANDSHAKE_FIELD` disconnect with **no**
  `HandshakeResponse`, before the peer ever reaches `AUTHENTICATED` — same
  convention as malformed `PlayerInitialState` seeds.

On success:

- `PeerState.ConnectionState = AUTHENTICATED`; `IdentityBoard.Set` as today.
  Duplicate-wallet eviction is unchanged: a listener handshake with a wallet
  already connected (as player or listener) evicts the existing session.
- **No** `SnapshotBoard.SetActive`, **no** snapshot seed, **no**
  `SpatialGrid.Set`.
- A listener descriptor is stamped onto `PeerState`: the realm, a
  `HashSet<int>` of parcel indices, and the precomputed, deduped array of
  covering `SpatialGrid` cell keys. Each 16 m parcel overlaps 1–4 of the
  100-unit grid cells (parcels straddling cell boundaries in X and/or Z), so
  the covering set is the union over all parcels, computed once. The
  descriptor is immutable for the connection's lifetime; re-announcing
  requires reconnecting.

`PeerState` and its listener descriptor are owned by the peer's shard worker,
which is also the worker that runs its observer simulation — no cross-worker
access, per the worker-shard isolation rule.

## Interest management & simulation

Per tick, `PeerSimulation.SimulateTick` branches for observers carrying a
listener descriptor into a new private method (per the `PeerSimulation`
method-decoupling rule), replacing the snapshot-read + radius-AoI steps:

1. For each precomputed cell key, query `SpatialGrid` occupants. This needs a
   small cell-key-based query overload next to the existing position-based
   `GetPeers(Vector3)`.
2. For each candidate subject: read its snapshot from `SnapshotBoard`; require
   `snapshot.Realm == listener.Realm` (ordinal); require the subject's parcel
   index to be **in the listener's parcel set** — parcel-exact, not
   cell-approximate. `PeerSnapshot` already stores the packed parcel index
   (`Parcel`), so this is a direct `HashSet<int>.Contains(snapshot.Parcel)`.
3. Accepted subjects are collected at fixed `TIER_0` into the same
   `InterestCollector`.
4. The collected set feeds the unchanged `ProcessVisibleSubjects` pipeline:
   `PlayerJoined` (with full state) on entry, per-tick `PlayerStateDelta`
   diffs against `observerViews`, `Teleported` relay, resync handling, and
   `PlayerLeft` via the existing sweep when a subject leaves the parcels or
   disconnects.

One addition to the shared pipeline: a per-observer **positional-only** flag
(true for listeners) gates off `EmoteStarted`, `EmoteStopped`, and
`PlayerProfileVersionAnnounced` emission. During an emote the subject sends no
`MovementInput`, so the listener sees it stationary; the post-emote-stop delta
is never suppressed, so position resynchronizes on resume. `PlayerJoined` for
a mid-emote subject skips the usual companion `EmoteStarted` for listener
observers.

The existing aliasing detection and stale-view sweep apply unchanged, since
listeners use the same view machinery.

## Inbound message policy

Enforced at a single choke point in the worker's message dispatch, not
per-handler: for an `AUTHENTICATED` listener peer, only `Resync` is processed.
`Input`, `EmoteStart`, `EmoteStop`, `Teleport`, `ProfileAnnouncement`, and any
further handshake are **silently dropped** and counted in a metric — mirroring
the pre-auth "non-handshake silently dropped" convention. No disconnect on
violation: a buggy listener degrades to noise, not connection churn.

There is no message that can mutate the parcel set; immutability is by
construction.

## Config, metrics, liveness

- New options section `SceneListener` (`SceneListenerOptions`, bound like the
  other sections; env-overridable, e.g. `SceneListener__MaxParcels`):
  - `MaxParcels` — default 256.
- Metrics (existing analytics layer, via the `add-metric` pattern):
  - connected scene-listener count,
  - subjects-in-view aggregate for listener observers,
  - forbidden-inbound-dropped counter.
- Liveness: no new code. ENet protocol-level keepalive plus the listener's
  ACKs of reliable sends keep the 30 s ENet timeout fed; there is no
  application-level idle disconnect for authenticated peers. The PENDING_AUTH
  30 s deadline applies to the listener's handshake as usual.

## Error handling & edge cases

- Empty parcel list, invalid index, over-cap, bad realm → handshake reject +
  `INVALID_HANDSHAKE_FIELD` disconnect.
- Listener disconnect → normal `DISCONNECTING` cleanup. Its `observerViews`
  die with the worker's per-peer state; nothing to remove from
  `SnapshotBoard`/`SpatialGrid` because it was never registered there.
- Subject on a recycled `PeerIndex` slot → existing aliasing detection.
- Subject teleporting out of the parcel set mid-batch → last-event-wins
  intermediate scanning already applies; the sweep emits `PlayerLeft`.
- Subject in a covering grid cell but outside the announced parcels → filtered
  by the parcel-exact membership check; never visible.

## Testing

- **Handshake** (NSubstitute, per repo convention): listener accept path; each
  reject path (over-cap, invalid index, empty list, bad realm); duplicate-
  wallet eviction still fires; no `SetActive`/no grid registration on accept;
  PENDING_AUTH gate admits the new message case.
- **Simulation**: parcel-exact inclusion/exclusion (in-cell-but-out-of-parcel
  subject invisible); realm gate; fixed TIER_0; `PlayerJoined`/`PlayerLeft` on
  parcel entry/exit; emote/profile suppression (including mid-emote join);
  resync served; delta stream for a listener matches a player-observer's
  stream for the same subject.
- **Inbound policy**: each forbidden message type dropped + counted; `Resync`
  honored.
- **End-to-end** (`DCLPulseTestClient`): a listener-mode bot (new flag) plus a
  moving player bot; assert the listener receives joins/deltas only while the
  player is inside the announced parcels, and `PlayerLeft` when it walks out.

## Out of scope

- Changing the parcel set without reconnecting.
- Listener-specific bandwidth tiers or cadence config (fixed TIER_0 for now).
- Wallet allowlists or listener-specific auth purposes.
- Any server→client protocol additions.
