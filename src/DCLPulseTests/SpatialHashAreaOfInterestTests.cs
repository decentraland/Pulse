using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
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

    private RealmSpatialGrids grids;
    private SnapshotBoard snapshotBoard;
    private IdentityBoard identityBoard;
    private SpatialHashAreaOfInterest aoi;
    private InterestCollector collector;

    [SetUp]
    public void SetUp()
    {
        grids = new RealmSpatialGrids(CELL_SIZE, MAX_PEERS);
        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        identityBoard = new IdentityBoard(MAX_PEERS);
        collector = new InterestCollector();

        var options = Substitute.For<IOptions<SpatialHashAreaOfInterestOptions>>();

        options.Value.Returns(new SpatialHashAreaOfInterestOptions
        {
            Tier0Radius = TIER_0_RADIUS,
            Tier1Radius = TIER_1_RADIUS,
            MaxRadius = MAX_RADIUS,
            CellSize = CELL_SIZE,
        });

        aoi = new SpatialHashAreaOfInterest(grids, snapshotBoard, identityBoard, options);
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

        grids.Remove(subject);

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
        grids.Set(subject, REALM, newPos);
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
    public void PeersAtTheSameCoordinatesInDifferentRealms_SeeOnlyTheirOwn()
    {
        // Realms occupy independent grids, so identical coordinates do not collide across them.
        PeerIndex observerA = new (0);
        PeerIndex subjectA = new (1);
        PeerIndex observerB = new (2);
        PeerIndex subjectB = new (3);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observerA, observerPos, "realm-a");
        SetupPeer(subjectA, subjectPos, "realm-a");
        SetupPeer(observerB, observerPos, "realm-b");
        SetupPeer(subjectB, subjectPos, "realm-b");

        PeerSnapshot snapshotA = MakeSnapshot(observerPos, realm: "realm-a");
        aoi.GetVisibleSubjects(observerA, in snapshotA, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subjectA));

        collector.Clear();

        PeerSnapshot snapshotB = MakeSnapshot(observerPos, realm: "realm-b");
        aoi.GetVisibleSubjects(observerB, in snapshotB, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subjectB));
    }

    [Test]
    public void SubjectChangingRealm_LeavesTheOldRealmBehind()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);

        Vector3 observerPos = new (100, 0, 100);
        Vector3 subjectPos = new (110, 0, 100);

        SetupPeer(observer, observerPos, "realm-a");
        SetupPeer(subject, subjectPos, "realm-a");

        // Teleporting to another realm at the same spot moves the subject between grids.
        grids.Set(subject, "realm-b", subjectPos);
        PublishSnapshot(subject, subjectPos, realm: "realm-b");

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPos, realm: "realm-a");
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.EqualTo(0));
    }

    [TestCase("realm-b")]
    [TestCase("REALM-A")]
    public void SubjectTeleportsWhileItsOldCellIsBeingCollected_NotVisibleInOldRealm(string destinationRealm)
    {
        PeerIndex observer = new (0);
        PeerIndex first = new (1);
        PeerIndex second = new (2);
        Vector3 observerPosition = Vector3.Zero;
        SetupPeer(observer, observerPosition);
        SetupPeer(first, new Vector3(1, 0, 1));
        SetupPeer(second, new Vector3(2, 0, 2));

        SpatialGrid? oldGrid = grids.GetGrid(REALM);
        Assert.That(oldGrid, Is.Not.Null);

        HashSet<PeerIndex>? retainedOccupants = oldGrid?.GetPeers(grids.ComputeCellKey(1, 1));
        Assert.That(retainedOccupants, Is.Not.Null);

        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, Substitute.For<ITimeProvider>());
        IInterestCollector interleavingCollector = Substitute.For<IInterestCollector>();
        PeerIndex teleported = default;
        bool didTeleport = false;

        interleavingCollector.When(c => c.Add(Arg.Any<PeerIndex>(), Arg.Any<PeerViewSimulationTier>(),
            Arg.Any<PeerSnapshot>(), Arg.Any<IdentityRegistration>())).Do(call =>
        {
            PeerIndex accepted = call.ArgAt<PeerIndex>(0);
            collector.Add(accepted, call.ArgAt<PeerViewSimulationTier>(1), call.ArgAt<PeerSnapshot>(2),
                call.ArgAt<IdentityRegistration>(3));

            if (didTeleport)
                return;

            didTeleport = true;
            teleported = accepted == first ? second : first;
            publisher.PublishTeleport(teleported, new TeleportRequest
            {
                Realm = destinationRealm,
                ParcelIndex = parcelEncoder.Encode(0, 0),
                PositionXQuantized = 2,
                PositionZQuantized = 2,
            });
        });

        PeerSnapshot observerSnapshot = MakeSnapshot(observerPosition);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, interleavingCollector);

        Assert.That(didTeleport, Is.True);
        Assert.That(retainedOccupants, Does.Contain(teleported), "The query retains the old copy-on-write cell set.");
        Assert.That(oldGrid?.GetPeers(grids.ComputeCellKey(1, 1)), Does.Not.Contain(teleported));
        Assert.That(snapshotBoard.TryRead(teleported, out PeerSnapshot destination), Is.True);
        Assert.That(destination.Realm, Is.EqualTo(destinationRealm));
        Assert.That(collector.Entries.Select(entry => entry.Subject), Does.Not.Contain(teleported));
        Assert.That(collector.Count, Is.EqualTo(1));
    }

    [Test]
    public void SubjectMovesToLaterScannedCell_AcceptedOnce()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        SetupPeer(observer, Vector3.Zero);
        SetupPeer(subject, new Vector3(1, 0, 1));
        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot acceptedSnapshot), Is.True);
        IdentityRegistration? acceptedIdentity = identityBoard.GetIdentity(subject);

        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, Substitute.For<ITimeProvider>());
        IInterestCollector interleavingCollector = Substitute.For<IInterestCollector>();
        bool didTeleport = false;

        interleavingCollector.When(c => c.Add(Arg.Any<PeerIndex>(), Arg.Any<PeerViewSimulationTier>(),
            Arg.Any<PeerSnapshot>(), Arg.Any<IdentityRegistration>())).Do(call =>
        {
            collector.Add(call.ArgAt<PeerIndex>(0), call.ArgAt<PeerViewSimulationTier>(1), call.ArgAt<PeerSnapshot>(2),
                call.ArgAt<IdentityRegistration>(3));

            if (didTeleport)
                return;

            didTeleport = true;
            publisher.PublishTeleport(subject, new TeleportRequest
            {
                Realm = REALM,
                ParcelIndex = parcelEncoder.Encode(3, 0),
                PositionXQuantized = 3,
                PositionZQuantized = 1,
            });
        });

        PeerSnapshot observerSnapshot = MakeSnapshot(Vector3.Zero);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, interleavingCollector);

        Assert.That(didTeleport, Is.True);
        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
        Assert.That(collector.Entries[0].Snapshot, Is.EqualTo(acceptedSnapshot));
        Assert.That(collector.Entries[0].Identity, Is.SameAs(acceptedIdentity));
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
        grids.Set(subject, REALM, subjectPos);
        snapshotBoard.SetActive(subject);
        identityBoard.Set(subject, "subject-wallet");
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
        // Edge case: an authenticated subject that has never sent a TeleportRequest has snapshots
        // but no realm, so it was never placed in a grid. An observer with a valid realm scans its
        // own realm's grid and finds nothing there to see.
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

    [Test]
    public void AcceptedSnapshot_LaterPublicationsAndRingEvictionDoNotReplaceIt()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        SetupPeer(observer, Vector3.Zero);
        SetupPeer(subject, new Vector3(1, 0, 1));
        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot accepted), Is.True);
        IdentityRegistration? identity = identityBoard.GetIdentity(subject);

        PeerSnapshot observerSnapshot = MakeSnapshot(Vector3.Zero);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        for (int i = 0; i < RING_CAPACITY; i++)
            PublishSnapshot(subject, new Vector3(200 + i, 0, 200), realm: "realm-b");

        Assert.That(snapshotBoard.TryRead(subject, accepted.Seq, out _), Is.False);
        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Snapshot, Is.EqualTo(accepted));
        Assert.That(collector.Entries[0].Identity, Is.SameAs(identity));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
    }

    [Test]
    public void SubjectWithoutIdentity_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        SetupPeer(observer, Vector3.Zero);
        SetupPeer(subject, new Vector3(1, 0, 1));
        identityBoard.Remove(subject);

        PeerSnapshot observerSnapshot = MakeSnapshot(Vector3.Zero);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.Zero);
    }

    [Test]
    public void SubjectWithInactiveSnapshot_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        SetupPeer(observer, Vector3.Zero);
        SetupPeer(subject, new Vector3(1, 0, 1));
        snapshotBoard.ClearActive(subject);

        PeerSnapshot observerSnapshot = MakeSnapshot(Vector3.Zero);
        aoi.GetVisibleSubjects(observer, in observerSnapshot, collector);

        Assert.That(collector.Count, Is.Zero);
    }

    [Test]
    public void Listener_AnnouncedParcelFiltersOtherOccupantsOfCoveringCell()
    {
        PeerIndex observer = new (0);
        PeerIndex inside = new (1);
        PeerIndex outside = new (2);
        SetupPeer(observer, new Vector3(1, 0, 1), REALM, parcel: 10);
        SetupPeer(inside, new Vector3(2, 0, 2), REALM, parcel: 10);
        SetupPeer(outside, new Vector3(3, 0, 3), REALM, parcel: 20);
        Assert.That(snapshotBoard.TryRead(inside, out PeerSnapshot accepted), Is.True);

        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>> { [REALM] = [10] },
            [grids.ComputeCellKey(1, 1)]);
        aoi.GetVisibleSubjects(observer, listener, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(inside));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
        Assert.That(collector.Entries[0].Snapshot, Is.EqualTo(accepted));
        Assert.That(collector.Entries[0].Identity, Is.SameAs(identityBoard.GetIdentity(inside)));
    }

    [Test]
    public void Listener_ParcelMembershipIsSpecificToItsAnnouncedRealm()
    {
        PeerIndex observer = new (0);
        PeerIndex insideA = new (1);
        PeerIndex outsideA = new (2);
        PeerIndex insideB = new (3);
        PeerIndex outsideB = new (4);
        Vector3 position = new (1, 0, 1);
        SetupPeer(insideA, position, REALM, parcel: 10);
        SetupPeer(outsideA, position, REALM, parcel: 20);
        SetupPeer(insideB, position, "realm-b", parcel: 20);
        SetupPeer(outsideB, position, "realm-b", parcel: 10);

        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>>
        {
            [REALM] = [10],
            ["realm-b"] = [20],
        }, [grids.ComputeCellKey(1, 1)]);
        aoi.GetVisibleSubjects(observer, listener, collector);

        Assert.That(collector.Entries.Select(entry => entry.Subject), Is.EquivalentTo(new[] { insideA, insideB }));
    }

    [Test]
    public void Listener_RepeatedCoveringCell_SubjectAcceptedOnce()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        SetupPeer(subject, new Vector3(1, 0, 1), REALM, parcel: 10);
        long cellKey = grids.ComputeCellKey(1, 1);

        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>> { [REALM] = [10] },
            [cellKey, cellKey]);
        aoi.GetVisibleSubjects(observer, listener, collector);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
    }

    [Test]
    public void Listener_SubjectTeleportsFromRetainedCellToUnannouncedParcelInAnotherObservedRealm_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex first = new (1);
        PeerIndex second = new (2);
        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        int sourceParcel = parcelEncoder.Encode(0, 0);
        SetupPeer(first, new Vector3(1, 0, 1), REALM, sourceParcel);
        SetupPeer(second, new Vector3(2, 0, 2), REALM, sourceParcel);

        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>>
        {
            [REALM] = [sourceParcel],
            ["realm-b"] = [parcelEncoder.Encode(1, 0)],
        }, [grids.ComputeCellKey(1, 1)]);
        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, Substitute.For<ITimeProvider>());
        IInterestCollector interleavingCollector = Substitute.For<IInterestCollector>();
        PeerIndex teleported = default;
        bool didTeleport = false;

        interleavingCollector.When(c => c.Add(Arg.Any<PeerIndex>(), Arg.Any<PeerViewSimulationTier>(),
            Arg.Any<PeerSnapshot>(), Arg.Any<IdentityRegistration>())).Do(call =>
        {
            PeerIndex accepted = call.ArgAt<PeerIndex>(0);
            collector.Add(accepted, call.ArgAt<PeerViewSimulationTier>(1), call.ArgAt<PeerSnapshot>(2),
                call.ArgAt<IdentityRegistration>(3));

            if (didTeleport)
                return;

            didTeleport = true;
            teleported = accepted == first ? second : first;
            publisher.PublishTeleport(teleported, new TeleportRequest
            {
                Realm = "realm-b",
                ParcelIndex = sourceParcel,
                PositionXQuantized = 2,
                PositionZQuantized = 2,
            });
        });

        aoi.GetVisibleSubjects(observer, listener, interleavingCollector);

        Assert.That(didTeleport, Is.True);
        Assert.That(collector.Entries.Select(entry => entry.Subject), Does.Not.Contain(teleported));
        Assert.That(collector.Count, Is.EqualTo(1));
    }

    [Test]
    public void Listener_SubjectTeleportsAfterAcceptance_FirstObservedRealmSnapshotWins()
    {
        PeerIndex observer = new (0);
        PeerIndex subject = new (1);
        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        int parcel = parcelEncoder.Encode(0, 0);
        SetupPeer(subject, new Vector3(1, 0, 1), REALM, parcel);
        Assert.That(snapshotBoard.TryRead(subject, out PeerSnapshot accepted), Is.True);

        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>>
        {
            [REALM] = [parcel],
            ["realm-b"] = [parcel],
        }, [grids.ComputeCellKey(1, 1)]);
        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, Substitute.For<ITimeProvider>());
        IInterestCollector interleavingCollector = Substitute.For<IInterestCollector>();
        bool didTeleport = false;

        interleavingCollector.When(c => c.Add(Arg.Any<PeerIndex>(), Arg.Any<PeerViewSimulationTier>(),
            Arg.Any<PeerSnapshot>(), Arg.Any<IdentityRegistration>())).Do(call =>
        {
            collector.Add(call.ArgAt<PeerIndex>(0), call.ArgAt<PeerViewSimulationTier>(1), call.ArgAt<PeerSnapshot>(2),
                call.ArgAt<IdentityRegistration>(3));

            if (didTeleport)
                return;

            didTeleport = true;
            publisher.PublishTeleport(subject, new TeleportRequest
            {
                Realm = "realm-b",
                ParcelIndex = parcel,
                PositionXQuantized = 2,
                PositionZQuantized = 2,
            });
        });

        aoi.GetVisibleSubjects(observer, listener, interleavingCollector);

        Assert.That(didTeleport, Is.True);
        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Snapshot, Is.EqualTo(accepted));
    }

    [Test]
    public void Listener_UnannouncedRealmAndEmptyAnnouncement_ReturnNoSubjects()
    {
        PeerIndex observer = new (0);
        SetupPeer(new PeerIndex(1), new Vector3(1, 0, 1), "realm-b", parcel: 10);
        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>> { [REALM] = [10] },
            [grids.ComputeCellKey(1, 1)]);

        aoi.GetVisibleSubjects(observer, listener, collector);
        Assert.That(collector.Count, Is.Zero);

        aoi.GetVisibleSubjects(observer, new SceneListenerState([], []), collector);
        Assert.That(collector.Count, Is.Zero);
    }

    [Test]
    public void Listener_UnregisteredAndInactiveSubjects_NotVisible()
    {
        PeerIndex observer = new (0);
        PeerIndex unregistered = new (1);
        PeerIndex inactive = new (2);
        SetupPeer(unregistered, new Vector3(1, 0, 1), REALM, parcel: 10);
        SetupPeer(inactive, new Vector3(2, 0, 2), REALM, parcel: 10);
        identityBoard.Remove(unregistered);
        snapshotBoard.ClearActive(inactive);
        var listener = new SceneListenerState(new Dictionary<string, HashSet<int>> { [REALM] = [10] },
            [grids.ComputeCellKey(1, 1)]);

        aoi.GetVisibleSubjects(observer, listener, collector);

        Assert.That(collector.Count, Is.Zero);
    }

    private void SetupPeer(PeerIndex peer, Vector3 position) =>
        SetupPeer(peer, position, REALM);

    private void SetupPeer(PeerIndex peer, Vector3 position, string? realm, int parcel = 0)
    {
        // A peer with no realm belongs to no grid, which is exactly how the publisher treats it.
        if (realm is not null)
            grids.Set(peer, realm, position);

        snapshotBoard.SetActive(peer);
        identityBoard.Set(peer, $"wallet-{peer.Value}");
        PublishSnapshot(peer, position, realm, parcel);
    }

    private void PublishSnapshot(PeerIndex peer, Vector3 position, string? realm = REALM, int parcel = 0)
    {
        // Keep the global position exact; the positional wire codes retain their defaults.
        snapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: snapshotBoard.LastSeq(peer) + 1,
            parcel: parcel,
            globalPosition: position,
            realm: realm));
    }

    private static PeerSnapshot MakeSnapshot(Vector3 position, string? realm = REALM) =>
        TestSnapshots.Make(seq: 1, globalPosition: position, realm: realm);
}
