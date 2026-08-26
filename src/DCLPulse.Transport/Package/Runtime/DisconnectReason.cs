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
        ///     Banned platform-wide
        /// </summary>
        BANNED = 5,

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
        INVALID_TELEPORT_FIELD = 13,

        /// <summary>
        ///     A handshake with the same (wallet, timestamp) was already accepted within the
        ///     server's anti-replay window. Terminal — indicates a replayed capture, not a
        ///     legitimate client scenario.
        /// </summary>
        HANDSHAKE_REPLAY_REJECTED = 14,

        /// <summary>
        ///     HandshakeRequest carried a malformed PlayerInitialState (out-of-range parcel,
        ///     non-finite floats, oversized emote id, excessive duration). Terminal — the auth
        ///     chain itself was valid, but the asserted starting state isn't usable.
        /// </summary>
        INVALID_HANDSHAKE_FIELD = 15,

        /// <summary>
        ///     Peer sustained more corrupted packets per second than the transport tolerates.
        ///     Covers both oversized packets (larger than the receive buffer) and protobuf
        ///     parse failures. Terminal — well-formed clients never produce corrupt packets;
        ///     a sustained rate indicates a buggy client, fuzzer, or amplification probe.
        /// </summary>
        PACKET_CORRUPTED = 16,

        /// <summary>
        ///     Hard per-source-IP concurrent-connection cap exceeded. Unlike
        ///     PRE_AUTH_IP_LIMIT_EXHAUSTED, authenticated connections count against this cap, and
        ///     the connection is refused before a PeerIndex is allocated. Retryable with backoff —
        ///     capacity frees as other connections from the same IP close.
        /// </summary>
        IP_CONNECTION_LIMIT_EXCEEDED = 17,

        /// <summary>
        ///     Per-source-IP concurrent scene-listener cap exceeded. Separate from
        ///     IP_CONNECTION_LIMIT_EXCEEDED because the two need different operator fixes — the
        ///     listener budget, not the player one — and because it is refused later, when the
        ///     SCENE_LISTENER_HANDSHAKE validates, rather than at connect. Retryable, but capacity
        ///     only frees when another listener from the same IP disconnects, so retry with a long
        ///     backoff and jitter rather than a tight loop.
        /// </summary>
        SCENE_LISTENER_IP_LIMIT_EXCEEDED = 18,

        /// <summary>
        ///     SceneListenerUpdate carried an invalid AoI (empty rect list, inverted or
        ///     out-of-range rect, or a nominal area over SceneListener:MaxParcels). Terminal —
        ///     same class of client bug as the other INVALID_*_FIELD reasons. The AoI in force
        ///     when the bad update arrived is left untouched.
        /// </summary>
        INVALID_SCENE_LISTENER_FIELD = 19,
    }
}
