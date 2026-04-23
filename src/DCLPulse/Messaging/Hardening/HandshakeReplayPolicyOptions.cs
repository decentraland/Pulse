namespace Pulse.Messaging.Hardening;

public sealed class HandshakeReplayPolicyOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:HandshakeReplay";

    /// <summary>
    ///     When <c>false</c>, the cache admits every handshake without recording it. Use in
    ///     dev / load tests. The cache has no numeric knobs of its own — TTL tracks
    ///     <c>PeerOptions.PendingAuthCleanTimeoutMs</c> and the memory cap derives from
    ///     <c>ENetTransportOptions.MaxPeers</c>, giving a single source of truth for both
    ///     durations and sizes.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
