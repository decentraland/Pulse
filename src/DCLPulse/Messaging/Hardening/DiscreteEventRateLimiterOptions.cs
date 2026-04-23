namespace Pulse.Messaging.Hardening;

public sealed class DiscreteEventRateLimiterOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:DiscreteEvent";

    /// <summary>
    ///     Sustained rate of discrete events (emote start/stop, teleport) accepted per peer,
    ///     in events per second. Each event triggers an O(observers) reliable broadcast, so
    ///     the cap bounds the fan-out amplification an attacker can drive. Zero disables.
    /// </summary>
    public double RatePerSecond { get; set; } = 5.0;

    /// <summary>
    ///     Maximum number of discrete events a peer can fire in a burst before waiting for the
    ///     bucket to refill at <see cref="RatePerSecond" />. Accommodates reasonable user
    ///     behaviour like chained emote→teleport without sustained-rate violations. Stored as
    ///     a byte per peer, so values above 255 are clamped on startup.
    /// </summary>
    public int BurstCapacity { get; set; } = 10;
}
