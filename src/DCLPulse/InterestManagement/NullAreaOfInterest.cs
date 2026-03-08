using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     Placeholder that reports no visible subjects.
///     Replace with a real implementation (e.g. distance-based, spatial hash) to enable simulation.
/// </summary>
public sealed class NullAreaOfInterest : IAreaOfInterest
{
    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        // No subjects visible — simulation produces no output.
    }
}
