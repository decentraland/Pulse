using System.Reflection;

namespace Pulse.Stats;

/// <summary>
///     What this build calls itself: the two values <c>/status</c> and <c>/about</c> report so an
///     operator (and the Godot client, which reads <c>version</c>) can tell which Pulse answered.
///     <para />
///     A type rather than two statics because both are read from the environment, and tests have to
///     pin them: a golden response cannot contain the build's real commit hash.
/// </summary>
public sealed record ServiceIdentity(string Version, string CommitHash)
{
    private const string UNKNOWN = "unknown";

    /// <summary>
    ///     From the environment, as the deployment provides it: <c>COMMIT_HASH</c> is injected by
    ///     decentraland/definitions, and the version is the assembly's informational version, which is
    ///     what the build stamps.
    /// </summary>
    public static ServiceIdentity FromEnvironment() =>
        new (
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
         ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
         ?? UNKNOWN,
            Environment.GetEnvironmentVariable("COMMIT_HASH") ?? UNKNOWN);
}
