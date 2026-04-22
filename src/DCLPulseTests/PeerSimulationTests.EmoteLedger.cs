using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

/// <summary>
///     Pins the <see cref="SnapshotBoard" /> emote-ledger invariant: every snapshot between
///     EmoteStart and EmoteStop carries the active emote forward; the stop marker is transient;
///     a post-stop movement resolves to idle. Consequence: the latest snapshot is self-sufficient
///     for "is this peer emoting" regardless of ring depth.
/// </summary>
public partial class PeerSimulationTests
{
    [Test]
    public void Publish_InheritsEmote_OnIntermediateSnapshot()
    {
        // EmoteStart at seq 2, then a plain movement snapshot at seq 3 with Emote=null.
        // The ledger should carry the emote forward onto seq 3.
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "clap", startTick: 1000, durationMs: 500);
        PublishSnapshot(subject, seq: 3, position: new Vector3(7f, 0f, 0f));

        Assert.That(snapshotBoard.TryRead(subject, seq: 3, out PeerSnapshot read), Is.True);
        Assert.That(read.Emote, Is.Not.Null);
        Assert.That(read.Emote!.Value.EmoteId, Is.EqualTo("clap"));
        Assert.That(read.Emote.Value.StartTick, Is.EqualTo(1000u));
        Assert.That(read.Emote.Value.DurationMs, Is.EqualTo(500u));
        Assert.That(read.Emote.Value.StopReason, Is.Null);

