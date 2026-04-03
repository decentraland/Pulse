using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static readonly Meter METER = new ("DCLPulse", "1.0");
}
