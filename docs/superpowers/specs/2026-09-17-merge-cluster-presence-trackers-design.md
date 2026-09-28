# Merge ParcelChangeTracker into ClusterTracker

Date: 2026-09-17
Status: approved, not yet implemented

## Goal

Less code, fewer layers. `ParcelChangeTracker` and `ClusterTracker` keep parallel copies of the same
per-peer facts and walk the same peer set repeatedly. Fold the first into the second.

## Redundancies this removes

| # | Today | After |
|---|---|---|
| 1 | Two per-peer arrays sized `maxPeers`: `PeerClusterState[]`, `Slot[]` | One `PeerState[]` |
| 2 | Two "last seen pass" stamps: `LastSeenPass`, `SeenAtPass` | One `LastSeenPass` |
| 3 | Two sweeps plus `IsPlaced`'s linear scan per disconnect | One sweep, no scan |
| 4 | Six walks of the member set per pass | Four |
| 5 | `clusterIdByPeer` — a `string?[maxPeers]` allocated every pass | Deleted |
| 6 | `RememberComputedAssignments` — a walk that writes one field | Folded into `BuildCluster` |

## Design

### One per-peer struct

`PeerClusterState` and `Slot` merge into `PeerState`, indexed by `PeerIndex`, sized `maxPeers`.

Fields group by **owning thread**, and that grouping is the whole correctness argument:

```
private struct PeerState
{
    // --- presence: written by the pass's publish walk AND by OnPeerRemoved. Both under stateLock.
    public string? Wallet;
    public string? Address;      // lowercase form, recomputed only when Wallet changes
    public string? Realm;        // realm as of the last presence publish; doubles as the occupancy flag
    public ParcelCoord Parcel;
    public ulong OccupiedAt;

    // --- clustering: written only by the pass. No lock.
    public string? PreviousPassClusterId;
    public string? PublishedClusterId;
    public string? PublishedRealm;   // realm as of the last assignment publish
    public string? CandidateClusterId;
    public int CandidateStreak;

    // --- written by the pass only, read by the sweep.
    public long LastSeenPass;
}
```

`Realm` and `PublishedRealm` stay separate on purpose. The dwell debounce can hold an assignment
back while a parcel change goes out, so the two genuinely diverge. They now share one array, one
cache line and one lifecycle, which is the win; collapsing them would be a behaviour change.

`SeenAtPass` disappears: after the merge there is one collection point (`TryCollectMember`), so
`LastSeenPass` serves both the sweep and `Supersedes`.

### Locking

Unchanged in scope from today. `stateLock` covers the presence fields only:

- the pass takes it around the publish walk and around snapshot collection — exactly the span
  `ParcelChangeTracker.ObservePass` holds it for today;
- `OnPeerRemoved` takes it on the peer-worker thread;
- collect / union / group / sticky-ID run outside it, as now.

**The sweep must not use `state = default(PeerState)`.** That writes the presence fields from the
unlocked thread and races `OnPeerRemoved`. `ForgetVanishedPeers` clears the clustering fields
individually and leaves the presence fields to `OnPeerRemoved`, which is already the only thing
allowed to retire a presence entry (A1, C1.2).

### Walks per pass: 6 to 4

Today: `CollectRealmNodes`, `Centroid`, `BuildCluster`, `RememberComputedAssignments`,
`ObservePass`, `PublishAssignmentChanges`.

After:

1. **Collect** — `CollectAndUnionRealms` / `TryCollectMember`, unchanged.
2. **Centroid** — unchanged. Radius needs the centroid first, so this walk cannot fold into the next.
3. **Build** — `BuildCluster` additionally writes `PreviousPassClusterId`, which deletes
   `RememberComputedAssignments`.
4. **Publish** — one walk replacing `ObservePass`'s loop and `PublishAssignmentChanges`'s loop. It
   runs after `feedPublisher.PublishTopology`, preserving "topology before the per-peer events", and
   emits the parcel change and the assignment for each member in turn.

Snapshot collection stays a separate walk of the slot array, because a snapshot lists every live
peer rather than this pass's members.

### Deletions

- `src/DCLPulse/Presence/ParcelChangeTracker.cs`
- its DI registration in `Program.cs` and its `ClusterTracker` constructor parameter
- `RememberComputedAssignments`, `IsPlaced`, the `Slot` struct, the second sweep
- `clusterIdByPeer`: allocated every pass to back `ClusterBoard.ClusterIdOf`, which has no
  production caller — only tests. The accessor reads `PeerState.PreviousPassClusterId` instead.

`PeerSimulation.CleanupDisconnectedPeer` calls `clusterTracker.OnPeerRemoved`. `ClusterTracker` is
already a DI singleton, so this is a constructor-parameter swap.

### Not touched

The union-find core — `Find`, `Union`, `AttachToComponent`, `AssignStickyIds` and the sticky-ID
inheritance. Reviewed for redundancy; none found that is worth the regression risk against
`ClusterTrackerBenchmarks`. `NodeKey` and `ClusterSession` are single-use types worth ~15 lines;
`NodeKey` carries the documented hash-collision fix, so both stay.

## Behaviour changes

1. **`IsPlaced` becomes an `IdentityBoard` lookup.** Today: "no live presence slot holds this
   wallet". After: "no live peer is bound to this wallet" — the same call `TryCollectMember` already
   makes. These differ for an authenticated-but-not-yet-placed reconnect, where the exit is now
   suppressed slightly earlier. This is the A1 rule stated directly, but it is a change and
   `PresenceGuaranteeTests` pins the current wording.
2. **`ClusterBoard.ClusterIdOf` reads tracker state**, not an immutable pass snapshot. Only tests
   call it; they call it after a pass, so the value is the same.

Nothing else changes on the wire: no proto, no fixture byte, no `http/*.json` golden.

## Testing

The existing suite is the spec. `PresenceGuaranteeTests`, `PresenceWireFixtureTests` and
`ClusterTrackerTests` all keep passing unchanged, except:

- `PresenceScenario` drops the separate `ParcelChanges` field and drives `Tracker` directly;
- the one test pinning `IsPlaced`'s slot-based meaning is rewritten for the `IdentityBoard` meaning;
- `ClusterTrackerTests.ClusterIdOf` keeps working through the new accessor.

The byte fixtures are the strongest guard: `PresenceWireFixtureTests` compares the real publisher's
output against `parcel_changes/NN-*.bin`, so any change in what the merged walk emits, or in what
order, fails on bytes.

`ClusterTrackerBenchmarks` must still run, and is the check that the merged walk did not regress the
pass. Record the before/after in the class docs.

## Success criteria

- `ParcelChangeTracker.cs` is gone and no file replaces it.
- Net line count across `Clusters/` + `Presence/` falls by at least 250.
- `dotnet build` clean; full suite green; fixture bytes unchanged.
- One array, one sweep, one lock, four walks.
