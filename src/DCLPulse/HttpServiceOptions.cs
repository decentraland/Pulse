namespace Pulse;

public sealed class HttpServiceOptions
{
    public const string SECTION_NAME = "HttpService";

    public ushort Port { get; set; } = 5000;
}

public sealed record MetricsBearerToken(string? Value)
{
    public const string ENV_VAR = "WKC_METRICS_BEARER_TOKEN";
}
