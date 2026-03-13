using BenchmarkDotNet.Attributes;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DCLPulseBenchmarks;

/// <summary>
///     Compares two implementations of spatial interest management:
///     - LinearScan: flat array of cell keys, scans all peer slots per query (PR #2 approach)
///     - CopyOnWrite: current SpatialGrid with lock + copy-on-write HashSet per cell
///     Peers are distributed pseudo-randomly in a ±500 world (cellSize=50).
///     Observer sits at origin. ~4% of peers fall in the 3×3 neighbourhood.
///     "1W" methods are single-threaded baselines.
///     "4W" methods model the production setup: 4 parallel workers, each owning
///     a peer stripe (PeerIndex % 4 == workerIndex), as in PeersManager.
/// </summary>
[MemoryDiagnoser]
public class SpatialInterestBenchmarks
{
    private const float CELL_SIZE = 50f;
    private const int WORKER_COUNT = 4;

    [Params(128, 512, 4095)]
    public int PeerCount { get; set; }

    // Current implementation (copy-on-write)
    private SpatialGrid _cowGrid = null!;
    private SpatialHashAreaOfInterest _cowAoi = null!;

    // Linear scan reference (PR #2)
    private LinearScanGrid _linearGrid = null!;
    private LinearScanAoi _linearAoi = null!;

    // ConcurrentDictionary reference (PR #2)
    private ConcurrentDictSpatialGrid _cdGrid = null!;
    private ConcurrentDictAoi _cdAoi = null!;

    private SnapshotBoard _snapshotBoard = null!;
    private Vector3[] _peerPositions = null!;
    private Vector3[] _altPositions = null!;

    // Single-worker baseline
    private InterestCollector _collector = null!;
    private PeerSnapshot _observerSnapshot;
    private PeerIndex _observer;

    // Per-worker data: worker W observes from peer index W's position
    private InterestCollector[] _workerCollectors = null!;
    private PeerSnapshot[] _workerObserverSnapshots = null!;
    private PeerIndex[] _workerObservers = null!;

    [GlobalSetup]
    public void Setup()
    {
        _observer = new PeerIndex(0);
        _peerPositions = new Vector3[PeerCount];
        _snapshotBoard = new SnapshotBoard(PeerCount, ringCapacity: 4);
        _collector = new InterestCollector();

        _cowGrid = new SpatialGrid(CELL_SIZE, PeerCount);
        _linearGrid = new LinearScanGrid(CELL_SIZE, PeerCount);

        IOptions<SpatialHashAreaOfInterestOptions> aoiOptions = Options.Create(new SpatialHashAreaOfInterestOptions
        {
            Tier0Radius = 20f, Tier1Radius = 50f, MaxRadius = 100f, CellSize = CELL_SIZE,
        });

        _cdGrid = new ConcurrentDictSpatialGrid(CELL_SIZE);

        _cowAoi = new SpatialHashAreaOfInterest(_cowGrid, _snapshotBoard, aoiOptions);
        _linearAoi = new LinearScanAoi(_linearGrid, _snapshotBoard, aoiOptions);
        _cdAoi = new ConcurrentDictAoi(_cdGrid, _snapshotBoard, aoiOptions);

        _altPositions = new Vector3[PeerCount];

        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var peer = new PeerIndex(i);
            float x = (int)(i * 7919u % 1001u) - 500f;
            float z = (int)(i * 6271u % 1001u) - 500f;
            var pos = new Vector3(x, 0, z);
            _peerPositions[i] = pos;
            _altPositions[i] = new Vector3(x + CELL_SIZE, 0, z + CELL_SIZE);

            _cowGrid.Set(peer, pos);
            _linearGrid.Set(peer, pos);
            _cdGrid.Set(peer, pos);

            _snapshotBoard.SetActive(peer);

            _snapshotBoard.Publish(peer, new PeerSnapshot(
                Seq: 1, ServerTick: 0, Parcel: 0,
                Position: pos, Velocity: Vector3.Zero,
                RotationY: 0f, MovementBlend: 0f, SlideBlend: 0f,
                HeadYaw: null, HeadPitch: null,
                AnimationFlags: PlayerAnimationFlags.None,
                GlideState: GlideState.PropClosed));
        }

        _observerSnapshot = new PeerSnapshot(
            Seq: 1, ServerTick: 0, Parcel: 0,
            Position: Vector3.Zero, Velocity: Vector3.Zero,
            RotationY: 0f, MovementBlend: 0f, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed);

