namespace Pulse.Clusters;

/// <summary>
///     Outbound cluster feed. Both methods are fire-and-forget hand-offs: they enqueue and return
///     without waiting on the broker, so a stalled or absent NATS server never slows a tracker pass.
///     Implementations must not throw.
/// </summary>
public interface IClusterFeedPublisher
{
    /// <summary>
    ///     A peer's published (post-debounce) cluster assignment changed. <paramref name="session" />
    ///     names the session that owns it and, on a takeover, the one it displaced.
    /// </summary>
    void PublishClusterChange(string wallet, string clusterId, string realm, ClusterSession session);

    /// <summary>
    ///     A peer's assignment re-emitted unchanged by the periodic sweep. Carries the same payload as
    ///     <see cref="PublishClusterChange" /> on a separate subject, so a consumer can tell a refresh
    ///     from a real assignment event: a refresh is safe to ignore when the consumer already holds
    ///     the assignment, whereas a change always warrants acting on.
    /// </summary>
    void PublishClusterRefresh(string wallet, string clusterId, string realm, ClusterSession session);

    /// <summary>
    ///     The full cluster topology for a completed pass. Callers must serialize their calls to this
    ///     method — it is not safe to invoke from two threads at once.
    /// </summary>
    void PublishTopology(ClusterPass pass);
}
