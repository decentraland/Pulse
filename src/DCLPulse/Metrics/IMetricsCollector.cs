namespace Pulse.Metrics;

public interface IMetricsCollector
{
    MetricsSnapshot TakeSnapshot();
}