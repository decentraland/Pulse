using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

/// <summary>
///     <see cref="ClusterTracker" /> instances for fixtures that have to construct the peer pipeline.
/// </summary>
internal static class PresenceTestFactory
{
    public const int MAX_PEERS = 100;

    private const float CELL_SIZE = 100f;

    /// <summary>
    ///     A tracker with the feed off — no broker configured, so every presence entry point is a
    ///     no-op. Its boards are throwaway; nothing here is exercised beyond satisfying the constructor.
    /// </summary>
    public static ClusterTracker Disabled(IClusterFeedPublisher? feed = null) =>
        Create(feed ?? Substitute.For<IClusterFeedPublisher>(), natsUrl: string.Empty);

    public static ClusterTracker Publishing(IClusterFeedPublisher feed, int maxPeers = MAX_PEERS) =>
        Create(feed, natsUrl: "nats://localhost:4222", maxPeers);

    public static ParcelEncoder Encoder() =>
        new (Options.Create(new ParcelEncoderOptions()));

    private static ClusterTracker Create(IClusterFeedPublisher feed, string natsUrl, int maxPeers = MAX_PEERS) =>
        new (
            NullLogger<ClusterTracker>.Instance,
            Options.Create(new ClusterOptions { Enabled = true, PassIntervalMs = 1000, DwellPasses = 1, IdPrefix = "C" }),
            new RealmSpatialGrids(CELL_SIZE, maxPeers),
            new SnapshotBoard(maxPeers, ringCapacity: 8),
            new IdentityBoard(maxPeers),
            new ClusterBoard(),
            feed,
            Encoder(),
            Options.Create(new NatsOptions { Url = natsUrl }),
            Substitute.For<ITimeProvider>(),
            maxPeers);
}
