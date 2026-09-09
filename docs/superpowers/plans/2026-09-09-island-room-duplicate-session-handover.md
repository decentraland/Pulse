# Island Room Duplicate-Session Handover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a duplicate session's supersede deterministic — exactly one peer publishes per wallet, and an incoming session is first announced into the outgoing session's cluster so LiveKit's `DUPLICATE_IDENTITY` evicts the outgoing participant.

**Architecture:** Two composing rules, both confined to `ClusterTracker`. Rule 1 skips any peer that is not its wallet's current `IdentityBoard` binding, so an evicted-but-still-connected peer stops feeding the wallet's NATS subject. Rule 2 adds a wallet-keyed ledger of last-published assignments that survives the `PeerIndex` change, and publishes the retained assignment on a replacement peer's first pass before letting it migrate to its own cluster on the next.

**Tech Stack:** .NET 10, NUnit, NSubstitute. `dotnet` resolves to `C:\Program Files\dotnet\dotnet` (10.0.303) — use it directly, **not** the `~/.dotnet` override in CLAUDE.md, which is a stale 8.0.422 on this machine.

Spec: [docs/superpowers/specs/2026-09-09-island-room-duplicate-session-handover-design.md](../specs/2026-09-09-island-room-duplicate-session-handover-design.md)

## Global Constraints

- Branch: `feat/users-clustering` (PR #34's head). Do not push; commit locally only.
- Build: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
- Test: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
- Never regenerate protos. There is no wire-protocol change in this plan.
- Code convention (`DCLPulse.sln.DotSettings`): instance fields camelCase no prefix; constants UPPER_SNAKE_CASE; file-scoped namespaces; `var` when the type is clear.
- Member ordering within a type: fields → properties → methods → nested types. A private helper goes **immediately after** the method that calls it.
- Comments: XML `/// <summary>` on non-obvious members; sentence case; end with a period; no block comments; **a comment describes only what the annotated code does, never what a caller will do with it**.
- No LINQ, no allocation, no boxing on the hot path. `ClusterTracker` runs on its own 1 Hz `BackgroundService` thread and is **not** the hot path — ordinary dictionary use is fine there. LINQ in tests is fine and already used.
- Nullable reference types are enabled everywhere. Never use `!` to silence a warning outside an NSubstitute proxy in tests.
- Do not touch comms-gatekeeper, scene-room logic, or the explorer.

## Release gate — not a code change, but this work is unsafe without it

The explorer's `alfa-stop-on-duplicate-identity` flag **must be enabled** in any environment where `Clusters:Enabled` and `Nats:Url` are both set. It gates both halves of the receiving behaviour: the reconnect-loop stop in `ConnectiveRoom.OnConnectionUpdated` and the terminal popup registered by `DynamicWorldContainer`.

With the flag off, this change is worse than shipping nothing. The superseded client is evicted but not stopped, so it retries its cached LiveKit token — valid for five minutes — on its next `HEARTBEATS_INTERVAL` tick (1 s; the 5 s `RECONNECT_BACKOFF` applies only after a *failed* attempt), and the two sessions evict each other about once a second indefinitely.

`Clusters:HandoverPasses = 0` disables the handover and is the rollback if the flag cannot be enabled. Requirement 4 of the spec — "a superseded session does not reconnect" — is satisfied entirely by that flag plus existing explorer code, which is why no task here touches the client.

---

### Task 1: Rule 1 — one wallet, one publishing peer

Fixes the wedge: today both peers of a duplicated wallet are collected (dedup is by `PeerIndex`, not wallet), both publish on `peer.{addr}.cluster_change`, and `NatsPublisher.QueueChange` coalesces latest-wins in grid-cell enumeration order. Also fixes a topology double-count, since `NatsPublisher.FillIslandStatus` adds `peer.Wallet` per `ClusterPeerInfo` with no dedup.

**Files:**
- Modify: `src/DCLPulse/Clusters/ClusterTracker.cs` (`TryCollectMember`)
- Test: `src/DCLPulseTests/ClusterTrackerTests.cs`

**Interfaces:**
- Consumes: `IdentityBoard.TryGetPeerIndexByWallet(string, out PeerIndex)` — already injected as the `identityBoard` field.
- Produces: nothing new. Behavioural change only.

- [ ] **Step 1: Add the shared wallet constant to the test fixture**

In `src/DCLPulseTests/ClusterTrackerTests.cs`, next to the existing `REALM` / `OTHER_REALM` constants:

```csharp
    private const string WALLET = "0xduplicate";
```

- [ ] **Step 2: Write the failing tests**

Append to `ClusterTrackerTests` (before the private helpers region):

```csharp
    [Test]
    public void OutgoingDuplicateSession_IsNotClustered()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        // Far enough apart to form two clusters if both were collected. SetupPeer's identityBoard.Set
        // rebinds the wallet to `incoming`, exactly as the duplicate-session handshake does before the
        // outgoing peer's transport disconnect lands.
        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        Assert.That(clusterBoard.Current.GetClusterId(outgoing), Is.Null);
        Assert.That(clusterBoard.Current.GetClusterId(incoming), Is.Not.Null);
        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
    }

    [Test]
    public void OutgoingDuplicateSession_DoesNotPublishOnTheWalletsSubject()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(new PeerIndex(1), new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, Arg.Any<string>(), REALM);
    }

    [Test]
    public void DuplicatedWallet_AppearsOnceInThePassRoster()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(new PeerIndex(1), new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        // FillIslandStatus adds one roster entry per ClusterPeerInfo with no de-duplication, so a
        // second entry here would list the wallet in two islands of one engine.islands snapshot.
        Assert.That(clusterBoard.Current.Peers.Count(info => info.Wallet == WALLET), Is.EqualTo(1));
    }
```

No new `using` is needed — `DCLPulseTests.csproj` sets `<ImplicitUsings>enable</ImplicitUsings>`, so `System.Linq` is already in scope (the existing `ClusterIdOf` helper relies on it).

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~ClusterTrackerTests"
```

Expected: the three new tests FAIL. `OutgoingDuplicateSession_IsNotClustered` fails on `GetClusterId(outgoing)` being a cluster id rather than null; the other two fail with 2 received calls / 2 roster entries.

- [ ] **Step 4: Implement the guard**

In `src/DCLPulse/Clusters/ClusterTracker.cs`, inside `TryCollectMember`, insert the guard between the `wallet is null` check and the `state.LastSeenPass` assignment:

```csharp
        string? wallet = identityBoard.GetWalletIdByPeerIndex(peer);

        if (wallet is null) return;

        // One wallet, one peer. A duplicate-session eviction rebinds the wallet to the replacement
        // before the outgoing peer's transport disconnect lands, so both sit in the grid for up to
        // Transport:PeerTimeoutMs. Collecting the outgoing one would address the wallet's feed
        // subject with the departing session's cluster and list the wallet twice in one topology
        // snapshot. Outside that overlap the reverse lookup returns the peer itself, so this is a
        // no-op — one dictionary read per occupant on a 1 Hz thread.
        if (!identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex live) || live != peer) return;

        state.LastSeenPass = passNumber;
