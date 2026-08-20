# Scene Listener Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scene-listener client type: authenticates with the standard ECDSA auth chain via a dedicated `SceneListenerHandshakeRequest`, announces an immutable parcel set as its area of interest, never sends state updates, and receives the positional observer stream (joins/leaves/deltas/teleports) for players inside its parcels only.

**Architecture:** The listener is a peer variant, not a subsystem. It flows through the normal auth pipeline and per-worker simulation, but is never published as a subject (no `SnapshotBoard` slot, no `SpatialGrid` entry) and its interest set is computed by a new parcel-based private path in `PeerSimulation` (fixed TIER_0, parcel-exact filtering) instead of the radius AoI. A single choke point in `PeersManager` drops every post-auth message except `Resync`. Spec: [docs/superpowers/specs/2026-07-07-scene-listener-design.md](../specs/2026-07-07-scene-listener-design.md).

**Tech Stack:** .NET 10, protobuf + protoc-gen-bitwise (sibling `@dcl/protocol` repo), NUnit + NSubstitute, ENet transport.

## Global Constraints

- Build: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false` (plain `dotnet` — the SDK on this machine is at `C:\Program Files\dotnet`; do NOT use the `~/.dotnet` override from CLAUDE.md, that install is stale).
- Test: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false` (add `--filter` per task).
- Code style: instance fields `camelCase` no prefix; constants/static readonly `UPPER_SNAKE_CASE`; file-scoped namespaces; primary constructors for trivial DI; `var` when the type is clear.
- Tests use NSubstitute (never hand-rolled fakes); no line numbers in comments.
- Worker-shard isolation: per-peer state (`PeerState`, `observerViews`) is read/written only by the owning worker. The listener descriptor on `PeerState` respects this automatically.
- `PeerSimulation` decoupling rule: new logic goes into its own private method; orchestrators read as short sequences of named calls.
- No backward-compat concerns: the protocol is pre-GA; do not add compatibility shims.
- Commit messages: concise — short subject, short body only if needed.

## Agent Distribution (Opus 4.8)

Dispatch each task to a fresh subagent with `model: "opus"` (resolves to Opus 4.8). Tasks within a wave touch **disjoint files** and may run in parallel in the same working tree; waves are sequential barriers. All `Program.cs`/DI wiring is deliberately concentrated in Task 7 so Wave-1/2 agents never contend on it.

| Wave | Tasks (parallel) | Depends on |
|---|---|---|
| 1 | 1 (protocol), 2 (listener state + grid), 3 (options + metrics) | — |
| 2 | 4 (validator), 5 (simulation), 6 (choke point) | 4→{1,3}, 5→{2,3}, 6→{1,2,3} |
| 3 | 7 (handshake handler + all wiring) | 1–6 |
| 4 | 8 (E2E test client + docs + full verification) | 7 |

Each task = TDD + its own commit(s). A task's agent sees only its task text plus Global Constraints — the **Interfaces** blocks are the contract between tasks.

---

### Task 1: Protocol — `SceneListenerHandshakeRequest` + envelope case

**Files:**
- Modify (proto repo, via skill): `decentraland/pulse/pulse_client.proto` in the sibling `@dcl/protocol` checkout
- Regenerated: `src/Protocol/Generated/PulseClient.cs` (and any sibling `Generated/*.cs` the regen touches)

