using Microsoft.Extensions.Configuration;
using Pulse.Clusters;

namespace DCLPulseTests;

/// <summary>
///     <c>Nats:ServerName</c> scopes presence state on the consumer side, so two replicas sharing
///     one value wipe each other's populations every snapshot interval — silently, since both
///     instances look healthy. Hence an instance-unique default (A3), with an explicit value still
///     winning, which is how a deployment pins pod names.
/// </summary>
[TestFixture]
public class NatsServerNameTests
{
    /// <summary>
    ///     The machine name is the pod name under default Kubernetes networking and the container id
    ///     under plain Docker, so replicas of one deployment do not share it. The cases that break
    ///     that — <c>hostNetwork: true</c>, a fixed <c>spec.hostname</c>, two processes per host —
    ///     are the ones the docs tell operators to configure.
    /// </summary>
    [Test]
    public void ServerName_DefaultsToTheHostname_WhenNotConfigured()
    {
        Assert.That(new NatsOptions().ServerName, Is.EqualTo($"pulse-{Environment.MachineName}"));
    }

    [Test]
    public void ServerName_IsWhateverWasConfigured()
    {
        Assert.That(Bind(("Nats:ServerName", "pulse-eu-1")).ServerName, Is.EqualTo("pulse-eu-1"));
    }

    /// <summary>
    ///     Blank is "not configured", not a server with an empty name — <c>appsettings.json</c> ships
    ///     the key empty so the configuration shape is discoverable.
    /// </summary>
    [TestCase("")]
    [TestCase("   ")]
    public void ABlankServerName_FallsBackToTheHostnameDefault(string configured)
    {
        Assert.That(Bind(("Nats:ServerName", configured)).ServerName,
            Is.EqualTo($"pulse-{Environment.MachineName}"));
    }

    /// <summary>A value that drifted mid-process would read as a second server (C1.5).</summary>
    [Test]
    public void ServerName_IsStableAcrossReads()
    {
        var options = new NatsOptions();

        string first = options.ServerName;
        string second = options.ServerName;

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Is.Not.Empty);
    }

    /// <summary>A literal left in <c>appsettings.json</c> would defeat the default entirely.</summary>
    [Test]
    public void TheShippedAppsettings_DoesNotPinASharedServerName()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
                                          .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                                          .Build();

        var options = new NatsOptions();
        configuration.GetSection(NatsOptions.SECTION_NAME).Bind(options);

        Assert.That(options.ServerName, Is.EqualTo($"pulse-{Environment.MachineName}"));
    }

    private static NatsOptions Bind(params (string Key, string Value)[] settings)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
                                          .AddInMemoryCollection(settings.Select(static setting =>
                                               new KeyValuePair<string, string?>(setting.Key, setting.Value)))
                                          .Build();

        var options = new NatsOptions();
        configuration.GetSection(NatsOptions.SECTION_NAME).Bind(options);

        return options;
    }
}
