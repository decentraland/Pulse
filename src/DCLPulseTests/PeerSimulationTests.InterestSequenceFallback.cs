using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Transport;
using System.Diagnostics.Metrics;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_EvictedTargetInPlayerRealm_DeliversLatestAndCountsOnce(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "old", globalPosition: new Vector3(200, 0, 0)));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        // The hard fallback deliberately retains the previous realm-only player guard.
        AssertAcceptedInterestMessage(previouslyVisible, 3 + RING_CAPACITY, 9);
        Assert.Multiple(() =>
        {
            Assert.That(evictions, Is.EqualTo(new[] { 1L }));
            Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.EqualTo(1u));
        });
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(evictions, Is.EqualTo(new[] { 1L }), "The same eviction must not be counted again on a new query");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_EvictedTargetOutsidePlayerRealm_RetiresViewOnce(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "new"));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        AssertEvictionRejection(previouslyVisible, evictions);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(evictions, Is.EqualTo(new[] { 1L }));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InterestSequence_EvictedTargetOutsideListenerAoi_RetiresViewOnce(bool previouslyVisible, bool realmChange)
    {
        PrepareCapturedInterest(previouslyVisible, sceneListener: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, realmChange ? "new" : "old", parcel: realmChange ? 0 : 5));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        AssertEvictionRejection(previouslyVisible, evictions);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_EvictedTargetInsideListenerAoi_DeliversLatest(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible, sceneListener: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "old"));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        AssertAcceptedInterestMessage(previouslyVisible, 3 + RING_CAPACITY, 9);
        Assert.That(evictions, Is.EqualTo(new[] { 1L }));
    }

    [Test]
    public void InterestSequence_EvictedTargetInAnotherAnnouncedListenerRealm_RetiresThenJoinsLatest()
    {
        PrepareCapturedInterest(previouslyVisible: true, sceneListener: true);
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0], ["new"] = [5] });
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "new", parcel: 5));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
        Assert.Multiple(() =>
        {
            Assert.That(messages[1].Message.PlayerJoined.Realm, Is.EqualTo("new"));
            Assert.That(messages[1].Message.PlayerJoined.State.Sequence, Is.EqualTo(3u + RING_CAPACITY));
            Assert.That(messages[1].Message.PlayerJoined.State.State.PositionXQuantized,
                Is.EqualTo(9).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(evictions, Is.EqualTo(new[] { 1L }));
        });
    }

    [Test]
    public void InterestSequence_EvictedTargetWithResyncOnSkippedTier_ServesLatestReliably()
    {
        PrepareCapturedInterest(previouslyVisible: true, resyncWithDelta: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0), globalPosition: new Vector3(60, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "old", globalPosition: new Vector3(60, 0, 0)));
        AddResyncRequest(observer, subject, knownSeq: 2);
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        OutgoingMessage message = DrainSingleMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
            Assert.That(message.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateFull));
            Assert.That(message.Message.PlayerStateFull.Sequence, Is.EqualTo(3u + RING_CAPACITY));
            Assert.That(message.Message.PlayerStateFull.State.PositionXQuantized,
                Is.EqualTo(9).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(peers[observer].ResyncRequests, Is.Empty);
            Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.EqualTo(1u));
            Assert.That(evictions, Is.EqualTo(new[] { 1L }));
        });
    }

    [Test]
    public void InterestSequence_EvictedTargetOnSkippedTier_StampsViewWithoutDelivery()
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0), globalPosition: new Vector3(60, 0, 0));
        InterleaveAfterSpatialCollection(() => EvictAcceptedInterest(3, "old", globalPosition: new Vector3(60, 0, 0)));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        Assert.Multiple(() =>
        {
            Assert.That(DrainAllMessages(), Is.Empty);
            Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.EqualTo(1u));
            Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Seq, Is.EqualTo(2u));
            Assert.That(evictions, Is.EqualTo(new[] { 1L }));
        });
    }

    [Test]
    public void InterestSequence_RetainedTargetAfterRealmChange_DeliversExactSequenceWithoutEviction()
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, "new", new Vector3(9, 0, 0), teleport: true));
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        AssertAcceptedInterestMessage(previouslyVisible: true, 3, 2);
        Assert.That(evictions, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSequence_RecycledSlotWithSameSequence_DiscardsEntryWithoutEviction(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() =>
        {
            snapshotBoard.ClearActive(subject);
            identityBoard.Remove(subject);
            identityBoard.Set(subject, "0xSUBJECT_WALLET");
            snapshotBoard.SetActive(subject);
            PublishInterestSnapshot(3, "old", new Vector3(9, 0, 0));
        });
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        Assert.Multiple(() =>
        {
            Assert.That(DrainAllMessages(), Is.Empty);
            Assert.That(evictions, Is.Empty);
            if (previouslyVisible)
                Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.Zero);
            else
                Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
        });
    }

    [Test]
    public void InterestSequence_DisconnectedSlotAfterEviction_IsNotCountedAsLiveEviction()
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() =>
        {
            EvictAcceptedInterest(3, "old");
            snapshotBoard.ClearActive(subject);
        });
        (MeterListener listener, List<long> evictions) = CaptureHistogram("pulse.sim.interest_snapshot_evicted");
        using MeterListener _ = listener;

        simulation.SimulateTick(peers, 1);

        Assert.Multiple(() =>
        {
            Assert.That(DrainAllMessages(), Is.Empty);
            Assert.That(evictions, Is.Empty);
            Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.Zero);
        });
    }

    private void EvictAcceptedInterest(uint acceptedSeq, string realm, int parcel = 0, Vector3? globalPosition = null)
    {
        for (uint seq = acceptedSeq + 1; seq <= acceptedSeq + RING_CAPACITY; seq++)
            PublishInterestSnapshot(seq, realm, new Vector3(9, 0, 0), parcel: parcel, globalPosition: globalPosition);
        Assert.That(snapshotBoard.TryRead(subject, acceptedSeq, out _), Is.False);
    }

    private void AssertEvictionRejection(bool previouslyVisible, List<long> evictions)
    {
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.Multiple(() =>
        {
            Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(previouslyVisible
                ? new[] { ServerMessage.MessageOneofCase.PlayerLeft }
                : Array.Empty<ServerMessage.MessageOneofCase>()));
            Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
            Assert.That(evictions, Is.EqualTo(new[] { 1L }));
        });
        if (previouslyVisible)
            Assert.That(messages[0].Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
    }
}