```

- [ ] **Step 5: Update the `TryCollectMember` XML summary**

Replace the existing summary's second sentence so it documents the new skip:

```csharp
    /// <summary>
    ///     Resolves one occupant into a <see cref="PassMember" />, or skips it. A peer whose snapshot is
    ///     unreadable, whose wallet is unknown, or which is no longer the wallet's live binding in
    ///     <see cref="IdentityBoard" /> cannot be published as a cluster member. A peer already
    ///     collected this pass is skipped too: the grid read is weakly consistent, so a peer that
    ///     changes cell — or realm — mid-enumeration can surface twice, and every later step assumes a
    ///     peer appears at most once. The grid it was found in first decides the realm it clusters in.
    /// </summary>
```

- [ ] **Step 6: Run the full tracker suite**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~ClusterTrackerTests"
```

Expected: PASS, all tests including the 30 pre-existing ones. The guard must be a no-op for every single-session test.

- [ ] **Step 7: Run the whole suite**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Expected: PASS, 0 failures.

- [ ] **Step 8: Commit**

```bash
git add src/DCLPulse/Clusters/ClusterTracker.cs src/DCLPulseTests/ClusterTrackerTests.cs
git commit -m "fix: cluster only the wallet's live peer

An evicted peer lingers in the grid until its transport disconnect lands and
was still publishing on the wallet's feed subject.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Rule 2 — session handover

**Files:**
- Modify: `src/DCLPulse/Clusters/ClusterOptions.cs`
- Modify: `src/DCLPulse/appsettings.json`
- Modify: `src/DCLPulse/Clusters/ClusterTracker.cs`
- Test: `src/DCLPulseTests/ClusterTrackerTests.cs`

**Interfaces:**
- Consumes: `ClusterOptions.HandoverPasses` (added in this task); `IClusterFeedPublisher.PublishClusterChange(string wallet, string clusterId, string realm)`.
- Produces: `ClusterOptions.HandoverPasses` (int, default 15) — read by Task 3's metric test setup and named in Task 4's docs. `ClusterTracker` private members `assignmentByWallet`, `expiredWallets`, `WalletAssignment`, `PeerClusterState.HandoverPending`, `TryHandOverFromOutgoingSession`, `ForgetExpiredHandovers` — all private, no external consumer.

- [ ] **Step 1: Add the config knob**

In `src/DCLPulse/Clusters/ClusterOptions.cs`, after `IdPrefix`:

```csharp
    /// <summary>
    ///     Passes a departed wallet's published assignment is retained for a session handover. A
    ///     replacement session arriving inside this window is first published into the outgoing
    ///     session's cluster, so both hold the same LiveKit room and LiveKit's duplicate-identity rule
    ///     supersedes the outgoing participant; the replacement's own assignment follows on the next
    ///     pass. Zero disables the handover.
    /// </summary>
    public int HandoverPasses { get; set; } = 15;