        // Worker W observes from peer W's actual world position
        _workerCollectors = new InterestCollector[WORKER_COUNT];
        _workerObservers = new PeerIndex[WORKER_COUNT];
        _workerObserverSnapshots = new PeerSnapshot[WORKER_COUNT];

        for (var w = 0; w < WORKER_COUNT; w++)
        {
            _workerCollectors[w] = new InterestCollector();
            _workerObservers[w] = new PeerIndex((uint)w);
            _snapshotBoard.TryRead(new PeerIndex((uint)w), out _workerObserverSnapshots[w]);
        }
    }

    // ── Single-worker baselines ───────────────────────────────────────────────

    [BenchmarkCategory("Read")]
    [Benchmark(Baseline = true)]
    public void LinearScan_GetVisibleSubjects_1W()
    {
        _collector.Clear();
        _linearAoi.GetVisibleSubjects(_observer, in _observerSnapshot, _collector);
    }

    [BenchmarkCategory("Read")]
    [Benchmark]
    public void CopyOnWrite_GetVisibleSubjects_1W()
    {
        _collector.Clear();
        _cowAoi.GetVisibleSubjects(_observer, in _observerSnapshot, _collector);
    }

    [BenchmarkCategory("Read")]
    [Benchmark]
    public void ConcurrentDict_GetVisibleSubjects_1W()
    {
        _collector.Clear();
        _cdAoi.GetVisibleSubjects(_observer, in _observerSnapshot, _collector);
    }

    // ── 4-worker parallel variants ────────────────────────────────────────────

    [BenchmarkCategory("Read")]
    [Benchmark]
    public void LinearScan_GetVisibleSubjects_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            _workerCollectors[w].Clear();

            _linearAoi.GetVisibleSubjects(
                _workerObservers[w], in _workerObserverSnapshots[w], _workerCollectors[w]);
        });
    }

    [BenchmarkCategory("Read")]
    [Benchmark]
    public void CopyOnWrite_GetVisibleSubjects_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            _workerCollectors[w].Clear();

            _cowAoi.GetVisibleSubjects(
                _workerObservers[w], in _workerObserverSnapshots[w], _workerCollectors[w]);
        });
    }

    [BenchmarkCategory("Read")]
    [Benchmark]
    public void ConcurrentDict_GetVisibleSubjects_4W()
    {
        Parallel.For(0, WORKER_COUNT, (int w) =>
        {
            _workerCollectors[w].Clear();

            _cdAoi.GetVisibleSubjects(
                _workerObservers[w], in _workerObserverSnapshots[w], _workerCollectors[w]);
        });
    }

    // ── Write path ────────────────────────────────────────────────────────────

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void LinearScan_Set_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
            _linearGrid.Set(new PeerIndex(i), _peerPositions[i]);
    }

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void CopyOnWrite_Set_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
            _cowGrid.Set(new PeerIndex(i), _peerPositions[i]);
    }

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void LinearScan_Set_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
                _linearGrid.Set(new PeerIndex(i), _peerPositions[i]);
        });
    }

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void CopyOnWrite_Set_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
                _cowGrid.Set(new PeerIndex(i), _peerPositions[i]);
        });
    }

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void ConcurrentDict_Set_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
            _cdGrid.Set(new PeerIndex(i), _peerPositions[i]);
    }

    [BenchmarkCategory("Write")]
    [Benchmark]
    public void ConcurrentDict_Set_4W()
    {
        Parallel.For(0, WORKER_COUNT, (int w) =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
                _cdGrid.Set(new PeerIndex(i), _peerPositions[i]);
        });
    }

    // ── Write path (cell change) ──────────────────────────────────────────────
    // Every call moves all peers to a different cell, forcing actual mutations.

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void LinearScan_SetCellChange_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _linearGrid.Set(pi, _peerPositions[i]);
            _linearGrid.Set(pi, _altPositions[i]);
        }
    }

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void CopyOnWrite_SetCellChange_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _cowGrid.Set(pi, _peerPositions[i]);
            _cowGrid.Set(pi, _altPositions[i]);
        }
    }

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void ConcurrentDict_SetCellChange_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _cdGrid.Set(pi, _peerPositions[i]);
            _cdGrid.Set(pi, _altPositions[i]);
        }
    }

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void LinearScan_SetCellChange_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _linearGrid.Set(pi, _peerPositions[i]);
                _linearGrid.Set(pi, _altPositions[i]);
            }
        });
    }

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void CopyOnWrite_SetCellChange_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _cowGrid.Set(pi, _peerPositions[i]);
                _cowGrid.Set(pi, _altPositions[i]);
            }
        });
    }

    [BenchmarkCategory("WriteCellChange")]
    [Benchmark]
    public void ConcurrentDict_SetCellChange_4W()
    {
        Parallel.For(0, WORKER_COUNT, (int w) =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _cdGrid.Set(pi, _peerPositions[i]);
                _cdGrid.Set(pi, _altPositions[i]);
            }
        });
    }

    // ── Add + Remove ──────────────────────────────────────────────────────────

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void LinearScan_AddRemove_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _linearGrid.Set(pi, _peerPositions[i]);
            _linearGrid.Remove(pi);
        }
    }

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void CopyOnWrite_AddRemove_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _cowGrid.Set(pi, _peerPositions[i]);
            _cowGrid.Remove(pi);
        }
    }

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void ConcurrentDict_AddRemove_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var pi = new PeerIndex(i);
            _cdGrid.Set(pi, _peerPositions[i]);
            _cdGrid.Remove(pi);
        }
    }

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void LinearScan_AddRemove_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _linearGrid.Set(pi, _peerPositions[i]);
                _linearGrid.Remove(pi);
            }
        });
    }

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void CopyOnWrite_AddRemove_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _cowGrid.Set(pi, _peerPositions[i]);
                _cowGrid.Remove(pi);
            }
        });
    }

    [BenchmarkCategory("AddRemove")]
    [Benchmark]
    public void ConcurrentDict_AddRemove_4W()
    {
        Parallel.For(0, WORKER_COUNT, (int w) =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
            {
                var pi = new PeerIndex(i);
                _cdGrid.Set(pi, _peerPositions[i]);
                _cdGrid.Remove(pi);
            }
        });
    }
}

