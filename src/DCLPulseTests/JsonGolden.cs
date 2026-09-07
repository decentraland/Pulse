using System.Text.Json.Nodes;

namespace DCLPulseTests;

/// <summary>
///     Structural comparison against a contract golden, reporting the JSON path of the first
///     difference rather than dumping two documents and leaving the reader to diff them.
///     <para />
///     Numbers compare with an absolute tolerance of 1e-3, which is what the pack specifies for the
///     fields Pulse computes in <c>float32</c> — <c>position</c>, <c>center</c>, <c>radius</c>. Every
///     other number in these shapes is an integer (counts, parcels, unix milliseconds), and a
///     tolerance below half a unit is exact for those, so one rule covers the whole document without
///     a per-field table that could drift from the contract.
/// </summary>
internal static class JsonGolden
{
    private const double TOLERANCE = 1e-3;

    /// <summary>
    ///     Fails unless <paramref name="actual" /> matches <paramref name="expected" /> exactly.
    ///     <paramref name="allowedExtraKeys" /> names root-level keys the response may carry that the
    ///     golden does not — used only where Pulse's response is a documented superset of the
    ///     contract, so that the extra key has to be named here rather than slipping past.
    /// </summary>
    public static void AssertMatches(
        JsonNode? expected,
        JsonNode? actual,
        string what,
        params string[] allowedExtraKeys)
    {
        var differences = new List<string>();

        Compare(expected, actual, "$", differences, allowedExtraKeys);

        if (differences.Count == 0) return;

        Assert.Fail($"{what} does not match the contract golden:\n  {string.Join("\n  ", differences)}\n"
                  + $"expected: {expected?.ToJsonString() ?? "null"}\nactual:   {actual?.ToJsonString() ?? "null"}");
    }

    private static void Compare(
        JsonNode? expected,
        JsonNode? actual,
        string path,
        List<string> differences,
        string[] allowedExtraKeys)
    {
        switch (expected)
        {
            case null:
                if (actual is not null)
                    differences.Add($"{path}: expected no body, got {actual.ToJsonString()}");

                return;

            case JsonObject expectedObject:
                CompareObjects(expectedObject, actual, path, differences, allowedExtraKeys);

                return;

            case JsonArray expectedArray:
                CompareArrays(expectedArray, actual, path, differences, allowedExtraKeys);

                return;

            default:
                CompareValues(expected, actual, path, differences);

                return;
        }
    }

    private static void CompareObjects(
        JsonObject expected,
        JsonNode? actual,
        string path,
        List<string> differences,
        string[] allowedExtraKeys)
    {
        if (actual is not JsonObject actualObject)
        {
            differences.Add($"{path}: expected an object, got {Describe(actual)}");

            return;
        }

        foreach ((string key, JsonNode? value) in expected)
            Compare(value, actualObject[key], $"{path}.{key}", differences, allowedExtraKeys);

        foreach (string key in actualObject.Select(static entry => entry.Key))
        {
            if (expected.ContainsKey(key)) continue;

            // Extra keys are allowed only at the root, and only when named: a nested shape is the
            // contract verbatim, and an unexpected field inside one is drift.
            if (path == "$" && allowedExtraKeys.Contains(key, StringComparer.Ordinal)) continue;

            differences.Add($"{path}.{key}: not in the contract golden");
        }
    }

    private static void CompareArrays(
        JsonArray expected,
        JsonNode? actual,
        string path,
        List<string> differences,
        string[] allowedExtraKeys)
    {
        if (actual is not JsonArray actualArray)
        {
            differences.Add($"{path}: expected an array, got {Describe(actual)}");

            return;
        }

        if (expected.Count != actualArray.Count)
        {
            differences.Add($"{path}: expected {expected.Count} entries, got {actualArray.Count}");

            return;
        }

        for (var i = 0; i < expected.Count; i++)
            Compare(expected[i], actualArray[i], $"{path}[{i}]", differences, allowedExtraKeys);
    }

    private static void CompareValues(JsonNode expected, JsonNode? actual, string path, List<string> differences)
    {
        if (actual is null)
        {
            differences.Add($"{path}: missing, expected {expected.ToJsonString()}");

            return;
        }

        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Number
         && actual.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            double expectedNumber = expected.GetValue<double>();
            double actualNumber = actual.GetValue<double>();

            if (Math.Abs(expectedNumber - actualNumber) > TOLERANCE)
                differences.Add($"{path}: expected {expectedNumber}, got {actualNumber}");

            return;
        }

        if (!string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal))
            differences.Add($"{path}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
    }

    private static string Describe(JsonNode? node) =>
        node is null ? "nothing" : node.ToJsonString();
}
