using Pulse.Metrics;

namespace Pulse.Dashboard;

public sealed class NullDashboard : IDashboard
{
    public void Update(MetricsSnapshot snapshot) { }
}