```

In `src/DCLPulse/appsettings.json`, extend the `Clusters` section:

```json
  "Clusters": {
    "Enabled": true,
    "PassIntervalMs": 1000,
    "DwellPasses": 3,
    "IdPrefix": "C",
    "HandoverPasses": 15
  },
```

- [ ] **Step 2: Let the test fixture set it**

In `src/DCLPulseTests/ClusterTrackerTests.cs`, change the `CreateTracker` signature and options literal:

```csharp
    private ClusterTracker CreateTracker(bool enabled = true, int dwellPasses = 1, int handoverPasses = 15)
    {
        // Options.Create rather than a substitute: IOptions<T> has a real, trivial implementation, and
        // a substituted property getter depends on NSubstitute's ambient call context — which
        // neighbouring tests in this fixture were perturbing, so `enabled: false` silently arrived as
        // the default.
        IOptions<ClusterOptions> options = Options.Create(new ClusterOptions
        {
            Enabled = enabled,
            PassIntervalMs = 1000,
            DwellPasses = dwellPasses,
            IdPrefix = "C",
            HandoverPasses = handoverPasses,
        });
```

Leave the rest of the method unchanged.

- [ ] **Step 3: Add the `RemovePeer` test helper**

In `src/DCLPulseTests/ClusterTrackerTests.cs`, immediately after the `SetupPeer` helper:

```csharp
    /// <summary>
    ///     Tears a peer down the way the simulation's disconnect cleanup does: out of the grid, its
    ///     snapshot slot released, its identity wiped.
    /// </summary>
    private void RemovePeer(PeerIndex peer)
    {
        grids.Remove(peer);
        snapshotBoard.ClearActive(peer);
        identityBoard.Remove(peer);
    }
```

Then replace the three inline teardown lines inside the existing `DepartedPeer_LosesItsCarriedAssignment` test with `RemovePeer(new PeerIndex(0));`, keeping the `feedPublisher.ClearReceivedCalls();` line that follows them.

- [ ] **Step 4: Write the failing tests**

Append to `ClusterTrackerTests`, before the private helpers:

```csharp
    [Test]
    public void IncomingSession_IsFirstPublishedIntoTheOutgoingSessionsCluster()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        feedPublisher.ClearReceivedCalls();

        // The replacement arrives far away, so its own cluster differs from the one whose LiveKit room
        // the outgoing session still holds.
        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    [Test]
    public void AfterAHandover_ThePeersOwnClusterFollowsImmediatelyDespiteDwell()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        string ownCluster = ClusterIdOf(incoming);
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();

        // One pass, not DwellPasses: the migration off a handover is exempt from the debounce, which
        // would otherwise hold the session in the outgoing one's room.
        feedPublisher.Received(1).PublishClusterChange(WALLET, ownCluster, REALM);
    }

    [Test]
    public void HandoverIntoASurvivingCrowd_IsANoOpAndPublishesOnce()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        // Two bystanders keep the crowd's sticky ID alive across the session change, so the
        // replacement lands in the very cluster the ledger remembers.
        SetupPeer(new PeerIndex(2), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(3), new Vector3(30, 0, 30));
        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string crowdCluster = ClusterIdOf(outgoing);
        feedPublisher.ClearReceivedCalls();

        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(10, 0, 10), wallet: WALLET);

        tracker.RunPass();
        tracker.RunPass();

        Assert.That(ClusterIdOf(incoming), Is.EqualTo(crowdCluster));
        feedPublisher.Received(1).PublishClusterChange(WALLET, crowdCluster, REALM);
    }

    [Test]
    public void Handover_FiresEvenWhenTheRememberedClusterNoLongerExists()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);

        // A pass with the wallet absent, so its cluster is pruned and is demonstrably not live when the
        // replacement arrives. The outgoing session's LiveKit room outlives the cluster, which is why
        // the handover must not be gated on liveness.
        tracker.RunPass();
        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    [Test]
    public void Handover_CarriesTheRememberedRealmWhenTheSessionsRealmsDiffer()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        // The replacement authenticates into a different world. The room it must collide in is still
        // the outgoing session's, so the retained realm rides along with the retained cluster.
        SetupPeer(incoming, new Vector3(10, 0, 10), OTHER_REALM, WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);

        string ownCluster = ClusterIdOf(incoming);
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, ownCluster, OTHER_REALM);
    }

    [Test]
    public void RecycledSlotWithADifferentWallet_DoesNotInheritAHandover()
    {
        ClusterTracker tracker = CreateTracker();
        var peer = new PeerIndex(0);

        SetupPeer(peer, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string firstCluster = ClusterIdOf(peer);
        RemovePeer(peer);
        feedPublisher.ClearReceivedCalls();

        // The ledger is keyed by wallet, not by the recycled ENet slot, so the next tenant inherits
        // nothing.
        SetupPeer(peer, new Vector3(500, 0, 500), wallet: "0xother");
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange("0xother", firstCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange("0xother", ClusterIdOf(peer), REALM);
    }

    [Test]
    public void HandoverEntry_ExpiresAfterHandoverPasses()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 2);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);

        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange(WALLET, outgoingCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange(WALLET, ClusterIdOf(incoming), REALM);
    }

    [Test]
    public void HandoverEntry_SurvivesAWalletThatPublishesNothingForLongerThanTheWindow()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 2);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);

        // Standing still: the assignment is unchanged so nothing is published, but the entry must be
        // refreshed on every pass the wallet is seen or it would expire under a stationary player.
        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();

        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    [Test]
    public void HandoverPassesZero_DisablesTheHandover()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 0);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange(WALLET, outgoingCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange(WALLET, ClusterIdOf(incoming), REALM);
    }
