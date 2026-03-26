namespace PulseTestClient.Timing;

public interface ITimeProvider
{
    public uint TimeSinceStartupMs { get; }
}