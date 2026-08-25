using System.Text.Json;

namespace Pulse.FeatureFlags;

/// <summary>
///     Fetches and parses the Unleash document for this application. Shared by the blocking first
///     load in <see cref="PulseFlagsConfigurationProvider.Load" />, which runs before DI exists, and
///     by <see cref="FeatureFlagsPoller" /> afterwards, so both build the same URL, send the same
///     headers and strip the app-name prefix the same way.
/// </summary>
public sealed class FeatureFlagsClient : IDisposable
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new ()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;
    private readonly string appNamePrefix;

    public FeatureFlagsClient(FeatureFlagsOptions options, EnvName envName)
    {
        string origin = string.IsNullOrWhiteSpace(options.Url)
            ? $"https://feature-flags.decentraland.{envName.HttpSuffix}"
            : options.Url.TrimEnd('/');

        Url = $"{origin}/{options.AppName}.json";
        appNamePrefix = $"{options.AppName}-";

        httpClient = new HttpClient
        {
            Timeout = options.HttpTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
                : Timeout.InfiniteTimeSpan,
        };

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Debug", "false");

        // Matched by Unleash's hostname strategy. No X-Address-Hash: the server fetches one document
        // for itself and has no user identity to target with.
        if (!string.IsNullOrWhiteSpace(options.Hostname))
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("referer", options.Hostname);
    }

    /// <summary>Absolute URL of the document this client fetches.</summary>
    public string Url { get; }

    public void Dispose() => httpClient.Dispose();

    /// <summary>
    ///     Fetches the document and strips the app-name prefix from every flag and variant key.
    ///     Throws <see cref="HttpRequestException" /> on a transport or status failure and
    ///     <see cref="JsonException" /> on a malformed body.
    /// </summary>
    public async Task<FeatureFlagsDocument> FetchAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(Url, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);

        FeatureFlagsDocument? parsed =
            await JsonSerializer.DeserializeAsync<FeatureFlagsDocument>(stream, JSON_OPTIONS, ct);

        if (parsed is null)
            throw new JsonException($"Feature flags document at {Url} deserialized to null");

        StripAppNamePrefix(parsed);

        return parsed;
    }

    /// <summary>
    ///     Rebuilds both maps with the <c>{AppName}-</c> prefix removed, so flag names in code match
    ///     what an operator sees in Unleash minus the application namespace.
    /// </summary>
    private void StripAppNamePrefix(FeatureFlagsDocument document)
    {
        if (document.Flags is not null)
        {
            var flags = new Dictionary<string, bool>(document.Flags.Count, StringComparer.OrdinalIgnoreCase);

            foreach ((string key, bool value) in document.Flags)
                flags[Strip(key)] = value;

            document.Flags = flags;
        }

        if (document.Variants is null)
            return;

        var variants = new Dictionary<string, FeatureFlagVariant>(document.Variants.Count, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, FeatureFlagVariant value) in document.Variants)
            variants[Strip(key)] = value;

        document.Variants = variants;
    }

    /// <summary>
    ///     Removes the leading <c>{AppName}-</c> namespace and only that: the application name can
    ///     recur inside a flag name, and stripping every occurrence would rewrite the name rather
    ///     than unqualify it.
    /// </summary>
    private string Strip(string key) =>
        key.StartsWith(appNamePrefix, StringComparison.OrdinalIgnoreCase)
            ? key[appNamePrefix.Length..]
            : key;
}
