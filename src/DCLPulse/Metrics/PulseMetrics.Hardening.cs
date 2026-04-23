using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class Hardening
    {
        public static readonly Counter<long> PRE_AUTH_IP_LIMIT_REFUSED =
            METER.CreateCounter<long>("pulse.hardening.pre_auth_ip_limit_refused");

        public static readonly Counter<long> PRE_AUTH_REFUSED =
            METER.CreateCounter<long>("pulse.hardening.pre_auth_refused");

        public static readonly UpDownCounter<int> PRE_AUTH_IN_FLIGHT =
            METER.CreateUpDownCounter<int>("pulse.hardening.pre_auth_in_flight");

        public static readonly Counter<long> HANDSHAKE_ATTEMPTS_EXCEEDED =
            METER.CreateCounter<long>("pulse.hardening.handshake_attempts_exceeded");

        public static readonly Counter<long> INPUT_RATE_THROTTLED =
            METER.CreateCounter<long>("pulse.hardening.input_rate_throttled");

        public static readonly Counter<long> DISCRETE_EVENT_THROTTLED =
            METER.CreateCounter<long>("pulse.hardening.discrete_event_throttled");

        public static readonly Counter<long> FIELD_VALIDATION_FAILED =
            METER.CreateCounter<long>("pulse.hardening.field_validation_failed");

        public static readonly Counter<long> HANDSHAKE_REPLAY_REJECTED =
            METER.CreateCounter<long>("pulse.hardening.handshake_replay_rejected");
    }
}
