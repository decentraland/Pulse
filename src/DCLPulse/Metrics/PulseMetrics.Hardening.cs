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
    }
}
