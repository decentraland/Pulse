namespace Pulse.Metrics;

/// <summary>
///     Per-enum-value atomic counters. Thread-safe — written on hot-path threads,
///     read by <see cref="MeterListenerMetricsCollector" />.
/// </summary>
public sealed class EnumCounters<TEnum>(int bucketCount) where TEnum: Enum
{
    private readonly long[] counts = new long[bucketCount];

    public void Increment(TEnum value) =>
        Interlocked.Increment(ref counts[Convert.ToInt32(value)]);

    public long Read(TEnum value) =>
        Interlocked.Read(ref counts[Convert.ToInt32(value)]);
}
