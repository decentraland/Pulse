using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    /// <summary>
    ///     Fixture grid cellSize = 50. These tests stamp raw parcel indices onto snapshots and
    ///     place subjects at test-chosen world positions, so the covering cell keys are computed
    ///     directly from those positions (default: the cell containing the origin) rather than
    ///     through SceneListenerCellMapper — the mapper has its own tests in Task 2.
    /// </summary>
    private void MakeSceneListener(PeerIndex listener, string realm = "main", int[]? parcels = null, long[]? cellKeys = null)
    {
        peers[listener] = new PeerState(PeerConnectionState.AUTHENTICATED)
        {
            SceneListener = new SceneListenerState(realm,
                new HashSet<int>(parcels ?? []),
                cellKeys ?? [spatialGrid.ComputeCellKey(0f, 0f)]),
        };

        identityBoard.Set(listener, "0xLISTENER_WALLET");
    }

    private void PublishSubjectInParcel(PeerIndex peer, uint seq, int parcel, Vector3 worldPos, string realm = "main")
    {
        snapshotBoard.SetActive(peer);
        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: seq, serverTick: seq * 10, parcel: parcel,
            globalPosition: worldPos, realm: realm));
        spatialGrid.Set(peer, worldPos);
    }

    [Test]
    public void SceneListener_SubjectInsideParcel_GetsPlayerJoined()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage joined = messages.Single(m => m.To == listener && m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined);
        Assert.That(joined.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
    }

    [Test]
    public void SceneListener_SubjectInCellButOutsideParcelSet_Invisible()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);

        // Same grid cell (pos inside [0,50)²) but a different parcel index on the snapshot.
        PublishSubjectInParcel(subject, seq: 2, parcel: 6, worldPos: new Vector3(20f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(DrainAllMessages().Where(m => m.To == listener), Is.Empty,
            "Cell-level match must not leak subjects outside the announced parcels.");
    }

    [Test]
    public void SceneListener_CrossRealmSubject_Invisible()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "other", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f), realm: "main");

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(DrainAllMessages().Where(m => m.To == listener), Is.Empty);
    }

    [Test]
    public void SceneListener_MovingSubject_GetsDeltaEveryTick()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // PlayerJoined

        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(9f, 0f, 8f));

        // TIER_0 fires on every tick — including odd ticks that would gate TIER_1.
        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> messages = DrainAllMessages().Where(m => m.To == listener).ToList();
        Assert.That(messages.Single().Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
        Assert.That(messages.Single().PacketMode, Is.EqualTo(PacketMode.UNRELIABLE_SEQUENCED));
    }

    [Test]
    public void SceneListener_EmoteStart_SuppressedButPositionStillFlows()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Real emote start snapshot (Seq == StartSeq) inside the parcel.
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, parcel: 5,
            globalPosition: new Vector3(8f, 0f, 8f), realm: "main",
            emote: new EmoteState("wave", StartSeq: 3, StartTick: 30)));

        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages().Where(m => m.To == listener).ToList();
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.None.EqualTo(ServerMessage.MessageOneofCase.EmoteStarted),
            "Positional-only listeners must not receive emote broadcasts.");
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta),
            "The position carried by the emote snapshot must still arrive as a delta.");
    }

    [Test]
    public void SceneListener_MidEmoteJoin_GetsPlayerJoinedButNoEmoteStarted()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);

        // Subject is already mid-emote when the listener first sees it. A player observer would
        // get a companion EmoteStarted alongside PlayerJoined (see
        // PlayerJoined_AlsoAnnouncesActiveEmote_ForNewSubject); a positional-only listener must not.
        snapshotBoard.SetActive(subject);
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 2, serverTick: 20, parcel: 5,
            globalPosition: new Vector3(8f, 0f, 8f), realm: "main",
            emote: new EmoteState("wave", StartSeq: 2, StartTick: 20)));
        spatialGrid.Set(subject, new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages().Where(m => m.To == listener).ToList();
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined),
            "The mid-emote subject must still be announced to the listener.");
        Assert.That(messages.Select(m => m.Message.MessageCase),
            Has.None.EqualTo(ServerMessage.MessageOneofCase.EmoteStarted),
            "Listeners must not get the companion EmoteStarted that players receive on mid-emote join.");
    }

    [Test]
    public void SceneListener_ProfileAnnouncement_Suppressed()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        profileBoard.Set(subject, 42);
        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(8.5f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 2);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.None.EqualTo(ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced));
    }

    [Test]
    public void SceneListener_Teleport_StillDelivered()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, parcel: 5,
            globalPosition: new Vector3(12f, 0f, 12f), realm: "main", isTeleport: true));
        spatialGrid.Set(subject, new Vector3(12f, 0f, 12f));

        simulation.SimulateTick(peers, tickCounter: 2);

        Assert.That(DrainAllMessages().Where(m => m.To == listener).Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.Teleported));
    }

    [Test]
    public void SceneListener_SubjectLeavesParcels_SweptWithPlayerLeft()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Subject moves far away — different cell, different parcel.
        PublishSubjectInParcel(subject, seq: 3, parcel: 900, worldPos: new Vector3(500f, 0f, 500f));

        // Advance past the sweep interval.
        for (uint tick = 2; tick <= SWEEP_INTERVAL * 2 + 1; tick++)
            simulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages()
                .Where(m => m.To == listener)
                .Select(m => m.Message.MessageCase),
            Has.Some.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft));
    }

    [Test]
    public void SceneListener_Resync_ServedWithReliableResponse()
    {
        var listener = new PeerIndex(9);
        MakeSceneListener(listener, realm: "main", parcels: [5]);
        PublishSubjectInParcel(subject, seq: 2, parcel: 5, worldPos: new Vector3(8f, 0f, 8f));

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        PublishSubjectInParcel(subject, seq: 3, parcel: 5, worldPos: new Vector3(9f, 0f, 8f));
        AddResyncRequest(listener, subject, knownSeq: 1);

        simulation.SimulateTick(peers, tickCounter: 2);

        OutgoingMessage response = DrainAllMessages().Single(m => m.To == listener);
        Assert.That(response.PacketMode, Is.EqualTo(PacketMode.RELIABLE),
            "Resync responses ride the reliable channel for listeners exactly as for players.");
    }
}
