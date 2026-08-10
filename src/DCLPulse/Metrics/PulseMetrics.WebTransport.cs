using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    /// <summary>
    ///     WebTransport-specific counters. These have no ENet analogue — ENet frames its own packets
    ///     and drops stale unreliable-sequenced packets at the transport, whereas WebTransport
    ///     reconstructs both in the channel-semantics layer — so they are not <c>transport</c>-tagged.
    /// </summary>
    public static class WebTransport
    {
        public static readonly Counter<long> DATAGRAMS_DROPPED_STALE =
            METER.CreateCounter<long>("pulse.webtransport.datagrams_dropped_stale");

        public static readonly Counter<long> DATAGRAMS_DROPPED_OVERSIZE =
            METER.CreateCounter<long>("pulse.webtransport.datagrams_dropped_oversize");
    }
}