```

- [ ] **Step 5: Run the tests to verify they fail**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~ClusterTrackerTests"
```

Expected: the six handover tests that expect the remembered cluster FAIL (the replacement publishes its own cluster). `RecycledSlotWithADifferentWallet_DoesNotInheritAHandover`, `HandoverEntry_ExpiresAfterHandoverPasses` and `HandoverPassesZero_DisablesTheHandover` may already pass — that is expected; they are the guards that must keep passing after the implementation lands.

- [ ] **Step 6: Add the ledger fields**

In `src/DCLPulse/Clusters/ClusterTracker.cs`, after the `clusterRecords` field:

```csharp
    // Last assignment published for each wallet, retained across the PeerIndex change of a
    // duplicate-session eviction. Keyed by wallet rather than PeerIndex: the slot is recycled, and
    // the outgoing session is about to give its own up, so slot-keyed state cannot survive the change.
    private readonly Dictionary<string, WalletAssignment> assignmentByWallet = new (StringComparer.OrdinalIgnoreCase);

    // Wallets whose entry aged out, collected before removal so the dictionary is not mutated
    // mid-enumeration. Cleared and reused between passes.
    private readonly List<string> expiredWallets = [];
```

- [ ] **Step 7: Add the nested `WalletAssignment` type**

At the end of `ClusterTracker`, alongside the other nested types (after `PeerClusterState`):

```csharp
    /// <summary>
    ///     A wallet's last published assignment. <see cref="LastSeenPass" /> tracks when the wallet was
    ///     last <i>seen</i> rather than last published: a stationary peer publishes once and never
    ///     again, and its entry must not expire underneath it.
    /// </summary>
    private struct WalletAssignment
    {
        public string ClusterId;
        public string Realm;
        public long LastSeenPass;
    }
```

