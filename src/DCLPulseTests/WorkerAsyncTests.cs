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
    private Channel<MessagePipe.IncomingMessage> messageChannel;
    private Channel<MessagePipe.PeerLifeCycleEvent> lifeCycleChannel;
    private ITimeProvider timeProvider;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(0u);

        var snapshotBoard = new SnapshotBoard(100, 10);

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>()),
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
            new EmoteBoard(100));

        messageChannel = Channel.CreateUnbounded<MessagePipe.IncomingMessage>();
        lifeCycleChannel = Channel.CreateUnbounded<MessagePipe.PeerLifeCycleEvent>();
    }

    [TearDown]
    public void TearDown()
    {
        manager.Dispose();
    }

    [Test]
    public async Task DrainsLifeCycleEvents_BeforeExiting()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        var peer0 = new PeerIndex(0);
        var peer1 = new PeerIndex(1);

        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peer0, MessagePipe.PeerEventType.Connected));
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peer1, MessagePipe.PeerEventType.Connected));

        // Pre-cancelled token: the while-loop body never executes,
        // but the post-loop drain still processes buffered events.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await manager.WorkerAsync(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, cts.Token);

        Dictionary<PeerIndex, PeerState> peers = manager.peerStates[0];
        Assert.That(peers, Has.Count.EqualTo(2));
        Assert.That(peers[peer0].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer1].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public async Task SimulationTickFires_WhenTimeAdvancesPastDeadline()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        using var cts = new CancellationTokenSource();
        var callIndex = 0;

        // MonotonicTime call sequence:
        // call 0: init → nextTickTime = 0 + 50 = 50
        // call 1: loop check → now = 100 >= 50 → tick fires, nextTickTime = 100 + 50 = 150
        // call 2: WaitForMessagesOrTick → remaining = 150 - 200 = -50 → returns immediately
        // call 3: loop check → now = 300 >= 150 → tick fires again, then cancel
        timeProvider.MonotonicTime.Returns(_ =>
        {
            var value = (uint)(callIndex * 100);
            callIndex++;

            if (callIndex >= 4)
                cts.Cancel();

            return value;
        });

        await manager.WorkerAsync(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, cts.Token);

        simulation.Received()
                  .SimulateTick(
                       Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                       Arg.Any<uint>());
    }

    [Test]
    public async Task SimulationTickDoesNotFire_BeforeDeadline()
    {
        IPeerSimulation? simulation = Substitute.For<IPeerSimulation>();
        simulation.BaseTickMs.Returns(50u);

        // MonotonicTime always returns 0 → now (0) never reaches nextTickTime (50)
        timeProvider.MonotonicTime.Returns(0u);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await manager.WorkerAsync(0, messageChannel.Reader, lifeCycleChannel.Reader, simulation, cts.Token);

        simulation.DidNotReceive()
                  .SimulateTick(
                       Arg.Any<Dictionary<PeerIndex, PeerState>>(),
                       Arg.Any<uint>());
    }
}