**Interfaces:**
- Consumes: nothing.
- Produces (generated C#, namespace `Decentraland.Pulse`):
  - `class SceneListenerHandshakeRequest` with `ByteString AuthChain`, `string Realm`, `RepeatedField<int> ParcelIndices`
  - `ClientMessage.MessageOneofCase.SceneListenerHandshake` (= 8) and `ClientMessage.SceneListenerHandshake` property

- [ ] **Step 1: Invoke the `modify-protocol` skill** (Skill tool). Change to apply in `pulse_client.proto`:

```proto
// Scene-listener connect: same signed-fetch auth chain as HandshakeRequest, plus an
// immutable parcel-set area of interest. No initial state — a listener is never a subject.
message SceneListenerHandshakeRequest {
  // Signed-fetch headers JSON — identical shape to HandshakeRequest.auth_chain.
  bytes auth_chain = 1;
  // AoI realm partition — same rules as TeleportRequest.realm.
  string realm = 2;
  // ParcelEncoder-packed parcel indices; fixed for the connection lifetime.
  repeated int32 parcel_indices = 3;
}
```

and in the `ClientMessage` oneof, after `teleport = 7`:

```proto
    SceneListenerHandshakeRequest scene_listener_handshake = 8;
```

Follow the skill's regen procedure. Note: the working tree already carries small uncommitted descriptor/line-ending diffs under `src/Protocol/Generated/` from a prior regen — that is expected; include whatever the regen produces.

- [ ] **Step 2: Verify the generated surface**

Run: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: build succeeds; `Grep` for `SceneListenerHandshake` in `src/Protocol/Generated/PulseClient.cs` finds the message class and the `MessageOneofCase.SceneListenerHandshake` enum member.

- [ ] **Step 3: Add a round-trip test** — Create `src/DCLPulseTests/SceneListenerHandshakeProtoTests.cs`:

```csharp
using Decentraland.Pulse;
using Google.Protobuf;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerHandshakeProtoTests
{
    [Test]
    public void SceneListenerHandshake_RoundTripsThroughEnvelope()
    {
        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8("{}"),
            Realm = "main",
        };

        request.ParcelIndices.AddRange(new[] { 100, 101, 417 });

        var envelope = new ClientMessage { SceneListenerHandshake = request };

        ClientMessage parsed = ClientMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.That(parsed.MessageCase, Is.EqualTo(ClientMessage.MessageOneofCase.SceneListenerHandshake));
        Assert.That(parsed.SceneListenerHandshake.Realm, Is.EqualTo("main"));
        Assert.That(parsed.SceneListenerHandshake.ParcelIndices, Is.EqualTo(new[] { 100, 101, 417 }));
    }
}
```

- [ ] **Step 4: Run the test**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerHandshakeProtoTests"`
Expected: PASS

- [ ] **Step 5: Commit** — `git add` the proto-repo change is committed per the skill's procedure; in this repo `git add src/Protocol/Generated src/DCLPulseTests/SceneListenerHandshakeProtoTests.cs` then `git commit -m "feat: add SceneListenerHandshakeRequest to protocol"`

---

### Task 2: `SceneListenerState`, `SpatialGrid` cell-key API, `SceneListenerCellMapper`

**Files:**
- Create: `src/DCLPulse/Peers/SceneListenerState.cs`
- Create: `src/DCLPulse/InterestManagement/SceneListenerCellMapper.cs`
- Modify: `src/DCLPulse/Peers/PeerState.cs`
- Modify: `src/DCLPulse/InterestManagement/SpatialGrid.cs`
- Test: `src/DCLPulseTests/SceneListenerCellMapperTests.cs`

**Interfaces:**
- Consumes: `ParcelEncoder.Decode(int, out int x, out int z)`, `ParcelEncoderOptions.ParcelSize`, `SpatialGrid` internals (`PackKey`/`CellCoord` already exist as private members).
- Produces:
  - `Pulse.Peers.SceneListenerState` — `sealed class`, ctor `(string realm, HashSet<int> parcels, long[] cellKeys)`, props `string Realm`, `HashSet<int> Parcels`, `long[] CellKeys`
  - `PeerState.SceneListener` — `public SceneListenerState? SceneListener { get; set; }` (null for players)
  - `SpatialGrid.GetPeersByCell(long cellKey)` → `HashSet<PeerIndex>?`
  - `SpatialGrid.ComputeCellKey(float x, float z)` → `long`
  - `Pulse.InterestManagement.SceneListenerCellMapper` — ctor `(ParcelEncoder, SpatialGrid, IOptions<ParcelEncoderOptions>)`, method `long[] ComputeCellKeys(IReadOnlyCollection<int> parcelIndices)`
  - **No DI registration here** — Task 7 owns `Program.cs`.

- [ ] **Step 1: Write the failing tests** — `src/DCLPulseTests/SceneListenerCellMapperTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerCellMapperTests
{
    // ParcelEncoderOptions defaults: MinParcelX/Z=-150, Padding=2, ParcelSize=16 → minX=minZ=-152.
    // Grid cellSize 100 → parcel (0,0) spans world [0,16)² inside cell (0,0).
    private SpatialGrid grid;
    private ParcelEncoder encoder;
    private SceneListenerCellMapper mapper;

    [SetUp]
    public void SetUp()
    {
        IOptions<ParcelEncoderOptions> options = Options.Create(new ParcelEncoderOptions());
        grid = new SpatialGrid(100, 100);
        encoder = new ParcelEncoder(options);
        mapper = new SceneListenerCellMapper(encoder, grid, options);
    }

    [Test]
    public void SingleParcelInsideOneCell_CoversThatCell()
    {
        // Parcel (1,1) spans world [16,32)² — fully inside grid cell (0,0).
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(1, 1) });

        var peer = new PeerIndex(7);
        grid.Set(peer, new Vector3(20f, 0f, 20f));

        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(peer) == true), Is.True,
            "A peer standing inside the parcel must be reachable through the covering cell keys.");
    }

    [Test]
    public void ParcelStraddlingCellBoundary_CoversBothCells()
    {
        // Parcel (6,0) spans world x [96,112) — straddles the x=100 cell boundary.
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(6, 0) });

        var left = new PeerIndex(1);
        var right = new PeerIndex(2);
        grid.Set(left, new Vector3(97f, 0f, 5f));
        grid.Set(right, new Vector3(105f, 0f, 5f));

        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(left) == true), Is.True);
        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(right) == true), Is.True);
    }

    [Test]
    public void AdjacentParcelsInSameCell_DedupeKeys()
    {
        // Parcels (1,1) and (2,1) both live inside cell (0,0) — keys must be deduped.
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(1, 1), encoder.Encode(2, 1) });

        Assert.That(keys, Is.Unique);
        Assert.That(keys.Length, Is.LessThanOrEqualTo(4),
            "Two adjacent interior parcels must not multiply covering cells.");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerCellMapperTests"`
Expected: FAIL — `SceneListenerCellMapper` / `GetPeersByCell` do not exist.

- [ ] **Step 3: Implement**

`src/DCLPulse/Peers/SceneListenerState.cs`:

```csharp
namespace Pulse.Peers;

/// <summary>
///     Immutable scene-listener descriptor stamped onto <see cref="PeerState" /> at handshake.
///     A peer carrying this never publishes snapshots (invisible to players) and observes a
///     fixed parcel set instead of a radius around its own position. Changing the set requires
///     reconnecting.
/// </summary>
public sealed class SceneListenerState(string realm, HashSet<int> parcels, long[] cellKeys)
{
    public string Realm { get; } = realm;

    /// <summary>Announced parcel set — the parcel-exact visibility filter.</summary>
    public HashSet<int> Parcels { get; } = parcels;

    /// <summary>
    ///     Deduped SpatialGrid cell keys covering the parcel set. May over-cover one
    ///     neighboring cell at exact boundaries — candidates are filtered parcel-exact
    ///     by the simulation, so over-coverage costs a lookup, never correctness.
    /// </summary>
    public long[] CellKeys { get; } = cellKeys;
}
```

`PeerState.cs` — add below `Throttle`:

```csharp
    /// <summary>
    ///     Non-null marks this peer as a scene listener (receive-only, parcel-set AoI).
    ///     Set once by the scene-listener handshake; immutable for the connection lifetime.
    /// </summary>
    public SceneListenerState? SceneListener { get; set; }
```

`SpatialGrid.cs` — add next to `GetPeers`:

```csharp
    public HashSet<PeerIndex>? GetPeersByCell(long cellKey) =>
        cells.GetValueOrDefault(cellKey);

    public long ComputeCellKey(float x, float z) =>
        PackKey(CellCoord(x), CellCoord(z));
```

`src/DCLPulse/InterestManagement/SceneListenerCellMapper.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Pulse.InterestManagement;

/// <summary>
///     Maps an announced parcel set to the deduped <see cref="SpatialGrid" /> cell keys
///     covering it. Computed once per scene-listener handshake; immutable thereafter.
///     Each 16m parcel overlaps 1–4 of the larger grid cells. The closed max corner may
///     over-cover one neighboring cell when a parcel edge lands exactly on a cell boundary —
///     harmless, the simulation filters candidates parcel-exact.
/// </summary>
public sealed class SceneListenerCellMapper(
    ParcelEncoder parcelEncoder,
    SpatialGrid grid,
    IOptions<ParcelEncoderOptions> parcelOptions)
{
    private readonly int parcelSize = parcelOptions.Value.ParcelSize;

    public long[] ComputeCellKeys(IReadOnlyCollection<int> parcelIndices)
    {
        var keys = new HashSet<long>();

        foreach (int index in parcelIndices)
        {
            parcelEncoder.Decode(index, out int px, out int pz);
            float minX = px * parcelSize;
            float minZ = pz * parcelSize;

            keys.Add(grid.ComputeCellKey(minX, minZ));
            keys.Add(grid.ComputeCellKey(minX + parcelSize, minZ));
            keys.Add(grid.ComputeCellKey(minX, minZ + parcelSize));
            keys.Add(grid.ComputeCellKey(minX + parcelSize, minZ + parcelSize));
        }

        var result = new long[keys.Count];
        keys.CopyTo(result);
        return result;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerCellMapperTests"`
Expected: PASS (3/3)

- [ ] **Step 5: Commit** — `git add src/DCLPulse/Peers/SceneListenerState.cs src/DCLPulse/Peers/PeerState.cs src/DCLPulse/InterestManagement/SpatialGrid.cs src/DCLPulse/InterestManagement/SceneListenerCellMapper.cs src/DCLPulseTests/SceneListenerCellMapperTests.cs` then `git commit -m "feat: scene-listener state and parcel-to-cell mapping"`

---

### Task 3: `SceneListenerOptions` + metrics

**Files:**
- Create: `src/DCLPulse/Peers/SceneListenerOptions.cs`
- Create: `src/DCLPulse/Metrics/PulseMetrics.SceneListener.cs`
- Modify: `src/DCLPulse/appsettings.json`
- Modify (via `add-metric` skill): whatever the metrics pipeline requires (`MeterListenerMetricsCollector`, `PrometheusFormatter`, `ConsoleDashboard`, …)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces:
  - `Pulse.Peers.SceneListenerOptions` — `SECTION_NAME = "SceneListener"`, `int MaxParcels { get; set; } = 256;`
  - `PulseMetrics.SceneListener.CONNECTED` — `UpDownCounter<int>`, name `pulse.scene_listener.connected`
  - `PulseMetrics.SceneListener.FORBIDDEN_MESSAGES_DROPPED` — `Counter<long>`, name `pulse.scene_listener.forbidden_messages_dropped`
  - `PulseMetrics.SceneListener.VISIBLE_SUBJECTS` — `Histogram<int>`, name `pulse.scene_listener.visible_subjects`
  - **No `Program.cs` binding here** — Task 7 owns `Program.cs`. The options class is dormant until then; that is expected.

- [ ] **Step 1: Create the options class** — `src/DCLPulse/Peers/SceneListenerOptions.cs`:

```csharp
namespace Pulse.Peers;

public sealed class SceneListenerOptions
{
    public const string SECTION_NAME = "SceneListener";

    /// <summary>
    ///     Maximum number of distinct parcels a single scene listener may announce.
    ///     A handshake exceeding this after dedup is rejected — never clamped.
    /// </summary>
    public int MaxParcels { get; set; } = 256;
}
```

- [ ] **Step 2: Add the config section** — in `src/DCLPulse/appsettings.json`, after the `"ParcelEncoder"` section:

```json
  "SceneListener": {
    "MaxParcels": 256
  },
```

- [ ] **Step 3: Create the metric instruments** — `src/DCLPulse/Metrics/PulseMetrics.SceneListener.cs`, mirroring `PulseMetrics.Hardening.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class SceneListener
    {
        public static readonly UpDownCounter<int> CONNECTED =
            METER.CreateUpDownCounter<int>("pulse.scene_listener.connected");

        public static readonly Counter<long> FORBIDDEN_MESSAGES_DROPPED =
            METER.CreateCounter<long>("pulse.scene_listener.forbidden_messages_dropped");

        public static readonly Histogram<int> VISIBLE_SUBJECTS =
            METER.CreateHistogram<int>("pulse.scene_listener.visible_subjects");
    }
}
```

- [ ] **Step 4: Invoke the `add-metric` skill** (Skill tool) to wire these three instruments through accumulation, Prometheus export, and the console dashboard, following the same treatment the `pulse.hardening.*` counters get. The increment call-sites land in later tasks (Tasks 5–7) — this task only makes the metrics exist end-to-end with zero values.

- [ ] **Step 5: Build**

Run: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: build succeeds.

- [ ] **Step 6: Commit** — `git add -A src/DCLPulse/Peers/SceneListenerOptions.cs src/DCLPulse/Metrics src/DCLPulse/appsettings.json` (plus any files the skill touched) then `git commit -m "feat: scene-listener options and metrics plumbing"`

---

### Task 4: `FieldValidator.ValidateSceneListenerHandshake`

**Files:**
- Modify: `src/DCLPulse/Messaging/Hardening/FieldValidator.cs`
- Modify: `src/DCLPulseTests/HandshakeHandlerTests.cs` (FieldValidator construction gains a parameter)
- Test: `src/DCLPulseTests/Hardening/FieldValidatorTests.cs` (extend)

**Interfaces:**
- Consumes: `SceneListenerHandshakeRequest` (Task 1), `SceneListenerOptions` (Task 3), existing `PeerDefense.Reject`, `ParcelEncoder.IsValidIndex`.
- Produces:
  - `FieldValidator` ctor gains a 4th parameter: `IOptions<SceneListenerOptions> sceneListenerOptions`
  - `public bool ValidateSceneListenerHandshake(PeerIndex from, PeerState state, SceneListenerHandshakeRequest request, [NotNullWhen(true)] out HashSet<int>? parcels)` — validates realm (non-empty, ≤ MaxRealmLength), non-empty parcel list, every index valid, deduped count ≤ `MaxParcels`; on success `parcels` is the deduped set; on failure the peer is rejected with `DisconnectReason.INVALID_HANDSHAKE_FIELD` and the method returns `false`.

- [ ] **Step 1: Write the failing tests** — append to `src/DCLPulseTests/Hardening/FieldValidatorTests.cs` (read the existing fixture first and mirror its construction style; the fixture must now pass `Options.Create(new SceneListenerOptions { MaxParcels = 4 })` as the new ctor arg). Test cases:

```csharp
    // Build a request helper inside the test class:
    private static SceneListenerHandshakeRequest ListenerRequest(string realm, params int[] parcels)
    {
        var request = new SceneListenerHandshakeRequest { Realm = realm };
        request.ParcelIndices.AddRange(parcels);
        return request;
    }

    [Test]
    public void SceneListener_ValidRequest_ReturnsDedupedParcels()
    {
        bool ok = validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("main", 10, 11, 10), out HashSet<int>? parcels);

        Assert.That(ok, Is.True);
        Assert.That(parcels, Is.EquivalentTo(new[] { 10, 11 }));
    }

    [Test]
    public void SceneListener_EmptyRealm_Rejects()
    {
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("", 10), out _), Is.False);
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void SceneListener_EmptyParcelList_Rejects()
    {
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("main"), out _), Is.False);
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_InvalidParcelIndex_Rejects()
    {
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("main", -1), out _), Is.False);
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_OverCapAfterDedup_Rejects()
    {
        // Fixture MaxParcels = 4; five distinct parcels must reject, duplicates must not count.
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("main", 1, 2, 3, 4, 5), out _), Is.False);
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_DuplicatesWithinCap_Accepted()
    {
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest("main", 1, 1, 2, 2, 3, 3), out HashSet<int>? parcels), Is.True);
        Assert.That(parcels!.Count, Is.EqualTo(3));
    }

    [Test]
    public void SceneListener_RealmTooLong_Rejects()
    {
        // Use the fixture's MaxRealmLength to build an over-length realm string.
        Assert.That(validator.ValidateSceneListenerHandshake(peer, state, ListenerRequest(new string('a', 300), 1), out _), Is.False);
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }
```

Adapt names (`validator`, `state`, `peer`, `transport`) to the existing fixture's fields.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~FieldValidatorTests"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement** — in `FieldValidator.cs`:

Change the primary constructor to:

```csharp
public sealed class FieldValidator(
    IOptions<FieldValidatorOptions> options,
    IOptions<SceneListenerOptions> sceneListenerOptions,
    ParcelEncoder parcelEncoder,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.FIELD_VALIDATION_FAILED)
```

add the field `private readonly int maxSceneListenerParcels = sceneListenerOptions.Value.MaxParcels;` next to the existing option fields, add `using System.Diagnostics.CodeAnalysis;` and the method:

```csharp
    /// <summary>
    ///     Validates a scene-listener handshake: realm rules identical to
    ///     <see cref="ValidateTeleport" />, every parcel index in range, and the deduped
    ///     parcel count within <see cref="SceneListenerOptions.MaxParcels" /> — rejected,
    ///     never clamped. On success <paramref name="parcels" /> holds the deduped set.
    /// </summary>
    public bool ValidateSceneListenerHandshake(PeerIndex from, PeerState state, SceneListenerHandshakeRequest request,
        [NotNullWhen(true)] out HashSet<int>? parcels)
    {
        parcels = null;

        if (string.IsNullOrEmpty(request.Realm))
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (maxRealmLength > 0 && request.Realm.Length > maxRealmLength)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (request.ParcelIndices.Count == 0)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        var deduped = new HashSet<int>();

        foreach (int index in request.ParcelIndices)
        {
            if (!IsValidParcel(index))
                return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

            deduped.Add(index);
        }

        if (deduped.Count > maxSceneListenerParcels)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        parcels = deduped;
        return true;
    }
```

- [ ] **Step 4: Fix the existing FieldValidator constructions** — `src/DCLPulseTests/HandshakeHandlerTests.cs` (and any other test constructing `FieldValidator`; find with `Grep new FieldValidator`): add `Options.Create(new SceneListenerOptions())` as the second argument.

- [ ] **Step 5: Run tests**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~FieldValidatorTests|FullyQualifiedName~HandshakeHandlerTests"`
Expected: PASS

- [ ] **Step 6: Commit** — `git commit -m "feat: validate scene-listener handshake fields"`

---

### Task 5: `PeerSimulation` — parcel-set interest path + positional-only gating

**Files:**
- Modify: `src/DCLPulse/Peers/Simulation/PeerSimulation.cs`
- Test: `src/DCLPulseTests/PeerSimulationTests.SceneListener.cs` (new partial)

**Interfaces:**
- Consumes: `SceneListenerState` / `PeerState.SceneListener` (Task 2), `SpatialGrid.GetPeersByCell` (Task 2), `PulseMetrics.SceneListener.VISIBLE_SUBJECTS` (Task 3).
- Produces (all private/internal to `PeerSimulation` — no cross-task surface):
  - Listener observers are simulated from their `SceneListenerState` without a `SnapshotBoard` read.
  - `ProcessVisibleSubjects`, `HandleNewSubject`, `ProcessExistingSubject` gain a `bool positionalOnly` parameter (default behavior unchanged for players).

- [ ] **Step 1: Write the failing tests** — `src/DCLPulseTests/PeerSimulationTests.SceneListener.cs`, following the existing partial-fixture style (`PeerSimulationTests.cs` owns SetUp; grid cellSize is 50 in the fixture):

```csharp
using Decentraland.Pulse;
using Pulse.Peers;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    /// <summary>
    ///     Fixture grid cellSize = 50. These tests stamp raw parcel indices onto snapshots and
    ///     place subjects at test-chosen world positions, so the covering cell keys are computed
    ///     directly from those positions (default: the cell containing the origin) rather than
    ///     through SceneListenerCellMapper — the mapper has its own tests in Task 2.
    /// </summary>
    private void MakeSceneListener(PeerIndex listener, string realm = "main", int[]? parcels = null, long[]? cellKeys = null)
    {
        peers[listener] = new PeerState(PeerConnectionState.AUTHENTICATED)
        {
            SceneListener = new SceneListenerState(realm,
                new HashSet<int>(parcels ?? []),
                cellKeys ?? [spatialGrid.ComputeCellKey(0f, 0f)]),
        };

        identityBoard.Set(listener, "0xLISTENER_WALLET");
    }

    private void PublishSubjectInParcel(PeerIndex peer, uint seq, int parcel, Vector3 worldPos, string realm = "main")
    {
        snapshotBoard.SetActive(peer);
        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: seq, serverTick: seq * 10, parcel: parcel,
            globalPosition: worldPos, realm: realm));
        spatialGrid.Set(peer, worldPos);
    }

    [Test]
    public void SceneListener_SubjectInsideParcel_GetsPlayerJoined()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage joined = messages.Single(m => m.To == listener && m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined);
        Assert.That(joined.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
    }

    [Test]
    public void SceneListener_SubjectInCellButOutsideParcelSet_Invisible()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);

        // Same grid cell (pos inside [0,50)²) but a different parcel index on the snapshot.
        PublishSubjectInParcel(subject, seq: 2, parcel: 6, worldPos: new Vector3(20f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(DrainAllMessages().Where(m => m.To == listener), Is.Empty,
            "Cell-level match must not leak subjects outside the announced parcels.");
    }

    [Test]
    public void SceneListener_CrossRealmSubject_Invisible()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "other", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f), realm: "main");

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(DrainAllMessages().Where(m => m.To == listener), Is.Empty);
    }

    [Test]
    public void SceneListener_MovingSubject_GetsDeltaEveryTick()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // PlayerJoined

        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(9f, 0f, 8f));

        // TIER_0 fires on every tick — including odd ticks that would gate TIER_1.
        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> messages = DrainAllMessages().Where(m => m.To == listener).ToList();
        Assert.That(messages.Single().Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
        Assert.That(messages.Single().PacketMode, Is.EqualTo(PacketMode.UNRELIABLE_SEQUENCED));
    }

    [Test]
    public void SceneListener_EmoteStart_SuppressedButPositionStillFlows()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Real emote start snapshot (Seq == StartSeq) inside the parcel.
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, parcel: 5,
            globalPosition: new Vector3(8f, 0f, 8f), realm: "main",
            emote: new EmoteState("wave", StartSeq: 3, StartTick: 30)));

        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages().Where(m => m.To == listener).ToList();
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.None.EqualTo(ServerMessage.MessageOneofCase.EmoteStarted),
            "Positional-only listeners must not receive emote broadcasts.");
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta),
            "The position carried by the emote snapshot must still arrive as a delta.");
    }

    [Test]
    public void SceneListener_ProfileAnnouncement_Suppressed()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        profileBoard.Set(subject, 42);
        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(8.5f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 2);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.None.EqualTo(ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced));
    }

    [Test]
    public void SceneListener_Teleport_StillDelivered()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, parcel: 5,
            globalPosition: new Vector3(12f, 0f, 12f), realm: "main", isTeleport: true));
        spatialGrid.Set(subject, new Vector3(12f, 0f, 12f));

        simulation.SimulateTick(peers, tickCounter: 2);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.Teleported));
    }

    [Test]
    public void SceneListener_SubjectLeavesParcels_SweptWithPlayerLeft()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Subject moves far away — different cell, different parcel.
        PublishSubjectInParcel(subject, seq: 3, parcel: 900, worldPos: new Vector3(500f, 0f, 500f));

        // Advance past the sweep interval.
        for (uint tick = 2; tick <= SWEEP_INTERVAL * 2 + 1; tick++)
            simulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages()
                .Where(m => m.To == listener)
                .Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft));
    }

    [Test]
    public void SceneListener_Resync_ServedWithReliableResponse()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(9f, 0f, 8f));
        AddResyncRequest(listener, subject, knownSeq: 1);

        simulation.SimulateTick(peers, tickCounter: 2);

        OutgoingMessage response = DrainAllMessages().Single(m => m.To == listener);
        Assert.That(response.PacketMode, Is.EqualTo(PacketMode.RELIABLE),
            "Resync responses ride the reliable channel for listeners exactly as for players.");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~PeerSimulationTests.SceneListener"`
Expected: FAIL — listener branch does not exist (listener peers are skipped because `snapshotBoard.TryRead(listener)` fails).

- [ ] **Step 3: Implement** — in `PeerSimulation.cs`:

3a. In `SimulateTick`, insert the branch after the `AUTHENTICATED` check and **before** the `snapshotBoard.TryRead(observerId, …)` call:

```csharp
            if (observerState.SceneListener is { } listener)
            {
                SimulateSceneListenerObserver(observerId, observerState, listener, tickCounter);
                continue;
            }
```

3b. Add the two new private methods (own section, per the decoupling rule):

```csharp
    // ── Scene-listener observers ────────────────────────────────────

    /// <summary>
    ///     Scene listeners have no snapshot of their own — their interest set is the fixed
    ///     parcel set announced at handshake, always at TIER_0, positional messages only.
    ///     Everything downstream (views, diffs, resync, sweeps) is the shared pipeline.
    /// </summary>
    private void SimulateSceneListenerObserver(PeerIndex observerId, PeerState observerState, SceneListenerState listener, uint tickCounter)
    {
        if (!observerViews.TryGetValue(observerId, out Dictionary<PeerIndex, PeerToPeerView>? views))
        {
            views = new Dictionary<PeerIndex, PeerToPeerView>();
            observerViews[observerId] = views;
        }

        collector.Clear();
        CollectSceneListenerSubjects(observerId, listener);

        PulseMetrics.SceneListener.VISIBLE_SUBJECTS.Record(collector.Count);

        string? observerWallet = identityBoard.GetWalletIdByPeerIndex(observerId);

        ProcessVisibleSubjects(observerId, observerWallet, views, observerState.ResyncRequests, tickCounter, positionalOnly: true);

        observerState.ResyncRequests?.Clear();

        if (tickCounter % SWEEP_INTERVAL == 0)
            SweepStaleViews(observerId, views, tickCounter);
    }

    /// <summary>
    ///     Fills the collector with subjects standing inside the listener's parcels: union the
    ///     occupants of the precomputed covering cells, then filter parcel-exact (the covering
    ///     cells over-approximate — a 100-unit cell holds ~6×6 parcels) and by realm. Every
    ///     accepted subject is TIER_0: a parcel set has no distance to tier by.
    /// </summary>
    private void CollectSceneListenerSubjects(PeerIndex observerId, SceneListenerState listener)
    {
        foreach (long cellKey in listener.CellKeys)
        {
            HashSet<PeerIndex>? cellPeers = spatialGrid.GetPeersByCell(cellKey);

            if (cellPeers == null)
                continue;

            foreach (PeerIndex subject in cellPeers)
            {
                if (subject == observerId)
                    continue;

                if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot))
                    continue;

                if (!string.Equals(subjectSnapshot.Realm, listener.Realm, StringComparison.Ordinal))
                    continue;

                if (!listener.Parcels.Contains(subjectSnapshot.Parcel))
                    continue;

                collector.Add(subject, PeerViewSimulationTier.TIER_0);
            }
        }
    }
```

(Each grid peer occupies exactly one cell, so the union cannot produce duplicate collector entries.)

3c. Thread `positionalOnly` through the shared pipeline:

- `ProcessVisibleSubjects(..., uint tickCounter, bool positionalOnly = false)` — the existing call site in `SimulateTick` stays unchanged (default `false`).
- Inside `ProcessVisibleSubjects`: wrap the profile announcement — `if (!positionalOnly) TryAnnounceProfile(observerId, entry.Subject, ref view);` — and pass `positionalOnly` to `HandleNewSubject` and `ProcessExistingSubject`.
- `HandleNewSubject(..., bool positionalOnly)`: change the mid-emote companion broadcast condition from `if (latestSnapshot.Emote is { EmoteId: not null } activeEmote)` to `if (!positionalOnly && latestSnapshot.Emote is { EmoteId: not null } activeEmote)` — a positional-only observer falls into the `else` branch that seeds `view.LastSentSeq`.
- `ProcessExistingSubject(..., bool positionalOnly)`: add `&& !positionalOnly` to the emote-start broadcast condition (the `if (emoteStartIsEffective && lastEmoteStart!.Value.Emote is ...)` block). `TrySyncEmoteStop` needs no gate — for listeners `view.LastSentEmote` is never set, so its first guard already makes it a no-op. Teleport and delta/resync phases are untouched.

- [ ] **Step 4: Run the new tests and the full simulation suite**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~PeerSimulationTests"`
Expected: PASS — all existing partials (player path unchanged) plus the new listener partial.

- [ ] **Step 5: Commit** — `git commit -m "feat: parcel-set interest path for scene listeners"`

---

### Task 6: Inbound-message choke point in `PeersManager`

**Files:**
- Modify: `src/DCLPulse/Peers/PeersManager.cs`
- Test: `src/DCLPulseTests/SceneListenerMessagePolicyTests.cs`

**Interfaces:**
- Consumes: `PeerState.SceneListener` (Task 2), `PulseMetrics.SceneListener.FORBIDDEN_MESSAGES_DROPPED` (Task 3), `ClientMessage.MessageOneofCase.SceneListenerHandshake` (Task 1).
- Produces: `internal static bool PeersManager.IsForbiddenForSceneListener(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)` — `true` when the message must be dropped. Wired into `HandleMessage` before handler dispatch.

- [ ] **Step 1: Write the failing tests** — `src/DCLPulseTests/SceneListenerMessagePolicyTests.cs`. The policy method is static and takes plain collections, so no `PeersManager` construction is needed:

```csharp
using Decentraland.Pulse;
using Pulse.Peers;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerMessagePolicyTests
{
    private static PeerState Listener() =>
        new (PeerConnectionState.AUTHENTICATED)
        {
            SceneListener = new SceneListenerState("main", new HashSet<int> { 1 }, new long[] { 0L }),
        };

    private static Dictionary<PeerIndex, PeerState> Peers(PeerIndex peer, PeerState state) =>
        new () { [peer] = state };

    [TestCase(ClientMessage.MessageOneofCase.Input)]
    [TestCase(ClientMessage.MessageOneofCase.EmoteStart)]
    [TestCase(ClientMessage.MessageOneofCase.EmoteStop)]
    [TestCase(ClientMessage.MessageOneofCase.Teleport)]
    [TestCase(ClientMessage.MessageOneofCase.ProfileAnnouncement)]
    [TestCase(ClientMessage.MessageOneofCase.Handshake)]
    [TestCase(ClientMessage.MessageOneofCase.SceneListenerHandshake)]
    public void ForbiddenCases_AreDropped(ClientMessage.MessageOneofCase messageCase)
    {
        var peer = new PeerIndex(1);
        ClientMessage message = BuildMessage(messageCase);

        Assert.That(PeersManager.IsForbiddenForSceneListener(Peers(peer, Listener()), peer, message), Is.True);
    }

    [Test]
    public void Resync_IsAllowed()
    {
        var peer = new PeerIndex(1);
        var message = new ClientMessage { Resync = new ResyncRequest() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(Peers(peer, Listener()), peer, message), Is.False);
    }

    [Test]
    public void PlayerPeer_IsNeverGated()
    {
        var peer = new PeerIndex(1);
        var message = new ClientMessage { Input = new PlayerStateInput() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(
            Peers(peer, new PeerState(PeerConnectionState.AUTHENTICATED)), peer, message), Is.False);
    }

    [Test]
    public void UnknownPeer_IsNeverGated()
    {
        var message = new ClientMessage { Input = new PlayerStateInput() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(
            new Dictionary<PeerIndex, PeerState>(), new PeerIndex(1), message), Is.False);
    }

    private static ClientMessage BuildMessage(ClientMessage.MessageOneofCase messageCase) =>
        messageCase switch
        {
            ClientMessage.MessageOneofCase.Input => new ClientMessage { Input = new PlayerStateInput() },
            ClientMessage.MessageOneofCase.EmoteStart => new ClientMessage { EmoteStart = new EmoteStart() },
            ClientMessage.MessageOneofCase.EmoteStop => new ClientMessage { EmoteStop = new EmoteStop() },
            ClientMessage.MessageOneofCase.Teleport => new ClientMessage { Teleport = new TeleportRequest() },
            ClientMessage.MessageOneofCase.ProfileAnnouncement => new ClientMessage { ProfileAnnouncement = new ProfileAnnouncement() },
            ClientMessage.MessageOneofCase.Handshake => new ClientMessage { Handshake = new HandshakeRequest() },
            ClientMessage.MessageOneofCase.SceneListenerHandshake => new ClientMessage { SceneListenerHandshake = new SceneListenerHandshakeRequest() },
            _ => throw new ArgumentOutOfRangeException(nameof(messageCase)),
        };
}
```

If any inner message type name differs (e.g. `ProfileAnnouncement`), check the oneof property types in `src/Protocol/Generated/PulseClient.cs` and use those.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerMessagePolicyTests"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement** — in `PeersManager.cs`, wire into `HandleMessage` between the counter increment and handler dispatch:

```csharp
    private void HandleMessage(IncomingEvent evt, Dictionary<PeerIndex, PeerState> peers)
    {
        ClientMessage message = evt.Message!;

        incomingMessageCounters.Increment(message.MessageCase);

        if (IsForbiddenForSceneListener(peers, evt.From, message))
            return;

        if (messageHandlers.TryGetValue(message.MessageCase, out IMessageHandler? handler))
            handler.Handle(peers, evt.From, message);
        else
            logger.LogWarning("No handler found for message {MessageCase}, skipped processing", message.MessageCase);
    }

    /// <summary>
    ///     Post-auth choke point for scene listeners: only <c>Resync</c> is processed — a
    ///     listener never mutates state, so <c>Input</c>/emotes/<c>Teleport</c>/profile
    ///     announcements and repeat handshakes are dropped silently (mirrors the pre-auth
    ///     "non-handshake silently dropped" convention; no disconnect — a buggy listener
    ///     degrades to noise, not connection churn). The parcel set is immutable by
    ///     construction: no message can change it.
    /// </summary>
    internal static bool IsForbiddenForSceneListener(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (!peers.TryGetValue(from, out PeerState? state) || state.SceneListener == null)
            return false;

        if (message.MessageCase == ClientMessage.MessageOneofCase.Resync)
            return false;

        PulseMetrics.SceneListener.FORBIDDEN_MESSAGES_DROPPED.Add(1);
        return true;
    }
```

(A pre-auth listener has no `SceneListener` descriptor yet — it's stamped at promotion — so the gate never blocks the handshake itself.)

- [ ] **Step 4: Run tests**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerMessagePolicyTests"`
Expected: PASS

- [ ] **Step 5: Commit** — `git commit -m "feat: drop forbidden inbound messages from scene listeners"`

---

### Task 7: `SceneListenerHandshakeHandler`, shared `HandshakeAuthenticator`, all wiring

**Files:**
- Create: `src/DCLPulse/Messaging/HandshakeAuthenticator.cs`
- Create: `src/DCLPulse/Messaging/SceneListenerHandshakeHandler.cs`
- Modify: `src/DCLPulse/Messaging/HandshakeHandler.cs` (use the extracted authenticator)
- Modify: `src/DCLPulse/Program.cs` (all DI: authenticator, cell mapper, options binding, handler + dict entry, `ClientMessageCounters` bump)
- Modify: `src/DCLPulse/Peers/Simulation/PeerSimulation.cs` (CONNECTED gauge decrement on cleanup)
- Test: `src/DCLPulseTests/SceneListenerHandshakeHandlerTests.cs`
- Modify: `src/DCLPulseTests/HandshakeHandlerTests.cs` (handler ctor gains the authenticator)

**Interfaces:**
- Consumes: everything produced by Tasks 1–4 (`SceneListenerHandshakeRequest`, `SceneListenerState`, `SceneListenerCellMapper`, `SceneListenerOptions`, `FieldValidator.ValidateSceneListenerHandshake`, metrics).
- Produces:
  - `Pulse.Messaging.HandshakeAuthenticator` — `sealed class`, ctor `(AuthChainValidator)`, method `AuthResult? Authenticate(ByteString authChain)` with `readonly record struct AuthResult(string UserAddress, string Timestamp)`; returns `null` on unparseable JSON, throws on invalid chains (same exceptions as `AuthChainValidator.Validate`).
  - `Pulse.Messaging.SceneListenerHandshakeHandler : IMessageHandler`, registered for `ClientMessage.MessageOneofCase.SceneListenerHandshake`.

- [ ] **Step 1: Extract `HandshakeAuthenticator`** — create `src/DCLPulse/Messaging/HandshakeAuthenticator.cs`:

```csharp
using DCL.Auth;
using Google.Protobuf;
using System.Text.Json;

namespace Pulse.Messaging;

/// <summary>
///     The auth-chain half of a handshake, shared by <see cref="HandshakeHandler" /> and
///     <see cref="SceneListenerHandshakeHandler" />: parse the signed-fetch headers JSON,
///     rebuild the expected connect payload, and validate the ECDSA chain. Returns
///     <c>null</c> when the JSON cannot be parsed; throws (same exceptions as
///     <see cref="AuthChainValidator.Validate" />) when the chain is invalid — callers
///     translate both into a handshake reject.
/// </summary>
public sealed class HandshakeAuthenticator(AuthChainValidator authChainValidator)
{
    public readonly record struct AuthResult(string UserAddress, string Timestamp);

    public AuthResult? Authenticate(ByteString authChain)
    {
        string authChainJson = authChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize(authChainJson, HandshakeJsonContext.Default.DictionaryStringString);

        if (headers == null)
            return null;

        IReadOnlyList<AuthLink> chain = AuthChainParser.ParseFromSignedFetchHeaders(headers);

        string timestamp = string.Empty;
        string metadata = string.Empty;

        foreach (KeyValuePair<string, string> kv in headers)
        {
            if (kv.Key.Equals("x-identity-timestamp", StringComparison.OrdinalIgnoreCase))
                timestamp = kv.Value;

            if (kv.Key.Equals("x-identity-metadata", StringComparison.OrdinalIgnoreCase))
                metadata = kv.Value;
        }

        string expectedPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata);
        AuthChainValidationResult result = authChainValidator.Validate(chain, expectedPayload);

        return new AuthResult(result.UserAddress, timestamp);
    }
}
```

- [ ] **Step 2: Refactor `HandshakeHandler` onto the authenticator** — replace its `AuthChainValidator authChainValidator` ctor parameter with `HandshakeAuthenticator authenticator`; replace the body from the JSON deserialize through `authChainValidator.Validate` with:

```csharp
        HandshakeRequest handshakeRequest = message.Handshake;

        try
        {
            HandshakeAuthenticator.AuthResult? auth = authenticator.Authenticate(handshakeRequest.AuthChain);

            if (auth == null)
            {
                // (send the existing "Invalid auth chain JSON" HandshakeResponse and return —
                //  move the current headers==null block here)
            }

            (string userAddress, string timestamp) = auth.Value;
            // ... the rest of the existing flow, with result.UserAddress → userAddress ...
```

Keep every subsequent step (ban list, replay, initial-state validation, promotion, duplicate-session, seed, response) byte-for-byte in the same order. Update `src/DCLPulseTests/HandshakeHandlerTests.cs` to construct the handler with `authenticator: new HandshakeAuthenticator(new AuthChainValidator(verifier))`.

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~HandshakeHandlerTests"`
Expected: PASS — pure refactor, existing tests green. Commit: `git commit -m "refactor: extract HandshakeAuthenticator from HandshakeHandler"`

- [ ] **Step 3: Write the failing listener-handshake tests** — `src/DCLPulseTests/SceneListenerHandshakeHandlerTests.cs`. Mirror the `HandshakeHandlerTests` fixture (stubbed `ISignatureVerifier`, real boards) but build a `SceneListenerHandshake` envelope. Reuse its `BuildHandshake` header-bundle shape:

```csharp
using DCL.Auth;
using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Text.Json;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerHandshakeHandlerTests
{
    private const string WALLET = "0xabc0000000000000000000000000000000000001";
    private const string EPHEMERAL = "0xdef0000000000000000000000000000000000002";
    private const string TIMESTAMP = "1700000000000";

    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ITransport transport;
    private IdentityBoard identityBoard;
    private Dictionary<PeerIndex, PeerState> peers;
    private SceneListenerHandshakeHandler handler;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        spatialGrid = new SpatialGrid(100, 100);
        IOptions<ParcelEncoderOptions> parcelOptions = Options.Create(new ParcelEncoderOptions());
        var parcelEncoder = new ParcelEncoder(parcelOptions);
        transport = Substitute.For<ITransport>();
        identityBoard = new IdentityBoard(100);

        ISignatureVerifier verifier = Substitute.For<ISignatureVerifier>();
        verifier.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(10_000u);

        var fieldValidator = new FieldValidator(
            Options.Create(new FieldValidatorOptions { MaxRealmLength = 16, MaxEmoteDurationMs = 60_000 }),
            Options.Create(new SceneListenerOptions { MaxParcels = 8 }),
            parcelEncoder,
            transport);

        handler = new SceneListenerHandshakeHandler(
            messagePipe: new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            authenticator: new HandshakeAuthenticator(new AuthChainValidator(verifier)),
            peerStateFactory: new PeerStateFactory(),
            identityBoard: identityBoard,
            transport: transport,
            attemptPolicy: new HandshakeAttemptPolicy(
                Options.Create(new HandshakeAttemptPolicyOptions()),
                Substitute.For<ITransport>()),
            preAuthAdmission: new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions())),
            replayPolicy: new HandshakeReplayPolicy(
                Options.Create(new HandshakeReplayPolicyOptions { Enabled = false }),
                new PeerOptions(),
                Options.Create(new ENetTransportOptions { MaxPeers = 100 }),
                timeProvider,
                Substitute.For<ITransport>()),
            banList: new BanList(),
            fieldValidator: fieldValidator,
            cellMapper: new SceneListenerCellMapper(parcelEncoder, spatialGrid, parcelOptions),
            logger: Substitute.For<ILogger<SceneListenerHandshakeHandler>>());

        peer = new PeerIndex(1);
        peers = new Dictionary<PeerIndex, PeerState> { [peer] = new (PeerConnectionState.PENDING_AUTH) };
    }

    [Test]
    public void Handle_ValidRequest_AuthenticatesWithListenerDescriptor()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", 10, 11));

        PeerState state = peers[peer];
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.AUTHENTICATED));
        Assert.That(state.SceneListener, Is.Not.Null);
        Assert.That(state.SceneListener!.Realm, Is.EqualTo("main"));
        Assert.That(state.SceneListener.Parcels, Is.EquivalentTo(new[] { 10, 11 }));
        Assert.That(state.SceneListener.CellKeys, Is.Not.Empty);
        Assert.That(state.WalletId, Is.EqualTo(WALLET).IgnoreCase);
    }

    [Test]
    public void Handle_ValidRequest_NeverRegistersAsSubject()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", 10));

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False,
            "A listener must never own a SnapshotBoard slot — it would become visible to players.");
        Assert.That(spatialGrid.GetPeers(new System.Numerics.Vector3(0, 0, 0)), Is.Null.Or.Not.Contains(peer));
    }

    [Test]
    public void Handle_ValidRequest_RegistersIdentity()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", 10));

        Assert.That(identityBoard.TryGetPeerIndexByWallet(WALLET, out PeerIndex found), Is.True);
        Assert.That(found, Is.EqualTo(peer));
    }

    [Test]
    public void Handle_DuplicateWallet_EvictsExistingSession()
    {
        var other = new PeerIndex(2);
        identityBoard.Set(other, WALLET);

        handler.Handle(peers, peer, BuildListenerHandshake("main", 10));

        transport.Received(1).Disconnect(other, DisconnectReason.DUPLICATE_SESSION);
    }

    [Test]
    public void Handle_OverCapParcels_RejectsBeforeAuthenticated()
    {
        // Fixture MaxParcels = 8.
        handler.Handle(peers, peer, BuildListenerHandshake("main", Enumerable.Range(0, 9).ToArray()));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_EmptyParcels_Rejects()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main"));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_InvalidAuthChainJson_RespondsWithError()
    {
        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8("not json"),
            Realm = "main",
        };
        request.ParcelIndices.Add(10);

        handler.Handle(peers, peer, new ClientMessage { SceneListenerHandshake = request });

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH),
            "A parse failure responds with an error but leaves the peer awaiting a retry within the attempt budget.");
    }

    private ClientMessage BuildListenerHandshake(string realm, params int[] parcels)
    {
        var ephemeralPayload =
            $"Decentraland Login\nEphemeral address: {EPHEMERAL}\nExpiration: 2099-01-01T00:00:00Z";

        string connectPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", TIMESTAMP, "{}");

        var headers = new Dictionary<string, string>
        {
            ["x-identity-auth-chain-0"] = JsonSerializer.Serialize(
                new AuthLink(Type: "SIGNER", Payload: WALLET, Signature: string.Empty)),
            ["x-identity-auth-chain-1"] = JsonSerializer.Serialize(
                new AuthLink(Type: "ECDSA_EPHEMERAL", Payload: ephemeralPayload, Signature: "0xdeadbeef")),
            ["x-identity-auth-chain-2"] = JsonSerializer.Serialize(
                new AuthLink(Type: "ECDSA_SIGNED_ENTITY", Payload: connectPayload, Signature: "0xdeadbeef")),
            ["x-identity-timestamp"] = TIMESTAMP,
            ["x-identity-metadata"] = "{}",
        };

        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8(JsonSerializer.Serialize(headers)),
            Realm = realm,
        };

        request.ParcelIndices.AddRange(parcels);

        return new ClientMessage { SceneListenerHandshake = request };
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter "FullyQualifiedName~SceneListenerHandshakeHandlerTests"`
Expected: FAIL — handler does not exist.

- [ ] **Step 5: Implement the handler** — `src/DCLPulse/Messaging/SceneListenerHandshakeHandler.cs`:

```csharp
using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;

namespace Pulse.Messaging;

/// <summary>
///     Authenticates a scene-listener connection: same ECDSA auth chain and anti-abuse
///     pipeline as <see cref="HandshakeHandler" /> (attempt throttle, ban list, replay
///     guard), but the peer announces an immutable parcel-set AoI instead of an initial
///     state. The listener is never registered as a subject — no SnapshotBoard slot, no
///     SpatialGrid entry — so it stays invisible to every player observer. Re-announcing
///     the parcel set requires reconnecting: no post-auth message can mutate it.
/// </summary>
public class SceneListenerHandshakeHandler(MessagePipe messagePipe,
    HandshakeAuthenticator authenticator,
    PeerStateFactory peerStateFactory,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    FieldValidator fieldValidator,
    SceneListenerCellMapper cellMapper,
    ILogger<SceneListenerHandshakeHandler> logger) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        // The router guarantees a Connected lifecycle event precedes the first message,
        // so a missing state means the peer is already being torn down — nothing to do.
        if (!peers.TryGetValue(from, out PeerState? existingState))
            return;

        if (!attemptPolicy.TryRecordAttempt(from, existingState))
            return;

        SceneListenerHandshakeRequest request = message.SceneListenerHandshake;

        try
        {
            HandshakeAuthenticator.AuthResult? auth = authenticator.Authenticate(request.AuthChain);

            if (auth == null)
            {
                SendResponse(from, success: false, "Invalid auth chain JSON");
                logger.LogInformation("Scene-listener handshake failed: cannot parse auth-chain");
                return;
            }

            (string wallet, string timestamp) = auth.Value;

            if (banList.IsBanned(wallet))
            {
                SendResponse(from, success: false, "banned");
                existingState.ConnectionState = PeerConnectionState.PENDING_DISCONNECT;
                PulseMetrics.Hardening.BANNED_REFUSED.Add(1);
                transport.Disconnect(from, DisconnectReason.BANNED);
                logger.LogInformation("Scene-listener handshake rejected: wallet {Wallet} is banned", wallet);
                return;
            }

            if (!replayPolicy.TryAdmit(from, existingState, wallet, timestamp))
                return;

            if (!fieldValidator.ValidateSceneListenerHandshake(from, existingState, request, out HashSet<int>? parcels))
                return;

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = wallet;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;
            peer.SceneListener = new SceneListenerState(request.Realm, parcels, cellMapper.ComputeCellKeys(parcels));

            peers[from] = peer;

            preAuthAdmission.ReleaseOnPromotion(from);

            if (identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex duplicatedPeer) && duplicatedPeer != from)
            {
                transport.Disconnect(duplicatedPeer, DisconnectReason.DUPLICATE_SESSION);
                logger.LogInformation("Duplicated peer found {Wallet}, disconnecting peer {Peer}", wallet, duplicatedPeer);
            }

            identityBoard.Set(from, wallet);

            // Deliberately no snapshotBoard.SetActive / snapshot seed / spatialGrid.Set:
            // a listener is never a subject.

            PulseMetrics.SceneListener.CONNECTED.Add(1);

            SendResponse(from, success: true, error: null);

            logger.LogInformation("Scene listener accepted with wallet {Wallet} - peerId {Peer} ({ParcelCount} parcels, realm '{Realm}')",
                wallet, from, parcels.Count, request.Realm);
        }
        catch (Exception e)
        {
            SendResponse(from, success: false, e.Message);
            logger.LogInformation("Scene-listener handshake failed: {Error}", e.Message);
        }
    }

    private void SendResponse(PeerIndex to, bool success, string? error)
    {
        var response = new HandshakeResponse { Success = success };

        if (error != null)
            response.Error = error;

        messagePipe.Send(new MessagePipe.OutgoingMessage(to, new ServerMessage
        {
            Handshake = response,
        }, PacketMode.RELIABLE));
    }
}
```

(The handler deliberately does **not** inject `SnapshotBoard`, `PeerSnapshotPublisher`, or `SpatialGrid` — a listener must never touch them. The fixture's `snapshotBoard` field exists only for the never-registers-as-subject assertion.)

- [ ] **Step 6: Gauge symmetry** — in `PeerSimulation.CleanupDisconnectedPeer`, the caller (`SimulateTick` DISCONNECTING branch) has `observerState` in scope. Change the call to pass it — `CleanupDisconnectedPeer(observerId, observerState)` — and inside, before the boards are wiped:

```csharp
        if (observerState.SceneListener != null)
            PulseMetrics.SceneListener.CONNECTED.Add(-1);
```

- [ ] **Step 7: Wire everything in `Program.cs`:**

```csharp
builder.Services.Configure<SceneListenerOptions>(
    builder.Configuration.GetSection(SceneListenerOptions.SECTION_NAME));
```

(next to the other `Configure<>` calls), and next to the other singletons:

```csharp
builder.Services.AddSingleton<HandshakeAuthenticator>();
builder.Services.AddSingleton<SceneListenerCellMapper>();
builder.Services.AddSingleton<SceneListenerHandshakeHandler>();
```

update the counters registration for the new envelope case (9 cases: None + 8 messages):

```csharp
builder.Services.AddSingleton(new ClientMessageCounters(9));
```

and add to the handler dictionary:

```csharp
    { ClientMessage.MessageOneofCase.SceneListenerHandshake, sp.GetRequiredService<SceneListenerHandshakeHandler>() },
```

Also verify `PrometheusFormatter.INCOMING_MESSAGE_TYPES` and `ConsoleDashboard.INCOMING_MESSAGES_CONFIG` list the new case (add `ClientMessage.MessageOneofCase.SceneListenerHandshake` entries if Task 3's metric wiring didn't already).

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: PASS — all suites.

- [ ] **Step 9: Commit** — `git commit -m "feat: scene-listener handshake handler and wiring"`

---

### Task 8: Test-client listener mode, E2E verification, docs

**Files:**
- Modify: `src/DCLPulseTestClient/Networking/PulseMultiplayerService.cs`
- Modify: `src/DCLPulseTestClient/ClientOptions.cs`
- Modify: `src/DCLPulseTestClient/Program.cs` + `src/DCLPulseTestClient/BotSession.cs`/`SimulationLoop.cs` (read them first — wire the listener mode following the existing bot-session flow)
- Modify: `CLAUDE.md` (message architecture), `docs/superpowers/specs/2026-07-07-scene-listener-design.md` (status line → Implemented)

**Interfaces:**
- Consumes: the full server feature (Tasks 1–7); `metaforge` auth chain via existing `MetaForge.cs` plumbing.
- Produces: `--scene-listener-parcels=x1:z1,x2:z2,...` CLI mode on the test client.

- [ ] **Step 1: Add the listener connect method** — in `PulseMultiplayerService.cs`, next to `ConnectAsync`:

```csharp
    public async Task ConnectAsSceneListenerAsync(string address, int port, string authChain,
        string realm, IReadOnlyList<int> parcelIndices, CancellationToken ct)
    {
        connectionLifeCycleCts.SafeCancelAndDispose();
        connectionLifeCycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await transport.ConnectAsync(address, port, ct);

        _ = RouteIncomingMessagesAsync(connectionLifeCycleCts.Token);

        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8(authChain),
            Realm = realm,
        };

        request.ParcelIndices.AddRange(parcelIndices);

        pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
        {
            SceneListenerHandshake = request,
        }, PacketMode.RELIABLE));

        await foreach (HandshakeResponse response in SubscribeAsync<HandshakeResponse>(
                           ServerMessage.MessageOneofCase.Handshake, ct))
        {
            if (!response.Success)
            {
                connectionLifeCycleCts.SafeCancelAndDispose();
                await transport.DisconnectAsync(DisconnectReason.AUTH_FAILED, ct);

                throw new PulseException(response.HasError ? response.Error : "Scene-listener handshake failed");
            }

            break;
        }
    }
```

- [ ] **Step 2: Add the CLI option** — in `ClientOptions.cs`, add `public string SceneListenerParcels { get; init; } = "";` and in `FromArgs`: `SceneListenerParcels = Arg("scene-listener-parcels", ""),`. Format: comma-separated `x:z` parcel coordinates (converted to indices with the client-side `ParcelEncoder`).

- [ ] **Step 3: Wire the listener session** — read `src/DCLPulseTestClient/Program.cs`, `BotSession.cs`, `SimulationLoop.cs`, and `ServerEventHandler.cs` first. When `SceneListenerParcels` is non-empty: create the account/auth chain exactly like a bot, call `ConnectAsSceneListenerAsync` instead of `ConnectAsync`, skip the input/simulation loop entirely, and subscribe to `PlayerJoined`/`PlayerLeft`/`PlayerStateDelta`/`PlayerStateFull`/`Teleported`, logging each received message with subject id + parcel so a human can eyeball the E2E flow. Do not send any message after the handshake.

- [ ] **Step 4: E2E run** — invoke the `run-test-client` skill twice against a locally running server (`dotnet run --project src/DCLPulse -p:GenerateProto=false` or the skill's own server-start procedure):
  1. a listener with `--scene-listener-parcels` covering the player bot's spawn parcel,
  2. a normal moving player bot in the same realm.

  Verify from the logs: listener receives `PlayerJoined` then a delta stream while the bot is inside the parcels; `PlayerLeft` after the bot disperses out; no `EmoteStarted`/profile messages ever; the player bot never receives a `PlayerJoined` for the listener. Then invoke the `stop-test-client` skill.

- [ ] **Step 5: Update docs** — in `CLAUDE.md` under *Client → Server* add a `SCENE_LISTENER_HANDSHAKE` entry (ch0, reliable; auth chain + realm + immutable parcel set; listener is receive-only, positional stream, `Resync` allowed, everything else dropped; never a subject). Flip the spec's Status line to `Implemented`.

- [ ] **Step 6: Final verification** — run the full suite one more time:

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: PASS.

- [ ] **Step 7: Commit** — `git commit -m "feat: scene-listener mode for test client + docs"`

---

## Plan Self-Review Notes

- Spec coverage: protocol (T1), handshake & authorization (T4, T7), interest management & simulation (T2, T5), inbound policy (T6), config/metrics (T3), liveness (no code — verified by design), error handling (T4, T7 tests), testing incl. E2E (all tasks + T8). No gaps.
- The `SceneListenerState`/`SpatialGrid`/`FieldValidator`/`HandshakeAuthenticator` signatures are used consistently across Tasks 2, 4, 5, 6, 7.
- `Program.cs` and the metrics display tables are each owned by exactly one wave — no parallel-agent file contention.
