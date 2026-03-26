namespace PulseTestClient;

public static class CancellationTokenExtensions
{
    public static void SafeCancelAndDispose(this CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
            source?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
    
    public static CancellationTokenSource SafeRestart(this CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
            source?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        return new CancellationTokenSource();
    }
    
    public static CancellationTokenSource SafeRestartLinked(this CancellationTokenSource? cancellationToken,
        params CancellationToken[] cancellationTokens)
    {
        try
        {
            cancellationToken?.Cancel();
            cancellationToken?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        return CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens);
    }
}