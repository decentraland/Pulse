using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Transport.Geo;
using System.Diagnostics.Metrics;
using System.Text;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class PeerRttMetricsTests
{
    [Test]
    public void Rtt_measurement_routes_to_the_matching_continent_histogram()
    {
        var messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10));
        using var collector = new MeterListenerMetricsCollector(messagePipe, new ClientMessageCounters(8), new ServerMessageCounters(10));
        collector.StartAsync(CancellationToken.None);

        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Transport.PEER_RTT_MS[(int)Continent.EUROPE].Record(42);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Transport.PeerRttMs, Is.Not.Null);
        Assert.That(after.Transport.PeerRttMs![(int)Continent.EUROPE].Count - before.Transport.PeerRttMs![(int)Continent.EUROPE].Count, Is.EqualTo(1));
        Assert.That(after.Transport.PeerRttMs[(int)Continent.ASIA].Count, Is.EqualTo(before.Transport.PeerRttMs[(int)Continent.ASIA].Count));
    }

    [Test]
    public void Prometheus_exports_one_block_with_seven_region_series()
    {
        var snap = new MetricsSnapshot
        {
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
            Transport = new MetricsSnapshot.TransportSnapshot
            {
                PeerRttMs =
                [
                    default, default,
                    new HistogramSnapshot { UpperBounds = [15L, 30L], Counts = [1L, 2L, 0L], Count = 3, Sum = 60 },
                    default, default, default, default,
                ],
            },
        };

        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        string output = Encoding.UTF8.GetString(stream.ToArray());

        Assert.That(output, Does.Contain("# TYPE dcl_pulse_peer_rtt_ms histogram"));
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_bucket{region=\"eu\",le=\"15\"} 1"));
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_bucket{region=\"eu\",le=\"30\"} 3"));
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_bucket{region=\"eu\",le=\"+Inf\"} 3"));
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_count{region=\"eu\"} 3"));
        // Default-element tolerance: the array is present (7 elements) but its na element is a
        // default HistogramSnapshot (null arrays); the formatter must still emit na's zeroed
        // series. The fully-null-array case is covered by Prometheus_tolerates_null_rtt_array.
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_bucket{region=\"na\",le=\"+Inf\"} 0"));
    }

    [Test]
    public void Prometheus_tolerates_null_rtt_array()
    {
        var snap = new MetricsSnapshot
        {
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
        };

        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        string output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.That(output, Does.Contain("dcl_pulse_peer_rtt_ms_bucket{region=\"unknown\",le=\"+Inf\"} 0"));
    }

    // Locks the instrument at index i to the label at index i, so a future Continents.LABELS
    // reorder can't silently misroute recordings relative to the collector's hand-written case list.
    [Test]
    public void Peer_rtt_instrument_at_each_index_carries_the_matching_region_label()
    {
        var recorded = new Dictionary<string, long>();

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PulseMetrics.METER.Name && instrument.Name.StartsWith("pulse.transport.peer_rtt_"))
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => recorded[instrument.Name] = value);
        listener.Start();

        for (var i = 0; i < Continents.COUNT; i++)
        {
            long distinct = 100 + i;
            recorded.Clear();

            PulseMetrics.Transport.PEER_RTT_MS[i].Record(distinct);

            string expectedName = $"pulse.transport.peer_rtt_{Continents.LABELS[i]}_ms";
            Assert.That(recorded, Does.ContainKey(expectedName), $"index {i} did not land on {expectedName}");
            Assert.That(recorded[expectedName], Is.EqualTo(distinct));
            Assert.That(recorded.Count, Is.EqualTo(1), $"index {i} recorded on more than one instrument");
        }
    }
}
