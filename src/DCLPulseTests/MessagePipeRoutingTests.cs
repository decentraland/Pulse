using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     Pins the per-transport outgoing routing: <c>MessagePipe.Send</c> picks the channel from the
///     <see cref="TransportId" /> stamped on the recipient's <see cref="PeerIndex" /> — no side
///     registry. A default-constructed index is <see cref="TransportId.ENet" />, so the legacy path
///     and the rest of the suite are unaffected. Routing is a function of the stamp, not the slot
///     value, so identity (equality/hash, keyed by value) and routing stay orthogonal.
/// </summary>
[TestFixture]
public class MessagePipeRoutingTests
{
    private MessagePipe pipe;

    [SetUp]
    public void SetUp()
    {
        pipe = new MessagePipe(
            Substitute.For<ILogger<MessagePipe>>(),
            new ServerMessageCounters(10));
    }

    [Test]
    public void Send_EnetStampedPeer_RoutesToEnetChannel()
    {
        var peer = new PeerIndex(7); // default stamp == ENet

        pipe.SendDisconnect(peer, DisconnectReason.AUTH_TIMEOUT);

        Assert.That(pipe.TryReadOutgoingMessage(TransportId.WebTransport, out _), Is.False,
            "an ENet-stamped peer must not route to the WebTransport channel");
        Assert.That(pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg), Is.True,
            "an ENet-stamped peer routes to the ENet (default) channel");
        Assert.That(msg.To, Is.EqualTo(peer));
    }

    [Test]
    public void Send_WebTransportStampedPeer_RoutesToWebTransportChannel()
    {
        var peer = new PeerIndex(7, TransportId.WebTransport);

        pipe.SendDisconnect(peer, DisconnectReason.AUTH_TIMEOUT);

        Assert.That(pipe.TryReadOutgoingMessage(out _), Is.False,
            "a WebTransport peer's message must not appear on the ENet channel");
        Assert.That(pipe.TryReadOutgoingMessage(TransportId.WebTransport, out MessagePipe.OutgoingMessage msg), Is.True);
        Assert.That(msg.To, Is.EqualTo(peer));
        Assert.That(msg.IsDisconnect, Is.True);
    }

    [Test]
    public void DataSend_WebTransportStampedPeer_RoutesToWebTransportChannel()
    {
        var peer = new PeerIndex(2, TransportId.WebTransport);
        var response = new ServerMessage { Handshake = new HandshakeResponse { Success = true } };

        pipe.Send(new MessagePipe.OutgoingMessage(peer, response, PacketMode.RELIABLE));

        Assert.That(pipe.TryReadOutgoingMessage(out _), Is.False);
        Assert.That(pipe.TryReadOutgoingMessage(TransportId.WebTransport, out MessagePipe.OutgoingMessage msg), Is.True);
        Assert.That(msg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(msg.IsDisconnect, Is.False);
    }

    [Test]
    public void Routing_KeyedByTransportStamp_NotByPeerValue()
    {
        // Same logical slot value, different transport stamp: routing follows the stamp, proving the
        // channel is chosen by TransportId and not by PeerIndex.Value (which drives identity/equality).
        pipe.SendDisconnect(new PeerIndex(5, TransportId.ENet), DisconnectReason.BANNED);
        pipe.SendDisconnect(new PeerIndex(5, TransportId.WebTransport), DisconnectReason.BANNED);

        Assert.That(pipe.TryReadOutgoingMessage(out _), Is.True, "the ENet-stamped send is on the ENet channel");
        Assert.That(pipe.TryReadOutgoingMessage(out _), Is.False, "only one message routed to ENet");
        Assert.That(pipe.TryReadOutgoingMessage(TransportId.WebTransport, out _), Is.True,
            "the WebTransport-stamped send is on the WebTransport channel");
        Assert.That(pipe.TryReadOutgoingMessage(TransportId.WebTransport, out _), Is.False, "only one message routed to WebTransport");
    }

    [Test]
    public void OutgoingQueueDepth_AggregatesAcrossTransports()
    {
        var enetPeer = new PeerIndex(1);
        var wtPeer = new PeerIndex(2, TransportId.WebTransport);

        pipe.SendDisconnect(enetPeer, DisconnectReason.BANNED); // ENet channel
        pipe.SendDisconnect(wtPeer, DisconnectReason.BANNED);   // WebTransport channel

        Assert.That(pipe.OutgoingQueueDepth, Is.EqualTo(2), "depth counts both channels");

        pipe.TryReadOutgoingMessage(out _);                          // drain ENet
        pipe.TryReadOutgoingMessage(TransportId.WebTransport, out _); // drain WebTransport
        Assert.That(pipe.OutgoingQueueDepth, Is.EqualTo(0));
    }
}
