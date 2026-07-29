using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using Decentraland.Pulse;

namespace DCLPulseBenchmarks;

/// <summary>
///     Cost of one <see cref="ClusterTracker" /> pass — the 1 Hz off-hot-path job that derives cluster
///     membership by union-find over each realm's occupied <see cref="SpatialGrid" /> cells — across the
///     population shapes in <see cref="ClusterScenario" />. Every scenario puts its peers in one realm,
///     so these figures do not exercise the per-realm loop's overhead, only its per-cell savings.
///     <para />
///     <b>Which number to read.</b> <see cref="Pass" /> repeats over an unchanging grid, so its working
///     set is in cache; a second of tick and packet traffic between two production passes evicts all of
///     it. <see cref="PassWithChurn" /> is the realistic figure — it moves peers first, dirtying the
///     grid and cold-starting the caches — and includes the movement cost, so subtract
///     <see cref="Churn" />. At the 4095 ceiling that is ~494 us cold against ~321 us warm. Prefer
///     <see cref="Pass" /> for regression detection (much lower variance) and the cold figure when
///     quoting a real cost.
///     <para />
///     <b>Geometry.</b> <see cref="CELL_SIZE" /> matches <c>SpatialHashAreaOfInterest:CellSize</c> in
///     appsettings.json, and worlds are Genesis City sized. The scenarios were drawn at a 50-unit cell
///     size, where the join band was 0–141 units against 0–283 at 100, so regions documented as staying
///     split may merge here — setup prints the realized cluster count per scenario.
///     <para />
///     <b>Churn model.</b> Walkers are leashed to their starting point (<see cref="LEASH" />) rather
///     than travelling, so a long run cannot dissolve the scenario it is measuring.
///     <para />
///     <b>Measured and rejected.</b> Narrowing the per-peer board read to the handful of fields a pass
///     consumes (position, parcel, teleport flag — and realm, which the pass read at the time) measured
///     at parity with the full <see cref="PeerSnapshot" /> read — 8.09 us vs 8.17 us for 4095 peers.
///     Those fields are scattered across the struct, so a narrowed read touches the same cache lines.
///     Do not re-try it without new evidence.
/// </summary>
[MemoryDiagnoser]
public class ClusterTrackerBenchmarks
{
    // Matches SpatialHashAreaOfInterest:CellSize in appsettings.json. Note the class default in
    // SpatialHashAreaOfInterestOptions is still 50 — configuration is what ships.
    private const float CELL_SIZE = 100f;

    // Genesis City is roughly 300x300 parcels of 16 units.
    private const float WORLD_SIZE = 4800f;

    private const int RING_CAPACITY = 4;
    private const string REALM = "main";

    // Passes per PassWithChurn invocation, so the reported mean is per pass.
    private const int CHURN_PASSES = 8;

    // A walking avatar covers a few units per second, only a fraction of a population moves at any
    // moment, and each mover stays within LEASH of where it started.
    private const float MOVING_FRACTION = 0.4f;
    private const float SPEED_PER_PASS = 4f;
    private const float LEASH = 60f;

    [ParamsAllValues]
    public ClusterScenario Scenario { get; set; }

    private ClusterTracker tracker = null!;
    private RealmSpatialGrids grids = null!;
    private SnapshotBoard snapshotBoard = null!;

    private Vector3[] positions = null!;
    private Vector3[] homes = null!;
    private Vector2[] headings = null!;
    private bool[] moving = null!;
    private uint[] seq = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Deterministic seed, so a regression is a real regression and not a different world.
        var random = new Random(1234);

        homes = BuildWorld(Scenario, random);
        positions = (Vector3[])homes.Clone();

        var peerCount = positions.Length;
        var identityBoard = new IdentityBoard(peerCount);
        var clusterBoard = new ClusterBoard();

        grids = new RealmSpatialGrids(CELL_SIZE, peerCount);
        snapshotBoard = new SnapshotBoard(peerCount, RING_CAPACITY);
        headings = new Vector2[peerCount];
        moving = new bool[peerCount];
        seq = new uint[peerCount];

        var options = new OptionsWrapper<ClusterOptions>(new ClusterOptions
        {
            Enabled = true,
            PassIntervalMs = 1000,
            DwellPasses = 3,
            IdPrefix = "C",
        });

        tracker = new ClusterTracker(
            NullLogger<ClusterTracker>.Instance,
            options,
            grids,
            snapshotBoard,
            identityBoard,
            clusterBoard,
            new NoOpFeedPublisher(),
            peerCount);

