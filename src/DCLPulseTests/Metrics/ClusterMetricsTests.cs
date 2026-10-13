using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using System.Text;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Guards the stage an instrument declaration silently skips: a <see cref="PulseMetrics" /> counter
///     without a matching case in <see cref="MeterListenerMetricsCollector" /> never reaches the
///     snapshot, and one missing from <c>PrometheusFormatter</c> never reaches <c>/metrics</c>. Neither
///     is a compile error, so both are pinned here.
/// </summary>
[TestFixture]
public class ClusterMetricsTests
{
    private MeterListenerMetricsCollector collector;

    [SetUp]
    public void SetUp()
    {
        var messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters());
        collector = new MeterListenerMetricsCollector(messagePipe, new ClientMessageCounters(), new ServerMessageCounters());
        collector.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => collector.Dispose();

    [Test]
    public void TakeoverInstrument_ReachesTheClustersSnapshot()
    {
        // Deltas, not absolutes: PulseMetrics instruments are static and shared across the fixture run.
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Clusters.TAKEOVERS.Add(2);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Clusters.TotalTakeovers - before.Clusters.TotalTakeovers, Is.EqualTo(2));
    }

    [Test]
    public void TakeoverCounter_ReachesTheMetricsEndpoint()
    {
        PulseMetrics.Clusters.TAKEOVERS.Add(1);
        MetricsSnapshot snapshot = collector.TakeSnapshot();

        using var buffer = new MemoryStream();

        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            PrometheusFormatter.Write(writer, snapshot);

        // Asserted against the snapshot's own value rather than a literal, since the instrument is
        // static and shared across the fixture run. This is what ties the exported line to the right
        // snapshot field: the metric name alone would still match if the formatter read a sibling.
        Assert.That(Encoding.UTF8.GetString(buffer.ToArray()),
            Does.Contain($"dcl_pulse_cluster_takeovers_total {snapshot.Clusters.TotalTakeovers}"));
    }

    [Test]
    public void AssignmentRequestInstruments_ReachTheirOwnSnapshotFields()
    {
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Nats.ASSIGNMENT_REQUESTS_REJECTED.Add(3);
        PulseMetrics.Nats.ASSIGNMENT_REQUESTS_THROTTLED.Add(5);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(after.Clusters.TotalNatsAssignmentRequestsRejected - before.Clusters.TotalNatsAssignmentRequestsRejected,
                Is.EqualTo(3));
            Assert.That(after.Clusters.TotalNatsAssignmentRequestsThrottled - before.Clusters.TotalNatsAssignmentRequestsThrottled,
                Is.EqualTo(5));
        });
    }

    [Test]
    public void AssignmentRequestCounters_ReachTheMetricsEndpoint()
    {
        PulseMetrics.Nats.ASSIGNMENT_REQUESTS_REJECTED.Add(1);
        PulseMetrics.Nats.ASSIGNMENT_REQUESTS_THROTTLED.Add(2);
        MetricsSnapshot snapshot = collector.TakeSnapshot();

        using var buffer = new MemoryStream();

        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            PrometheusFormatter.Write(writer, snapshot);

        string output = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain(
                $"dcl_pulse_nats_assignment_requests_rejected_total {snapshot.Clusters.TotalNatsAssignmentRequestsRejected}"));
            Assert.That(output, Does.Contain(
                $"dcl_pulse_nats_assignment_requests_throttled_total {snapshot.Clusters.TotalNatsAssignmentRequestsThrottled}"));
        });
    }

    [Test]
    public void RecoveryCapacityGauges_ReachTheirOwnSnapshotFieldsAndEndpoint()
    {
        MetricsSnapshot before = collector.TakeSnapshot();
        PulseMetrics.Clusters.RECOVERY_RETAINED_WALLETS.Add(7);
        PulseMetrics.Clusters.RECOVERY_PENDING_OPERATIONS.Add(3);
        PulseMetrics.Clusters.RECOVERY_CAPACITY_BLOCKED.Add(1);
        try
        {
            MetricsSnapshot after = collector.TakeSnapshot();
            using var buffer = new MemoryStream();
            using (var writer = new StreamWriter(buffer, leaveOpen: true))
                PrometheusFormatter.Write(writer, after);
            string output = Encoding.UTF8.GetString(buffer.ToArray());
            Assert.Multiple(() =>
            {
                Assert.That(after.Clusters.RecoveryRetainedWallets - before.Clusters.RecoveryRetainedWallets, Is.EqualTo(7));
                Assert.That(after.Clusters.RecoveryPendingOperations - before.Clusters.RecoveryPendingOperations, Is.EqualTo(3));
                Assert.That(after.Clusters.RecoveryCapacityBlocked - before.Clusters.RecoveryCapacityBlocked, Is.EqualTo(1));
                Assert.That(output, Does.Contain($"dcl_pulse_cluster_recovery_retained_wallets {after.Clusters.RecoveryRetainedWallets}"));
                Assert.That(output, Does.Contain($"dcl_pulse_cluster_recovery_pending_operations {after.Clusters.RecoveryPendingOperations}"));
                Assert.That(output, Does.Contain($"dcl_pulse_cluster_recovery_capacity_blocked {after.Clusters.RecoveryCapacityBlocked}"));
            });
        }
        finally
        {
            PulseMetrics.Clusters.RECOVERY_RETAINED_WALLETS.Add(-7);
            PulseMetrics.Clusters.RECOVERY_PENDING_OPERATIONS.Add(-3);
            PulseMetrics.Clusters.RECOVERY_CAPACITY_BLOCKED.Add(-1);
        }
        MetricsSnapshot restored = collector.TakeSnapshot();
        Assert.That(restored.Clusters.RecoveryCapacityBlocked, Is.EqualTo(before.Clusters.RecoveryCapacityBlocked));
    }
}
