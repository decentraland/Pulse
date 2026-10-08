using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Peers.Simulation;
using System.Diagnostics.Metrics;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_EvictedTeleportHistoryWithRetainedTarget_IsNotTargetEviction(bool realmRoundTrip)
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, realmRoundTrip ? "away" : "old", new Vector3(3, 0, 0), teleport: true);
        PublishInterestSnapshot(4, "old", new Vector3(4, 0, 0), teleport: true);
        uint targetSeq = 4u + RING_CAPACITY;
        for (uint seq = 5; seq <= targetSeq; seq++)
            PublishInterestSnapshot(seq, "old", new Vector3(9, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(snapshotBoard.TryRead(subject, 3, out _), Is.False);
            Assert.That(snapshotBoard.TryRead(subject, 4, out _), Is.False);
            Assert.That(snapshotBoard.TryRead(subject, targetSeq, out _), Is.True);
        });
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener evictionListener = listener;

        simulation.SimulateTick(peers, 1);

        OutgoingMessage message = DrainSingleMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
            Assert.That(message.Message.PlayerStateDelta.NewSeq, Is.EqualTo(targetSeq));
            Assert.That(message.Message.PlayerStateDelta.PositionXQuantized,
                Is.EqualTo(9).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(evictions, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_RegistrationChangesDuringAliasingRetirement_DoesNotSeedStaleTarget(bool recycle)
    {
        var simulationLogger = Substitute.For<ILogger<PeerSimulation>>();
        PrepareCapturedInterest(previouslyVisible: true, simulationLogger: simulationLogger);
        snapshotBoard.ClearActive(subject);
        identityBoard.Remove(subject);
        identityBoard.Set(subject, "0xSUBJECT_WALLET");
        snapshotBoard.SetActive(subject);
        PublishInterestSnapshot(3, "old", new Vector3(3, 0, 0));
        bool retiredDuringLog = false;
        simulationLogger.When(log => log.Log(
                Arg.Is<LogLevel>(level => level == LogLevel.Warning), Arg.Any<EventId>(),
                Arg.Any<Arg.AnyType>(), Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(_ =>
            {
                retiredDuringLog = true;
                snapshotBoard.ClearActive(subject);
                identityBoard.Remove(subject);
                if (!recycle)
                    return;

                identityBoard.Set(subject, "0xSUBJECT_WALLET");
                snapshotBoard.SetActive(subject);
                PublishInterestSnapshot(3, "old", new Vector3(9, 0, 0));
            });

        simulation.SimulateTick(peers, 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.Multiple(() =>
        {
            Assert.That(retiredDuringLog, Is.True);
            Assert.That(messages.Select(message => message.Message.MessageCase),
                Is.EqualTo(new[] { ServerMessage.MessageOneofCase.PlayerLeft }));
            Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
        });
    }
}
