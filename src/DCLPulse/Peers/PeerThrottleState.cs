namespace Pulse.Peers;

/// <summary>
///     Per-peer throttle bookkeeping used by the Messaging/Hardening rate limiters.
///     Kept separate from <see cref="PeerTransportState" /> so gameplay-handler throttling
///     doesn't clutter transport-lifecycle fields. Mutated from the owning worker thread only.
/// </summary>
public readonly record struct PeerThrottleState(
    uint LastInputMs,
    byte DiscreteEventTokens,
    uint DiscreteEventLastRefillMs);
