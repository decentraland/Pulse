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
    ///     Peer is on its way out. The goal is clean state removal without affecting other players
    /// </summary>
    DISCONNECTING,
}
