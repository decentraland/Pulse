using Pulse.Peers;

namespace Pulse.Messaging;

/// <summary>
///     Wraps a transport-layer packet with enough metadata to parse and route it.
///     Call <see cref="Dispose" /> once raw bytes have been consumed to release native memory.
/// </summary>
public readonly ref struct MessagePacket<TTransportPacket>(
    TTransportPacket transportPacket,
    ReadOnlySpan<byte> data,
    PeerId fromPeer)
    where TTransportPacket: IDisposable
{
    /// <summary>Pointer to native packet data. Valid only until <see cref="Dispose" /> is called.</summary>
    public readonly ReadOnlySpan<byte> Data = data;
    public readonly PeerId FromPeer = fromPeer;

    /// <summary>Releases the underlying native ENet packet memory.</summary>
    public void Dispose() =>
        transportPacket.Dispose();
}
