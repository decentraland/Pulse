namespace Pulse.Peers;

public static class DiffComparison
{
    private const float TOLERANCE = 0.001f;

    internal static bool FloatEquals(in float a, in float b) =>
        Math.Abs(a - b) < TOLERANCE;

    internal static bool FloatEquals(float? a, float? b, float tolerance = TOLERANCE) =>
        (!a.HasValue && !b.HasValue) || (a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) < tolerance);
}
