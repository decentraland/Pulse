namespace Pulse;

public interface ITimeProvider
{
    /// <summary>
    ///     Time since start up of the server
    /// </summary>
    public uint MonotonicTime { get; }

    /// <summary>
    ///     Wall-clock now, in unix milliseconds. What the stats surface and the presence feed put on
    ///     the wire — <c>lastUpdated</c>, <c>currentTime</c>, <c>server_time</c> — none of which a
    ///     consumer can interpret against this process's start.
    /// </summary>
    public long UnixTimeMs { get; }

    /// <summary>
    ///     The wall-clock unix millisecond a <see cref="MonotonicTime" /> stamp taken by this process
    ///     corresponds to, for translating a stored tick (a snapshot's <c>ServerTick</c>) into the
    ///     <c>lastPing</c> the stats surface reports. Inherits <see cref="MonotonicTime" />'s 32-bit
    ///     range, so a stamp older than ~49.7 days of uptime has already wrapped and cannot be
    ///     recovered here.
    /// </summary>
    public long ToUnixTimeMs(uint monotonicTime);
}
