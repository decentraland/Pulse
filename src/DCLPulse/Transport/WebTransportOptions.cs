namespace Pulse.Transport;

/// <summary>
///     Configuration for the WebTransport transport. Bound from the <c>WebTransport</c> config
///     section. The peer pool is <b>not</b> sized here — WebTransport draws from the shared
///     <see cref="Peers.PeerIndexAllocator" /> pool (sized by <see cref="ENetTransportOptions.MaxPeers" />)
///     so both transports share one capacity budget.
/// </summary>
public sealed class WebTransportOptions
{
    public const string SECTION_NAME = "WebTransport";

    /// <summary>Whether to start the WebTransport transport. Disabled by default; ENet is unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>QUIC/UDP bind address handed to the native host, e.g. <c>[::]:7443</c>.</summary>
    public string BindAddr { get; set; } = "[::]:7443";

    /// <summary>PEM-encoded server certificate chain. Takes precedence over <see cref="CertPath" />.</summary>
    public string? CertPem { get; set; }

    /// <summary>PEM-encoded private key. Takes precedence over <see cref="KeyPath" />.</summary>
    public string? KeyPem { get; set; }

    /// <summary>Path to a PEM certificate file, read at startup when <see cref="CertPem" /> is unset.</summary>
    public string? CertPath { get; set; }

    /// <summary>Path to a PEM private-key file, read at startup when <see cref="KeyPem" /> is unset.</summary>
    public string? KeyPath { get; set; }

    /// <summary>Poll timeout handed to the native host per service call, in milliseconds.</summary>
    public uint ServiceTimeoutMs { get; set; } = 1;

    /// <summary>
    ///     Largest framed datagram sent on an unreliable channel. A message that would exceed this
    ///     falls back to the reliable stream. Sized under the QUIC path MTU browsers enforce (~1200 B).
    /// </summary>
    public int MaxDatagramBytes { get; set; } = 1200;

    /// <summary>
    ///     Largest inbound message payload accepted from a stream frame, and the outbound serialization
    ///     buffer size. Frames declaring more are treated as corruption.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 4096;
}
