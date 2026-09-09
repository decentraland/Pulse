# Island Room — Duplicate-Session Handover — Design

Date: 2026-09-09
Status: Implemented — **Rule 2 must not be enabled yet, see the warning below**

> [!WARNING]
> **This document's safety argument is wrong, and the code implementing it is merged.**
> The whole-branch review found that Rule 2 re-announces a *single* reconnecting client
> into the island room it is already in — the ordinary outcome of any Pulse-only
> reconnect inside the retention window, since the explorer reconnects Pulse in place
> and never touches the island room. `ConnectiveRoom.ChangeRoomsAsync` connects the new
> room before releasing the old one, both still subscribed, so LiveKit evicts the
> client's **own** previous participant. With `alfa-stop-on-duplicate-identity` on —
> the precondition this document calls mandatory — that raises the quit-only modal and
> a network hiccup terminates the user's only client. With the flag off, the 1 s
> mutual-eviction loop described under "Deployment precondition" applies instead.
> Neither flag state is safe while Rule 2 is on.
>
> Rule 1 is unaffected and is a strict improvement: it only stops the *outgoing* peer
> from publishing.
>
> The premise below that "no explorer change is needed" is the specific thing that is
> wrong. Superseding via LiveKit's identity collision cannot be made safe until the
> explorer distinguishes a self-inflicted `DUPLICATE_IDENTITY` raised during a room
> swap from a genuine second session. Until then ship with `Clusters:HandoverPasses`
> set to `0`.

## Overview

When one wallet holds two overlapping sessions, the surviving session must end up
alone in its LiveKit island room and the outgoing session must be superseded.
Today neither happens reliably: the two sessions can be assigned **different**
island rooms, so LiveKit never sees an identity collision, and the outgoing
session's LiveKit participant is never evicted by anything.

This design makes the supersede deterministic by having Pulse point the incoming
session at the room the outgoing one still holds. LiveKit's native
`DUPLICATE_IDENTITY` rule then evicts the outgoing participant, and the explorer's
existing duplicate-identity handling terminates that client. On the next pass Pulse
moves the surviving session to its own cluster.

Scope is the island room only. No changes to comms-gatekeeper, scene rooms, or
explorer code.

## Background — why it is broken today

Pulse's clustering is keyed by `PeerIndex`. Everything downstream of it — the NATS
subject `peer.{addr}.cluster_change`, gatekeeper's `peerState`, the LiveKit
participant identity, `engine.peer.{addr}.island_changed`, ws-connector's registry —
is keyed by **wallet**. Pulse deliberately allows two live `PeerIndex`es per wallet
while a duplicate-session eviction completes, and that window is now load-bearing
for room assignment.

`HandshakeHandlerBase.EvictDuplicateSession` disconnects the incumbent
asynchronously and cannot flip its `ConnectionState` — the incumbent is owned by
another worker shard. It stays `AUTHENTICATED`, in its `SpatialGrid` and
`SnapshotBoard`, until the ENet disconnect event lands: one RTT if the client acks,
up to `Transport:PeerTimeoutMs` (5000 ms in production) if it does not.

Two distinct defects follow.

**Defect A — the surviving session is wedged in the departed session's room.**
`ClusterTracker.TryCollectMember` de-duplicates by `PeerIndex`, not by wallet, so
both peers are collected and both publish on the same subject.
`NatsPublisher.QueueChange` coalesces per subject with latest-wins, and publish
order follows component index, which is grid-cell enumeration order — arbitrary
with respect to which session is live. `TryPublishAssignment` records
`state.PublishedClusterId` **before** calling the publisher, so when the outgoing
peer's message wins, Pulse believes it announced the surviving peer's cluster and
never re-announces it. The surviving session stays in the wrong room until its own
cluster genuinely changes, which for a stable crowd may be never.

**Defect B — the outgoing session's LiveKit participant is orphaned.** Nothing
evicts it. `removeParticipantFromAllRooms` is wired only to bans
(`comms-gatekeeper/src/logic/user-moderation/component.ts:96`). The island room's
lifecycle lives in the explorer's `RoomHub`, independent of the Pulse transport. In
the two-window case the outgoing client loses its Pulse peer (`DUPLICATE_SESSION`,
terminal per `PulseHandshakeDisconnectedException.IsRetriableReason`) and its
ws-connector socket (`KR_NEW_SESSION` kick), but `ArchipelagoIslandRoom` sees
`roomIsDisconnected == false` and does nothing, so it sits in its old room
indefinitely.

