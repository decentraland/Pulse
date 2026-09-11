namespace Pulse.Presence;

/// <summary>
///     A parcel coordinate as the presence feed carries it — the pair
///     <c>(floor(x / ParcelSize), floor(z / ParcelSize))</c>, named x/y because that is what the
///     platform calls the two axes of a parcel.
/// </summary>
public readonly record struct ParcelCoord(int X, int Y);

/// <summary>
///     One peer's presence: the wallet, the realm it is in, and the parcel it stands on. Every field
///     is already canonical — the address and realm lowercase — because the value is built from what
///     was last published for that peer.
/// </summary>
public readonly record struct PeerPresence(string Address, string Realm, ParcelCoord Parcel);

/// <summary>
///     Why the publisher is sending a full snapshot instead of a delta. The label on
///     <c>dcl_pulse_presence_snapshots_total</c>, so an operator can tell a routine 60 s refresh from
///     a snapshot forced by loss.
/// </summary>
public enum PresenceSnapshotReason
{
    /// <summary>First batch of the process: consumers have nothing for this server_name yet.</summary>
    Start,

    /// <summary><c>Presence:SnapshotIntervalMs</c> elapsed.</summary>
    Interval,

    /// <summary>
    ///     The outbox evicted an undelivered change, so the delta stream is known-lossy and a
    ///     consumer applying it would hold a peer at a parcel it has left.
    /// </summary>
    Eviction,
}
