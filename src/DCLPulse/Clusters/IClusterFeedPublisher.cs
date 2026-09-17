using Pulse.Presence;

namespace Pulse.Clusters;

/// <summary>
///     Outbound feed to the broker — cluster assignments, topology and presence. Every method enqueues
///     and returns without waiting on the broker, so a stalled or absent NATS server never slows a
///     tracker pass. Must not throw.
/// </summary>
public interface IClusterFeedPublisher
{
    /// <summary>
    ///     A peer's published (post-debounce) cluster assignment changed. <paramref name="session" />
    ///     names the owning session and, on a takeover, the one it displaced.
    /// </summary>
    void PublishClusterChange(string wallet, string clusterId, string realm, ClusterSession session);

    /// <summary>
    ///     The full cluster topology for a completed pass. Callers must serialize their calls.
    /// </summary>
    void PublishTopology(ClusterPass pass);

    /// <summary>
    ///     One peer's presence changed: it now stands on <paramref name="parcel" /> in
    ///     <paramref name="realm" />, or with a null parcel has left it. Coalesced to one entry per
    ///     address per <c>Presence:BatchIntervalMs</c> (C1.3); both strings must be lowercase (C1.5).
    /// </summary>
    void PublishParcelChange(string address, string realm, ParcelCoord? parcel);

    /// <summary>
    ///     The full presence state of this server, superseding every undelivered change; the batch
    ///     carries <c>snapshot = true</c>. Only sent in answer to <see cref="TryTakeParcelSnapshotRequest" />.
    /// </summary>
    void PublishParcelSnapshot(IReadOnlyList<PeerPresence> presence, PresenceSnapshotReason reason);

    /// <summary>
    ///     Whether the next presence batch must be a full snapshot rather than a delta — the publisher
    ///     has just started, <c>Presence:SnapshotIntervalMs</c> has elapsed, or the outbox evicted a
    ///     change. Consumes the request, so exactly one <see cref="PublishParcelSnapshot" /> answers it.
    /// </summary>
    bool TryTakeParcelSnapshotRequest(out PresenceSnapshotReason reason);
}
