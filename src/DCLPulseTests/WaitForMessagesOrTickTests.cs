using System.Diagnostics;
using System.Threading.Channels;
using Decentraland.Pulse;
using NSubstitute;
using Pulse;
using Pulse.Peers;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

[TestFixture]
public class WaitForMessagesOrTickTests
{
    private Channel<IncomingMessage> channel;
    private ITimeProvider timeProvider;

    [SetUp]
    public void SetUp()
    {
        channel = Channel.CreateUnbounded<IncomingMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(1000u);
    }

    [Test]
    public async Task ReturnsImmediately_WhenTickTimeIsInThePast()
    {
        // nextTickTime is behind MonotonicTime → remaining <= 0
        long nextTickTime = timeProvider.MonotonicTime - 500;

        var sw = Stopwatch.StartNew();
        await PeersManager.WaitForMessagesOrTick(channel.Reader, nextTickTime, timeProvider, CancellationToken.None);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(50));
    }

    [Test]
    public async Task CompletesImmediately_WhenMessageAlreadyAvailable()
    {
        long nextTickTime = timeProvider.MonotonicTime + 5000; // 5 seconds away

        // Data is already in the channel before calling WaitForMessagesOrTick.
        // WaitToReadAsync should complete synchronously.
        channel.Writer.TryWrite(new IncomingMessage(new PeerIndex(0), new ClientMessage()));

        var sw = Stopwatch.StartNew();
        await PeersManager.WaitForMessagesOrTick(channel.Reader, nextTickTime, timeProvider, CancellationToken.None);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(50));
    }

    [Test]
    public async Task CompletesOnMessage_BeforeTickDeadline()
    {
        // Tick deadline is 2 seconds away — method should NOT wait that long.
        long nextTickTime = timeProvider.MonotonicTime + 2000;

        var sw = Stopwatch.StartNew();

        // Start waiting. The synchronous portion runs through to `await Task.WhenAny(...)`,
        // then yields back to us because neither sub-task is complete yet.
        Task waitTask = PeersManager.WaitForMessagesOrTick(channel.Reader, nextTickTime, timeProvider, CancellationToken.None);

        // Write a message — completes the WaitToReadAsync task, which completes WhenAny.
        channel.Writer.TryWrite(new IncomingMessage(new PeerIndex(0), new ClientMessage()));

        await waitTask;
        sw.Stop();

        // Completed almost instantly, well before the 2-second tick deadline.
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500));
    }

    [Test]
    public async Task WaitsUntilTickDeadline_WhenNoMessagesArrive()
    {
        const int TICK_MS = 150;
        long nextTickTime = timeProvider.MonotonicTime + TICK_MS;

        var sw = Stopwatch.StartNew();
        await PeersManager.WaitForMessagesOrTick(channel.Reader, nextTickTime, timeProvider, CancellationToken.None);
        sw.Stop();

        // Should wait roughly tickMs. Use generous bounds to avoid flaky CI.
        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80));
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000));
    }

    [Test]
    public async Task CompletesPromptly_WhenCancellationRequested()
    {
        long nextTickTime = timeProvider.MonotonicTime + 5000;
        using var cts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        Task waitTask = PeersManager.WaitForMessagesOrTick(channel.Reader, nextTickTime, timeProvider, cts.Token);

        // Cancel — both WaitToReadAsync and Task.Delay observe the token.
        // Task.WhenAny completes without propagating the inner cancellation.
        await cts.CancelAsync();
        await waitTask;
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500));
    }
}
