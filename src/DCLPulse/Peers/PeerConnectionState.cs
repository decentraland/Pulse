namespace Pulse.Peers;

public enum PeerConnectionState : byte
{
    NONE,

    /// <summary>
    ///     The peer exists but has no identity
    ///     Handshake is expected
    /// </summary>
    PENDING_AUTH,

    /// <summary>
    ///     The peer has a verified wallet id and is receiving game traffic
    /// </summary>
    AUTHENTICATED,

    /// <summary>
    ///     The server has called <c>transport.Disconnect</c> on this peer but ENet has not yet
    ///     emitted its disconnect event. Messages queued or newly arriving from this peer in
    ///     this window must be skipped (SkipFromUnauthorizedPeer rejects non-AUTHENTICATED).
    ///     Transitions to <see cref="DISCONNECTING" /> when the ENet event lands.
    /// </summary>
    PENDING_DISCONNECT,

    /// <summary>
    ///     Peer is on its way out. The goal is clean state removal without affecting other players
    /// </summary>
    DISCONNECTING,
}
