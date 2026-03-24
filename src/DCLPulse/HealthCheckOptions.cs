namespace Pulse;

public sealed class HealthCheckOptions
{
    public const string SECTION_NAME = "HealthCheck";

    public ushort Port { get; set; } = 5000;
}