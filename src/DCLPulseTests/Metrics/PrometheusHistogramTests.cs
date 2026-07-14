using Pulse.Metrics;
using System.Text;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class PrometheusHistogramTests
{
    private static string Format(MetricsSnapshot snap)
    {
        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static MetricsSnapshot SnapshotWith(MetricsSnapshot.SimulationSnapshot simulation) =>
        new ()
        {
            // Write() iterates the enum counters unconditionally — they must be non-null.
            IncomingMessages = new ClientMessageCounters(10),
            OutgoingMessages = new ServerMessageCounters(10),
            Simulation = simulation,
        };

    [Test]
    public void Histogram_buckets_are_cumulative_with_inf_equal_to_count()
    {
        var h = new HistogramSnapshot
        {
            UpperBounds = [10L, 20L],
            Counts = [1L, 2L, 3L], // 1 in ≤10, 2 in ≤20, 3 in +Inf
            Count = 6,
            Sum = 100,
        };

        string output = Format(SnapshotWith(new MetricsSnapshot.SimulationSnapshot { DeltaStalenessTier0Ms = h }));

        Assert.That(output, Does.Contain("# TYPE dcl_pulse_delta_staleness_ms histogram"));
        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_bucket{tier=\"0\",le=\"10\"} 1"));
        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_bucket{tier=\"0\",le=\"20\"} 3"));
        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_bucket{tier=\"0\",le=\"+Inf\"} 6"));
        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_sum{tier=\"0\"} 100"));
        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_count{tier=\"0\"} 6"));
    }

    [Test]
    public void Default_histogram_snapshot_writes_empty_series_without_throwing()
    {
        // A default MetricsSnapshot has default HistogramSnapshots (null arrays) —
        // the formatter must not throw and must emit a zeroed +Inf bucket.
        string output = Format(SnapshotWith(default(MetricsSnapshot.SimulationSnapshot)));

        Assert.That(output, Does.Contain("dcl_pulse_delta_staleness_ms_bucket{tier=\"1\",le=\"+Inf\"} 0"));
        Assert.That(output, Does.Contain("dcl_pulse_tick_duration_us_bucket{le=\"+Inf\"} 0"));
    }

    [Test]
    public void Unlabeled_histogram_and_overrun_counter_are_emitted()
    {
        var h = new HistogramSnapshot
        {
            UpperBounds = [50L],
            Counts = [4L, 0L],
            Count = 4,
            Sum = 120,
        };

        string output = Format(SnapshotWith(new MetricsSnapshot.SimulationSnapshot
        {
            TickDurationUs = h,
            TotalTickOverruns = 2,
        }));

        Assert.That(output, Does.Contain("dcl_pulse_tick_duration_us_bucket{le=\"50\"} 4"));
        Assert.That(output, Does.Contain("dcl_pulse_tick_duration_us_sum 120"));
        Assert.That(output, Does.Contain("dcl_pulse_tick_duration_us_count 4"));
        Assert.That(output, Does.Contain("dcl_pulse_tick_overruns_total 2"));
        Assert.That(output, Does.Contain("# TYPE dcl_pulse_outgoing_drain_cycle_us histogram"));
    }
}
