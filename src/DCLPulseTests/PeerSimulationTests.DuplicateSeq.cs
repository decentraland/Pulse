using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void SendTracked_DoesNotLogError_OnCompletedEmote_WithSubsequentDelta()
    {
        // Scenario that used to trigger the duplicate-seq tripwire: a one-shot emote expires
        // and the observer's subject has already moved past it. Before the EmoteCompleter
        // restructure, Phase 2 fired EmoteStopped(Completed) using latestSnapshot.Seq and
        // Phase 3 then sent a STATE_DELTA with target.Seq = latestSnapshot.Seq — same seq
        // twice to the same observer. With EmoteCompleter publishing a real stop snapshot
        // and Phase 2 advancing the baseline, the tripwire must stay silent.
        ILogger<PeerSimulation>? simulationLogger = Substitute.For<ILogger<PeerSimulation>>();

        var sim = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, simulationLogger);

        // EmoteCompleter needs the subject peer to be authenticated.
        peers[subject] = new PeerState(PeerConnectionState.AUTHENTICATED);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // Tick 0 — first visibility.
        sim.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Tick 1 — one-shot emote at seq 2 (500ms duration).
        timeProvider.MonotonicTime.Returns(1000u);
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "clap", startTick: 1000, durationMs: 500);
        sim.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Tick 2 — time past the duration. Per the protocol the client stops sending movement
        // while emoting, so the emote snapshot remains the latest in the ring until a stop lands.
        //   EmoteCompleter publishes Completed stop at seq 3 (real snapshot, own seq).
        //   Observer Phase 2 sends EmoteStopped at seq 3, advances baseline to seq 3.
        //   Observer Phase 3 diffs seq 3 → seq 3 and returns early — no duplicate, no delta.
        timeProvider.MonotonicTime.Returns(1500u);
        emoteCompleter.CompleteExpiredEmotes(peers);
        sim.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped
                                      && m.Message.EmoteStopped.Reason == EmoteStopReason.Completed),
            Is.True, "Completed EmoteStopped must be sent");

        bool errorLogged = simulationLogger.ReceivedCalls()
                                           .Any(call =>
                                                call.GetMethodInfo().Name == nameof(ILogger.Log)
                                                && call.GetArguments().Length >= 1
                                                && call.GetArguments()[0] is LogLevel.Error);

        Assert.That(errorLogged, Is.False,
            "Duplicate-seq tripwire must not fire: EmoteCompleter + Phase 2 baseline advance "
            + "eliminate the seq collision between EmoteStopped and the subsequent delta.");
    }

    [Test]
    public void SendTracked_DoesNotLogError_WhenSeqsAdvanceMonotonically()
    {
        ILogger<PeerSimulation>? simulationLogger = Substitute.For<ILogger<PeerSimulation>>();

        var sim = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, simulationLogger);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // Three ticks of plain movement — each publishes a fresh seq, delta goes out each tick.
        sim.SimulateTick(peers, tickCounter: 0);
        PublishSnapshot(subject, seq: 2, position: new Vector3(1f, 0f, 0f));
        sim.SimulateTick(peers, tickCounter: 1);
        PublishSnapshot(subject, seq: 3, position: new Vector3(2f, 0f, 0f));
        sim.SimulateTick(peers, tickCounter: 2);
        DrainAllMessages();

        bool anyErrorLogged = simulationLogger.ReceivedCalls()
                                              .Any(call =>
                                                   call.GetMethodInfo().Name == nameof(ILogger.Log)
                                                   && call.GetArguments().Length >= 1
                                                   && call.GetArguments()[0] is LogLevel.Error);

        Assert.That(anyErrorLogged, Is.False,
            "No error log should fire when seqs advance monotonically across ticks.");
    }
}
