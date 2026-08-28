namespace Pulse.Metrics;

/// <summary>
///     Per-enum-value atomic counters. Thread-safe — written on hot-path threads,
///     read by <see cref="MeterListenerMetricsCollector" />.
/// </summary>
public sealed class EnumCounters<TEnum>(int bucketCount) where TEnum: Enum
{
    private readonly long[] counts = new long[bucketCount];

    /// <summary>
    ///     Sizes the counter array from the enum itself (max defined value + 1),
    ///     so the bucket count never goes stale when <typeparamref name="TEnum" /> grows.
    /// </summary>
    public EnumCounters() : this(DeriveBucketCount()) { }

    public void Increment(TEnum value) =>
        Interlocked.Increment(ref counts[Convert.ToInt32(value)]);

    public long Read(TEnum value) =>
        Interlocked.Read(ref counts[Convert.ToInt32(value)]);

    private static int DeriveBucketCount()
    {
        var max = 0;

        foreach (var value in Enum.GetValues(typeof(TEnum)))
            max = Math.Max(max, Convert.ToInt32(value));

        return max + 1;
    }
}
