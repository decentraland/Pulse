using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Threading.Channels;

namespace DCLPulseTests;

[TestFixture]
public class WorkerAsyncTests
{
    private PeersManager manager;
    private Channel<MessagePipe.IncomingEvent> eventChannel;
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
            new EmoteCompleter(snapshotBoard, timeProvider));

        eventChannel = Channel.CreateUnbounded<MessagePipe.IncomingEvent>();
        signal = new ManualResetEventSlim();
    }

    [TearDown]
    public void TearDown()
    {
        signal.Dispose();
        manager.Dispose();
    }

    [Test]
    public void DrainsLifeCycleEvents_BeforeExiting()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        var peer0 = new PeerIndex(0);
        var peer1 = new PeerIndex(1);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer0));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer1));

        // Pre-cancelled token: the while-loop body never executes,
        // but the post-loop drain still processes buffered events.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token);

        Dictionary<PeerIndex, PeerState> peers = manager.peerStates[0];
        Assert.That(peers, Has.Count.EqualTo(2));
        Assert.That(peers[peer0].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer1].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void SimulationTickFires_WhenTimeAdvancesPastDeadline()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        using var cts = new CancellationTokenSource();
        var callIndex = 0;

        timeProvider.MonotonicTime.Returns(_ =>
        {
            var value = (uint)(callIndex * 100);
            callIndex++;

            if (callIndex >= 3)
                cts.Cancel();

            signal.Set();

            return value;
        });

        manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token);

        simulation.Received()
                  .SimulateTick(
                       Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                       Arg.Any<uint>());
    }

    [Test]
    public void SimulationTickDoesNotFire_BeforeDeadline()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        timeProvider.MonotonicTime.Returns(0u);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        manager.RunWorker(0, eventChannel.Reader, simulation, signal, cts.Token);

        simulation.DidNotReceive()
                  .SimulateTick(
                       Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                       Arg.Any<uint>());
    }
}
