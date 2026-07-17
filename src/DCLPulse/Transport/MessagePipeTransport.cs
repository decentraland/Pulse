using Pulse.Messaging;
using Pulse.Peers;

namespace Pulse.Transport;

/// <summary>
///     The <see cref="ITransport" /> seam used by application code (handshake, simulation, defense)
///     to disconnect a peer. Routes the request through <see cref="MessagePipe" />. The recipient's
///     <see cref="PeerIndex" /> carries its owning transport, so one implementation serves both ENet
///     and WebTransport peers — the disconnect is enqueued on the owning transport's outgoing channel
///     and that transport performs the actual close on its own thread.
/// </summary>
public sealed class MessagePipeTransport(MessagePipe messagePipe) : ITransport
{
    public void Disconnect(PeerIndex pi, DisconnectReason reason) =>
        messagePipe.SendDisconnect(pi, reason);
}
