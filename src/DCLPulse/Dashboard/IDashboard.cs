using Pulse.Metrics;

namespace Pulse.Dashboard;

public interface IDashboard
{
    public void Update(MetricsSnapshot snapshot);
}
