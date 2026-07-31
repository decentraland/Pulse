namespace PulseTestClient.Bridge;

/// <summary>
///     Mints a real LiveKit token per assignment, so a bot can actually join the room the bridge
///     names. Host and credentials are read from the environment at run time and never reach a log
///     line, a fixture or the repository — <see cref="Description" /> names the host alone.
/// </summary>
public sealed class LiveKitConnStringSource : IConnStringSource
{
    /// <summary>Environment variable holding the LiveKit host, with or without a scheme.</summary>
    public const string HOST_VAR = "LIVEKIT_HOST";

    /// <summary>Environment variable holding the LiveKit API key.</summary>
    public const string API_KEY_VAR = "LIVEKIT_API_KEY";

    /// <summary>Environment variable holding the LiveKit API secret.</summary>
    public const string API_SECRET_VAR = "LIVEKIT_API_SECRET";

    // Matches the real gatekeeper, which mints a five-minute token per cluster assignment.
    private static readonly TimeSpan TOKEN_TTL = TimeSpan.FromMinutes(5);

    private readonly string host;
    private readonly string apiKey;
    private readonly string apiSecret;

    private LiveKitConnStringSource(string host, string apiKey, string apiSecret)
    {
        this.host = host;
        this.apiKey = apiKey;
        this.apiSecret = apiSecret;
    }

    public string Description =>
        $"real LiveKit tokens against {host}";

    /// <summary>
    ///     Reads the three variables this mode needs, naming every missing one at once. Never falls
    ///     back to synthetic: a run that asked for real tokens and silently got stand-ins would pass
    ///     its assertions while proving nothing.
    /// </summary>
    public static LiveKitConnStringSource FromEnvironment()
    {
        string? host = Environment.GetEnvironmentVariable(HOST_VAR);
        string? apiKey = Environment.GetEnvironmentVariable(API_KEY_VAR);
        string? apiSecret = Environment.GetEnvironmentVariable(API_SECRET_VAR);

        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(host)) missing.Add(HOST_VAR);
        if (string.IsNullOrWhiteSpace(apiKey)) missing.Add(API_KEY_VAR);
        if (string.IsNullOrWhiteSpace(apiSecret)) missing.Add(API_SECRET_VAR);

        if (missing.Count > 0)
            throw new PulseException(
                $"--bridge-mode=livekit needs {string.Join(", ", missing)} set in the environment");

        return new LiveKitConnStringSource(Normalize(host!), apiKey!, apiSecret!);
    }

    public string Build(string wallet, string room, string clusterId) =>
        $"livekit:{host}?access_token={LiveKitAccessToken.Mint(apiKey, apiSecret, wallet, room, TOKEN_TTL)}";

    /// <summary>
    ///     Adds the scheme the real gatekeeper adds when its configured host lacks one. Unlike that
    ///     one, a plain-<c>ws://</c> host is left alone rather than turned into
    ///     <c>wss://ws://…</c>, because a local LiveKit under test is routinely unencrypted.
    /// </summary>
    private static string Normalize(string host) =>
        host.Contains("://", StringComparison.Ordinal) ? host : $"wss://{host}";
}
