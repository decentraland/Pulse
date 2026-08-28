using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class Simulation
    {
        /// <summary>
        ///     Bucket upper bounds (ms) for delta staleness — dense around the 40/80 ms SLO region.
        /// </summary>
        public static readonly long[] STALENESS_BUCKETS_MS = [1, 2, 4, 8, 16, 32, 48, 64, 96, 128, 192, 256, 512, 1024];

        /// <summary>
        ///     Bucket upper bounds (µs) for loop timings — sub-ms resolution up to 100 ms.
        /// </summary>
        public static readonly long[] DURATION_BUCKETS_US = [50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 25_000, 50_000, 100_000];

        /// <summary>
        ///     Publish→fan-out staleness of STATE_DELTA per AoI tier:
        ///     MonotonicTime − target.ServerTick at SendDelta. Indexed by
        ///     PeerViewSimulationTier.Value (clamped by the caller).
        /// </summary>
        public static readonly Histogram<long>[] DELTA_STALENESS_MS =
        [
            METER.CreateHistogram<long>("pulse.sim.delta_staleness_tier0_ms"),
            METER.CreateHistogram<long>("pulse.sim.delta_staleness_tier1_ms"),
            METER.CreateHistogram<long>("pulse.sim.delta_staleness_tier2_ms"),
        ];

        public static readonly Histogram<long> TICK_DURATION_US =
            METER.CreateHistogram<long>("pulse.sim.tick_duration_us");

        public static readonly Counter<long> TICK_OVERRUNS =
            METER.CreateCounter<long>("pulse.sim.tick_overruns");
    }
}
