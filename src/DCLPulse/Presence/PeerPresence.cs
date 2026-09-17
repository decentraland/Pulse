namespace Pulse.Presence;

/// <summary>
///     A parcel coordinate, <c>(floor(x / ParcelSize), floor(z / ParcelSize))</c> — named x/y because
///     that is what the platform calls a parcel's two axes.
/// </summary>
public readonly record struct ParcelCoord(int X, int Y);

/// <summary>
///     One peer's presence: the wallet, the realm it is in, and the parcel it stands on. Address and
///     realm are already lowercase, being whatever was last published for that peer.
/// </summary>
public readonly record struct PeerPresence(string Address, string Realm, ParcelCoord Parcel);

/// <summary>
///     Why the publisher is sending a full snapshot instead of a delta — the <c>reason</c> label on
///     <c>dcl_pulse_presence_snapshots_total</c>.
/// </summary>
public enum PresenceSnapshotReason
{
    /// <summary>First batch of the process: consumers have nothing for this server_name yet.</summary>
    Start,

    /// <summary><c>Presence:SnapshotIntervalMs</c> elapsed.</summary>
    Interval,

    /// <summary>The outbox evicted an undelivered change, so the delta stream is known-lossy.</summary>
    Eviction,
}
