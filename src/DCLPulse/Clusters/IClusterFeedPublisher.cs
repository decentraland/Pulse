namespace Pulse.Clusters;

/// <summary>
///     Outbound cluster feed. Both methods are fire-and-forget hand-offs: they enqueue and return
///     without waiting on the broker, so a stalled or absent NATS server never slows a tracker pass.
///     Implementations must not throw.
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
}
