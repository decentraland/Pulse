using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Transport.Geo;
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

        PulseMetrics.Transport.PEER_RTT_MS[(int)Continent.Europe].Record(42);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Transport.PeerRttMs, Is.Not.Null);
        Assert.That(after.Transport.PeerRttMs![(int)Continent.Europe].Count - before.Transport.PeerRttMs![(int)Continent.Europe].Count, Is.EqualTo(1));
        Assert.That(after.Transport.PeerRttMs[(int)Continent.Asia].Count, Is.EqualTo(before.Transport.PeerRttMs[(int)Continent.Asia].Count));
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
        // Null-array tolerance: a fully-default snapshot (PeerRttMs == null) must still emit all 7 zeroed series.
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
}
