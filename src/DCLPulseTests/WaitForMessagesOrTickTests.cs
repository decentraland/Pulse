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
/// </summary>
[TestFixture]
public class WorkerSignalTests
{
    private PeersManager manager;
    private Channel<IncomingEvent> eventChannel;
    private ITimeProvider timeProvider;
    private ManualResetEventSlim signal;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(0u);

        var snapshotBoard = new SnapshotBoard(100, 10);

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            snapshotBoard,
            new SpatialGrid(50, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new ClientMessageCounters(8),
            new EmoteCompleter(snapshotBoard, timeProvider),
            Substitute.For<IPeerIndexAllocator>());

        eventChannel = Channel.CreateUnbounded<IncomingEvent>();
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
        simulation.BaseTickMs.Returns(5000u);

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        Task workerTask = Task.Factory.StartNew(
            () => manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        Thread.Sleep(50);
        eventChannel.Writer.TryWrite(new IncomingEvent(new PeerIndex(0), new ClientMessage()));
        signal.Set();

        Thread.Sleep(50);
        cts.Cancel();
        signal.Set();
        workerTask.Wait(TimeSpan.FromSeconds(2));
        sw.Stop();

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
            () => manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        Thread.Sleep(50);
        eventChannel.Writer.TryWrite(IncomingEvent.Connected(new PeerIndex(0)));
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
        manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80));
    }

    [Test]
    public void CompletesPromptly_WhenCancellationRequested()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u);

        using var cts = new CancellationTokenSource();

        Task workerTask = Task.Factory.StartNew(
            () => manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token),
            TaskCreationOptions.LongRunning);

        Thread.Sleep(50);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        workerTask.Wait(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500));
    }

    [Test]
    public void DrainsBothEventTypes_WhenBothInSameChannel()
    {
        IMessageHandler? handler = Substitute.For<IMessageHandler>();

        var handlers = new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>
        {
            [ClientMessage.MessageOneofCase.Input] = handler,
        };

        var localSnapshotBoard = new SnapshotBoard(100, 10);

        var managerWithHandler = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            localSnapshotBoard,
            new SpatialGrid(50, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            handlers,
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new ClientMessageCounters(8),
            new EmoteCompleter(localSnapshotBoard, timeProvider),
            Substitute.For<IPeerIndexAllocator>());

        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(5000u);

        using var localSignal = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();

        var peer = new PeerIndex(0);

        // Connect arrives before message — ordering preserved in single channel
        eventChannel.Writer.TryWrite(IncomingEvent.Connected(peer));
        eventChannel.Writer.TryWrite(new IncomingEvent(peer, new ClientMessage { Input = new PlayerStateInput() }));

        Task workerTask = Task.Factory.StartNew(
            () => managerWithHandler.RunWorker(0, eventChannel.Reader, simulation, localSignal, cts.Token),
            TaskCreationOptions.LongRunning);

        Thread.Sleep(100);
        cts.Cancel();
        localSignal.Set();
        workerTask.Wait(TimeSpan.FromSeconds(2));

        Dictionary<PeerIndex, PeerState> peers = managerWithHandler.peerStates[0];
        Assert.That(peers, Has.Count.EqualTo(1));
        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));

        handler.Received(1)
               .Handle(
                    Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                    peer,
                    Arg.Any<ClientMessage>());

        managerWithHandler.Dispose();
    }
}
