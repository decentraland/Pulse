using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Runtime.CompilerServices;

namespace Pulse.InterestManagement;

/// <summary>
///     Zero-alloc buffer for interest query results.
///     Filled by <see cref="IAreaOfInterest" /> implementations, consumed by the simulation loop.
/// </summary>
public interface IInterestCollector
{
    public void Add(PeerIndex subject, PeerViewSimulationTier tier, uint seq, IdentityRegistration identity);

    public void Clear();
}

/// <summary>
///     A single entry in the interest result set.
/// </summary>
public readonly record struct InterestEntry(
    PeerIndex Subject,
    PeerViewSimulationTier Tier,
    uint Seq,
    IdentityRegistration Identity);

/// <summary>
///     Reusable collector that retains the first accepted sequence and registration for each subject.
/// </summary>
public sealed class InterestCollector : IInterestCollector
{
    private readonly HashSet<uint> subjects = new ();

    /// <summary>
    ///     Exposed as concrete List to avoid IReadOnlyList interface dispatch in the hot loop.
    /// </summary>
    public List<InterestEntry> Entries { get; } = new ();

    public int Count => Entries.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(PeerIndex subject, PeerViewSimulationTier tier, uint seq, IdentityRegistration identity)
    {
        if (subjects.Add(subject.Value))
            Entries.Add(new InterestEntry(subject, tier, seq, identity));
    }

    public void Clear()
    {
        Entries.Clear();
        subjects.Clear();
    }
}
