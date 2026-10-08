using BenchmarkDotNet.Attributes;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DCLPulseBenchmarks;

/// <summary>
///     Compares interest selection plus consumption before and after retaining an accepted sequence.
///     Both paths walk the same dense, static, single-realm grid. The legacy reference
///     reproduces the old ID/tier-only list, no deduplication, and the second latest-snapshot read
///     with its realm guard. The accepted path uses the production collector, including deduplication,
///     identity fencing, compact sequence entries, and the simulation's active/registration veto.
///     Consumption calls the production accepted-sequence reader and requires a retained target.
///     <para />
///     This is a warmed, single-threaded microbenchmark. There are no tier skips, concurrent writers, worker
///     scheduling, historical scans, profile lookups, or network encoding. Consumption reads pose,
///     sequence, animation, tier, and identity fields into an observable checksum. It does not measure
///     the complete simulation tick or prove concurrent correctness. Targets are retained throughout
///     this static run, so it measures the normal path and excludes hard-fallback frequency and cost.
///     <para />
///     Both lists are reserved before measurement, and setup warms the production collector's
///     deduplication storage. MemoryDiagnoser reports steady-state allocation; setup prints entry
///     sizes and retained list capacity separately, since retaining a larger buffer is not a
///     per-operation allocation. The production collector's deduplication storage is additional.
///     <para />
///     A reverted bitmap experiment on the superseded full-snapshot variant reduced collection
///     and consumption means by only 2–6%; complete query and consumption still cost about 3x
///     legacy time. The largest case was noisy. The simpler HashSet was retained; those results
///     do not measure this accepted-sequence variant.
/// </summary>
[MemoryDiagnoser]
public class InterestSnapshotBenchmarks
{
    private const string REALM = "benchmark";
    private const float CELL_SIZE = 100f;
    private const float TIER_0_SQ = 20f * 20f;
    private const float TIER_1_SQ = 50f * 50f;
    private const float MAX_DISTANCE_SQ = 100f * 100f;

    [Params(128, 512, 4095)]
    public int PeerCount { get; set; }

    private readonly PeerIndex observer = new (0);
    private PeerSnapshot observerSnapshot;
    private BenchmarkState? state;

    [GlobalSetup]
    public void Setup()
    {
        var current = new BenchmarkState(PeerCount);
        state = current;

        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var peer = new PeerIndex(i);
            // All subjects are candidates in the observer's scanned cells; most are within radius.
            Vector3 position = i == 0 ? Vector3.Zero : new Vector3(
                (int)(i * 37u % 159u) - 79f, 0, (int)(i * 71u % 159u) - 79f);
            PeerSnapshot snapshot = MakeSnapshot(i, position);
            current.IdentityBoard.Set(peer, $"benchmark-wallet-{i}");
            current.SnapshotBoard.SetActive(peer);
            current.SnapshotBoard.Publish(peer, in snapshot);
            current.Grids.Set(peer, REALM, position);
        }

        current.SnapshotBoard.TryRead(observer, out observerSnapshot);

        ulong legacyChecksum = LegacyQueryAndConsume();
        ulong acceptedChecksum = AcceptedQueryAndConsume();
        if (legacyChecksum != acceptedChecksum || current.LegacyEntries.Count != current.AcceptedCollector.Count)
            throw new InvalidOperationException("The benchmark paths must consume the same static subjects and states.");

        Dictionary<PeerIndex, InterestEntry> acceptedBySubject = current.AcceptedCollector.Entries.ToDictionary(entry => entry.Subject);
        foreach (LegacyInterestEntry entry in current.LegacyEntries)
        {
            if (!acceptedBySubject.TryGetValue(entry.Subject, out InterestEntry accepted)
                || accepted.Tier.Value != entry.Tier.Value
                || !current.SnapshotBoard.TryRead(entry.Subject, out PeerSnapshot target)
                || accepted.Seq != target.Seq
                || !ReferenceEquals(accepted.Identity, current.IdentityBoard.GetIdentity(entry.Subject)))
                throw new InvalidOperationException("The benchmark paths must agree on each subject, tier, sequence, and identity.");
        }

        // A second pass establishes the steady-state buffers used by both benchmark methods.
        LegacyQueryAndConsume();
        AcceptedQueryAndConsume();