        // Position still reflects the new snapshot's own data.
        Assert.That(read.LocalPosition.X, Is.EqualTo(7f));
    }

    [Test]
    public void Publish_ClearsEmote_AfterStopMarker()
    {
        // Stop markers are transient — consumed by the next publish so subsequent snapshots
        // correctly resolve to idle. Prevents a lingering StopReason from being carried forward.
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "clap", startTick: 1000, durationMs: 500);
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 1000, reason: EmoteStopReason.Cancelled);
        PublishSnapshot(subject, seq: 4, position: new Vector3(2f, 0f, 0f));

        Assert.That(snapshotBoard.TryRead(subject, seq: 4, out PeerSnapshot postStop), Is.True);
        Assert.That(postStop.Emote, Is.Null, "Post-stop snapshot must resolve to idle.");

        Assert.That(snapshotBoard.TryRead(subject, seq: 3, out PeerSnapshot stopSnap), Is.True);
        Assert.That(stopSnap.Emote, Is.Not.Null);
        Assert.That(stopSnap.Emote!.Value.StopReason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void Publish_CarriesEmote_AcrossRingWrap()
    {
        // The motivating scenario: EmoteStart rotates out of the ring, yet the latest snapshot
        // still reports the active emote thanks to the carry-forward, so EmoteCompleter can still
        // finalize a long-running one-shot after the original EmoteStart slot has been overwritten.
        peers[subject] = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "clap", startTick: 1000, durationMs: 500);

        // Flood the ring with more than RING_CAPACITY movement snapshots — the original EmoteStart
        // slot at seq 2 will be overwritten by seq (2 + RING_CAPACITY).
        for (uint s = 3; s < 3 + RING_CAPACITY + 5; s++)
            PublishSnapshot(subject, seq: s, position: new Vector3(s, 0f, 0f));

        // Original start must no longer be in the ring.
        Assert.That(snapshotBoard.TryRead(subject, seq: 2, out _), Is.False,
            "Sanity check — the EmoteStart seq should have been evicted by the flood.");

        // ...but the latest snapshot still carries the active emote through the ledger.
        Assert.That(snapshotBoard.IsEmoting(subject), Is.True);
        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot latest), Is.True);
        Assert.That(latest.Emote, Is.Not.Null);
        Assert.That(latest.Emote!.Value.EmoteId, Is.EqualTo("clap"));
        Assert.That(latest.Emote.Value.StartTick, Is.EqualTo(1000u));
        Assert.That(latest.Emote.Value.DurationMs, Is.EqualTo(500u));

        // EmoteCompleter can now finalize the expired one-shot.
        timeProvider.MonotonicTime.Returns(1500u);
        emoteCompleter.CompleteExpiredEmotes(peers);

        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot afterCompletion), Is.True);
        Assert.That(afterCompletion.Emote, Is.Not.Null);
        Assert.That(afterCompletion.Emote!.Value.StopReason, Is.EqualTo(EmoteStopReason.Completed));
    }

    [Test]
    public void Publish_ReplacesEmote_WhenNewStartArrivesDuringActive()
    {
        // A fresh EmoteStart supersedes the previously active emote even without an explicit stop.
        // Matches the existing "new emote replaces old" semantics — see EmoteStopSuppressed_WhenEmoteStartedSentSameTick.
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "wave", startTick: 100, durationMs: 3000);
        PublishEmoteSnapshot(subject, seq: 3, emoteId: "clap", startTick: 200, durationMs: 500);

        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot latest), Is.True);
        Assert.That(latest.Emote, Is.Not.Null);
        Assert.That(latest.Emote!.Value.EmoteId, Is.EqualTo("clap"));
        Assert.That(latest.Emote.Value.StartTick, Is.EqualTo(200u));
        Assert.That(latest.Emote.Value.DurationMs, Is.EqualTo(500u));
    }

    [Test]
    public void EmoteStarted_StillAnnounced_WhenRealStartEvictedFromRing()
    {
        // Ongoing observer: sees the subject first (no emote), baseline = seq 1. Subject then
        // starts emoting and floods the ring with carry-forward movement snapshots so the real
        // start slot is evicted. On the next tick the scan finds no real start in range, but
        // the ledger has carried the active emote onto every carry in the ring. The fallback
        // picks the *earliest* carry in range (closest in time/position to the true start),
        // not the latest, then announces the emote with the original StartTick.
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // Tick 0: observer sees subject idle — view initialized with LastSentEmote = null.
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Real EmoteStart at seq 2, then flood with carries to evict seq 2.
        // Each carry's position tracks `s` so we can tell which snapshot the fallback picked.
        PublishEmoteSnapshot(subject, seq: 2, emoteId: "dance", startTick: 20, position: new Vector3(2f, 0f, 0f));

        for (uint s = 3; s < 3 + RING_CAPACITY + 2; s++)
            PublishSnapshot(subject, seq: s, position: new Vector3(s, 0f, 0f));

        Assert.That(snapshotBoard.TryRead(subject, seq: 2, out _), Is.False,
            "Sanity — the real EmoteStart slot should have been evicted.");

        // Earliest surviving carry in the scan range. With RING_CAPACITY = 10 we flood 12 snapshots
        // (seqs 3..14), which evicts seqs 2..4 from the ring. The first carry still in the ring is seq 5.
        Assert.That(snapshotBoard.TryRead(subject, seq: 5, out _), Is.True,
            "Sanity — the earliest surviving carry should still be in the ring.");

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);

        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("dance"));

        // StartTick preserved by the ledger across the carry-forward window.
        Assert.That(emoteMsg.Message.EmoteStarted.ServerTick, Is.EqualTo(20u));

        // Fallback picks the EARLIEST carry in range (seq 5), not the latest (seq 14).
        Assert.That(emoteMsg.Message.EmoteStarted.Sequence, Is.EqualTo(5u));
        Assert.That(emoteMsg.Message.EmoteStarted.PlayerState.Position.X, Is.EqualTo(5f));
    }

    [Test]
    public void SendTracked_LogsWarningWithEvictionContext_WhenEvictedEmoteStartCollidesWithPriorSend()
    {
        // Use a capturable logger so we can assert the warning's content.
        ILogger<PeerSimulation>? simulationLogger = Substitute.For<ILogger<PeerSimulation>>();

        var sim = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, Substitute.For<IPeerIndexAllocator>(), simulationLogger);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // Tick 0: fresh visibility. Subject is emoting at seq 1 — HandleNewSubject takes the
        // eviction path because the only source of the emote is latestSnapshot (no intermediate
        // scan happens on first visibility). PlayerJoined is sent directly (not via SendTracked),
        // and SendEmoteStarted is then invoked with fromEviction=true; if the seq happens to
        // collide with view.LastSentSeq the tripwire must log a warning naming "evicted", not
        // an error.
        PublishEmoteSnapshot(subject, seq: 1, emoteId: "dance", startTick: 10);
        sim.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        bool evictionWarningLogged = simulationLogger.ReceivedCalls()
                                                     .Any(call =>
                                                      {
                                                          if (call.GetMethodInfo().Name != nameof(ILogger.Log))
                                                              return false;

                                                          object?[] args = call.GetArguments();

                                                          if (args.Length < 3 || args[0] is not LogLevel.Warning)
                                                              return false;

                                                          var formatted = args[2]?.ToString();

                                                          return formatted is not null
                                                                 && formatted.Contains("Duplicate seq")
                                                                 && formatted.Contains("evicted");
                                                      });

        bool errorLogged = simulationLogger.ReceivedCalls()
                                           .Any(call =>
                                                call.GetMethodInfo().Name == nameof(ILogger.Log)
                                                && call.GetArguments().Length >= 1
                                                && call.GetArguments()[0] is LogLevel.Error);

        // The only path to the duplicate-seq check here is via the eviction-tagged EmoteStarted
        // in HandleNewSubject (Phase 4). When seq collisions happen, they must be warnings
        // naming the eviction context — never errors.
        Assert.That(errorLogged, Is.False,
            "Eviction-path duplicate must not be logged as an error.");

        Assert.That(evictionWarningLogged || simulationLogger.ReceivedCalls()
                                                             .All(call =>
                                                                  call.GetMethodInfo().Name != nameof(ILogger.Log)
                                                                  || call.GetArguments()[0] is not LogLevel.Warning
                                                                  || call.GetArguments()[2]?.ToString()?.Contains("Duplicate seq") != true),
            "If a duplicate-seq warning fires, it must mention the eviction context.");
    }

    [Test]
    public void EmoteStarted_DiscriminatesRealStart_ByStartSeq_NotByServerTick()
    {
        // Multiple snapshots can legitimately share a ServerTick — e.g. a teleport at seq 2
        // and an EmoteStart at seq 3 processed on the same `now`. A ServerTick-based start
        // discriminator would then misidentify the teleport carry-forward (which inherits the
        // EmoteStart's StartTick) as the real start. StartSeq is per-snapshot and monotonic,
        // so `Seq == StartSeq` picks unambiguously.
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Seq 2 teleport and seq 3 EmoteStart — both stamped with ServerTick = 100.
        // The EmoteStart's StartSeq = 3 (its own Seq). The teleport is published later as a
        // carry-forward that inherits the emote (in reverse order here to mimic the teleport
        // arriving after a fresh start — the point is just that ServerTick collides).
        snapshotBoard.SetActive(subject);

        snapshotBoard.Publish(subject, new PeerSnapshot(
            Seq: 2, ServerTick: 100, Parcel: 0,
            LocalPosition: new Vector3(1f, 0f, 0f), GlobalPosition: new Vector3(1f, 0f, 0f), Velocity: Vector3.Zero,
            RotationY: 0f, JumpCount: 0, MovementBlend: 0f, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed));

        snapshotBoard.Publish(subject, new PeerSnapshot(
            Seq: 3, ServerTick: 100, Parcel: 0,
            LocalPosition: new Vector3(2f, 0f, 0f), GlobalPosition: new Vector3(2f, 0f, 0f), Velocity: Vector3.Zero,
            RotationY: 0f, JumpCount: 0, MovementBlend: 0f, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Emote: new EmoteState("wave", StartSeq: 3, StartTick: 100)));

        // Seq 4: a trailing carry — same ServerTick as the "start", but Seq differs so it
        // must NOT be treated as a real start.
        snapshotBoard.Publish(subject, new PeerSnapshot(
            Seq: 4, ServerTick: 100, Parcel: 0,
            LocalPosition: new Vector3(3f, 0f, 0f), GlobalPosition: new Vector3(3f, 0f, 0f), Velocity: Vector3.Zero,
            RotationY: 0f, JumpCount: 0, MovementBlend: 0f, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed));

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage emoteMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);

        Assert.That(emoteMsg.Message.EmoteStarted.Sequence, Is.EqualTo(3u),
            "Scan must pick seq 3 (the real start, Seq == StartSeq), not seq 4 (carry with the same ServerTick).");

        Assert.That(emoteMsg.Message.EmoteStarted.PlayerState.Position.X, Is.EqualTo(2f));
    }

    [Test]
    public void EmoteStarted_PicksRealStart_WhenMultipleStartsInScanRange()
    {
        // Multi-emote scan: "wave" starts at seq 2, stops at seq 3, "clap" starts at seq 4,
        // movement carries "clap" at seq 5. The scan must pick seq 4 as the real start of the
        // current emote (ServerTick == StartTick) — not seq 5 (a carry-forward) and not seq 2
        // (superseded by the stop at seq 3).
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        PublishEmoteSnapshot(subject, seq: 2, emoteId: "wave", startTick: 20);
        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20);
        PublishEmoteSnapshot(subject, seq: 4, emoteId: "clap", startTick: 40);
        PublishSnapshot(subject, seq: 5, position: new Vector3(9f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        OutgoingMessage clapStart = messages.First(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted
            && m.Message.EmoteStarted.EmoteId == "clap");

        Assert.That(clapStart.Message.EmoteStarted.Sequence, Is.EqualTo(4u),
            "Scan must pick the real start (seq 4) via ServerTick == StartTick, not the carry at seq 5.");

        Assert.That(clapStart.Message.EmoteStarted.ServerTick, Is.EqualTo(40u));
    }
}
