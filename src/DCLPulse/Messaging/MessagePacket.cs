using Pulse.Peers;

namespace Pulse.Messaging;

/// <summary>
///     Wraps a transport-layer packet with enough metadata to parse and route it.
/// </summary>
public readonly ref struct MessagePacket(
    ReadOnlySpan<byte> data,
    PeerIndex fromPeer)
{
    public readonly ReadOnlySpan<byte> Data = data;
    public readonly PeerIndex FromPeer = fromPeer;
}
