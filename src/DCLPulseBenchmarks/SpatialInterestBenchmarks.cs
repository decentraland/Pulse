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
///     - LinearScan: single Volatile.Read pass over peerCellKeys[maxPeers]
///     - ConcurrentDict: ConcurrentDictionary&lt;long, ConcurrentDictionary&lt;PeerIndex, byte&gt;&gt;
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
    private const float TIER_0_SQ = 20f * 20f;
    private const float TIER_1_SQ = 50f * 50f;
    private const float MAX_DIST_SQ = 100f * 100f;
    private const int WORKER_COUNT = 4;

    [Params(128, 512, 4095)]
    public int PeerCount { get; set; }

    private SpatialGrid _linearGrid = null!;
    private SpatialHashAreaOfInterest _linearAoi = null!;
    private ConcurrentDictSpatialGrid _cdGrid = null!;
    private SnapshotBoard _snapshotBoard = null!;
    private Vector3[] _peerPositions = null!;

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

        _linearGrid = new SpatialGrid(CELL_SIZE, PeerCount);
        _cdGrid = new ConcurrentDictSpatialGrid(CELL_SIZE);

        IOptions<SpatialHashAreaOfInterestOptions> aoiOptions = Options.Create(new SpatialHashAreaOfInterestOptions
        {
            Tier0Radius = 20f, Tier1Radius = 50f, MaxRadius = 100f, CellSize = CELL_SIZE,
        });

        _linearAoi = new SpatialHashAreaOfInterest(_linearGrid, _snapshotBoard, aoiOptions);

        for (uint i = 0; i < (uint)PeerCount; i++)
        {
            var peer = new PeerIndex(i);
            float x = (int)(i * 7919u % 1001u) - 500f;
            float z = (int)(i * 6271u % 1001u) - 500f;
            var pos = new Vector3(x, 0, z);
            _peerPositions[i] = pos;

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
    public void ConcurrentDict_GetVisibleSubjects_1W()
    {
        _collector.Clear();
        Vector3 observerPos = _observerSnapshot.Position;

        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            ConcurrentDictionary<PeerIndex, byte>? peers = _cdGrid.GetPeers(
                new Vector3(observerPos.X + (dx * CELL_SIZE), 0, observerPos.Z + (dz * CELL_SIZE)));

            if (peers == null) continue;

            foreach (KeyValuePair<PeerIndex, byte> kvp in peers)
            {
                PeerIndex subject = kvp.Key;
                if (subject == _observer) continue;
                if (!_snapshotBoard.TryRead(subject, out PeerSnapshot snap)) continue;

                float distX = snap.Position.X - observerPos.X;
                float distZ = snap.Position.Z - observerPos.Z;
                float distSq = (distX * distX) + (distZ * distZ);

                if (distSq > MAX_DIST_SQ) continue;

                PeerViewSimulationTier tier = distSq <= TIER_0_SQ ? PeerViewSimulationTier.TIER_0 :
                    distSq <= TIER_1_SQ ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

                _collector.Add(subject, tier);
            }
        }
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
    public void ConcurrentDict_GetVisibleSubjects_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            _workerCollectors[w].Clear();
            Vector3 observerPos = _workerObserverSnapshots[w].Position;
            PeerIndex observer = _workerObservers[w];

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                ConcurrentDictionary<PeerIndex, byte>? peers = _cdGrid.GetPeers(
                    new Vector3(observerPos.X + (dx * CELL_SIZE), 0, observerPos.Z + (dz * CELL_SIZE)));

                if (peers == null) continue;

                foreach (KeyValuePair<PeerIndex, byte> kvp in peers)
                {
                    PeerIndex subject = kvp.Key;
                    if (subject == observer) continue;
                    if (!_snapshotBoard.TryRead(subject, out PeerSnapshot snap)) continue;

                    float distX = snap.Position.X - observerPos.X;
                    float distZ = snap.Position.Z - observerPos.Z;
                    float distSq = (distX * distX) + (distZ * distZ);

                    if (distSq > MAX_DIST_SQ) continue;

                    PeerViewSimulationTier tier = distSq <= TIER_0_SQ ? PeerViewSimulationTier.TIER_0 :
                        distSq <= TIER_1_SQ ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

                    _workerCollectors[w].Add(subject, tier);
                }
            }
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
    public void ConcurrentDict_Set_1W()
    {
        for (uint i = 0; i < (uint)PeerCount; i++)
            _cdGrid.Set(new PeerIndex(i), _peerPositions[i]);
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
    public void ConcurrentDict_Set_4W()
    {
        Parallel.For(0, WORKER_COUNT, w =>
        {
            for (var i = (uint)w; i < (uint)PeerCount; i += WORKER_COUNT)
                _cdGrid.Set(new PeerIndex(i), _peerPositions[i]);
        });
    }
}

/// <summary>
///     Reference implementation using ConcurrentDictionary&lt;long, ConcurrentDictionary&lt;PeerIndex, byte&gt;&gt;.
///     Mirrors the shape of the old SpatialGrid + GetPeers design with proper thread safety on
///     the cell containers (replaces HashSet with a concurrent equivalent).
/// </summary>
internal sealed class ConcurrentDictSpatialGrid(float cellSize)
{
    private readonly float inverseCellSize = 1f / cellSize;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<PeerIndex, byte>> cells = new ();
    private readonly ConcurrentDictionary<PeerIndex, long> peerCellKeys = new ();

    public void Set(PeerIndex peer, Vector3 position)
    {
        long key = ComputeKey(position);
        ConcurrentDictionary<PeerIndex, byte> cell = cells.GetOrAdd(key, _ => new ConcurrentDictionary<PeerIndex, byte>());

        if (!cell.TryAdd(peer, 0)) return; // already in this cell

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
