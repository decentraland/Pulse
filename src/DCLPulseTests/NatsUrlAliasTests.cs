using Microsoft.Extensions.Configuration;
using Pulse.Clusters;

namespace DCLPulseTests;

/// <summary>
///     Precedence rules for the two accepted spellings of the broker URL: the service-specific
///     <c>Nats__Url</c> and the flat <c>NATS_URL</c> that archipelago's services read. Getting this
///     wrong fails silently — an unresolved URL leaves the publisher in stats-only mode with no
///     error, so the rules are pinned here rather than left to inspection.
/// </summary>
[TestFixture]
public class NatsUrlAliasTests
{
    private const string ALIAS_URL = "nats://from-alias:4222";
    private const string SECTION_URL = "nats://from-section:4222";

    [Test]
    public void Alias_PopulatesUrl_WhenSectionKeyAbsent()
    {
        ConfigurationManager configuration = new ();

        configuration.AddNatsUrlAlias(ALIAS_URL);

        Assert.That(BindUrl(configuration), Is.EqualTo(ALIAS_URL));
    }

    [Test]
    public void SectionKey_Wins_WhenBothAreSet()
    {
        ConfigurationManager configuration = Configured(NatsOptions.URL_KEY, SECTION_URL);

        configuration.AddNatsUrlAlias(ALIAS_URL);

        Assert.That(BindUrl(configuration), Is.EqualTo(SECTION_URL));
    }

    [Test]
    public void SectionKey_Wins_WhenSetInEnvironmentDoubleUnderscoreForm()
    {
        // Nats__Url is how the section key arrives from CI; the environment provider translates the
        // double underscore, so the alias must not override it.
        ConfigurationManager configuration = new ();
        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("Nats:Url", SECTION_URL)]);

        configuration.AddNatsUrlAlias(ALIAS_URL);

        Assert.That(BindUrl(configuration), Is.EqualTo(SECTION_URL));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BlankAlias_LeavesFeedUnconfigured(string? aliasValue)
    {
        ConfigurationManager configuration = new ();

        configuration.AddNatsUrlAlias(aliasValue);

        Assert.That(Bind(configuration).IsConfigured, Is.False);
    }

    [Test]
    public void BlankAlias_DoesNotClearAnExistingSectionKey()
    {
        ConfigurationManager configuration = Configured(NatsOptions.URL_KEY, SECTION_URL);

        configuration.AddNatsUrlAlias(null);

        Assert.That(BindUrl(configuration), Is.EqualTo(SECTION_URL));
    }

    [Test]
    public void NeitherSet_LeavesFeedUnconfigured()
    {
        NatsOptions options = Bind(new ConfigurationManager());

        Assert.That(options.IsConfigured, Is.False);
        Assert.That(options.Url, Is.Empty);
    }

    [Test]
    public void Alias_IsAppliedIdempotently()
    {
        ConfigurationManager configuration = new ();

        configuration.AddNatsUrlAlias(ALIAS_URL);
        configuration.AddNatsUrlAlias("nats://second-call:4222");

        // The first call satisfied the key, so the second must not layer over it.
        Assert.That(BindUrl(configuration), Is.EqualTo(ALIAS_URL));
    }

    private static ConfigurationManager Configured(string key, string value)
    {
        ConfigurationManager configuration = new ();
        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]);

        return configuration;
    }

    private static string BindUrl(IConfiguration configuration) =>
        Bind(configuration).Url;

    private static NatsOptions Bind(IConfiguration configuration)
    {
        var options = new NatsOptions();
        configuration.GetSection(NatsOptions.SECTION_NAME).Bind(options);

        return options;
    }
}
