using Pulse.InterestManagement;
using System.Runtime.CompilerServices;

namespace Pulse.Peers.Simulation;

internal enum InterestSnapshotReadResult : byte
{
    Retained,
    Evicted,
    Inactive,
}

/// <summary>
///     Reads an accepted sequence and rejects inactive or changed registrations.
/// </summary>
internal static class InterestSnapshotReader
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InterestSnapshotReadResult Read(SnapshotBoard snapshots, IdentityBoard identities,
        in InterestEntry entry, out PeerSnapshot snapshot)
    {
        bool retained = snapshots.TryRead(entry.Subject, entry.Seq, out snapshot);
        // Interest already captured the registration. TryRead rejects inactive slots;
        // this final veto also rejects disconnects and slot reuse during the read.
        if (!IsRegistrationActive(snapshots, identities, entry.Subject, entry.Identity))
            return InterestSnapshotReadResult.Inactive;

        return retained ? InterestSnapshotReadResult.Retained : InterestSnapshotReadResult.Evicted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRegistrationActive(SnapshotBoard snapshots, IdentityBoard identities,
        PeerIndex subject, IdentityRegistration identity) =>
        snapshots.IsActive(subject) && ReferenceEquals(identities.GetIdentity(subject), identity);
}
