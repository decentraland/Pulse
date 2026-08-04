using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

/// <summary>
///     Regression guard for the multi-transport routing invariant: the <see cref="TransportId" />
///     stamped on an observer's <see cref="PeerIndex" /> must survive every pass of the simulation
///     fan-out (peerStates key → <c>ProcessVisibleSubjects</c> → the <c>SendXxx</c> helpers →
///     <c>MessagePipe.Send</c>), so each observer's messages are drained on its own transport's
///     channel. Because <see cref="TransportId.ENet" /> is value 0, a lost stamp is indistinguishable
///     from a legitimately-ENet peer and would silently route a WebTransport peer's traffic to the
///     ENet channel where it is dropped — this test is the tripwire for that.
/// </summary>
public partial class PeerSimulationTests
{
    [Test]
    public void SimulateTick_PreservesObserverTransportStamp_RoutesToPerTransportChannel()
    {
        // A second observer stamped WebTransport, alongside the ENet-default `observer` from SetUp.
        var wtObserver = new PeerIndex(2, TransportId.WebTransport);
        peers[wtObserver] = new PeerState(PeerConnectionState.AUTHENTICATED);
        PublishSnapshot(wtObserver, seq: 1);

        // The shared AoI mock makes `subject` visible to every observer, so each gets a first-contact
        // fan-out. Neither observer sees the other.
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 1);

        var wtMessages = new List<OutgoingMessage>();
        while (messagePipe.TryReadOutgoingMessage(TransportId.WebTransport, out OutgoingMessage wt))
            wtMessages.Add(wt);

        var enetMessages = new List<OutgoingMessage>();
        while (messagePipe.TryReadOutgoingMessage(out OutgoingMessage en))
            enetMessages.Add(en);

        Assert.That(wtMessages, Is.Not.Empty,
            "the WebTransport observer's fan-out must arrive on the WebTransport channel");
        foreach (OutgoingMessage msg in wtMessages)
        {
            Assert.That(msg.To, Is.EqualTo(wtObserver));
            Assert.That(msg.To.Transport, Is.EqualTo(TransportId.WebTransport),
                "the observer's transport stamp survived the full simulation pass into MessagePipe");
        }

        Assert.That(enetMessages, Is.Not.Empty,
            "the ENet observer's fan-out must arrive on the ENet channel");
        foreach (OutgoingMessage msg in enetMessages)
        {
            Assert.That(msg.To, Is.EqualTo(observer));
            Assert.That(msg.To.Transport, Is.EqualTo(TransportId.ENet));
        }
    }
}
