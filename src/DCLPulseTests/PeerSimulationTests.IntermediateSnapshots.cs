using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void EmoteStarted_DetectedFromIntermediateSnapshot()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        // Multiple snapshots between ticks — emote starts at seq 3, seq 4 is a trailing movement
        // that inherits the emote via the ledger. ScanIntermediateEvents must still latch onto the
        // real start (seq 3) via the ServerTick == StartTick discriminator, not the latest carry.
        PublishSnapshot(subject, seq: 2);
        PublishEmoteSnapshot(subject, seq: 3);
        PublishSnapshot(subject, seq: 4);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);

        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);
        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(emoteMsg.Message.EmoteStarted.Sequence, Is.EqualTo(3u));
    }

    [Test]
    public void EmoteStarted_CarriesStateFromEmoteSnapshot_NotFromLatest()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Emote snapshot at position A, trailing movement at position B. Even though the ledger
        // carries the emote forward onto seq 3, the scan picks the real start (seq 2) via
        // ServerTick == StartTick — so EmoteStarted carries the position the subject had when
        // they began emoting, not a later carry-forward position.
        PublishEmoteSnapshot(subject, seq: 2, position: new Vector3(10f, 20f, 30f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(99f, 99f, 99f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);

        Assert.That(emoteMsg.Message.EmoteStarted.PlayerState.Position.X, Is.EqualTo(10f));
        Assert.That(emoteMsg.Message.EmoteStarted.PlayerState.Position.Y, Is.EqualTo(20f));
        Assert.That(emoteMsg.Message.EmoteStarted.PlayerState.Position.Z, Is.EqualTo(30f));
    }

    [Test]
    public void Teleport_DetectedFromIntermediateSnapshot()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Teleport is intermediate, followed by normal movement
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(50f, 60f, 70f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(51f, 60f, 70f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);

        OutgoingMessage teleportMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.Message.Teleported.State.Position.X, Is.EqualTo(50f));
        Assert.That(teleportMsg.Message.Teleported.Sequence, Is.EqualTo(2u));
    }

    [Test]
    public void TeleportAndEmote_BothDetected_FromIntermediateSnapshots()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Teleport at seq 2, emote at seq 3, normal movement at seq 4
        // emote metadata is now inline in the snapshot
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(50f, 0f, 0f));
        PublishEmoteSnapshot(subject, seq: 3, position: new Vector3(50f, 0f, 0f));
        PublishSnapshot(subject, seq: 4, position: new Vector3(51f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);

        // No delta or full state — discrete events already carried full player state
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void DiscreteEvent_AdvancesBaseline_ToEventSnapshot()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Emote at seq 2 (pos X=10), normal movement at seq 3 (pos X=20)
        // emote metadata is now inline in the snapshot
        PublishEmoteSnapshot(subject, seq: 2, position: new Vector3(10f, 0f, 0f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(20f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // EmoteStarted only — discrete event suppressed delta

        // Next tick: new movement at seq 4 (pos X=30)
        // Delta should diff from baseline = seq 2 (emote snapshot), not seq 3
        PublishSnapshot(subject, seq: 4, position: new Vector3(30f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage deltaMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta);

        // Baseline was seq 2 (X=10), target is seq 4 (X=30) — X changed
        Assert.That(deltaMsg.Message.PlayerStateDelta.HasPositionX, Is.True);
        Assert.That(deltaMsg.Message.PlayerStateDelta.PositionXQuantized, Is.EqualTo(30f));
    }

    [Test]
    public void DiscreteEvent_SuppressesDelta_EvenWhenLatestSnapshotDiffers()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Teleport in intermediate, latest snapshot differs significantly
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(100f, 0f, 0f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(101f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        // Only teleport, no delta despite the positional difference
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void DiscreteEvent_ConsumesResync_WhenFoundInIntermediates()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Teleport in intermediates + pending resync
        AddResyncRequest(observer, subject, knownSeq: 1);
        PublishTeleportSnapshot(subject, seq: 3, new Vector3(50f, 0f, 0f));
        PublishSnapshot(subject, seq: 4);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        // Teleport consumes the resync — no STATE_FULL sent
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void EmoteStarted_ConsumesResync_WhenFoundInIntermediates()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Emote in intermediates + pending resync
        AddResyncRequest(observer, subject, knownSeq: 1);

        // emote metadata is now inline in the snapshot
        PublishEmoteSnapshot(subject, seq: 3);
        PublishSnapshot(subject, seq: 4);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        // EmoteStarted consumes the resync — no STATE_FULL or delta sent
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
    }

    [Test]
    public void NoDiscreteEvents_FallsThroughToDelta()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Multiple normal snapshots — no teleport, no emote
        PublishSnapshot(subject, seq: 2, position: new Vector3(1f, 0f, 0f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(2f, 0f, 0f));
        PublishSnapshot(subject, seq: 4, position: new Vector3(3f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        // Only a delta, no discrete events
        Assert.That(messages.Count, Is.EqualTo(1));
        Assert.That(messages[0].Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
        Assert.That(messages[0].PacketMode, Is.EqualTo(PacketMode.UNRELIABLE_SEQUENCED));
    }

    [Test]
    public void EmoteStopSuppressed_WhenEmoteStartedSentSameTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Emote starts — observer records it
        // emote metadata is now inline in the snapshot
        PublishEmoteSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // EmoteStarted

        // New emote starts (preemptive) — the old emote was never explicitly stopped,
        // but a new one replaces it. The snapshot with IsEmote triggers EmoteStarted.
        // Phase 2 (TrySyncEmoteStop) must be skipped because emoteStartedSent=true.
        // emote metadata is now inline in the snapshot
        PublishEmoteSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStopped), Is.False);
    }

    [Test]
    public void Baseline_AfterTeleportInIntermediate_DiffsFromTeleportSnapshot()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Teleport at seq 2, then normal movement at seq 3
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(100f, 0f, 0f));
        PublishSnapshot(subject, seq: 3, position: new Vector3(101f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // Teleported

        // Next tick: new movement at seq 4
        PublishSnapshot(subject, seq: 4, position: new Vector3(102f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage deltaMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta);

        // Baseline was teleport at seq 2 (X=100), target is seq 4 (X=102)
        Assert.That(deltaMsg.Message.PlayerStateDelta.HasPositionX, Is.True);
        Assert.That(deltaMsg.Message.PlayerStateDelta.PositionXQuantized, Is.EqualTo(102f));
    }

    [Test]
    public void MultipleTeleports_InIntermediates_OnlyLastSent()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Two teleports between ticks — only the final destination matters
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10f, 0f, 0f));
        PublishTeleportSnapshot(subject, seq: 3, new Vector3(50f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        var teleports = messages
                       .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported)
                       .ToList();

        Assert.That(teleports.Count, Is.EqualTo(1));
        Assert.That(teleports[0].Message.Teleported.State.Position.X, Is.EqualTo(50f));
    }

    [Test]
    public void OnlyLastEmote_DetectedInIntermediates_WhenMultipleEmoteSnapshots()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Two emote snapshots — only the last one is the currently active emote
        PublishEmoteSnapshot(subject, seq: 2, position: new Vector3(1f, 0f, 0f));
        PublishEmoteSnapshot(subject, seq: 3, position: new Vector3(2f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        var emotes = messages
                    .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted)
                    .ToList();

        Assert.That(emotes.Count, Is.EqualTo(1));
        Assert.That(emotes[0].Message.EmoteStarted.Sequence, Is.EqualTo(3u));
    }
}
