using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     Zero-alloc buffer for interest query results.
///     Filled by <see cref="IAreaOfInterest" /> implementations, consumed by the simulation loop.
/// </summary>
public interface IInterestCollector
{
    public void Add(PeerIndex subject, PeerViewSimulationTier tier);

    public void Clear();
}

/// <summary>
///     A single entry in the interest result set.
/// </summary>
public readonly record struct InterestEntry(PeerIndex Subject, PeerViewSimulationTier Tier);

/// <summary>
///     List-backed collector. Pre-allocated, reused across ticks to avoid allocation.
/// </summary>
public sealed class InterestCollector : IInterestCollector
{
    /// <summary>
    ///     Exposed as concrete List to avoid IReadOnlyList interface dispatch in the hot loop.
    /// </summary>
    public List<InterestEntry> Entries { get; } = new ();

    public int Count => Entries.Count;

    public void Add(PeerIndex subject, PeerViewSimulationTier tier)
    {
        Entries.Add(new InterestEntry(subject, tier));
    }

    public void Clear()
    {
        Entries.Clear();
    }
}
