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
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
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
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
        });

        Assert.That(output, Does.Contain("dcl_pulse_wt_datagrams_dropped_stale_total 7"));
        Assert.That(output, Does.Contain("dcl_pulse_wt_datagrams_dropped_oversize_total 9"));
    }

    /// <summary>
    ///     An outbox eviction and a failed publish are remedied in opposite directions — raise the
    ///     capacity, or fix the broker — so they have to reach the exposition as two independent series.
    ///     A single number for both would send an operator to whichever lever they guessed.
    /// </summary>
    [Test]
    public void Write_NatsLossCounters_AreSeparateSeries()
    {
        var byTransport = new MetricsSnapshot.PerTransportCounters[2];

        string output = Format(new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = byTransport },
            Clusters = new MetricsSnapshot.ClustersSnapshot
            {
                TotalNatsPublished = 11,
                TotalNatsPublishFailed = 6,
                TotalNatsDropped = 4,
            },
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
        });

        Assert.That(output, Does.Contain("dcl_pulse_nats_published_total 11"));
        Assert.That(output, Does.Contain("dcl_pulse_nats_publish_failed_total 6"));
        Assert.That(output, Does.Contain("dcl_pulse_nats_dropped_total 4"));
    }

    /// <summary>
    ///     Buckets reach the formatter non-cumulative and must leave it cumulative, because <c>le</c>
    ///     means "at most this bound". Emitting the raw per-bucket counts would make every
    ///     <c>histogram_quantile</c> silently wrong rather than fail.
    /// </summary>
    [Test]
    public void Write_ClusterSizeHistogram_BucketsAreCumulative()
    {
        // One cluster of size 1, two of size 2, one of size 5 (which lands in le="8").
        long[] buckets = new long[ClusterSizeHistogram.BUCKET_COUNT];
        buckets[0] = 1;
        buckets[1] = 2;
        buckets[3] = 1;

        string output = Format(WithClusters(new MetricsSnapshot.ClustersSnapshot
        {
            ClusterSizeBuckets = buckets,
            ClusterSizeSum = 10,
            ClusterSizeCount = 4,
        }));

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("# TYPE dcl_pulse_cluster_size histogram"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"1\"} 1"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"2\"} 3"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"4\"} 3"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"8\"} 4"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"+Inf\"} 4"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_sum 10"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_count 4"));
        });
    }

    /// <summary>
    ///     The <c>+Inf</c> bucket holds every observation including any that overflowed the top bound,
    ///     so it is the count rather than the running total of the labelled buckets.
    /// </summary>
    [Test]
    public void Write_ClusterSizeHistogram_InfiniteBucketCarriesOverflow()
    {
        long[] buckets = new long[ClusterSizeHistogram.BUCKET_COUNT];
        buckets[0] = 3;
        buckets[ClusterSizeHistogram.BOUNDS.Length] = 2; // above the top bound

        string output = Format(WithClusters(new MetricsSnapshot.ClustersSnapshot
        {
            ClusterSizeBuckets = buckets,
            ClusterSizeCount = 5,
        }));

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain($"dcl_pulse_cluster_size_bucket{{le=\"{ClusterSizeHistogram.BOUNDS[^1]}\"}} 3"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"+Inf\"} 5"));
        });
    }

    /// <summary>
    ///     A snapshot with no histogram — every pre-existing test builds one — must still expose the
    ///     series, or a scrape reads as though the metric vanished. It must also not throw.
    /// </summary>
    [Test]
    public void Write_ClusterSizeHistogram_AbsentBuckets_EmitZeroes()
    {
        string output = Format(WithClusters(new MetricsSnapshot.ClustersSnapshot()));

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"1\"} 0"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_bucket{le=\"+Inf\"} 0"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_count 0"));
        });
    }

    [Test]
    public void Write_ClusterSizeHistogram_EmitsOneSeriesPerBoundPlusInf()
    {
        string output = Format(WithClusters(new MetricsSnapshot.ClustersSnapshot()));
        int emitted = output.Split('\n').Count(line => line.Contains("dcl_pulse_cluster_size_bucket{le="));

        Assert.That(emitted, Is.EqualTo(ClusterSizeHistogram.BOUNDS.Length + 1));
    }

    /// <summary>
    ///     The mean is exported as an aggregatable pair rather than pre-averaged, so both halves have to
    ///     be present for <c>peers / clusters</c> to be answerable.
    /// </summary>
    [Test]
    public void Write_ClusterSizeMean_IsExportedAsAPeersAndCountPair()
    {
        string output = Format(WithClusters(new MetricsSnapshot.ClustersSnapshot
        {
            ClusterCount = 4,
            ClusterPeers = 30,
            ClusterSizeMax = 21,
        }));

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("dcl_pulse_clusters 4"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_peers 30"));
            Assert.That(output, Does.Contain("dcl_pulse_cluster_size_max 21"));
            Assert.That(output, Does.Not.Contain("dcl_pulse_cluster_size_p50"),
                "pre-computed quantiles cannot be aggregated; the histogram replaced them");
        });
    }

    private static MetricsSnapshot WithClusters(MetricsSnapshot.ClustersSnapshot clusters) =>
        new ()
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = new MetricsSnapshot.PerTransportCounters[2] },
            Clusters = clusters,
            IncomingMessages = new ClientMessageCounters(8),
            OutgoingMessages = new ServerMessageCounters(10),
        };

    private static string Format(MetricsSnapshot snap)
    {
        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