Neither defect is visible on any dashboard. Pulse's `dcl_pulse_nats_superseded_total`
is the only counter that moves for the same-pass variant of Defect A, and its help
text reads "Expected under load and harmless".

## Requirements

1. A wallet's cluster assignment is published for exactly one peer at a time.
2. When a second session supersedes a first, the second is pointed at the first's
   island room so LiveKit raises `DUPLICATE_IDENTITY` against the first.
3. The surviving session converges on its own correct cluster promptly afterwards.
4. A superseded session does not reconnect.
5. No change to comms-gatekeeper, scene-room logic, or the wire protocol.

## Decisions (resolved during brainstorming)

| Question | Decision |
|---|---|
| What triggers the supersede | LiveKit's native identity collision, not a client-side teardown or a gatekeeper-side force-remove. Requires both sessions to converge on one room. |
| Which room they converge on | The **incumbent's**. The incoming session walks into it, then migrates. Converging on the incoming session's room is impossible — the outgoing client's ws-connector socket is already kicked, so it can never be told to move. |
| Where the convergence is decided | In `ClusterTracker`, keyed by wallet. Not at handshake time: the Pulse handshake and the explorer's ws-connector session are independent connections, and ws-connector silently drops `island_changed` for an address it holds no socket for. |
| Guard the handover on cluster liveness | **No.** If the outgoing peer was alone in its cluster, the cluster leaves Pulse the moment it leaves the grid, but its LiveKit participant is still in the room — LiveKit rooms outlive Pulse clusters. Guarding on `IsClusterLive` would skip the handover in precisely the case that needs it. |
| What happens if the first session reconnects | Nothing new. The explorer already makes a superseded session terminal — see below. |
| Identity suffixing | Rejected. Appending a UUID (as the Cast paths do) would prevent the collision this design depends on. The bare wallet identity is load-bearing. |

## Requirement 4 — the superseded session does not reconnect

Already implemented in the explorer; **no change needed**. Verified:

- `ConnectiveRoom.OnConnectionUpdated` catches `LKDisconnectReason.DuplicateIdentity`,
  sets `isDuplicateIdentityDetected`, cancels the CTS and breaks the connection loop.
- `DuplicateIdentityPlugin` opens `DuplicateIdentityWindowController`, which calls
  `DCLInput.Instance.Disable()` on show, overrides `WaitForCloseIntentAsync` to
  `UniTask.Never` so the modal cannot be dismissed, and offers a single button
  wired to `ExitUtils.Exit()`.

The superseded client is therefore terminal: input disabled, un-dismissable modal,
quit-only. `RestartRoomAsyncTeleportOperation` would reset the flag via
`roomHub.StartAsync()`, but with input disabled the user cannot reach a teleport.

**Deployment precondition.** Both halves are gated on the
`alfa-stop-on-duplicate-identity` feature flag — the loop stop in `ConnectiveRoom`
and the plugin registration in `DynamicWorldContainer`. The flag **must be enabled**
wherever Pulse clustering is enabled. With it off, converging the rooms makes things
strictly worse: the evicted client retries its cached token (valid 5 minutes) on the
next `HEARTBEATS_INTERVAL` tick — 1 s, since the 5 s `RECONNECT_BACKOFF` applies only
after a *failed* attempt — and the two sessions evict each other indefinitely.

## Architecture

Two composing rules, both inside `ClusterTracker`. Rule 1 fixes Defect A, Rule 2
fixes Defect B. Neither works alone: without Rule 1 the outgoing peer can re-publish
after the handover and re-wedge the survivor; without Rule 2 there is no collision.

### Rule 1 — one wallet, one publishing peer

In `TryCollectMember`, after the existing wallet lookup, skip any peer that is not
the wallet's current `IdentityBoard` binding:

```csharp
string? wallet = identityBoard.GetWalletIdByPeerIndex(peer);

if (wallet is null) return;

// One wallet, one peer. After a duplicate-session eviction IdentityBoard is already
// rebound to the replacement, while the outgoing peer lingers in the grid until its
// transport disconnect lands. Collecting it would address the wallet's subject with
// the outgoing session's cluster.
if (!identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex live) || live != peer) return;
```

