namespace Pulse.Transport;

/// <summary>
///     Abstraction for the transport in case it's used actively from other services
/// </summary>
public interface ITransport
{
    public enum PacketMode
    {
        RELIABLE = 0,
        UNRELIABLE_SEQUENCED = 1,
        UNRELIABLE_UNSEQUENCED = 2,
    }
}
