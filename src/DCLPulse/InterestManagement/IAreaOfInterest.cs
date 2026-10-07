using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     Determines visible subjects, accepting a snapshot and simulation tier for each registration.
///     Implementations must be thread-safe (called from multiple workers concurrently).
/// </summary>
public interface IAreaOfInterest
{
    /// <summary>
    ///     Queries the visible subjects for the given observer.
    ///     The implementation fills the <paramref name="collector" /> with accepted snapshots and tiers.
    /// </summary>
    public void GetVisibleSubjects(
        PeerIndex observer,
        in PeerSnapshot observerSnapshot,
        IInterestCollector collector);

    /// <summary>
    ///     Queries subjects whose snapshots lie in the listener's announced realm and parcel sets.
    /// </summary>
    public void GetVisibleSubjects(
        PeerIndex observer,
        SceneListenerState listener,
        IInterestCollector collector);
}
