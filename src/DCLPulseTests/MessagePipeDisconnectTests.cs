using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     Pins the contract that <c>transport.Disconnect</c> / <c>MessagePipe.SendDisconnect</c>
///     does not invoke the ENet API directly — it enqueues an <see cref="MessagePipe.OutgoingMessage" />
///     on the outgoing channel for the ENet thread to drain. Safe to call from worker threads.
/// </summary>
[TestFixture]
public class MessagePipeDisconnectTests
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
    public void SendDisconnect_EnqueuesOutgoingMessage()
    {
        var peer = new PeerIndex(7);

        pipe.SendDisconnect(peer, DisconnectReason.AUTH_TIMEOUT);

        Assert.That(pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg), Is.True);
        Assert.That(msg.To, Is.EqualTo(peer));
        Assert.That(msg.IsDisconnect, Is.True);
        Assert.That(msg.Disconnect, Is.EqualTo(DisconnectReason.AUTH_TIMEOUT));
    }

    [Test]
    public void SendDisconnect_CountsTowardOutgoingDepth()
    {
        pipe.SendDisconnect(new PeerIndex(1), DisconnectReason.KICKED);
        pipe.SendDisconnect(new PeerIndex(2), DisconnectReason.KICKED);

        Assert.That(pipe.OutgoingQueueDepth, Is.EqualTo(2));

        pipe.TryReadOutgoingMessage(out _);
        Assert.That(pipe.OutgoingQueueDepth, Is.EqualTo(1));
    }

    [Test]
    public void SendDisconnect_DoesNotIncrementMessageTypeCounter()
    {
        // Disconnects carry no ServerMessage, so they must not bump the per-type counter —
        // that would skew the Prometheus / dashboard outgoing-message rates.
        var counters = new ServerMessageCounters(10);
        var localPipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), counters);

        localPipe.SendDisconnect(new PeerIndex(1), DisconnectReason.AUTH_FAILED);

        foreach (Decentraland.Pulse.ServerMessage.MessageOneofCase kind
                 in Enum.GetValues(typeof(Decentraland.Pulse.ServerMessage.MessageOneofCase)))
        {
            Assert.That(counters.Read(kind), Is.EqualTo(0),
                $"Disconnect envelope must not bump ServerMessageCounters[{kind}]");
        }
    }

    [Test]
    public void SendDisconnect_PreservesOrderWithDataSends()
    {
        // Single outgoing channel → FIFO. A reliable response sent before a disconnect must
        // arrive at FlushOutgoing before the disconnect so ENet's DISCONNECT_LATER flushes it.
        var peer = new PeerIndex(3);
        var response = new Decentraland.Pulse.ServerMessage { Handshake = new Decentraland.Pulse.HandshakeResponse { Success = false } };

        pipe.Send(new MessagePipe.OutgoingMessage(peer, response, PacketMode.RELIABLE));
        pipe.SendDisconnect(peer, DisconnectReason.AUTH_FAILED);

        pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage first);
        pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage second);

        Assert.That(first.IsDisconnect, Is.False,
            "Data send must be drained before the disconnect that followed it");
        Assert.That(second.IsDisconnect, Is.True);
    }
}
