using System.Reflection;

namespace Pulse.Stats;

/// <summary>
///     What this build calls itself: the version and commit hash <c>/status</c> and <c>/about</c>
///     report. A type rather than two statics so tests can pin them — a golden response cannot
///     contain the build's real commit hash.
/// </summary>
public sealed record ServiceIdentity(string Version, string CommitHash)
{
    private const string UNKNOWN = "unknown";

    /// <summary>
    ///     <c>COMMIT_HASH</c> from the environment (injected by decentraland/definitions) and the
    ///     assembly's informational version, which is what the build stamps.
    /// </summary>
    public static ServiceIdentity FromEnvironment() =>
        new (
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
         ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
         ?? UNKNOWN,
            Environment.GetEnvironmentVariable("COMMIT_HASH") ?? UNKNOWN);
}
