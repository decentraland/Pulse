namespace Pulse.Metrics;

public sealed class MetricsOptions
{
    public const string SECTION_NAME = "Metrics";

    public DashboardType Type { get; set; } = DashboardType.None;
}

public enum DashboardType
{
    None,
    Console,
    Prometheus,
}
