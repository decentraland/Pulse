using System.Text.Json.Nodes;

namespace DCLPulseTests;

/// <summary>
///     Structural comparison against a contract golden, reporting the JSON path of each difference
///     rather than dumping two documents for the reader to diff.
///     <para />
///     A golden <c>null</c> means "present and null" — a response missing the key is a difference,
///     not a match. A golden with no <c>body</c> at all still means "no body".
///     <para />
///     Numbers compare with an absolute tolerance of 1e-3, the pack's figure for the <c>float32</c>
///     fields (<c>position</c>, <c>center</c>, <c>radius</c>). Everything else in these shapes is an
///     integer, for which a sub-half-unit tolerance is exact — so one rule covers the document and
///     no per-field table can drift from the contract.
/// </summary>
internal static class JsonGolden
{
    private const double TOLERANCE = 1e-3;

    /// <summary>
    ///     Fails unless <paramref name="actual" /> matches <paramref name="expected" /> exactly.
    ///     <paramref name="allowedExtraKeys" /> names root-level keys the response may carry and the
    ///     golden does not, so a documented superset has to be spelled out rather than slip past.
    /// </summary>
    public static void AssertMatches(
        JsonNode? expected,
        JsonNode? actual,
        string what,
        params string[] allowedExtraKeys)
    {
        List<string> differences = Differences(expected, actual, allowedExtraKeys);

        if (differences.Count == 0) return;

        Assert.Fail($"{what} does not match the contract golden:\n  {string.Join("\n  ", differences)}\n"
                  + $"expected: {expected?.ToJsonString() ?? "null"}\nactual:   {actual?.ToJsonString() ?? "null"}");
    }

    /// <summary>
    ///     Every difference between the two documents, as JSON paths. <see cref="AssertMatches" /> is
    ///     this plus a failure message; public on its own so tests can pin which differences the
    ///     harness is able to see at all, which no golden could check.
    /// </summary>
    public static List<string> Differences(JsonNode? expected, JsonNode? actual, params string[] allowedExtraKeys)
    {
        var differences = new List<string>();

        Compare(expected, actual, "$", differences, allowedExtraKeys);

        return differences;
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
                    differences.Add(path == "$"
                        ? $"{path}: expected no body, got {actual.ToJsonString()}"
                        : $"{path}: expected null, got {actual.ToJsonString()}");

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
        {
            // TryGetPropertyValue rather than the indexer: a JSON null is stored as a null JsonNode,
            // so the indexer cannot tell "present, null" from "absent".
            if (!actualObject.TryGetPropertyValue(key, out JsonNode? actualValue))
            {
                differences.Add($"{path}.{key}: missing, the contract golden has {value?.ToJsonString() ?? "null"}");

                continue;
            }

            Compare(value, actualValue, $"{path}.{key}", differences, allowedExtraKeys);
        }

        foreach (string key in actualObject.Select(static entry => entry.Key))
        {
            if (expected.ContainsKey(key)) continue;

            // Only at the root and only when named: a nested shape is the contract verbatim.
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