        for (var i = 0; i < peerCount; i++)
        {
            var peer = new PeerIndex((uint)i);

            double angle = random.NextDouble() * Math.PI * 2;
            headings[i] = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            moving[i] = random.NextSingle() < MOVING_FRACTION;

            snapshotBoard.SetActive(peer);
            identityBoard.Set(peer, $"0xwallet{i}");
            grids.Set(peer, REALM, positions[i]);
            Publish(i);
        }

        // Settle sticky IDs and published assignments, so the first measured pass is a steady-state one
        // rather than the first-assignment path.
        for (var i = 0; i < 4; i++)
            tracker.RunPass();

        ReportTopology(clusterBoard);
    }

    /// <summary>
    ///     One pass over an unchanging grid, everything in cache. Low variance, so it is the sensitive
    ///     regression check — see the class remarks before quoting it as a real cost.
    /// </summary>
    [Benchmark(Description = "Pass, warm working set")]
    public void Pass()
    {
        tracker.RunPass();
    }

    /// <summary>
    ///     A pass preceded by a second of movement, so the pass runs against a dirtied grid and an
    ///     evicted working set. Includes the movement cost — subtract <see cref="Churn" />.
    /// </summary>
    [Benchmark(Description = "Pass + churn, cold working set", OperationsPerInvoke = CHURN_PASSES)]
    public void PassWithChurn()
    {
        for (var i = 0; i < CHURN_PASSES; i++)
        {
            MoveWalkers();
            tracker.RunPass();
        }
    }

    /// <summary>
    ///     The movement alone, to be subtracted from <see cref="PassWithChurn" />: a grid update and a
    ///     snapshot publish per mover, which is ordinary server work rather than tracker cost.
    /// </summary>
    [Benchmark(Description = "Churn only (subtrahend)", OperationsPerInvoke = CHURN_PASSES)]
    public void Churn()
    {
        for (var i = 0; i < CHURN_PASSES; i++)
            MoveWalkers();
    }

    /// <summary>
    ///     Advances every moving peer by one pass worth of travel, turning it back when it reaches
    ///     <see cref="LEASH" /> from where it started so the scenario's topology survives the run.
    /// </summary>
    private void MoveWalkers()
    {
        for (var i = 0; i < positions.Length; i++)
        {
            if (!moving[i]) continue;

            var next = new Vector3(
                positions[i].X + headings[i].X * SPEED_PER_PASS,
                0,
                positions[i].Z + headings[i].Y * SPEED_PER_PASS);

            if (Vector3.DistanceSquared(next, homes[i]) > LEASH * LEASH)
            {
                headings[i] = -headings[i];
                next = positions[i];
            }

            positions[i] = next;

            grids.Set(new PeerIndex((uint)i), REALM, next);
            Publish(i);
        }
    }

    /// <summary>
    ///     Prints what the scenario actually built, so a documented cluster count that no longer holds
    ///     at the configured cell size shows up in the log.
    /// </summary>
    private void ReportTopology(ClusterBoard clusterBoard)
    {
        var cells = new HashSet<long>();

        foreach (var position in positions)
            cells.Add(SpatialGrid.PackKey(
                (int)MathF.Floor(position.X / CELL_SIZE), (int)MathF.Floor(position.Z / CELL_SIZE)));

        IReadOnlyList<ClusterInfo> clusters = clusterBoard.Current.Clusters;
        var largest = 0;

        foreach (ClusterInfo cluster in clusters)
            largest = Math.Max(largest, cluster.Count);

        Console.WriteLine(
            $"// {Scenario}: peers={positions.Length} occupiedCells={cells.Count} "
            + $"clusters={clusters.Count} largest={largest}");
    }

    private void Publish(int peer)
    {
        snapshotBoard.Publish(new PeerIndex((uint)peer), new PeerSnapshot(
            Seq: ++seq[peer],
            ServerTick: 0,
            Parcel: 0,
            PositionX: 0,
            PositionY: 0,
            PositionZ: 0,
            VelocityX: 0,
            VelocityY: 0,
            VelocityZ: 0,
            GlobalPosition: positions[peer],
            RotationY: 0,
            JumpCount: 0,
            MovementBlend: 0,
            SlideBlend: 0,
            HeadYaw: null,
            HeadPitch: null,
            PointAt: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Realm: REALM));
    }

    private static Vector3[] BuildWorld(ClusterScenario scenario, Random random) =>
        scenario switch
        {
            ClusterScenario.Sporadic => BuildSporadic(random),
            ClusterScenario.DenseAndSparse => BuildDenseAndSparse(random),
            ClusterScenario.Chained => BuildChained(random),
            ClusterScenario.CeilingUniform => BuildCeilingUniform(random),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unhandled scenario."),
        };

    /// <summary>
    ///     100 peers: areas A and B overlap into one cluster of 32, C and D sit far enough apart to
    ///     stay two, one duo, and 41 loners on a grid spaced well beyond the join band.
    /// </summary>
    private static Vector3[] BuildSporadic(Random random)
    {
        var peers = new List<Vector3>(100);

        AddDisc(peers, random, new Vector3(600, 0, 3600), radius: 60, count: 20);
        AddDisc(peers, random, new Vector3(720, 0, 3600), radius: 60, count: 12);

        AddDisc(peers, random, new Vector3(2000, 0, 3600), radius: 60, count: 15);
        AddDisc(peers, random, new Vector3(2500, 0, 3600), radius: 60, count: 10);

        AddDisc(peers, random, new Vector3(3500, 0, 3600), radius: 20, count: 2);

        for (var loner = 0; loner < 41; loner++)
            peers.Add(new Vector3(200 + (loner % 7 * 400), 0, 200 + (loner / 7 * 400)));

        return peers.ToArray();
    }

    /// <summary>
    ///     1 000 peers: a 450-peer crowd, a close pair merging into 160, a chained pair merging into
    ///     80, and six standalone regions — the shape behind the documented 9 clusters.
    /// </summary>
    private static Vector3[] BuildDenseAndSparse(Random random)
    {
        var peers = new List<Vector3>(1000);

        AddDisc(peers, random, new Vector3(1000, 0, 1000), radius: 180, count: 450);

        // Merges into one cluster of 160.
        AddDisc(peers, random, new Vector3(2600, 0, 1000), radius: 70, count: 100);
        AddDisc(peers, random, new Vector3(2740, 0, 1000), radius: 70, count: 60);

        // Merges into one cluster of 80.
        AddDisc(peers, random, new Vector3(2600, 0, 2200), radius: 60, count: 50);
        AddDisc(peers, random, new Vector3(2730, 0, 2200), radius: 60, count: 30);

        int[] standaloneCounts = [80, 60, 50, 45, 40, 35];

        Vector3[] standaloneCenters =
        [
            new (600, 0, 3200), new (1600, 0, 3200), new (2600, 0, 3200),
            new (3600, 0, 3200), new (1000, 0, 4200), new (2600, 0, 4200),
        ];

        for (var region = 0; region < standaloneCounts.Length; region++)
            AddDisc(peers, random, standaloneCenters[region], radius: 70, count: standaloneCounts[region]);

        return peers.ToArray();
    }

    /// <summary>
    ///     1 000 peers: 10 areas of 100 spaced so consecutive edges fall inside the join band, giving
    ///     one transitively connected cluster spanning ~1.2 km.
    /// </summary>
    private static Vector3[] BuildChained(Random random)
    {
        var peers = new List<Vector3>(1000);

        for (var area = 0; area < 10; area++)
            AddDisc(peers, random, new Vector3(400 + (area * 130), 0, 2400), radius: 40, count: 100);

        return peers.ToArray();
    }

    private static Vector3[] BuildCeilingUniform(Random random)
    {
        var peers = new Vector3[4095];

        for (var i = 0; i < peers.Length; i++)
            peers[i] = new Vector3(random.NextSingle() * WORLD_SIZE, 0, random.NextSingle() * WORLD_SIZE);

        return peers;
    }

    /// <summary>
    ///     Scatters <paramref name="count" /> peers uniformly over a disc, so density does not pile up at
    ///     the centre the way independent per-axis offsets would.
    /// </summary>
    private static void AddDisc(List<Vector3> into, Random random, Vector3 center, float radius, int count)
    {
        for (var i = 0; i < count; i++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            float distance = radius * MathF.Sqrt(random.NextSingle());

            into.Add(new Vector3(
                center.X + (distance * (float)Math.Cos(angle)),
                0,
                center.Z + (distance * (float)Math.Sin(angle))));
        }
    }

    private sealed class NoOpFeedPublisher : IClusterFeedPublisher
    {
        public void PublishClusterChange(string wallet, string clusterId, string realm) { }

        public void PublishTopology(ClusterPass pass) { }
    }
}
