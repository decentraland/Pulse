namespace Pulse;

public sealed class HttpServiceOptions
{
    public const string SECTION_NAME = "HttpService";

    public ushort Port { get; set; } = 5000;
}

/// <summary>
///     Environment variables injected by decentraland/definitions
/// </summary>
public abstract class EnvironmentVariableBase(string varName, string? defaultValue = "")
{
    public readonly string? Value = Environment.GetEnvironmentVariable(varName) ?? defaultValue;
}

public sealed class MetricsBearerToken() : EnvironmentVariableBase(ENV_VAR)
{
    private const string ENV_VAR = "WKC_METRICS_BEARER_TOKEN";
}

public sealed class CommsBearerToken() : EnvironmentVariableBase(ENV_VAR)
{
    private const string ENV_VAR = "COMMS_MODERATOR_TOKEN";
}

public sealed class EnvName : EnvironmentVariableBase
{
    private const string ENV_VAR = "ENV";
    public readonly string HttpSuffix;

    public EnvName() : base(ENV_VAR, "prd")
    {
        HttpSuffix = Value!.Equals("prd", StringComparison.OrdinalIgnoreCase) ? "org" : "zone";
    }
}
