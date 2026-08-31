namespace Pulse.Transport;

public sealed class ENetTransportOptions
{
    public const string SECTION_NAME = "Transport";

    /// <summary>
    ///     Host to bind the ENet/UDP socket to. Defaults to the IPv4 wildcard <c>0.0.0.0</c> for
    ///     behavior uniform with WebTransport across Windows and Linux. Set to <c>::</c> for the
    ///     IPv6 wildcard; ENet enables dual-stack, so <c>::</c> then also accepts IPv4.
    /// </summary>
    public string BindHost { get; set; } = "0.0.0.0";

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

    /// <summary>
    ///     Per-peer inactivity deadline in milliseconds, applied on the ENet <c>Connect</c> event as
    ///     <c>Peer.Timeout(0, PeerTimeoutMs, PeerTimeoutMs)</c> — this one value becomes both of
    ///     ENet's <c>timeoutMinimum</c> and <c>timeoutMaximum</c>.
    ///     <para />
    ///     ENet measures the deadline from the send time of the oldest unacknowledged reliable
    ///     packet, and its keepalive ping (~500 ms, ENet's default — <c>PingInterval</c> is never
    ///     called) guarantees such a packet exists within half a second of a peer falling silent, so
    ///     a peer that dies is detected roughly this long afterwards rather than anywhere up to it.
    ///     Passing minimum equal to maximum also neutralizes ENet's retransmission-count early path
    ///     (<c>timeoutLimit</c>, which the <c>0</c> argument leaves at ENet's default of 32), so the
    ///     rule collapses to a single flat deadline.
    ///     <para />
    ///     Tradeoff: a network stall longer than this drops the peer — absorbed by the reconnect
    ///     flow, which re-seeds state from <c>HandshakeRequest.PlayerInitialState</c>.
    ///     <para />
    ///     <c>appsettings.Development.json</c> deliberately overrides this to 300000: a paused
    ///     debugger must not drop peers. That override is intentional, not stale.
    /// </summary>
    public uint PeerTimeoutMs { get; set; } = 5000;

    /// <summary>
    ///     Directory containing the geo-whois-asn-country "-num" CSVs used to resolve peer
    ///     IP → continent for RTT metrics. Relative paths resolve against the app base
    ///     directory. Missing files are tolerated: peers report under region="unknown".
    ///     The Docker images fetch the CSVs into this directory at build time.
    /// </summary>
    public string GeoDbDirectory { get; set; } = "geodb";

    public int EffectiveMaxConcurrentConnections =>
        MaxConcurrentConnections > 0 ? MaxConcurrentConnections : MaxPeers;
}
