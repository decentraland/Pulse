using Pulse.Transport;

namespace Pulse.Peers;

/// <summary>
///     Zero-based index into fixed peer array (allocated at host creation with maxPeers).
///     It's up to the Transport Service implementation to ensure Indexing (instead of non-sequential IDs).
///     Always in the range [0, maxPeers). Safe to use as a direct array index.
///     <para />
///     Carries the <see cref="Transport" /> that owns the peer, stamped by the shared allocator when
///     the slot is handed out (see <c>IPeerIndexAllocator.TryAllocate</c>). This is a routing tag: it
///     records the owning transport so it can be read back off the index. It deliberately does <b>not</b> participate in equality, hashing,
///     the implicit <c>uint</c> conversion, or <see cref="ToString" /> — the logical slot
///     (<see cref="Value" />) is the sole identity, so dictionary keys, worker sharding, and the
///     allocator's free-list/pending sets are unchanged by it. Two indexes with the same
///     <see cref="Value" /> but different transports never coexist: one shared allocator hands each
///     slot to exactly one peer at a time.
/// </summary>
public readonly struct PeerIndex(uint value, TransportId transport = TransportId.ENet) : IEquatable<PeerIndex>
{
    public readonly uint Value = value;

    /// <summary>The transport that owns this peer. Defaults to <see cref="TransportId.ENet" />.</summary>
    public readonly TransportId Transport = transport;

    public static implicit operator uint(PeerIndex id) =>
        id.Value;

    public bool Equals(PeerIndex other) =>
        Value == other.Value;

    public override bool Equals(object? obj) =>
        obj is PeerIndex other && Equals(other);

    public override int GetHashCode() =>
        (int)Value;

    public override string ToString() =>
        Value.ToString();

    public static bool operator ==(PeerIndex left, PeerIndex right) =>
        left.Equals(right);

    public static bool operator !=(PeerIndex left, PeerIndex right) =>
        !(left == right);
}
