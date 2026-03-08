namespace Pulse;

public interface ITimeProvider
{
    /// <summary>
    ///     Time since start up of the server
    /// </summary>
    public uint MonotonicTime { get; }
}
