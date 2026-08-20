using Pulse.Transport.Hardening;
using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class Hardening
    {
        /// <summary>Tag key for the <c>class</c> dimension carried on <see cref="IP_LIMIT_REFUSED" />.</summary>
        public const string CONNECTION_CLASS_TAG_KEY = "class";

        // Cached per-class tag, indexed by (int)ConnectionClass, so the dimension can be attached to
        // a counter Add() without allocating. The boxed ConnectionClass value is unboxed by
        // MeterListenerMetricsCollector to bucket the measurement.
        private static readonly KeyValuePair<string, object?>[] CONNECTION_CLASS_TAGS = BuildConnectionClassTags();

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

        public static readonly Counter<long> BANNED_REFUSED =
            METER.CreateCounter<long>("pulse.hardening.banned_refused");

        public static readonly Counter<long> CORRUPTED_PACKET =
            METER.CreateCounter<long>("pulse.hardening.corrupted_packet");

        /// <summary>
        ///     Connections refused by the hard per-source-IP concurrent-connection cap, tagged with
        ///     the <see cref="ConnectionClass" /> budget that refused them — a player connect gate
        ///     and a scene-listener promotion gate need different operator responses, so they are
        ///     told apart by label rather than pooled. Distinct from
        ///     <see cref="PRE_AUTH_IP_LIMIT_REFUSED" />, which only counts peers in PENDING_AUTH.
        /// </summary>
        public static readonly Counter<long> IP_LIMIT_REFUSED =
            METER.CreateCounter<long>("pulse.hardening.ip_limit_refused");

        /// <summary>
        ///     Connections that were over the per-IP cap but admitted because the source IP is
        ///     whitelisted. A whitelist entry with a flat zero here is vestigial.
        /// </summary>
        public static readonly Counter<long> IP_LIMIT_WHITELIST_BYPASS =
            METER.CreateCounter<long>("pulse.hardening.ip_limit_whitelist_bypass");

        /// <summary>
        ///     Distinct source IPs currently holding at least one connection — the size of the
        ///     limiter's per-IP count table.
        /// </summary>
        public static readonly UpDownCounter<int> IP_LIMIT_TRACKED_IPS =
            METER.CreateUpDownCounter<int>("pulse.hardening.ip_limit_tracked_ips");

        /// <summary>The cached <c>class</c> tag for <paramref name="connectionClass" />, passed to a counter's <c>Add()</c>.</summary>
        public static KeyValuePair<string, object?> Tag(ConnectionClass connectionClass) =>
            CONNECTION_CLASS_TAGS[(int)connectionClass];

        private static KeyValuePair<string, object?>[] BuildConnectionClassTags()
        {
            var tags = new KeyValuePair<string, object?>[ConnectionClasses.COUNT];

            foreach (ConnectionClass connectionClass in Enum.GetValues<ConnectionClass>())
                tags[(int)connectionClass] = new KeyValuePair<string, object?>(CONNECTION_CLASS_TAG_KEY, connectionClass);

            return tags;
        }
    }
}
