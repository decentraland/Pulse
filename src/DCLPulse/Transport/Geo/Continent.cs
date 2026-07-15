namespace Pulse.Transport.Geo;

/// <summary>
///     Coarse peer-geography buckets for RTT metrics. Fixed at 7 values to keep Prometheus
///     label cardinality trivial (7 regions × ~11 buckets ≈ 80 series). Antarctica and
///     unassigned/reserved country codes fold into <see cref="UNKNOWN" />, as do private,
///     unparseable, and unallocated IPs.
/// </summary>
public enum Continent : byte
{
    AFRICA = 0,
    ASIA = 1,
    EUROPE = 2,
    NORTH_AMERICA = 3,
    OCEANIA = 4,
    SOUTH_AMERICA = 5,
    UNKNOWN = 6,
}

public static class Continents
{
    public const int COUNT = 7;

    /// <summary>Prometheus region label per continent — index matches the enum value.</summary>
    public static readonly string[] LABELS = ["af", "as", "eu", "na", "oc", "sa", "unknown"];
}
