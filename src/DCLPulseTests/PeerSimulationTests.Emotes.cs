using Decentraland.Pulse;
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
    public void EmoteStarted_SentWithSnapshotStartTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined

        timeProvider.MonotonicTime.Returns(4200u);
        PublishEmoteSnapshot(subject, seq: 2, startTick: 4200, durationMs: 3000);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(emoteMsg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(emoteMsg.Message.EmoteStarted.ServerTick, Is.EqualTo(4200u));
        Assert.That(emoteMsg.Message.EmoteStarted.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void EmoteStopped_SentWithStopSnapshotTick_WhenCancelled()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishEmoteSnapshot(subject, seq: 2, emoteId: "dance");
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Emote stop snapshot published by EmoteStopHandler
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage stopMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped);
        Assert.That(stopMsg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(stopMsg.Message.EmoteStopped.ServerTick, Is.EqualTo(30u)); // seq 3 * 10
        Assert.That(stopMsg.Message.EmoteStopped.Reason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void EmoteStopped_SentWithCompletedReason_WhenDurationExpires()
    {
        // The subject peer must be authenticated so EmoteCompleter scans it.
        peers[subject] = new PeerState(PeerConnectionState.AUTHENTICATED);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        timeProvider.MonotonicTime.Returns(1000u);
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "clap", startTick: 1000, durationMs: 500);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Advance time past the duration — EmoteCompleter (subject's worker loop) publishes a
        // real Completed stop snapshot with its own seq, which the observer picks up next tick.
        timeProvider.MonotonicTime.Returns(1500u);
        emoteCompleter.CompleteExpiredEmotes(peers);
        simulation.SimulateTick(peers, tickCounter: 2);

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

        PublishEmoteSnapshot(subject, seq: 2, durationMs: 3000);
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
        PublishEmoteSnapshot(subject, seq: 2, startTick: 1000, durationMs: durationMs);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> firstPlay = DrainAllMessages();
        OutgoingMessage firstEmote = firstPlay.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(firstEmote.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(firstEmote.Message.EmoteStarted.ServerTick, Is.EqualTo(1000u));

        if (!preemptive)
        {
            // End the emote: duration expiry for one-shot, stop snapshot for looping
            if (durationMs != null)
                timeProvider.MonotonicTime.Returns(1000u + durationMs.Value);
            else
                PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 1000);

            simulation.SimulateTick(peers, tickCounter: 2);
            DrainAllMessages();
        }

        // Second play of the same emote (preemptive: while first is still active)
        timeProvider.MonotonicTime.Returns(3000u);
        PublishEmoteSnapshot(subject, seq: preemptive ? 3u : 4u, startTick: 3000, durationMs: durationMs);
        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> secondPlay = DrainAllMessages();
        OutgoingMessage secondEmote = secondPlay.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(secondEmote.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(secondEmote.Message.EmoteStarted.ServerTick, Is.EqualTo(3000u));
    }

    [Test]
    public void EmoteStarted_SuppressesDelta_OnSameTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined

        // Subject moves AND starts emoting — EmoteStarted already carries PlayerState
        PublishEmoteSnapshot(subject, seq: 2, durationMs: 3000, position: new Vector3(5f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void EmoteStarted_SuppressesResync_OnSameTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume delta

        // Start emote + queue resync on the same tick
        AddResyncRequest(observer, subject, knownSeq: 1);
        PublishEmoteSnapshot(subject, seq: 3, emoteId: "dance");
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
    }

    [Test]
    public void EmoteStopped_SuppressesRedundantDelta_WhenStopIsLatest()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishEmoteSnapshot(subject, seq: 2, durationMs: 3000);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Stop is the latest snapshot in the ring. EmoteStopped already carries the full PlayerState,
        // so Phase 2 advances the baseline to the stop snapshot and Phase 3's delta (stop → stop)
        // suppresses early. The redundant delta is eliminated — and with it the duplicate-seq
        // collision that would have fired on SendTracked.
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20, position: new Vector3(5f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
    }

    [Test]
    public void EmoteStopped_AllowsDelta_WhenMovementFollowsInSameBatch()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishEmoteSnapshot(subject, seq: 2, durationMs: 3000);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Stop at seq 3, then movement at seq 4 — Phase 2 anchors the baseline to the stop,
        // Phase 3 diffs stop → movement and still delivers the post-stop position delta.
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20, position: new Vector3(5f, 0f, 0f));
        PublishSnapshot(subject, seq: 4, position: new Vector3(7f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.True);
    }

    [Test]
    public void Teleport_SentBeforeEmoteStarted_OnSameTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject teleports AND starts emoting in the same tick — separate snapshots
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(50, 60, 70));
        PublishEmoteSnapshot(subject, seq: 3, durationMs: 3000, position: new Vector3(50, 60, 70));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);

        // Teleported must precede EmoteStarted (teleport at seq 2 scanned before emote at seq 3)
        int teleportIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        int emoteIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(teleportIdx, Is.LessThan(emoteIdx));
    }

    [Test]
    public void Teleport_SentBeforeEmoteStopped_OnSameTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishEmoteSnapshot(subject, seq: 2, emoteId: "dance");
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume EmoteStarted

        // Subject cancels emote AND teleports in the same tick
        // Stop snapshot first, then teleport — phase 1 processes both
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20);
        PublishTeleportSnapshot(subject, seq: 4, new Vector3(50, 60, 70));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped), Is.True);

        int teleportIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        int emoteIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped);
        Assert.That(teleportIdx, Is.LessThan(emoteIdx));
    }

    [Test]
    public void PlayerJoined_AlsoAnnouncesActiveEmote_ForNewSubject()
    {
        // Subject is already emoting when observer first sees them. Thanks to the emote ledger,
        // latestSnapshot.Emote reflects the ongoing emote even if the original EmoteStart has
        // rotated out of the ring. HandleNewSubject announces the emote immediately so the
        // observer can animate it — without it the remainder of the emote would play silently.
        PublishEmoteSnapshot(subject, seq: 1, emoteId: "dance", startTick: 50);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);

        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("dance"));
        Assert.That(emoteMsg.Message.EmoteStarted.ServerTick, Is.EqualTo(50u));

        // PlayerJoined must precede the EmoteStarted so the client has the subject in its world
        // before the animation event arrives.
        int joinIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined);
        int emoteIdx = messages.FindIndex(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(joinIdx, Is.LessThan(emoteIdx));
    }
}