- [ ] **Step 8: Add `HandoverPending` to `PeerClusterState`**

In the `PeerClusterState` struct, after `CandidateStreak`:

```csharp
        // Set on the pass a handover was published, cleared once the peer's own assignment follows.
        // Exempts that migration from the dwell debounce.
        public bool HandoverPending;
```

- [ ] **Step 9: Refresh the ledger from `TryCollectMember`**

In `TryCollectMember`, immediately after `state.LastSeenPass = passNumber;`:

```csharp
        state.LastSeenPass = passNumber;

        // Keep the wallet's handover entry alive while it has a live peer. Refreshing only on publish
        // would expire it under a stationary peer — the peer a duplicate session is most likely to
        // arrive for. A dictionary read and write per occupant, on a 1 Hz thread.
        if (assignmentByWallet.TryGetValue(wallet, out WalletAssignment seen))
        {
            seen.LastSeenPass = passNumber;
            assignmentByWallet[wallet] = seen;
        }
```

- [ ] **Step 10: Apply the handover in `TryPublishAssignment`**

Replace the body of `TryPublishAssignment` with:

```csharp
    private bool TryPublishAssignment(PassMember member, string clusterId, string realm)
    {
        ref PeerClusterState state = ref peerStates[member.Peer.Value];

        bool handingOver = TryHandOverFromOutgoingSession(in state, member.Wallet, ref clusterId, ref realm);

        bool realmChanged = !string.Equals(state.PublishedRealm, realm, StringComparison.Ordinal);

        if (!realmChanged && string.Equals(state.PublishedClusterId, clusterId, StringComparison.Ordinal))
        {
            state.CandidateClusterId = null;
            state.CandidateStreak = 0;

            return false;
        }

        // The debounce is bypassed on first assignment, teleport, realm change, deletion of the
        // peer's previous cluster, and the migration off a handover — cases where the published
        // assignment is already known to be wrong, so waiting would only prolong it.
        bool immediate = state.PublishedClusterId is null
                         || state.HandoverPending
                         || member.IsTeleport
                         || realmChanged
                         || !IsClusterLive(state.PublishedClusterId);

        if (!immediate && !HasDwelled(ref state, clusterId)) return false;

        state.PublishedClusterId = clusterId;
        state.PublishedRealm = realm;
        state.HandoverPending = handingOver;
        state.CandidateClusterId = null;
        state.CandidateStreak = 0;

        assignmentByWallet[member.Wallet] = new WalletAssignment
        {
            ClusterId = clusterId,
            Realm = realm,
            LastSeenPass = passNumber,
        };

        feedPublisher.PublishClusterChange(member.Wallet, clusterId, realm);

        return true;
    }
```

Extend the method's XML summary to mention the substitution:

```csharp
    /// <summary>
    ///     Emits a feed event for one peer if its assignment — cluster and realm together — differs
    ///     from the last one published, and either the change is exempt from the debounce or the peer
    ///     has dwelled long enough. A slot that has never published may have its assignment replaced
    ///     first by <see cref="TryHandOverFromOutgoingSession" />. Returns whether it published.
    /// </summary>
```

- [ ] **Step 11: Add the handover helper**

Immediately after `TryPublishAssignment` (private helpers follow their caller):

```csharp
    /// <summary>
    ///     Substitutes the wallet's retained assignment for a slot that has never published, so an
    ///     incoming session is first announced into the room the outgoing one still holds and LiveKit's
    ///     duplicate-identity rule supersedes that participant. Returns whether a substitution was
    ///     made; the caller records it so the peer's own assignment can follow without waiting out the
    ///     debounce.
    ///     <para />
    ///     Deliberately not gated on the retained cluster still existing. When the outgoing peer was
    ///     that cluster's only member the cluster is already gone from this pass, while its LiveKit
    ///     room — which outlives it — is exactly what the incoming session has to be steered into.
    /// </summary>
    private bool TryHandOverFromOutgoingSession(in PeerClusterState state, string wallet, ref string clusterId, ref string realm)
    {
        if (options.HandoverPasses <= 0) return false;
        if (state.PublishedClusterId is not null) return false;
        if (!assignmentByWallet.TryGetValue(wallet, out WalletAssignment retained)) return false;

        if (string.Equals(retained.ClusterId, clusterId, StringComparison.Ordinal)
            && string.Equals(retained.Realm, realm, StringComparison.Ordinal))
            return false;

        clusterId = retained.ClusterId;
        realm = retained.Realm;

        return true;
    }
```

