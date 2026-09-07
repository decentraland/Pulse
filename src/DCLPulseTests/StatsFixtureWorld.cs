using DCLPulseTests.FeatureFlags;
using Pulse.Peers;
using Pulse.Stats;
using System.Numerics;

namespace DCLPulseTests;

/// <summary>
///     The contract pack's five-peer world, built into real boards by a real clustering pass, with a
///     <see cref="StatsRouter" /> over it. Every <c>http/*.json</c> golden is derived from this state,
///     so one setup answers the whole C2 surface.
///     <para />
///     The wall clock is anchored so that each peer's <c>lastPing</c> and the pass's
///     <c>lastUpdated</c> come out as the pack's exact unix milliseconds: the origin sits 100 ms
///     before T0, each peer's snapshot is published on the monotonic tick that lands on its stamp,
///     and the pass runs at T0 + 30.
///     <para />
///     Placement order is load-bearing. Cluster ids come from one global counter in the order the
///     pass walks realms and cells, so main's two cells are filled before the world's for the ids to
///     come out C1, C2, C3 as the pack has them — which is also the order a real server would produce
///     them in, since Genesis City is populated before any one world.
/// </summary>
internal static class StatsFixtureWorld
{
    /// <summary>The pass the goldens are read from: T0 + 30 ms.</summary>
    public const long PASS_TIME = IterationTwoFixtures.T0 + 30;

    private const long ORIGIN = IterationTwoFixtures.T0 - 100;

    public const string VERSION = "0.0.0-fixture";
    public const string COMMIT_HASH = "0000000";

    /// <summary>The wallet the pack uses for a peer that is never online.</summary>
    public static readonly string OFFLINE_WALLET = IterationTwoFixtures.Wallet(9);

    public static PresenceScenario Build()
    {
        var scenario = new PresenceScenario(unixOriginMs: ORIGIN);

        Place(scenario, 1, "main", tick: 100, parcel: (-1, 0), position: new Vector3(-0.31f, 1.73f, 4.62f));
        Place(scenario, 2, "main", tick: 106, parcel: (147, -3), position: new Vector3(2360.5f, 1.5f, -40.2f));
        Place(scenario, 4, "main", tick: 110, parcel: (-1, 0), position: new Vector3(-5.0f, 0.5f, 10.0f));
        Place(scenario, 5, "main", tick: 120, parcel: (147, -3), position: new Vector3(2355.0f, 0.0f, -35.0f));
        Place(scenario, 3, "cozyfarm.dcl.eth", tick: 86, parcel: (0, 0), position: new Vector3(8.0f, 0.0f, 8.0f));

        scenario.Clock.UnixTimeMs = PASS_TIME;
        scenario.RunPass();

        return scenario;
    }

    /// <summary>
    ///     A router over <paramref name="scenario" />'s boards, with the version and commit hash the
    ///     goldens were generated with — a real build's own values could not appear in a fixture.
    /// </summary>
    public static StatsRouter RouterOver(PresenceScenario scenario) =>
        new (
            scenario.ClusterBoard,
            scenario.SnapshotBoard,
            scenario.ParcelEncoder,
            scenario.Clock,
            new ServiceIdentity(VERSION, COMMIT_HASH),
            FeatureFlagsTestDoubles.Provider());

    private static void Place(
        PresenceScenario scenario,
        int wallet,
        string realm,
        uint tick,
        (int X, int Y) parcel,
        Vector3 position)
    {
        scenario.Clock.MonotonicTime = tick;

        scenario.Place(new PeerIndex((uint)wallet), IterationTwoFixtures.Wallet(wallet), realm,
            parcel.X, parcel.Y, position);
    }
}
