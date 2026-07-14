namespace Pulse.Transport.Geo;

/// <summary>
///     Coarse peer-geography buckets for RTT metrics. Fixed at 7 values to keep Prometheus
///     label cardinality trivial (7 regions × ~11 buckets ≈ 80 series). Antarctica and
///     unassigned/reserved country codes fold into <see cref="Unknown" />, as do private,
///     unparseable, and unallocated IPs.
/// </summary>
public enum Continent : byte
{
    Africa = 0,
    Asia = 1,
    Europe = 2,
    NorthAmerica = 3,
    Oceania = 4,
    SouthAmerica = 5,
    Unknown = 6,
}

public static class Continents
{
    public const int COUNT = 7;

    /// <summary>Prometheus region label per continent — index matches the enum value.</summary>
    public static readonly string[] LABELS = ["af", "as", "eu", "na", "oc", "sa", "unknown"];
}
