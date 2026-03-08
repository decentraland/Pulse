using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     Determines which subjects are visible to an observer and at what simulation tier.
///     Implementations must be thread-safe (called from multiple workers concurrently).
/// </summary>
public interface IAreaOfInterest
{
    /// <summary>
    ///     Queries the visible subjects for the given observer.
    ///     The implementation fills the <paramref name="collector" /> with (subject, tier) entries.
    /// </summary>
    public void GetVisibleSubjects(
        PeerIndex observer,
        in PeerSnapshot observerSnapshot,
        IInterestCollector collector);
}
