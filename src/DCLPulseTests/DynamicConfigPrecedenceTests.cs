using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;
using Pulse;
using Pulse.FeatureFlags;
using Pulse.Transport.Hardening;

namespace DCLPulseTests;

/// <summary>
///     Pins where <c>dynamicconfig.json</c> sits in the configuration chain. Its values are shipped
///     offline defaults, so every operator-controlled layer above it — environment variables, the
///     command line, the remote feature-flag document — has to win. Appending the file, which is
///     what <c>builder.Configuration.AddJsonFile(...)</c> does, silently inverts all three: the
///     host registers the environment-variable and command-line providers before application code
///     runs.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DynamicConfigPrecedenceTests
{
    // The knob the finding was reproduced against: the limiter ships disabled and an operator turns
    // it on for one environment from the task definition.
    private const string KEY = IpLimiterOptions.SECTION_NAME + ":Enabled";
    private const string CAP_KEY = IpLimiterOptions.SECTION_NAME + ":MaxConcurrency";
    private const string ENV_KEY = "Transport__Hardening__IpLimiter__Enabled";

    private readonly List<HostApplicationBuilder> builders = new ();

    private string contentRoot = "";

    [SetUp]
    public void SetUp()
    {
        contentRoot = Path.Combine(Path.GetTempPath(), "pulse-dynamicconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        // appsettings.json sits below dynamicconfig.json; the distinct value proves which one won.
        WriteJson("appsettings.json", cap: 1);
        WriteJson(DynamicConfigSchema.FILE_NAME, cap: 2);

        Environment.SetEnvironmentVariable(ENV_KEY, null);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(ENV_KEY, null);

        foreach (HostApplicationBuilder builder in builders)
            builder.Configuration.Dispose();

        builders.Clear();

        try { Directory.Delete(contentRoot, recursive: true); }
        catch (IOException) { /* the reload-on-change watcher can still hold the directory */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    [Test]
    public void SourceOrder_PlacesDynamicConfigBetweenAppSettingsAndEnvironmentVariables()
    {
        // An argument has to be present for the host to register a command-line source at all.
        HostApplicationBuilder builder = CreateBuilder("--Transport:MaxPeers=8");

        List<string> order = DescribeSources(builder);

        int appSettings = order.IndexOf("json:appsettings.json");
        int dynamicConfig = order.IndexOf("json:" + DynamicConfigSchema.FILE_NAME);
        int environmentVariables = order.IndexOf("env:");
        int commandLine = order.LastIndexOf("commandline");

        Assert.That(dynamicConfig, Is.GreaterThan(appSettings),
            "dynamicconfig.json must still override appsettings.json");
        Assert.That(environmentVariables, Is.GreaterThan(dynamicConfig),
            "the environment-variable provider must sit above the shipped defaults");
        Assert.That(commandLine, Is.GreaterThan(environmentVariables),
            "the command line keeps its place above the environment");
    }

    [Test]
    public void DynamicConfig_BeatsAppSettings()
    {
        HostApplicationBuilder builder = CreateBuilder();

        Assert.That(builder.Configuration[CAP_KEY], Is.EqualTo("2"));
    }

    [Test]
    public void NoOverride_UsesTheShippedDefault()
    {
        HostApplicationBuilder builder = CreateBuilder();

        // The JSON provider renders booleans PascalCase, hence the case-insensitive compare.
        Assert.That(builder.Configuration[KEY], Is.EqualTo("false").IgnoreCase);
    }

    [Test]
    public void EnvironmentVariable_BeatsDynamicConfig()
    {
        Environment.SetEnvironmentVariable(ENV_KEY, "true");

        HostApplicationBuilder builder = CreateBuilder();

        Assert.That(builder.Configuration[KEY], Is.EqualTo("true"),
            "an operator setting the knob on the task definition must not be outranked by a shipped default");
    }

    [Test]
    public void CommandLine_BeatsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(ENV_KEY, "true");

        HostApplicationBuilder builder = CreateBuilder("--" + KEY + "=false");

        Assert.That(builder.Configuration[KEY], Is.EqualTo("false"));
    }

    [Test]
    public void RemoteSource_AppendedLast_BeatsEnvironmentVariable()
    {
        // Stands in for PulseFlagsConfigurationSource, which Program.cs appends after everything
        // else: reconfiguring a live server from the remote document is the one case that is
        // supposed to outrank the environment the server booted with.
        Environment.SetEnvironmentVariable(ENV_KEY, "false");

        HostApplicationBuilder builder = CreateBuilder();

        builder.Configuration.Sources.Add(new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { [KEY] = "true" },
        });

        Assert.That(builder.Configuration[KEY], Is.EqualTo("true"));
    }

    private HostApplicationBuilder CreateBuilder(params string[] args)
    {
        // "Testing" rather than "Development" so the host does not reach for this machine's user
        // secrets and drop an unrelated source into the middle of the chain.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = contentRoot,
            EnvironmentName = "Testing",
            ApplicationName = "DCLPulseTests",
        });

        builders.Add(builder);
        builder.Configuration.AddDynamicConfig(Path.Combine(contentRoot, DynamicConfigSchema.FILE_NAME));
        return builder;
    }

    /// <summary>
    ///     Flattens the source list to comparable tags. The unprefixed environment-variable source
    ///     is the boundary that matters; the host's early <c>DOTNET_</c>-prefixed source carries
    ///     host settings and is deliberately left below the defaults.
    /// </summary>
    private static List<string> DescribeSources(HostApplicationBuilder builder)
    {
        var described = new List<string>();

        foreach (IConfigurationSource source in builder.Configuration.Sources)
            described.Add(source switch
            {
                JsonConfigurationSource json => "json:" + json.Path,
                EnvironmentVariablesConfigurationSource { Prefix: null or "" } => "env:",
                EnvironmentVariablesConfigurationSource prefixed => "env:" + prefixed.Prefix,
                CommandLineConfigurationSource => "commandline",
                _ => source.GetType().Name,
            });

        return described;
    }

    private void WriteJson(string fileName, int cap) =>
        File.WriteAllText(
            Path.Combine(contentRoot, fileName),
            $$"""
              { "Transport": { "Hardening": { "IpLimiter": { "Enabled": false, "MaxConcurrency": {{cap}} } } } }
              """);
}
