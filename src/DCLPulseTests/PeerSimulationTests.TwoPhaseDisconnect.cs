using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Numerics;
using System.Threading.Channels;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

// Two-phase disconnect. Phase 1 runs on the `Disconnected` lifecycle event and only stops the peer
// being a subject; phase 2 (CleanupDisconnectedPeer) still waits out
// PeerOptions.DisconnectionCleanTimeoutMs before releasing the PeerIndex. The player-observer tests
// drive the registered SpatialHashAreaOfInterest rather than the fixture's substituted AoI, because
// phase 1 works by clearing the board and the grid cell that the real query reads.
public partial class PeerSimulationTests
{
    /// <summary>
    ///     The whole point of the split: PlayerLeft reaches the observer within the sweep's worst
    ///     case measured from the lifecycle event, while the clock never advances past
    ///     <see cref="PeerOptions.DisconnectionCleanTimeoutMs" /> — so no assertion here can be
    ///     satisfied by phase-2 cleanup.
    /// </summary>
    [Test]
    public void PlayerLeft_ReachesObserverWithinSweepBound_WhenDisconnectedEventArrives()
    {
        PeerSimulation gridSimulation = CreateGridBackedSimulation();
        PlaceObserverAndSubjectInOneCell();

        gridSimulation.SimulateTick(peers, tickCounter: 0);

        Assert.That(DrainAllMessages().Where(m => m.To == observer).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined),
            "precondition: the observer knows about the subject");

        DispatchDisconnected(subject);

