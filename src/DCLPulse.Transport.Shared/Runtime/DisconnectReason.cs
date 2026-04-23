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

        /// <summary>
        ///     Per-source-IP pre-auth connection cap exceeded. Only PENDING_AUTH connections
        ///     count against the per-IP quota; once a peer authenticates it no longer counts.
        /// </summary>
        PRE_AUTH_IP_LIMIT_EXHAUSTED = 7,

        /// <summary>
        ///     Global pre-auth budget exhausted — too many connections currently in PENDING_AUTH
        /// </summary>
        PRE_AUTH_BUDGET_EXHAUSTED = 8,

        /// <summary>
        ///     Client sent PlayerStateInput faster than the server's MaxHz cap. Indicates a
        ///     misbehaving or malicious client — legitimate clients should not retry blindly.
        /// </summary>
        INPUT_RATE_EXCEEDED = 9,

        /// <summary>
        ///     Client exceeded the token-bucket cap on discrete events (emote start/stop,
        ///     teleport). Indicates a misbehaving or malicious client.
        /// </summary>
        DISCRETE_EVENT_RATE_EXCEEDED = 10,

        /// <summary>
        ///     PlayerStateInput carried an invalid field (e.g. out-of-range parcel index).
        ///     Terminal — client bug or attack, should not auto-retry.
        /// </summary>
        INVALID_INPUT_FIELD = 11,

        /// <summary>
        ///     EmoteStart carried an invalid field (oversized EmoteId, excessive DurationMs,
        ///     out-of-range parcel index). Terminal.
        /// </summary>
        INVALID_EMOTE_FIELD = 12,

        /// <summary>
        ///     TeleportRequest carried an invalid field (oversized Realm, out-of-range parcel
        ///     index, empty realm). Terminal.
        /// </summary>
        INVALID_TELEPORT_FIELD = 13
    }
}