- [ ] **Step 12: Sweep expired entries**

In `RunPass`, add the sweep after `ForgetVanishedPeers();`:

```csharp
        int reassignments = PublishAssignmentChanges();
        ForgetVanishedPeers();
        ForgetExpiredHandovers();
```

Add the method immediately after `ForgetVanishedPeers`:

```csharp
    /// <summary>
    ///     Drops handover entries for wallets absent for more than
    ///     <see cref="ClusterOptions.HandoverPasses" /> passes, which also bounds the map to concurrent
    ///     wallets plus recently departed ones. The window only has to span a duplicate-session
    ///     eviction; past it a handover would steer an arriving session into a room nobody is in.
    /// </summary>
    private void ForgetExpiredHandovers()
    {
        if (assignmentByWallet.Count == 0) return;

        expiredWallets.Clear();

        foreach (KeyValuePair<string, WalletAssignment> entry in assignmentByWallet)
            if (passNumber - entry.Value.LastSeenPass > options.HandoverPasses)
                expiredWallets.Add(entry.Key);

        for (var i = 0; i < expiredWallets.Count; i++)
            assignmentByWallet.Remove(expiredWallets[i]);
    }
```

- [ ] **Step 13: Run the tracker suite**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~ClusterTrackerTests"
```

Expected: PASS, all tests. Pay attention to the pre-existing debounce tests — `Reassignment_PublishesOnlyAfterDwellPassesAgree`, `UnchangedAssignment_IsNotRepublished`, `ClusterDeletion_BypassesTheDwellDebounce` — which must be unaffected, since `HandoverPending` is false for every peer that never handed over.

- [ ] **Step 14: Run the whole suite**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Expected: PASS, 0 failures.

- [ ] **Step 15: Commit**

```bash
git add src/DCLPulse/Clusters/ClusterTracker.cs src/DCLPulse/Clusters/ClusterOptions.cs src/DCLPulse/appsettings.json src/DCLPulseTests/ClusterTrackerTests.cs
git commit -m "feat: hand an incoming session into the outgoing session's cluster

Both sessions of a wallet then hold one LiveKit room, so LiveKit supersedes
the outgoing participant with DUPLICATE_IDENTITY. Retained per wallet for
Clusters:HandoverPasses passes.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Handover metric

A duplicate session is currently invisible in Pulse, comms-gatekeeper and ws-connector alike. This is the first counter anywhere in the chain that records one.

**Files:**
- Modify: `src/DCLPulse/Metrics/PulseMetrics.Clusters.cs`
- Modify: `src/DCLPulse/Metrics/MeterListenerMetricsCollector.cs`
- Modify: `src/DCLPulse/Metrics/MetricsSnapshot.cs`
- Modify: `src/DCLPulse/Metrics/PrometheusFormatter.cs`
- Modify: `src/DCLPulse/Metrics/Console/ConsoleDashboard.cs`
- Modify: `src/DCLPulse/Clusters/ClusterTracker.cs` (one line)
- Modify: `docs/metrics.md`

**Interfaces:**
- Consumes: `ClusterTracker.TryPublishAssignment`'s `handingOver` local (added in Task 2).
- Produces: `PulseMetrics.Clusters.HANDOVERS` (`Counter<long>`, instrument `pulse.clusters.handovers`) and `MetricsSnapshot.ClustersSnapshot.TotalHandovers` (`long`).

- [ ] **Step 1: Invoke the `add-metric` skill**

This repo owns the five-point wiring (instrument → `MeterListenerMetricsCollector` → `MetricsSnapshot` → `PrometheusFormatter` → `ConsoleDashboard`) as a skill. Use it rather than hand-editing, so the console row and its `RateStats` tracker are laid out the way every other counter is.

Parameters to give it:

| | |
|---|---|
| Instrument | `pulse.clusters.handovers`, `Counter<long>`, in the existing `PulseMetrics.Clusters` class |
| Field name | `HANDOVERS` |
| Snapshot property | `ClustersSnapshot.TotalHandovers` (`long`) |
| Collector field | `clusterHandovers`, `Interlocked.Add` in the `pulse.clusters.handovers` case alongside `pulse.clusters.reassignments` |
| Prometheus | `dcl_pulse_cluster_handovers_total`, written next to `dcl_pulse_cluster_reassignments_total` |
| Help text | `Incoming duplicate sessions published into an outgoing session's cluster, so LiveKit supersedes the outgoing participant. Each one is a wallet that was connected twice.` |
| Console row | Label `Handovers`, in the cluster block after `Reassignments`, rate-tracked like `clusterReassignments` |

XML summary for the instrument:

```csharp
        /// <summary>
        ///     Incoming sessions published into an outgoing session's cluster rather than their own, so
        ///     both hold one LiveKit room and the outgoing participant is superseded. One per duplicate
        ///     session observed.
        /// </summary>
```

- [ ] **Step 2: Increment it**

In `src/DCLPulse/Clusters/ClusterTracker.cs`, in `TryPublishAssignment`, between the `feedPublisher.PublishClusterChange(...)` call and `return true;`:

```csharp
        feedPublisher.PublishClusterChange(member.Wallet, clusterId, realm);

        if (handingOver)
            PulseMetrics.Clusters.HANDOVERS.Add(1);

        return true;
```

- [ ] **Step 3: Pin the wiring**

A counter declared without a matching case in `MeterListenerMetricsCollector` never reaches the snapshot, and one missing from `PrometheusFormatter` never reaches `/metrics`. Neither is a compile error. `HardeningMetricsTests` exists to pin exactly that; mirror it for clusters.

Create `src/DCLPulseTests/Metrics/ClusterMetricsTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using System.Text;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Guards the stage an instrument declaration silently skips: a <see cref="PulseMetrics" /> counter
///     without a matching case in <see cref="MeterListenerMetricsCollector" /> never reaches the
///     snapshot, and one missing from <c>PrometheusFormatter</c> never reaches <c>/metrics</c>. Neither
///     is a compile error, so both are pinned here.
/// </summary>
[TestFixture]
public class ClusterMetricsTests
{
    private MeterListenerMetricsCollector collector;

    [SetUp]
    public void SetUp()
    {
        var messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters());
        collector = new MeterListenerMetricsCollector(messagePipe, new ClientMessageCounters(), new ServerMessageCounters());
        collector.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => collector.Dispose();

    [Test]
    public void HandoverInstrument_ReachesTheClustersSnapshot()
    {
        // Deltas, not absolutes: PulseMetrics instruments are static and shared across the fixture run.
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Clusters.HANDOVERS.Add(2);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Clusters.TotalHandovers - before.Clusters.TotalHandovers, Is.EqualTo(2));
    }

    [Test]
    public void HandoverCounter_ReachesTheMetricsEndpoint()
    {
        PulseMetrics.Clusters.HANDOVERS.Add(1);
        MetricsSnapshot snapshot = collector.TakeSnapshot();

        using var buffer = new MemoryStream();

        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            PrometheusFormatter.Write(writer, snapshot);

        Assert.That(Encoding.UTF8.GetString(buffer.ToArray()), Does.Contain("dcl_pulse_cluster_handovers_total"));
    }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Expected: PASS, 0 failures, including both new `ClusterMetricsTests`.

- [ ] **Step 6: Document it**

In `docs/metrics.md`, add a subsection immediately after the reassignments one (around the `dcl_pulse_cluster_reassignments_total` paragraph), matching the surrounding style:

```markdown
#### Session handovers

Counter of incoming sessions published into an *outgoing* session's cluster instead of their own. `dcl_pulse_cluster_handovers_total`.

One handover is one wallet that was connected twice. The incoming session is announced into the room the outgoing one still holds so LiveKit's duplicate-identity rule supersedes that participant, and the incoming session migrates to its own cluster on the next pass — so every handover is normally followed by one `dcl_pulse_cluster_reassignments_total`.

