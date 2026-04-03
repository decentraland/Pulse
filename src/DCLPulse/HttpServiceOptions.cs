namespace Pulse;

public sealed class HttpServiceOptions
{
    public const string SECTION_NAME = "HttpService";

    public ushort Port { get; set; } = 5000;
}
