using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class SceneListener
    {
        public static readonly UpDownCounter<int> CONNECTED =
            METER.CreateUpDownCounter<int>("pulse.scene_listener.connected");

        public static readonly Counter<long> FORBIDDEN_MESSAGES_DROPPED =
            METER.CreateCounter<long>("pulse.scene_listener.forbidden_messages_dropped");

        public static readonly Histogram<int> VISIBLE_SUBJECTS =
            METER.CreateHistogram<int>("pulse.scene_listener.visible_subjects");
    }
}