/// <summary>
///     Linear scan grid from PR #2: flat array of per-peer cell keys,
///     accessed via Volatile.Read. No dictionary lookups on write.
/// </summary>
internal sealed class LinearScanGrid(float cellSize, int maxPeers)
{
    private const long NO_CELL = long.MinValue;

    private readonly float inverseCellSize = 1f / cellSize;
    private readonly long[] peerCellKeys = InitKeys(maxPeers);

    public int MaxPeers => maxPeers;

    private static long[] InitKeys(int count)
    {
        var keys = new long[count];
        Array.Fill(keys, NO_CELL);
        return keys;
    }

    public void Set(PeerIndex peer, Vector3 position)
    {
        Volatile.Write(ref peerCellKeys[peer.Value], ComputeKey(position));
    }

    public void Remove(PeerIndex peer)
    {
        Volatile.Write(ref peerCellKeys[peer.Value], NO_CELL);
    }

    public long ReadCellKey(uint index) =>
        Volatile.Read(ref peerCellKeys[index]);

    public long ComputeCellKey(Vector3 position) =>
        ComputeKey(position);

    public bool IsActive(uint index) =>
        Volatile.Read(ref peerCellKeys[index]) != NO_CELL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeKey(Vector3 position) =>
        PackKey((int)MathF.Floor(position.X * inverseCellSize),
            (int)MathF.Floor(position.Z * inverseCellSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int x, int z) =>
        ((long)x << (sizeof(int) * 8)) | (uint)z;
}

/// <summary>
///     Linear scan AOI from PR #2: iterates all peer slots, filters by
///     the observer's 3×3 cell neighbourhood, then checks distance.
/// </summary>
internal sealed class LinearScanAoi : IAreaOfInterest
{
    private readonly LinearScanGrid grid;
    private readonly SnapshotBoard snapshotBoard;
    private readonly float tier0Sq;
    private readonly float tier1Sq;
    private readonly float maxDistanceSq;
    private readonly float cellSize;

    public LinearScanAoi(LinearScanGrid grid, SnapshotBoard snapshotBoard,
        IOptions<SpatialHashAreaOfInterestOptions> optionsContainer)
    {
        this.grid = grid;
        this.snapshotBoard = snapshotBoard;

        SpatialHashAreaOfInterestOptions options = optionsContainer.Value;
        tier0Sq = options.Tier0Radius * options.Tier0Radius;
        tier1Sq = options.Tier1Radius * options.Tier1Radius;
        maxDistanceSq = options.MaxRadius * options.MaxRadius;
        cellSize = options.CellSize;
    }

    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        Vector3 observerPos = observerSnapshot.Position;

        // Build the 3×3 neighbourhood keys
        Span<long> neighborKeys = stackalloc long[9];
        int idx = 0;

        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                neighborKeys[idx++] = grid.ComputeCellKey(
                    new Vector3(observerPos.X + (dx * cellSize), 0, observerPos.Z + (dz * cellSize)));

