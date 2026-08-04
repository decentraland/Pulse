namespace Pulse.Transport
{
    /// <summary>
    ///     Identifies the transport that owns a peer, stamped onto a <c>PeerIndex</c> when the shared
    ///     allocator hands out the slot (see <c>IPeerIndexAllocator.TryAllocate</c>). <c>ENet</c> is the
    ///     default (value 0). Keep the values contiguous from 0.
    /// </summary>
    public enum TransportId : byte
    {
        ENet = 0,
        WebTransport = 1,
    }
}
