using Decentraland.Pulse;
using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void EmoteStarted_SentWithEmoteBoardStartTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined

        timeProvider.MonotonicTime.Returns(4200u);
        emoteBoard.Start(subject, "wave", serverTick: 4200, durationMs: 3000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(emoteMsg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(emoteMsg.Message.EmoteStarted.ServerTick, Is.EqualTo(4200u));
        Assert.That(emoteMsg.Message.EmoteStarted.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void EmoteStopped_SentWithEmoteBoardStopTick_WhenCancelled()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        emoteBoard.Start(subject, "dance", serverTick: 4000, durationMs: null);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        emoteBoard.Stop(subject, serverTick: 5500);
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage stopMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped);
        Assert.That(stopMsg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(stopMsg.Message.EmoteStopped.ServerTick, Is.EqualTo(5500u));
        Assert.That(stopMsg.Message.EmoteStopped.Reason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void EmoteStopped_SentWithCompletedReason_WhenDurationExpires()
    {
        peers[subject] = new PeerState(PeerConnectionState.AUTHENTICATED);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        timeProvider.MonotonicTime.Returns(1000u);
        emoteBoard.Start(subject, "clap", serverTick: 1000, durationMs: 500);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Advance time past the duration — TryComplete runs on the subject's own
        // outer-loop iteration; the observer picks up the completed state next tick
        timeProvider.MonotonicTime.Returns(1500u);
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);
        DrainAllMessages();

        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage stopMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped);
        Assert.That(stopMsg.Message.EmoteStopped.Reason, Is.EqualTo(EmoteStopReason.Completed));
    }

    [Test]
    public void EmoteStarted_NotSentAgain_WhenAlreadySynced()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        emoteBoard.Start(subject, "wave", serverTick: 4000, durationMs: 3000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Same emote still active, next tick should not re-send
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.False);
    }

    [Test]
    public void EmoteStopped_NotSent_WhenNoEmoteWasActive()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // No emote started, just tick
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped), Is.False);
    }

    [TestCase(null, false, TestName = "Looping_AfterCancel")]
    [TestCase(500u, false, TestName = "OneShot_AfterExpiration")]
    [TestCase(null, true, TestName = "Looping_Preemptive")]
    [TestCase(500u, true, TestName = "OneShot_Preemptive")]
    public void SameEmotePlayedTwice_PropagatedTwice(uint? durationMs, bool preemptive)
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // First play
        timeProvider.MonotonicTime.Returns(1000u);
        emoteBoard.Start(subject, "wave", serverTick: 1000, durationMs: durationMs);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> firstPlay = DrainAllMessages();
        OutgoingMessage firstEmote = firstPlay.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(firstEmote.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(firstEmote.Message.EmoteStarted.ServerTick, Is.EqualTo(1000u));

        if (!preemptive)
        {
            // End the emote: duration expiry for one-shot, client cancel for looping
            if (durationMs != null)
                timeProvider.MonotonicTime.Returns(1000u + durationMs.Value);
            else
                emoteBoard.Stop(subject, serverTick: 2000);

            simulation.SimulateTick(peers, tickCounter: 2);
            DrainAllMessages();
        }

        // Second play of the same emote (preemptive: while first is still active)
        timeProvider.MonotonicTime.Returns(3000u);
        emoteBoard.Start(subject, "wave", serverTick: 3000, durationMs: durationMs);
        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> secondPlay = DrainAllMessages();
        OutgoingMessage secondEmote = secondPlay.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(secondEmote.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(secondEmote.Message.EmoteStarted.ServerTick, Is.EqualTo(3000u));
    }
}
