using Microsoft.Extensions.Configuration;
using Pulse.Clusters;

namespace DCLPulseTests;

/// <summary>
///     <c>Nats:ServerName</c> is the key consumers replace presence state by: a
///     <c>snapshot=true</c> batch tells them to drop everything they hold for that
///     <c>server_name</c> and take the batch instead. Two replicas sharing one value therefore
///     delete each other's populations once per snapshot interval and read as a permanent
///     <c>seq</c> gap in between — a failure that is completely silent on the Pulse side, since both
///     instances look healthy.
///     <para />
///     So the default has to be instance-unique rather than a shared literal (A3), and an explicit
///     value still has to win, because that is how a deployment pins pod names.
/// </summary>
[TestFixture]
public class NatsServerNameTests
{
    /// <summary>
    ///     The unconfigured default. The machine name is the pod name in Kubernetes and the container
    ///     id under plain Docker, so two replicas of one deployment never share it.
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
    ///     A blank value is "not configured", not a server with an empty name: <c>appsettings.json</c>
    ///     ships the key empty so that the shape of the configuration is discoverable, and an
    ///     environment that sets <c>Nats__ServerName=</c> means the same thing.
    /// </summary>
    [TestCase("")]
    [TestCase("   ")]
    public void ABlankServerName_FallsBackToTheHostnameDefault(string configured)
    {
        Assert.That(Bind(("Nats:ServerName", configured)).ServerName,
            Is.EqualTo($"pulse-{Environment.MachineName}"));
    }

    /// <summary>
    ///     Stable for the life of the process, whatever it resolved to — consumers key their
    ///     per-server state on it, so a value that drifted would look like a second server (C1.5).
    /// </summary>
    [Test]
    public void ServerName_IsStableAcrossReads()
    {
        var options = new NatsOptions();

        string first = options.ServerName;
        string second = options.ServerName;

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Is.Not.Empty);
    }

    /// <summary>
    ///     And what the shipped configuration resolves to, since a literal left in
    ///     <c>appsettings.json</c> would defeat the whole default.
    /// </summary>
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
