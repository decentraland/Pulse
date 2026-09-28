namespace Pulse;

public interface ITimeProvider
{
    /// <summary>Time since start-up of the server.</summary>
    public uint MonotonicTime { get; }

    /// <summary>Wall-clock now, in unix milliseconds — the form used for values on the wire.</summary>
    public long UnixTimeMs { get; }

    /// <summary>
    ///     The wall-clock unix millisecond a <see cref="MonotonicTime" /> stamp from this process
    ///     corresponds to. Inherits its 32-bit range, so a stamp older than ~49.7 days of uptime has
    ///     already wrapped and cannot be recovered here.
    /// </summary>
    public long ToUnixTimeMs(uint monotonicTime);
}
