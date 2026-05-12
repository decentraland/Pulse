namespace Pulse.Messaging.Hardening;

public sealed class MovementInputRateLimiterOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:MovementInput";

    /// <summary>
    ///     Sustained rate of accepted <c>PlayerStateInput</c> messages per peer, in messages
    ///     per second. Refills the token bucket at one token per <c>1000/MaxHz</c> ms. Server
    ///     tick rate is 20 Hz by default, so the client gains nothing by sending faster than
    ///     this. Zero disables the limit.
    /// </summary>
    public int MaxHz { get; set; } = 20;

    /// <summary>
    ///     Maximum number of inputs a peer can send in a burst before waiting for the bucket
    ///     to refill at <see cref="MaxHz" />. Absorbs UDP jitter — packets sent evenly by the
    ///     client routinely arrive in tight clusters after ISP/NAT/Wi-Fi queueing, and the
    ///     owning worker drains its incoming channel in batches. Default 16 tolerates ~800 ms
    ///     of stall-then-burst at the default 20 Hz sustained rate. Stored as a byte per peer,
    ///     so values above 255 are clamped on startup.
    /// </summary>
    public int BurstCapacity { get; set; } = 16;
}
