using Pulse.Presence;

namespace Pulse.Clusters;

/// <summary>
///     Outbound feed to the broker — cluster assignments, topology and presence. Every method is a
///     fire-and-forget hand-off: they enqueue and return without waiting on the broker, so a stalled
///     or absent NATS server never slows a tracker pass. Implementations must not throw.
/// </summary>
public interface IClusterFeedPublisher
{
    /// <summary>
    ///     A peer's published (post-debounce) cluster assignment changed.
    /// </summary>
    void PublishClusterChange(string wallet, string clusterId, string realm);

    /// <summary>
    ///     The full cluster topology for a completed pass. Callers must serialize their calls to this
    ///     method — it is not safe to invoke from two threads at once.
    /// </summary>
    void PublishTopology(ClusterPass pass);

    /// <summary>
    ///     One peer's presence changed: it now stands on <paramref name="parcel" /> in
    ///     <paramref name="realm" />, or — with a null <paramref name="parcel" /> — it has left that
    ///     realm/instance. Held one entry per address until the next batch goes out, so a peer that
    ///     crosses several parcels within one <c>Presence:BatchIntervalMs</c> is published once, with
    ///     its latest state (C1.3). <paramref name="address" /> and <paramref name="realm" /> must
    ///     already be lowercase (C1.5).
    /// </summary>
    void PublishParcelChange(string address, string realm, ParcelCoord? parcel);

    /// <summary>
    ///     The full presence state of this server, superseding every undelivered change: the batch it
    ///     produces carries <c>snapshot = true</c>, which tells consumers to replace everything they
    ///     hold for this <c>server_name</c>. Only ever sent in answer to
    ///     <see cref="TryTakeParcelSnapshotRequest" />.
    /// </summary>
    void PublishParcelSnapshot(IReadOnlyList<PeerPresence> presence, PresenceSnapshotReason reason);

    /// <summary>
    ///     Whether the next presence batch has to be a full snapshot rather than a delta — the
    ///     publisher has just started, <c>Presence:SnapshotIntervalMs</c> has elapsed, or the outbox
    ///     evicted a change and so no longer has a complete picture of what consumers hold.
    ///     <para />
    ///     Consumes the request, so it is answered exactly once, by a
    ///     <see cref="PublishParcelSnapshot" /> from the caller that took it. False — the common case
    ///     — means carry on publishing deltas.
    /// </summary>
    bool TryTakeParcelSnapshotRequest(out PresenceSnapshotReason reason);
}
