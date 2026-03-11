using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void ObserverViews_CleanedUpWhenPeerDisconnects()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined — creates an entry in observerViews

        Assert.That(simulation.observerViews, Does.ContainKey(observer));

        // Transition observer to DISCONNECTING
        peers[observer].ConnectionState = PeerConnectionState.DISCONNECTING;
        peers[observer].TransportState = peers[observer].TransportState with { DisconnectionTime = 0 };

        // Advance time past PEER_DISCONNECTION_CLEAN_TIMEOUT (5000ms)
        timeProvider.MonotonicTime.Returns(6000u);

        simulation.SimulateTick(peers, tickCounter: 1);

        // The peer should be removed from peers
        Assert.That(peers, Does.Not.ContainKey(observer));

        // observerViews should also be cleaned up
        Assert.That(simulation.observerViews, Does.Not.ContainKey(observer),
            "observerViews leaked — entry not removed when peer disconnected");
    }
}
