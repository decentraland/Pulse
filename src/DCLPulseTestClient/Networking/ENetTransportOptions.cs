namespace PulseTestClient.Networking;

public sealed class ENetTransportOptions
{
    public ushort Port { get; set; } = 7777;
    public int ServiceTimeoutMs { get; set; } = 1000;
}