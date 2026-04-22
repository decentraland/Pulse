using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class SpatialHashAreaOfInterestTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 4;
    private const float CELL_SIZE = 50f;
    private const float TIER_0_RADIUS = 20f;
    private const float TIER_1_RADIUS = 50f;
    private const float MAX_RADIUS = 100f;

    private const string REALM = "realm-a";

    private SpatialGrid grid;
    private SnapshotBoard snapshotBoard;
    private SpatialHashAreaOfInterest aoi;
    private InterestCollector collector;

    [SetUp]
    public void SetUp()
    {
        grid = new SpatialGrid(CELL_SIZE, MAX_PEERS);
        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        collector = new InterestCollector();

        var options = Substitute.For<IOptions<SpatialHashAreaOfInterestOptions>>();

        options.Value.Returns(new SpatialHashAreaOfInterestOptions
        {
            Tier0Radius = TIER_0_RADIUS,
            Tier1Radius = TIER_1_RADIUS,
            MaxRadius = MAX_RADIUS,
            CellSize = CELL_SIZE,
        });

        aoi = new SpatialHashAreaOfInterest(grid, snapshotBoard, options);
    }

    [Test]
    public void SubjectInSameCell_WithinTier0_ReturnsTier0()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
    }

    [Test]
    public void SubjectInSameCell_WithinTier1_ReturnsTier1()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (130, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_1));
    }

    [Test]
    public void SubjectInSameCell_WithinTier2_ReturnsTier2()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (160, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_2));
    }

    [Test]
    public void SubjectBeyondMaxRadius_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (300, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [Test]
    public void ObserverDoesNotSeeItself()
    {
        PeerIndex observer = new (0);

        Vector3 observerPos = new (100, 0, 100);
        SetupPeer(observer, observerPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [Test]
    public void MultipleSubjects_ReturnsAllVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex close = new (1);
        PeerIndex mid = new (2);
        PeerIndex far = new (3);

        Vector3 observerPos = new (100, 0, 100);
        SetupPeer(observer, observerPos);
        SetupPeer(close, new Vector3(110, 0, 100));
        SetupPeer(mid, new Vector3(140, 0, 100));
        SetupPeer(far, new Vector3(500, 0, 100));

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(2));

        PeerIndex[] subjects = collector.Entries.Take(2).Select(e => e.Subject).ToArray();
        Assert.That(subjects, Does.Contain(close));
        Assert.That(subjects, Does.Contain(mid));
    }

    [Test]
    public void RemovedPeer_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        grid.Remove(subject);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [Test]
    public void PeerMovesToNewCell_VisibleFromNewPosition()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (200, 0, 200);
        SetupPeer(observer, observerPos);

        // Subject starts far away
        SetupPeer(subject, new Vector3(0, 0, 0));

        // Move subject close to observer
        Vector3 newPos = new (210, 0, 200);
        grid.Set(subject, newPos);
        PublishSnapshot(subject, newPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
    }

    [Test]
    public void NegativePositions_WorkCorrectly()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (-100, 0, -100);
        Vector3 subjectPos = new (-90, 0, -100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
    }

    [Test]
    public void DistanceUsesXZPlane_YIgnored()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (105, 9999, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
    }

    [Test]
    public void FarAwayPeers_StillVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex near = new (1);
        PeerIndex far = new (2);
        Vector3 observerPos = new (20000, 0, 20000);
        Vector3 nearPos = new (20010, 0, 20000);
        Vector3 farPos = new (20099, 0, 20000);
        SetupPeer(observer, observerPos);
        SetupPeer(near, nearPos);
        SetupPeer(far, farPos);
        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);
        Assert.That(collector.Count, Is.EqualTo(2));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(near));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
        Assert.That(collector.Entries[1].Subject, Is.EqualTo(far));
        Assert.That(collector.Entries[1].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_2));
    }

    [Test]
    public void SubjectInDifferentRealm_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos, "realm-a");
        SetupPeer(subject, subjectPos, "realm-b");

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos, realm: "realm-a");
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [Test]
    public void ObserverWithoutRealm_SeesNobody()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        // Observer has no realm — subject is otherwise perfectly in range and in a realm.
        SetupPeer(observer, observerPos, realm: null);
        SetupPeer(subject, subjectPos);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos, realm: null);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [Test]
    public void SubjectWithInheritedRealm_Visible()
    {
        // End-to-end realm-ledger check: a teleport-style publish seeds realm-a on seq 1, then
        // an input-style publish at seq 2 passes Realm=null. The SnapshotBoard inherits, so AoI
        // reads realm-a on the latest snapshot and the observer sees the subject.
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos);
        grid.Set(subject, subjectPos);
        snapshotBoard.SetActive(subject);
        PublishSnapshot(subject, subjectPos); // seq 1, explicit realm
        PublishSnapshot(subject, subjectPos, realm: null); // seq 2, inherits realm

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos, realm: REALM);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
    }

    [Test]
    public void SubjectWithoutRealm_NotVisible()
    {
        // Edge case: an authenticated subject that has never sent a TeleportRequest is in the
        // spatial grid and has snapshots, but its realm is null. An observer with a valid realm
        // must not see it — AoI filters by exact string equality, and `null != "realm-a"`.
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos);
        SetupPeer(subject, subjectPos, realm: null);

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos, realm: REALM);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    private void SetupPeer(PeerIndex peer, Vector3 position) =>
        SetupPeer(peer, position, REALM);

    private void SetupPeer(PeerIndex peer, Vector3 position, string? realm)
    {
        grid.Set(peer, position);
        snapshotBoard.SetActive(peer);
        PublishSnapshot(peer, position, realm);
    }

    private void PublishSnapshot(PeerIndex peer, Vector3 position, string? realm = REALM)
    {
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: snapshotBoard.LastSeq(peer) + 1,
            ServerTick: 0,
            Parcel: 0,
            LocalPosition: position,
            GlobalPosition: position,
            Velocity: Vector3.Zero,
            RotationY: 0f,
            MovementBlend: 0f,
            JumpCount: 0,
            SlideBlend: 0f,
            HeadYaw: null,
            HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Realm: realm));
    }

    private static PeerSnapshot MakeSnapshot(Vector3 position, string? realm = REALM) =>
        new (Seq: 1, ServerTick: 0, Parcel: 0,
            LocalPosition: position, Velocity: Vector3.Zero,
            GlobalPosition: position,
            RotationY: 0f, MovementBlend: 0f, JumpCount: 0, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Realm: realm);
}