        for (uint tick = 1; tick <= FirstSweepTickAfter(0); tick++)
            gridSimulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages().Where(m => m.To == observer).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft),
            "PlayerLeft must land within VIEW_STALE_TICKS + SWEEP_CHECK_INTERVAL ticks of the "
          + "lifecycle event, not of phase-2 cleanup");

        peerIndexAllocator.DidNotReceive().Release(subject);
    }

    /// <summary>
    ///     Phase 1 in isolation: the subject is out of the interest set on the very next tick,
    ///     while its <see cref="PeerState" /> is still parked in the worker's peer set.
    /// </summary>
    [Test]
    public void DisconnectedSubject_StopsBeingCollected_OnTheTickAfterTheLifecycleEvent()
    {
        PeerSimulation gridSimulation = CreateGridBackedSimulation();
        PlaceObserverAndSubjectInOneCell();

        gridSimulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // The subject's last movement lands before the lifecycle event, mirroring the ordering the
        // worker channel guarantees. A still-collected subject would produce a delta from it.
        PublishSubjectInParcel(subject, seq: 3, parcel: 0, worldPos: new Vector3(14f, 0f, 14f));

        DispatchDisconnected(subject);

        gridSimulation.SimulateTick(peers, tickCounter: 1);

        List<ServerMessage.MessageOneofCase> cases = DrainAllMessages()
                                                   .Where(m => m.To == observer)
                                                   .Select(m => m.Message.MessageCase)
                                                   .ToList();

        Assert.That(cases, Has.None.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
        Assert.That(cases, Has.None.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));

        Assert.That(gridSimulation.observerViews[observer][subject].LastSeenTick, Is.Zero,
            "the view must go unstamped from the tick after the lifecycle event — that is what "
          + "starts the sweep clock");

        Assert.That(peers, Does.ContainKey(subject));
        Assert.That(peers[subject].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING),
            "phase 1 must not remove the peer — the slot stays parked for phase 2");
    }

    /// <summary>
    ///     The deferred half must not have moved: the slot is released only once
    ///     <see cref="PeerOptions.DisconnectionCleanTimeoutMs" /> has elapsed, which is after the
    ///     sweep's worst case.
    /// </summary>
    [Test]
    public void DisconnectedSubject_PeerIndexStaysParked_UntilCleanupTimeoutElapses()
    {
        PeerSimulation gridSimulation = CreateGridBackedSimulation();
        PlaceObserverAndSubjectInOneCell();

        gridSimulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        DispatchDisconnected(subject);

        uint sweptAt = FirstSweepTickAfter(0);

        for (uint tick = 1; tick <= sweptAt; tick++)
            gridSimulation.SimulateTick(peers, tick);

        peerIndexAllocator.DidNotReceive().Release(subject);
        Assert.That(peers, Does.ContainKey(subject));

        // Past the default 5000 ms cleanup timeout — phase 2 finally runs.
        timeProvider.MonotonicTime.Returns(6000u);
        gridSimulation.SimulateTick(peers, sweptAt + 1);

        peerIndexAllocator.Received(1).Release(subject);
        Assert.That(peers, Does.Not.ContainKey(subject));
    }

    /// <summary>
    ///     A scene listener collects straight from the grid and the board, so phase 1 reaches it
    ///     through exactly the same two calls — no AoI implementation in between.
    /// </summary>
    [Test]
    public void SceneListener_DisconnectedSubject_StopsBeingCollected_ThenSweptWithPlayerLeft()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined),
            "precondition: the listener knows about the subject");

        // The subject's last movement lands before the lifecycle event, mirroring the ordering the
        // worker channel guarantees. A still-collected subject would produce a delta from it.
        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(9f, 0f, 8f));

        DispatchDisconnected(subject);

        simulation.SimulateTick(peers, tickCounter: 2);

        Assert.That(DrainAllMessages().Where(m => m.To == listener), Is.Empty,
            "the cleared board and grid entries drop the subject from the listener's collection at once");

        for (uint tick = 3; tick <= FirstSweepTickAfter(1); tick++)
            simulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    ///     A simulation wired to the registered AoI implementation over the fixture's own grid and
    ///     board, so what makes a subject disappear is the phase-1 clear rather than a
    ///     test-controlled list of visible subjects.
    /// </summary>
    private PeerSimulation CreateGridBackedSimulation() =>
        new (
            new SpatialHashAreaOfInterest(spatialGrid, snapshotBoard,
                Options.Create(new SpatialHashAreaOfInterestOptions())),
            snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, peerIndexAllocator,
            Substitute.For<ILogger<PeerSimulation>>());

    /// <summary>
    ///     Places the observer and the subject a few units apart in one realm and one grid cell,
    ///     which puts the subject inside the default TIER_0 radius.
    /// </summary>
    private void PlaceObserverAndSubjectInOneCell()
    {
        PublishSubjectInParcel(observer, seq: 2, parcel: 0, worldPos: new Vector3(4f, 0f, 4f));
        PublishSubjectInParcel(subject, seq: 2, parcel: 0, worldPos: new Vector3(8f, 0f, 8f));
    }

    /// <summary>
    ///     Runs the production seam: a <c>Disconnected</c> lifecycle event drained by
    ///     <see cref="PeersManager" /> against the worker's own peer set.
    /// </summary>
    private void DispatchDisconnected(PeerIndex peer)
    {
        using PeersManager manager = CreatePeersManager();

        Channel<IncomingEvent> events = Channel.CreateUnbounded<IncomingEvent>();
        events.Writer.TryWrite(IncomingEvent.Disconnected(peer));

        manager.DrainEvents(events.Reader, peers, workerIndex: 0);
    }

    private PeersManager CreatePeersManager() =>
        new (
            messagePipe, new PeerStateFactory(), areaOfInterest, snapshotBoard, spatialGrid,
            identityBoard, new PeerOptions(), Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(), timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(), profileBoard, new ClientMessageCounters(),
            emoteCompleter, peerIndexAllocator,
            new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
            {
                PreAuthBudget = 0, MaxConcurrentPreAuthPerIP = 0,
            })),
            DisabledIpLimiter());

    /// <summary>
    ///     Cap switched off — the limiter counts connections but refuses none, so it never
    ///     interferes with the lifecycle path under test.
    /// </summary>
    private static IpLimiter DisabledIpLimiter()
    {
        IOptionsMonitor<IpLimiterOptions> optionsMonitor = Substitute.For<IOptionsMonitor<IpLimiterOptions>>();
        optionsMonitor.CurrentValue.Returns(new IpLimiterOptions { Enabled = false, MaxConcurrency = 0 });
        optionsMonitor.OnChange(Arg.Any<Action<IpLimiterOptions, string?>>()).Returns(Substitute.For<IDisposable>());
        return new IpLimiter(optionsMonitor, Substitute.For<ILogger<IpLimiter>>());
    }
}
