using System.Globalization;
using System.Text.Json;

namespace DCL.Auth;

public static class AuthChainParser
{
    private static readonly JsonSerializerOptions JSON_OPTS = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<AuthLink> ParseJsonChain(string authChainJsonArray)
    {
        var links = JsonSerializer.Deserialize<List<AuthLink>>(authChainJsonArray, JSON_OPTS);
        if (links is null || links.Count == 0)
            throw new FormatException("authChain JSON must be a non-empty array.");
        return links;
    }

    /// <summary>
    /// Signed-fetch style: X-Identity-AuthChain-0..N each contains a JSON-serialized AuthLink.  [oai_citation:4‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/signed-fetch)
    /// </summary>
    public static IReadOnlyList<AuthLink> ParseFromSignedFetchHeaders(IReadOnlyDictionary<string, string> headers)
    {
        // TODO: optimize allocations
        var items = new List<(int Index, AuthLink Link)>();

        foreach (var kv in headers)
        {
            if (!kv.Key.StartsWith("x-identity-auth-chain-", StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = kv.Key["x-identity-auth-chain-".Length..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
                continue;

            var link = JsonSerializer.Deserialize<AuthLink>(kv.Value, JSON_OPTS)
                       ?? throw new FormatException($"Invalid authlink JSON in header {kv.Key}.");

            items.Add((idx, link));
        }

        if (items.Count == 0)
            throw new FormatException("No x-identity-auth-chain-* headers found.");

        return items
              .OrderBy(x => x.Index)
              .Select(x => x.Link)
              .ToList();
    }
}
