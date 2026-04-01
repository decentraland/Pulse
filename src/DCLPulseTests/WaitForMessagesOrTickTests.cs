using System.Diagnostics;
using System.Threading.Channels;
using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

/// <summary>
///     Tests for the sync worker loop's signal-based wake behavior.
///     Replaces the old WaitForMessagesOrTick tests — that method no longer exists.
/// </summary>
[TestFixture]
public class WorkerSignalTests
{
    private PeersManager manager;
    private Channel<IncomingMessage> messageChannel;
    private Channel<PeerLifeCycleEvent> lifeCycleChannel;
    private ITimeProvider timeProvider;
    private ManualResetEventSlim signal;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(0u);

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>()),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            new SnapshotBoard(100, 10),
            new SpatialGrid(50, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new EmoteBoard(100));

        messageChannel = Channel.CreateUnbounded<IncomingMessage>();
        lifeCycleChannel = Channel.CreateUnbounded<PeerLifeCycleEvent>();
        signal = new ManualResetEventSlim();
    }

    [TearDown]
    public void TearDown()
    {
        signal.Dispose();
        manager.Dispose();
    }

    [Test]
    public void WakesOnSignal_WhenMessageArrives()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u); // Very long tick so the worker would block

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        // Start worker on background thread
        Task workerTask = Task.Factory.StartNew(
            () => manager.RunWorker(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        // Wait for worker to enter signal.Wait, then wake it with a message + signal
        Thread.Sleep(50);
        messageChannel.Writer.TryWrite(new IncomingMessage(new PeerIndex(0), new ClientMessage()));
        signal.Set();

        // Let it process, then cancel
        Thread.Sleep(50);
        cts.Cancel();
        signal.Set(); // Wake so it sees cancellation
        workerTask.Wait(TimeSpan.FromSeconds(2));
        sw.Stop();

        // Should have completed well before the 5-second tick deadline
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000));
    }

    [Test]
    public void WakesOnSignal_WhenLifeCycleEventArrives()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u);

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        Task workerTask = Task.Factory.StartNew(
            () => manager.RunWorker(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        // Send a lifecycle event and signal — worker should wake and process it
        Thread.Sleep(50);
        lifeCycleChannel.Writer.TryWrite(new PeerLifeCycleEvent(new PeerIndex(0), PeerEventType.Connected));
        signal.Set();

        Thread.Sleep(50);
        cts.Cancel();
        signal.Set();
        workerTask.Wait(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000));

        Dictionary<PeerIndex, PeerState> peers = manager.peerStates[0];
        Assert.That(peers, Has.Count.EqualTo(1));
        Assert.That(peers[new PeerIndex(0)].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void WaitsUntilTickDeadline_WhenNoSignal()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(150u);

        // MonotonicTime returns 0 on init, then real time for subsequent calls
        var initDone = false;

        timeProvider.MonotonicTime.Returns(_ =>
        {
            if (!initDone)
            {
                initDone = true;
                return 0u;
            }

            return (uint)Environment.TickCount64 % uint.MaxValue;
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(500);

        var sw = Stopwatch.StartNew();
        manager.RunWorker(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, signal, cts.Token);
        sw.Stop();

        // Should have waited at least one tick period
        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80));
    }

    [Test]
    public void CompletesPromptly_WhenCancellationRequested()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u);

        using var cts = new CancellationTokenSource();

        Task workerTask = Task.Factory.StartNew(
            () => manager.RunWorker(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        Thread.Sleep(50);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        workerTask.Wait(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500));
    }

    [Test]
    public void DrainsBothChannels_WhenBothHaveData()
    {
        IMessageHandler? handler = Substitute.For<IMessageHandler>();

        var handlers = new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>
        {
            [ClientMessage.MessageOneofCase.Input] = handler,
        };

        var managerWithHandler = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>()),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            new SnapshotBoard(100, 10),
            new SpatialGrid(50, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            handlers,
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new EmoteBoard(100));

        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u);

        using var localSignal = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();

        var peer = new PeerIndex(0);

        // Lifecycle event arrives first (peer connects), then a message from that peer
        lifeCycleChannel.Writer.TryWrite(new PeerLifeCycleEvent(peer, PeerEventType.Connected));

        var msg = new ClientMessage { Input = new PlayerStateInput() };
        messageChannel.Writer.TryWrite(new IncomingMessage(peer, msg));

        Task workerTask = Task.Factory.StartNew(
            () => managerWithHandler.RunWorker(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, localSignal, cts.Token),
            TaskCreationOptions.LongRunning);

        // Give the worker time to drain both channels in the first iteration
        Thread.Sleep(100);
        cts.Cancel();
        localSignal.Set();
        workerTask.Wait(TimeSpan.FromSeconds(2));

        // Lifecycle event created the peer state
        Dictionary<PeerIndex, PeerState> peers = managerWithHandler.peerStates[0];
        Assert.That(peers, Has.Count.EqualTo(1));
        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));

        // Message was processed by the handler (peer existed because lifecycle ran first)
        handler.Received(1)
               .Handle(
                    Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                    peer,
                    Arg.Any<ClientMessage>());

        managerWithHandler.Dispose();
    }
}