        for (uint i = 0; i < (uint)grid.MaxPeers; i++)
        {
            if (!grid.IsActive(i)) continue;

            var subject = new PeerIndex(i);
            if (subject == observer) continue;

            long subjectKey = grid.ReadCellKey(i);
            bool inNeighborhood = false;

            for (int n = 0; n < 9; n++)
            {
                if (subjectKey != neighborKeys[n]) continue;
                inNeighborhood = true;
                break;
            }

            if (!inNeighborhood) continue;

            if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot)) continue;

            float distX = subjectSnapshot.Position.X - observerPos.X;
            float distZ = subjectSnapshot.Position.Z - observerPos.Z;
            float distSq = (distX * distX) + (distZ * distZ);

            if (distSq > maxDistanceSq) continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier);
        }
    }
}

/// <summary>
///     ConcurrentDictionary-based grid from PR #2:
///     ConcurrentDictionary&lt;long, ConcurrentDictionary&lt;PeerIndex, byte&gt;&gt; for cells.
/// </summary>
internal sealed class ConcurrentDictSpatialGrid(float cellSize)
{
    private readonly float inverseCellSize = 1f / cellSize;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<PeerIndex, byte>> cells = new();
    private readonly ConcurrentDictionary<PeerIndex, long> peerCellKeys = new();

    public void Set(PeerIndex peer, Vector3 position)
    {
        long key = ComputeKey(position);
        ConcurrentDictionary<PeerIndex, byte> cell = cells.GetOrAdd(key, _ => new ConcurrentDictionary<PeerIndex, byte>());

        if (!cell.TryAdd(peer, 0)) return;

        if (peerCellKeys.TryGetValue(peer, out long prevKey) && prevKey != key)
            if (cells.TryGetValue(prevKey, out ConcurrentDictionary<PeerIndex, byte>? prevCell))
                prevCell.TryRemove(peer, out _);

        peerCellKeys[peer] = key;
    }

    public void Remove(PeerIndex peer)
    {
        if (!peerCellKeys.TryRemove(peer, out long key)) return;

        if (cells.TryGetValue(key, out ConcurrentDictionary<PeerIndex, byte>? cell))
            cell.TryRemove(peer, out _);
    }

    public ConcurrentDictionary<PeerIndex, byte>? GetPeers(Vector3 position)
    {
        cells.TryGetValue(ComputeKey(position), out ConcurrentDictionary<PeerIndex, byte>? result);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeKey(Vector3 position) =>
        PackKey((int)MathF.Floor(position.X * inverseCellSize),
            (int)MathF.Floor(position.Z * inverseCellSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int x, int z) =>
        ((long)x << (sizeof(int) * 8)) | (uint)z;
}

/// <summary>
///     AOI using ConcurrentDictSpatialGrid: iterates peers in 3×3 cell neighbourhood
///     via ConcurrentDictionary lookups.
/// </summary>
internal sealed class ConcurrentDictAoi : IAreaOfInterest
{
    private readonly ConcurrentDictSpatialGrid grid;
    private readonly SnapshotBoard snapshotBoard;
    private readonly float tier0Sq;
    private readonly float tier1Sq;
    private readonly float maxDistanceSq;
    private readonly float cellSize;

    public ConcurrentDictAoi(ConcurrentDictSpatialGrid grid, SnapshotBoard snapshotBoard,
        IOptions<SpatialHashAreaOfInterestOptions> optionsContainer)
    {
        this.grid = grid;
        this.snapshotBoard = snapshotBoard;

        SpatialHashAreaOfInterestOptions options = optionsContainer.Value;
        tier0Sq = options.Tier0Radius * options.Tier0Radius;
        tier1Sq = options.Tier1Radius * options.Tier1Radius;
        maxDistanceSq = options.MaxRadius * options.MaxRadius;
        cellSize = options.CellSize;
    }

    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        Vector3 observerPos = observerSnapshot.Position;

        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            ConcurrentDictionary<PeerIndex, byte>? peers = grid.GetPeers(
                new Vector3(observerPos.X + (dx * cellSize), 0, observerPos.Z + (dz * cellSize)));

            if (peers == null) continue;

            foreach (KeyValuePair<PeerIndex, byte> kvp in peers)
            {
                PeerIndex subject = kvp.Key;
                if (subject == observer) continue;
                if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot)) continue;

                float distX = subjectSnapshot.Position.X - observerPos.X;
                float distZ = subjectSnapshot.Position.Z - observerPos.Z;
                float distSq = (distX * distX) + (distZ * distZ);

                if (distSq > maxDistanceSq) continue;

                PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                    distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

                collector.Add(subject, tier);
            }
        }
    }
}
