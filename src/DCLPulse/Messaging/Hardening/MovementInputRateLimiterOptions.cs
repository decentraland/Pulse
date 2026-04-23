namespace Pulse.Messaging.Hardening;

public sealed class MovementInputRateLimiterOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:MovementInput";

    /// <summary>
    ///     Maximum accepted <c>PlayerStateInput</c> messages per peer per second. Excess inputs
    ///     are dropped silently at the handler entry — ENet's unreliable-sequenced channel
    ///     already drops stale packets by sequence, so this only trims the upper bound the
    ///     server is willing to process. Server tick rate is 20 Hz by default, so the client
    ///     gains nothing by sending faster than this. Zero disables the limit.
    /// </summary>
    public int MaxHz { get; set; } = 20;
}
