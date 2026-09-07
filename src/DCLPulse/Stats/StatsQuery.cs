namespace Pulse.Stats;

/// <summary>
///     A parsed query string. Its own type rather than <c>NameValueCollection</c> because two things
///     the stats surface needs are awkward there: a repeated key
///     (<c>/peers?id=0x…&amp;id=0x…</c>) has to keep every value, and a redirect has to echo the
///     query back <em>exactly</em> as it arrived — so the raw text is kept alongside the parsed
///     values instead of being reconstructed from them.
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
