using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    /// <summary>
    ///     Same-wallet reconnect window: a still-active stale PeerIndex carrying the observer's
    ///     own wallet must not surface to the observer as a separate avatar. Reproduces the
    ///     self-ghost the reconnecting client sees while the previous PeerIndex is awaiting
    ///     <c>CleanupDisconnectedPeer</c>.
    /// </summary>
    [Test]
    public void PlayerJoined_NotSentForStaleSubjectSharingObserverWallet()
    {
        const string SHARED_WALLET = "0xSELF_WALLET";

        // Observer is the freshly authenticated PeerIndex; subject is the still-active stale
        // PeerIndex from the prior session — both bound to the same wallet, exactly the state
        // the boards hold between reconnect handshake and CleanupDisconnectedPeer.
        identityBoard.Set(observer, SHARED_WALLET);
        identityBoard.Set(subject, SHARED_WALLET);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> leaked = DrainAllMessages();

        Assert.That(leaked, Is.Empty,
            () => "Observer received unexpected messages for a stale PeerIndex carrying its own " +
                  "wallet (self-ghost on reconnect): " +
                  string.Join(", ", leaked.Select(m => m.Message.MessageCase.ToString())));
    }
}
