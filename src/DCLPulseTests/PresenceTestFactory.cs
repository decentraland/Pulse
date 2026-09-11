using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Presence;

namespace DCLPulseTests;

/// <summary>
///     <see cref="ParcelChangeTracker" /> instances for fixtures that have to construct the peer
///     pipeline but are not testing presence.
/// </summary>
internal static class PresenceTestFactory
{
    public const int MAX_PEERS = 100;

    /// <summary>
    ///     A tracker with the feed off — the shape of a deployment with no broker configured, where
    ///     every entry point is a no-op. What fixtures that only need the pipeline to compile want.
    /// </summary>
    public static ParcelChangeTracker Disabled() =>
        Create(Substitute.For<IClusterFeedPublisher>(), natsUrl: string.Empty);

    /// <summary>
    ///     A tracker publishing to <paramref name="feed" />, for fixtures that assert on the entries
    ///     a pass or a disconnect produces.
    /// </summary>
    public static ParcelChangeTracker Publishing(IClusterFeedPublisher feed, int maxPeers = MAX_PEERS) =>
        Create(feed, natsUrl: "nats://localhost:4222", maxPeers);

    public static ParcelEncoder Encoder() =>
        new (Options.Create(new ParcelEncoderOptions()));

    private static ParcelChangeTracker Create(IClusterFeedPublisher feed, string natsUrl, int maxPeers = MAX_PEERS) =>
        new (
            feed,
            Encoder(),
            Options.Create(new PresenceOptions()),
            Options.Create(new NatsOptions { Url = natsUrl }),
            maxPeers);
}
