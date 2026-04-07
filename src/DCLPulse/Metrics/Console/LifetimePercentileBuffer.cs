namespace Pulse.Metrics.Console;

/// <summary>
///     Unbounded buffer that accumulates all samples since creation for lifetime percentile computation.
///     Uses a reusable sort buffer to avoid per-tick allocations.
///     Not thread-safe — used only from the timer callback.
/// </summary>
internal sealed class LifetimePercentileBuffer
{
    private readonly List<double> values = new ();
    private double[]? sortBuffer;

    public void Add(double value) =>
        values.Add(value);

    public PercentileStats ToPercentileStats()
    {
        int count = values.Count;

        if (count == 0)
            return default(PercentileStats);

        if (sortBuffer == null || sortBuffer.Length < count)
            sortBuffer = new double[Math.Max(count, 256)];

        values.CopyTo(sortBuffer);
        Array.Sort(sortBuffer, 0, count);

        Span<double> span = sortBuffer.AsSpan(0, count);

        return new PercentileStats
        {
            P50 = span[PercentileIndex(count, 0.50)],
            P95 = span[PercentileIndex(count, 0.95)],
            P99 = span[PercentileIndex(count, 0.99)],
        };
    }

    private static int PercentileIndex(int count, double p) =>
        Math.Clamp((int)Math.Ceiling(p * count) - 1, 0, count - 1);
}