        Console.WriteLine(
            $"Interest buffers: {current.AcceptedCollector.Count} accepted / {PeerCount} peers; "
            + $"legacy entry {Unsafe.SizeOf<LegacyInterestEntry>()} B, "
            + $"accepted entry {Unsafe.SizeOf<InterestEntry>()} B; "
            + $"retained list payload {current.LegacyEntries.Capacity * Unsafe.SizeOf<LegacyInterestEntry>()} B "
            + $"vs {current.AcceptedCollector.Entries.Capacity * Unsafe.SizeOf<InterestEntry>()} B "
            + "(accepted deduplication storage additional).");
    }

    [Benchmark(Baseline = true)]
    public ulong LegacyQueryAndConsume()
    {
        BenchmarkState current = state ?? throw new InvalidOperationException("Benchmark setup has not run.");
        current.LegacyEntries.Clear();
        SpatialGrid? grid = current.Grids.GetGrid(observerSnapshot.Realm);
        if (grid == null)
            return 0;

        current.Grids.CellCoords(observerSnapshot.GlobalPosition, out int cellX, out int cellZ);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                CollectLegacy(current, grid.GetPeers(SpatialGrid.PackKey(cellX + dx, cellZ + dz)));

        ulong checksum = 0;
        ReadOnlySpan<LegacyInterestEntry> entries = CollectionsMarshal.AsSpan(current.LegacyEntries);
        foreach (ref readonly LegacyInterestEntry entry in entries)
        {
            // The old collector did not retain state or identity, so consumption looks them up.
            string? wallet = current.IdentityBoard.GetWalletIdByPeerIndex(entry.Subject);
            if (wallet == null || !current.SnapshotBoard.TryRead(entry.Subject, out PeerSnapshot target))
                continue;
            if (!string.Equals(target.Realm, observerSnapshot.Realm, StringComparison.Ordinal))
                continue;

            checksum += ConsumeSnapshot(in target, entry.Tier.Value, wallet.Length);
        }

        return checksum;
    }

    [Benchmark]
    public ulong AcceptedQueryAndConsume()
    {
        BenchmarkState current = state ?? throw new InvalidOperationException("Benchmark setup has not run.");
        current.AcceptedCollector.Clear();
        current.AcceptedAoi.GetVisibleSubjects(observer, in observerSnapshot, current.AcceptedCollector);

        ulong checksum = 0;
        ReadOnlySpan<InterestEntry> entries = CollectionsMarshal.AsSpan(current.AcceptedCollector.Entries);
        foreach (ref readonly InterestEntry entry in entries)
        {
            if (InterestSnapshotReader.Read(current.SnapshotBoard, current.IdentityBoard, in entry,
                    out PeerSnapshot target) != InterestSnapshotReadResult.Retained)
                throw new InvalidOperationException("The static benchmark requires retained targets and live registrations.");

            checksum += ConsumeSnapshot(in target, entry.Tier.Value, entry.Identity.Wallet.Length);
        }

        return checksum;
    }

    private void CollectLegacy(BenchmarkState current, HashSet<PeerIndex>? peers)
    {
        if (peers == null)
            return;

        foreach (PeerIndex subject in peers)
        {
            if (subject == observer || !current.SnapshotBoard.TryRead(subject, out PeerSnapshot snapshot))
                continue;

            float dx = snapshot.GlobalPosition.X - observerSnapshot.GlobalPosition.X;
            float dz = snapshot.GlobalPosition.Z - observerSnapshot.GlobalPosition.Z;
            float distanceSq = (dx * dx) + (dz * dz);
            if (distanceSq > MAX_DISTANCE_SQ)
                continue;

            PeerViewSimulationTier tier = distanceSq <= TIER_0_SQ ? PeerViewSimulationTier.TIER_0 :
                distanceSq <= TIER_1_SQ ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;
            current.LegacyEntries.Add(new LegacyInterestEntry(subject, tier));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ConsumeSnapshot(in PeerSnapshot snapshot, byte tier, int walletLength) =>
        snapshot.Seq + (ulong)snapshot.ServerTick + snapshot.PositionX + snapshot.PositionY
        + snapshot.PositionZ + snapshot.RotationY + (uint)snapshot.AnimationFlags
        + snapshot.MovementBlend + tier + (uint)walletLength;

    private static PeerSnapshot MakeSnapshot(uint index, Vector3 position) => new (
        Seq: 1, ServerTick: 1, Parcel: 0,
        PositionX: index, PositionY: index + 1, PositionZ: index + 2,
        VelocityX: 0, VelocityY: 0, VelocityZ: 0,
        GlobalPosition: position,
        RotationY: index + 3, JumpCount: 0, MovementBlend: index + 4, SlideBlend: 0,
        HeadYaw: null, HeadPitch: null, PointAt: null,
        AnimationFlags: PlayerAnimationFlags.Grounded,
        GlideState: GlideState.PropClosed,
        Realm: REALM);

    private sealed class BenchmarkState
    {
        public RealmSpatialGrids Grids { get; }
        public SnapshotBoard SnapshotBoard { get; }
        public IdentityBoard IdentityBoard { get; }
        public SpatialHashAreaOfInterest AcceptedAoi { get; }
        public InterestCollector AcceptedCollector { get; }
        public List<LegacyInterestEntry> LegacyEntries { get; }

        public BenchmarkState(int peerCount)
        {
            Grids = new RealmSpatialGrids(CELL_SIZE, peerCount);
            SnapshotBoard = new SnapshotBoard(peerCount, ringCapacity: 4);
            IdentityBoard = new IdentityBoard(peerCount);
            AcceptedCollector = new InterestCollector();
            AcceptedCollector.Entries.EnsureCapacity(peerCount);
            LegacyEntries = new List<LegacyInterestEntry>(peerCount);
            AcceptedAoi = new SpatialHashAreaOfInterest(Grids, SnapshotBoard, IdentityBoard,
                Options.Create(new SpatialHashAreaOfInterestOptions
                {
                    CellSize = CELL_SIZE,
                    ScanCellRadius = 1,
                    Tier0Radius = 20f,
                    Tier1Radius = 50f,
                    MaxRadius = 100f,
                }));
        }
    }

    private readonly record struct LegacyInterestEntry(PeerIndex Subject, PeerViewSimulationTier Tier);
}