| Reading | Meaning |
|---|---|
| Zero | No wallet has held two sessions since start |
| A steady trickle | Ordinary reconnect churn — clients recovering from network drops |
| A sustained spike | Clients reconnect-looping, or one wallet is being shared. Correlate with `dcl_pulse_peers_disconnected_total` and the `DUPLICATE_SESSION` disconnect reason |
| Non-zero while `alfa-stop-on-duplicate-identity` is off in the explorer | Actively harmful: the superseded client retries its cached token every second and the two sessions evict each other. Enable the flag or set `Clusters:HandoverPasses` to 0 |
```

- [ ] **Step 7: Commit**

```bash
git add src/DCLPulse/Metrics src/DCLPulseTests/Metrics/ClusterMetricsTests.cs src/DCLPulse/Clusters/ClusterTracker.cs docs/metrics.md
git commit -m "feat: count duplicate-session handovers

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Design-doc update

`docs/clustering-on-aoi.md` is the design of record shipping in PR #34 and currently claims duplicate-session eviction "needs no migration", which this work disproves.

**Files:**
- Modify: `docs/clustering-on-aoi.md`

**Interfaces:**
- Consumes: `Clusters:HandoverPasses` (Task 2), `dcl_pulse_cluster_handovers_total` (Task 3).
- Produces: nothing.

- [ ] **Step 1: Document the handover in the assignment-publishing section**

Find the paragraph describing `peer.{addr}.cluster_change` (it begins "Pulse publishes per-peer assignment changes **directly to NATS**" and contains "The wallet is lower-cased so one wallet always maps to one subject"). Append after it:

```markdown
**One wallet, one publishing peer.** A subject is per wallet, but a wallet can briefly own two `PeerIndex`es: `EvictDuplicateSession` disconnects the incumbent asynchronously and cannot flip its state — it belongs to another worker shard — so it stays in the grid until its transport disconnect lands, up to `Transport:PeerTimeoutMs`. The tracker therefore collects only the peer `IdentityBoard` currently binds the wallet to. Without that, both peers publish onto one subject, the outbox coalesces them latest-wins in grid-cell order, and the surviving session can be left addressed by the departing one's cluster with no further event to correct it.

**Session handover.** A replacement peer's *first* publish carries the wallet's retained assignment rather than its own, so the incoming session joins the LiveKit room the outgoing one still holds and LiveKit supersedes that participant with `DUPLICATE_IDENTITY`; its own cluster follows on the next pass, exempt from the dwell debounce. Retained per wallet for `Clusters:HandoverPasses` passes (default 15, `0` disables), and deliberately not gated on the retained cluster still existing — a peer that was alone takes its cluster with it while its LiveKit room outlives it. Counted by `dcl_pulse_cluster_handovers_total`.

This is what actually ends the outgoing session: nothing else does. `removeParticipantFromAllRooms` in comms-gatekeeper is wired only to bans, and the explorer's island room lives in `RoomHub`, independent of the Pulse transport — so an evicted client keeps a healthy LiveKit connection and never re-evaluates it. It requires the explorer's `alfa-stop-on-duplicate-identity` flag to be **enabled**: that flag gates both the reconnect-loop stop in `ConnectiveRoom` and the terminal popup in `DuplicateIdentityPlugin`. With it off, the superseded client retries its cached token — valid for five minutes — on its next one-second cycle, and the two sessions evict each other indefinitely.
```

- [ ] **Step 2: Correct the migration-scope claim**

Find the line listing what needs no migration (it reads "Everything else archipelago does is already Pulse-native and needs no migration: AuthChain auth, duplicate-session eviction, heartbeat expiry …"). Replace `duplicate-session eviction` with:

```markdown
duplicate-session eviction (plus the cluster-feed handover above, which archipelago had no equivalent of)
```

- [ ] **Step 3: Verify the rendered links and headings**

```bash
grep -n "HandoverPasses\|handovers_total\|stop-on-duplicate-identity" docs/clustering-on-aoi.md docs/metrics.md
```

Expected: matches in both files, no typos in the config key or metric name.

- [ ] **Step 4: Commit**

```bash
git add docs/clustering-on-aoi.md
git commit -m "docs: cluster-feed session handover

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Out of scope

Named here so no task quietly grows to include them:

- comms-gatekeeper's cross-replica ordering. Its per-wallet promise chain is process-local and queue groups have no subject affinity; already documented in that repo.
- Scene rooms, voice rooms, private-message rooms, and every other `generateCredentials` call site.
- Any explorer code change. Requirement 4 is satisfied by `ConnectiveRoom` and `DuplicateIdentityPlugin` as they already stand.
- Adding a sequence number to `PeerClusterChange`. That is a wire-protocol change and iteration-2 material.
