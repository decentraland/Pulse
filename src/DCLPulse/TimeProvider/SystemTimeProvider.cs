namespace Pulse;

public class SystemTimeProvider : ITimeProvider
{
    private readonly DateTime startTime = DateTime.Now;

    public uint MonotonicTime => (uint) (DateTime.Now - startTime).TotalMilliseconds;
}
