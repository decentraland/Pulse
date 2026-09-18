namespace Pulse.Stats;

/// <summary>
///     A parsed query string that keeps every value of a repeated key
///     (<c>/peers?id=0x…&amp;id=0x…</c>), and the raw text beside them, since a redirect has to echo
///     the query back exactly as it arrived rather than reconstruct it.
/// </summary>
public readonly struct StatsQuery
{
    private static readonly string[] NONE = [];

    private readonly Dictionary<string, List<string>>? values;

    private StatsQuery(string raw, Dictionary<string, List<string>>? values)
    {
        Raw = raw;
        this.values = values;
    }

    /// <summary>The query as received, without the leading <c>?</c>. Empty when there was none.</summary>
    public string Raw { get; }

    public static StatsQuery Parse(string? rawQuery)
    {
        string raw = rawQuery is null ? string.Empty : rawQuery.TrimStart('?');

        if (raw.Length == 0)
            return new StatsQuery(string.Empty, values: null);

        var parsed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Range range in raw.AsSpan().Split('&'))
        {
            ReadOnlySpan<char> pair = raw.AsSpan(range);

            if (pair.IsEmpty) continue;

            int equals = pair.IndexOf('=');
            string key = Decode(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);

            if (!parsed.TryGetValue(key, out List<string>? bucket))
                parsed[key] = bucket = [];

            bucket.Add(value);
        }

        return new StatsQuery(raw, parsed);
    }

    /// <summary>Whether the key was present at all, with or without a value.</summary>
    public bool Has(string key) =>
        values is not null && values.ContainsKey(key);

    /// <summary>Every value given for the key, in the order they appeared.</summary>
    public IReadOnlyList<string> All(string key) =>
        values is not null && values.TryGetValue(key, out List<string>? bucket) ? bucket : NONE;

    private static string Decode(ReadOnlySpan<char> value) =>
        Uri.UnescapeDataString(value.ToString().Replace('+', ' '));
}
