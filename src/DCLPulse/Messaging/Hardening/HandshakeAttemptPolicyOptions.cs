namespace Pulse.Messaging.Hardening;

public sealed class HandshakeAttemptPolicyOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:Handshake";

    /// <summary>
    ///     Maximum number of handshake attempts a single peer may make before the policy forces
    ///     disconnect. The counter lives on the peer's <c>PeerState.TransportState</c> and is
    ///     scoped to the peer's lifetime — a reconnect starts fresh. Default 2 leaves headroom
    ///     for a legitimate retry after a transient failure.
    /// </summary>
    public byte MaxAttempts { get; set; } = 2;
}
