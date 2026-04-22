namespace Pulse.Transport;

public sealed class ENetTransportOptions
{
    public const string SECTION_NAME = "Transport";

    public ushort Port { get; set; } = 7777;

    /// <summary>
    ///     Size of the <see cref="Peers.PeerIndexAllocator" /> pool and all array-backed per-peer
    ///     boards (<c>SnapshotBoard</c>, <c>IdentityBoard</c>, <c>ProfileBoard</c>, <c>SpatialGrid</c>).
    ///     Must be ≥ <see cref="MaxConcurrentConnections" />. The headroom between the two absorbs
    ///     slots held in the allocator's pending-recycle grace window during disconnect churn —
    ///     without it, a burst of reconnects can exhaust the pool even though ENet has free slots.
    /// </summary>
    public int MaxPeers { get; set; } = 4095;

    /// <summary>
    ///     Ceiling on concurrent ENet connections. 0 = use <see cref="MaxPeers" />. Set this lower
    ///     than <see cref="MaxPeers" /> (e.g. <c>MaxPeers - ceil(expectedChurnPerSec × graceSeconds)</c>)
    ///     to reserve PeerIndex slots for the grace window and avoid <c>SERVER_FULL</c> refusals
    ///     under heavy disconnect churn.
    /// </summary>
    public int MaxConcurrentConnections { get; set; }

    public int ServiceTimeoutMs { get; set; } = 1000;
    public int BufferSize { get; set; } = 4096;

    public uint PeerTimeoutMs { get; set; } = 30000;

    public int EffectiveMaxConcurrentConnections =>
        MaxConcurrentConnections > 0 ? MaxConcurrentConnections : MaxPeers;
}
