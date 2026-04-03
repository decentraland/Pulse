namespace Pulse.Formatting;

public static class ByteFormat
{
    private static readonly (double threshold, double divisor, string unit)[] UNITS =
    [
        (1_099_511_627_776, 1_099_511_627_776, "TB"),
        (1_073_741_824, 1_073_741_824, "GB"),
        (1_048_576, 1_048_576, "MB"),
        (1024, 1024, "KB"),
    ];

    public static string Format(double bytes, string? suffix = null)
    {
        foreach ((double threshold, double divisor, string unit) in UNITS)
        {
            if (bytes >= threshold)
            {
                string label = suffix is null ? unit : $"{unit}/{suffix}";
                return $"{bytes / divisor:F2} {label}";
            }
        }

        string baseLabel = suffix is null ? "B" : $"B/{suffix}";
        return $"{bytes:F0} {baseLabel}";
    }

    public static string FormatRate(double bytesPerSec) =>
        Format(bytesPerSec, "s");
}
