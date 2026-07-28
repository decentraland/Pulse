namespace Pulse.Clusters;

/// <summary>
///     Outbound cluster feed. Both methods are fire-and-forget hand-offs: they enqueue and return
///     without waiting on the broker, so a stalled or absent NATS server can never slow the
///     tracker pass down. Implementations must not throw.
/// </summary>
public interface IClusterFeedPublisher
{
    /// <summary>
    ///     A peer's published (post-debounce) cluster assignment changed.
    /// </summary>
    void PublishClusterChange(string wallet, string clusterId, string realm);

    /// <summary>
    ///     The full cluster topology for a completed pass.
    /// </summary>
    void PublishTopology(ClusterPass pass);
}
