using Pulse;
using Pulse.FeatureFlags;
using System.Text.Json;

namespace DCLPulseTests.FeatureFlags;

/// <summary>
///     Fetch and parse of the Unleash document, driven against a loopback stub so the app-name prefix
///     strip runs on the real wire shape rather than a hand-built object. The strip is what makes flag
///     names in code (<c>hardening</c>) match what an operator sees in Unleash
///     (<c>pulse-hardening</c>) minus the application namespace.
/// </summary>
[TestFixture]
public class FeatureFlagsClientTests
{
    [Test]
    public void Url_IsTheAppNameDocumentUnderTheConfiguredOrigin()
    {
        using var client = new FeatureFlagsClient(
            new FeatureFlagsOptions { Url = "https://feature-flags.example.test/", AppName = "pulse" }, new EnvName());

        Assert.That(client.Url, Is.EqualTo("https://feature-flags.example.test/pulse.json"));
    }

    [Test]
    public async Task FetchAsync_StripsTheAppNamePrefixFromFlagsAndVariants()
    {
        using var endpoint = new StubFlagsEndpoint(FeatureFlagsTestDoubles.REAL_DOCUMENT_BODY);
        using FeatureFlagsClient client = endpoint.Client();

        FeatureFlagsDocument document = await client.FetchAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(document.Flags?.Keys, Is.EquivalentTo(new[] { "hardening" }));
            Assert.That(document.Variants?.Keys, Is.EquivalentTo(new[] { "hardening" }));
            Assert.That(document.Variants?["hardening"].Name, Is.EqualTo("configuration"));
        });
    }

    /// <summary>
    ///     Only the leading application namespace is removed. An app name that recurs inside a flag
    ///     name is part of the name, and stripping every occurrence would rewrite it — <c>ip-limiter</c>
    ///     and <c>pulse-limiter</c> would collide on one entry.
    /// </summary>
    [Test]
    public async Task FetchAsync_AppNameRecurringInsideTheFlagName_IsNotStripped()
    {
        using var endpoint = new StubFlagsEndpoint(
            """{"flags":{"pulse-foo-pulse-bar":true},"variants":{}}""");

        using FeatureFlagsClient client = endpoint.Client();

        FeatureFlagsDocument document = await client.FetchAsync(CancellationToken.None);

        Assert.That(document.Flags?.Keys, Is.EquivalentTo(new[] { "foo-pulse-bar" }));
    }

    /// <summary>
    ///     A fetched document must survive the trip into the provider unchanged: the prefixless flag
    ///     name resolves its variant, and the escaped payload string flattens onto the three leaves.
    /// </summary>
    [Test]
    public async Task FetchAsync_ThenApply_ProducesTheDocumentsOverrides()
    {
        using var endpoint = new StubFlagsEndpoint(FeatureFlagsTestDoubles.REAL_DOCUMENT_BODY);
        using FeatureFlagsClient client = endpoint.Client();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(await client.FetchAsync(CancellationToken.None));

        Assert.That(provider.AppliedOverrides.Keys, Is.EquivalentTo(new[]
        {
            FeatureFlagsTestDoubles.ENABLED_KEY,
            FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY,
            FeatureFlagsTestDoubles.WHITELIST_KEY,
        }));
    }

    /// <summary>
    ///     A malformed body must surface as a <c>JsonException</c>, so the caller sees a broken
    ///     document as a fetch failure rather than an empty one it could mistake for no overrides.
    /// </summary>
    [Test]
    public void FetchAsync_MalformedBody_ThrowsJsonException()
    {
        using var endpoint = new StubFlagsEndpoint("{\"flags\": ");
        using FeatureFlagsClient client = endpoint.Client();

        Assert.That(
            Assert.CatchAsync(async () => await client.FetchAsync(CancellationToken.None)),
            Is.InstanceOf<JsonException>());
    }

    [Test]
    public void FetchAsync_ErrorStatus_ThrowsHttpRequestException()
    {
        using var endpoint = new StubFlagsEndpoint("{}", statusCode: 503);
        using FeatureFlagsClient client = endpoint.Client();

        Assert.That(
            Assert.CatchAsync(async () => await client.FetchAsync(CancellationToken.None)),
            Is.InstanceOf<HttpRequestException>());
    }
}
