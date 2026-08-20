using Decentraland.Pulse;
using Pulse.Metrics;
using Pulse.Transport;
using System.Text;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Pins the Prometheus exposition output: transport counters carry a <c>transport</c> label per
///     <see cref="TransportId" />, and the WebTransport-specific counters are emitted unlabeled.
/// </summary>
[TestFixture]
public class PrometheusFormatterTests
{
    [Test]
    public void Write_TransportCounters_AreLabeledPerTransport()
    {
        var byTransport = new MetricsSnapshot.PerTransportCounters[2];
        byTransport[(int)TransportId.ENet] = new MetricsSnapshot.PerTransportCounters { TotalPeersConnected = 3, ActivePeers = 2 };
        byTransport[(int)TransportId.WebTransport] = new MetricsSnapshot.PerTransportCounters { TotalPeersConnected = 5, ActivePeers = 4 };

        string output = Format(new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = byTransport },
            WebTransport = new MetricsSnapshot.WebTransportSnapshot
            {
                TotalDatagramsDroppedStale = 7,
                TotalDatagramsDroppedOversize = 9,
            },
            IncomingMessages = new ClientMessageCounters(),
            OutgoingMessages = new ServerMessageCounters(),
        });

        Assert.That(output, Does.Contain("dcl_pulse_peers_connected_total{transport=\"enet\"} 3"));
        Assert.That(output, Does.Contain("dcl_pulse_peers_connected_total{transport=\"webtransport\"} 5"));
        Assert.That(output, Does.Contain("dcl_pulse_active_peers{transport=\"enet\"} 2"));
        Assert.That(output, Does.Contain("dcl_pulse_active_peers{transport=\"webtransport\"} 4"));
    }

    [Test]
    public void Write_WebTransportDropCounters_AreUnlabeled()
    {
        var byTransport = new MetricsSnapshot.PerTransportCounters[2];

        string output = Format(new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = byTransport },
            WebTransport = new MetricsSnapshot.WebTransportSnapshot
            {
                TotalDatagramsDroppedStale = 7,
                TotalDatagramsDroppedOversize = 9,
            },
            IncomingMessages = new ClientMessageCounters(),
            OutgoingMessages = new ServerMessageCounters(),
        });

        Assert.That(output, Does.Contain("dcl_pulse_wt_datagrams_dropped_stale_total 7"));
        Assert.That(output, Does.Contain("dcl_pulse_wt_datagrams_dropped_oversize_total 9"));
    }

    private static string Format(MetricsSnapshot snap)
    {
        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
