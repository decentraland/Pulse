using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Pulse;

/// <summary>
///     Registration of <c>dynamicconfig.json</c> in the host's configuration chain.
/// </summary>
public static class DynamicConfigExtensions
{
    /// <summary>
    ///     Adds <paramref name="path" /> as a required, reload-on-change JSON source inserted
    ///     immediately <em>before</em> the application's environment-variable provider rather than
    ///     appended. <c>Host.CreateApplicationBuilder</c> registers the environment-variable and
    ///     command-line providers before application code runs, so an appended source would outrank
    ///     both — and this file holds shipped offline defaults, which must lose to anything an
    ///     operator sets on the task definition or command line. Sources added after this call still
    ///     win, which is how the remote feature-flag document keeps the last word.
    ///     <para />
    ///     <c>optional: false</c>: this file is the only source of the offline defaults, and its
    ///     values are also the type schema the remote document is checked against, so a missing file
    ///     would mean unconfigured knobs and unchecked overrides. Pass a rooted path to pin the
    ///     lookup rather than leave it to whichever file provider the builder carries.
    /// </summary>
    public static IConfigurationBuilder AddDynamicConfig(this IConfigurationBuilder configuration, string path)
    {
        var source = new JsonConfigurationSource
        {
            Path = path,
            Optional = false,
            ReloadOnChange = true,
        };

        source.ResolveFileProvider();
        configuration.Sources.Insert(EnvironmentVariablesIndex(configuration.Sources), source);
        return configuration;
    }

    /// <summary>
    ///     Index of the application's environment-variable source, or the end of the list when there
    ///     is none. The host registers two: an early <c>DOTNET_</c>-prefixed one carrying host
    ///     settings, and the unprefixed application one above <c>appsettings.json</c>. Only the
    ///     latter is the boundary defaults must stay below, so the match keys on the prefix rather
    ///     than a hardcoded index a future host version could shift.
    /// </summary>
    private static int EnvironmentVariablesIndex(IList<IConfigurationSource> sources)
    {
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                return i;

        return sources.Count;
    }
}
