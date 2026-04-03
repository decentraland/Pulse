namespace Pulse.Transport
{
    public enum DisconnectReason
    {
        NONE = 0,
        /// <summary>
        ///     clean shutdown / server stopping
        /// </summary>
        GRACEFUL = 1,

        /// <summary>
        ///     PENDING_AUTH deadline exceeded
        /// </summary>
        AUTH_TIMEOUT = 2,

        /// <summary>
        ///     Handshake validation failed
        /// </summary>
        AUTH_FAILED = 3,

        /// <summary>
        ///     Evicted by newer connection with same player_id
        /// </summary>
        DUPLICATE_SESSION = 4,

        /// <summary>
        ///     Admin Kick
        /// </summary>
        KICKED = 5,
        SERVER_FULL = 6,
    }
}