`identityBoard.Set` rebinds the wallet to the replacement **before** the replacement
is seeded into the grid, so the outgoing peer is excluded from the first pass after
the eviction rather than up to five seconds later.

This is a no-op outside a duplicate-session overlap: for any other peer the reverse
lookup returns the peer itself. Cost is one `ConcurrentDictionary` lookup per
occupant per pass, on the 1 Hz tracker thread — not the per-tick or per-packet path.

Skipping at collection rather than at publish also removes the outgoing peer from
`ClusterBoard` and from the `engine.islands` topology, which incidentally fixes a
double-count: `NatsPublisher.FillIslandStatus` adds `peer.Wallet` per
`ClusterPeerInfo` with no de-duplication, so a duplicated wallet currently appears in
two islands' `Peers` lists in one snapshot and is counted twice by
archipelago-stats' `/islands`.

### Rule 2 — session handover

The tracker gains a private wallet-keyed ledger, written on the tracker thread only:

```csharp
private readonly Dictionary<string, WalletAssignment> assignmentByWallet = new (StringComparer.OrdinalIgnoreCase);

private struct WalletAssignment
{
    public string ClusterId;
    public string Realm;
    public long LastSeenPass;
}
```

`PeerClusterState` gains one field:

```csharp
// Set on the pass a handover was published, cleared when the peer's own assignment
// follows. Exempts that migration from the dwell debounce.
public bool HandoverPending;
```

`TryPublishAssignment` becomes:

```
ref state = peerStates[member.Peer.Value]

// Session handover: this slot has never published, but the wallet still carries the
// outgoing session's assignment. Publish that instead, so the incoming session joins
// the room the outgoing one holds and LiveKit's identity collision supersedes it.
handingOver = state.PublishedClusterId is null
           && assignmentByWallet.TryGetValue(member.Wallet, out prev)
           && (prev.ClusterId != clusterId || prev.Realm != realm)

if handingOver:
    clusterId = prev.ClusterId
    realm     = prev.Realm

realmChanged = state.PublishedRealm != realm

if !realmChanged && state.PublishedClusterId == clusterId:
    clear candidate; return false

immediate = state.PublishedClusterId is null
         || state.HandoverPending          // ← the migration off a handover
         || member.IsTeleport
         || realmChanged
         || !IsClusterLive(state.PublishedClusterId)

if !immediate && !HasDwelled(ref state, clusterId): return false

state.PublishedClusterId = clusterId
state.PublishedRealm     = realm
state.HandoverPending    = handingOver
clear candidate

Remember(member.Wallet, clusterId, realm)   // upsert, stamps LastSeenPass = passNumber
feedPublisher.PublishClusterChange(member.Wallet, clusterId, realm)

if handingOver: PulseMetrics.Clusters.HANDOVERS.Add(1)
return true
```

The outgoing peer does not publish during the handover pass: Rule 1 has already
excluded it, and even if it had not, its own `PublishedClusterId` already equals its
cluster so `TryPublishAssignment` would return false.

### Ledger lifetime

An entry is **created** only by `Remember`, on a successful publish — a wallet that
has never been assigned a cluster has nothing to hand over. An existing entry's
`LastSeenPass` is **refreshed** in `TryCollectMember` whenever the wallet is seen,
whether or not it publishes. Refreshing on publish alone would expire the entry for a
player standing still, which is exactly the player a duplicate session is most likely
to arrive for.

Entries not seen for `Clusters:HandoverPasses` passes are dropped by a sweep that
runs alongside `ForgetVanishedPeers` at the end of each pass. Map size is therefore
bounded by concurrent wallets plus recently departed ones.

Default `HandoverPasses` is 15. With Rule 1 in place the wallet is seen continuously
across a duplicate-session eviction — the replacement is seeded into the grid at
handshake, so there is no gap — and the window only matters for a reconnect arriving
after the previous peer has fully left. Fifteen passes comfortably spans the 5 s
`PeerTimeoutMs` against a 1 s pass. A reconnect later than that gets no handover,
which is correct: there is nothing live left to supersede.

