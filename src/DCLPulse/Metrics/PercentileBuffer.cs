namespace Pulse.Metrics;

/// <summary>
///     Fixed-capacity circular buffer with percentile computation.
///     Not thread-safe — used only from the timer callback.
/// </summary>
internal sealed class PercentileBuffer(int capacity)
{
    private readonly double[] buffer = new double[capacity];
    private int count;
    private int head;

    public void Add(double value)
    {
        buffer[head] = value;
        head = (head + 1) % capacity;

        if (count < capacity)
            count++;
    }

    public PercentileStats ToPercentileStats()
    {
        if (count == 0)
            return default(PercentileStats);

        Span<double> sorted = stackalloc double[count];
        CopyTo(sorted);
        sorted.Sort();

        return new PercentileStats
        {
            P50 = PercentileFromSorted(sorted, 0.50),
            P95 = PercentileFromSorted(sorted, 0.95),
            P99 = PercentileFromSorted(sorted, 0.99),
        };
    }

    private static double PercentileFromSorted(Span<double> sorted, double p)
    {
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private void CopyTo(Span<double> destination)
    {
        if (count < capacity) { buffer.AsSpan(0, count).CopyTo(destination); }
        else
        {
            buffer.AsSpan(head, capacity - head).CopyTo(destination);
            buffer.AsSpan(0, head).CopyTo(destination[(capacity - head)..]);
        }
    }
}