`HandoverPasses = 0` disables Rule 2 and is the rollback.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Clusters:HandoverPasses` | `15` | Passes a departed wallet's assignment is retained for handover. `0` disables the handover. |

## Metrics

One new counter, wired through the standard path (instrument → collector → snapshot
→ Prometheus → console dashboard) using the `add-metric` skill:

| Instrument | Prometheus | Meaning |
|---|---|---|
| `pulse.clusters.handovers` | `dcl_pulse_cluster_handovers_total` | Incoming sessions pointed at an outgoing session's cluster. Each one is a duplicate session being superseded. |

This is the first counter anywhere in the chain that makes a duplicate session
observable; today it is invisible in Pulse, gatekeeper and ws-connector alike.

## Sequence

```
t0    B authenticates. EvictDuplicateSession disconnects A (DUPLICATE_SESSION).
      identityBoard rebinds wallet W → B. B seeded into grid.
t0+   Pass N: Rule 1 skips A. B collected, computes C_b.
      Rule 2 substitutes the remembered C_a. Publishes (W, C_a).
      HANDOVERS +1. HandoverPending = true.
      Gatekeeper mints a token for island-C_a, publishes island_changed.
      ws-connector forwards it to B's socket.
      B's client: Pending string → ShouldAttemptConnection returns true
      unconditionally → joins island-C_a.
      LiveKit: second participant with identity W → evicts A with DUPLICATE_IDENTITY.
      A's client: loop stops, modal opens, input disabled, exit-only.
t0+1s Pass N+1: B computes C_b ≠ C_a. HandoverPending makes it immediate.
      Publishes (W, C_b). B migrates to island-C_b.
```

When the remembered and computed clusters coincide the substitution is a no-op and
the sequence collapses to a single publish. That happens when the replacement lands
in a cluster that still carries the same sticky ID — rejoining a crowd that survived,
whose other members carry the `PreviousPassClusterId` the ID is inherited through.

A player who was **alone** does get a hop, even reconnecting on the same spot: their
cluster keeps no members across the gap, so inheritance has nothing to measure
overlap against and the replacement is minted a fresh ID. The handover then steers it
into an ID this pass no longer knows — which is precisely the intent, since that is
where the outgoing LiveKit participant still is.

## Testing

`ClusterTrackerTests`, NSubstitute, following the existing `SetupPeer` /
`PublishSnapshot` helpers.

Rule 1:
- Outgoing peer still in the grid after the wallet is rebound → not collected, not
  published, absent from `ClusterBoard` and from the topology snapshot.
- Wallet appears exactly once in `IslandStatusMessage` while two peers share it.
- Single-session peers are unaffected (regression guard for the no-op case).

Rule 2:
- New `PeerIndex` for a wallet with a remembered assignment → first publish carries
  the **remembered** cluster, not the computed one; `HANDOVERS` incremented.
- The following pass publishes the peer's own cluster, dwell bypassed.
- Remembered == computed → one publish, no handover, counter unmoved.
- Handover still fires when the remembered cluster no longer exists this pass
  (the `IsClusterLive` non-guard).
- Realm differs between the two sessions → handover still publishes the remembered
  realm, migration bypasses dwell via `realmChanged` and `HandoverPending`.
- **Recycled slot, different wallet** → no handover, because the ledger is keyed by
  wallet. Guards the failure `ForgetVanishedPeers` already warns about.
- Ledger entry expires after `HandoverPasses` passes of absence; a later reconnect
  gets no handover.
- Ledger entry survives `HandoverPasses` passes of a wallet publishing nothing while
  still present (the standing-still case).
- `HandoverPasses = 0` disables the handover entirely.

## Documentation

- `docs/clustering-on-aoi.md` — add the handover to the assignment-publishing
  section; correct the claim that duplicate-session eviction "needs no migration".
- `docs/metrics.md` — the new counter.

## Known gaps, accepted

- If the outgoing peer's Pulse connection dies but its LiveKit connection survives
  past `HandoverPasses` — a partition killing UDP but not TCP — the replacement gets
  no handover and the outgoing participant stays orphaned.
- Cross-replica ordering in comms-gatekeeper is unchanged and still unguarded; its
  per-wallet serialization is process-local. Out of scope here, already documented in
  that repo.
- Cluster IDs come from a single global `nextClusterNumber`, so they are unique
  within one Pulse instance only. A second publishing instance on the same NATS would
  collide from `C1` and gatekeeper, which ignores `realm`, could not tell them apart.
