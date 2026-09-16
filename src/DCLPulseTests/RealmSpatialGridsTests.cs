using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

/// <summary>
///     The routing and lifecycle rules of the per-realm grids: a peer occupies exactly one grid at a
///     time, moving realms vacates the old one, and a realm nobody occupies keeps no grid — realm names
///     come from clients, so retaining one per name ever seen would grow without bound.
/// </summary>
[TestFixture]
public class RealmSpatialGridsTests
{
    private const int MAX_PEERS = 16;
    private const float CELL_SIZE = 50f;

    private const string REALM_A = "realm-a";
    private const string REALM_B = "realm-b";

    private RealmSpatialGrids grids;

    [SetUp]
    public void SetUp()
    {
        grids = new RealmSpatialGrids(CELL_SIZE, MAX_PEERS);
    }

    [Test]
    public void UnknownRealm_HasNoGrid()
    {
        Assert.That(grids.GetGrid(REALM_A), Is.Null);
    }

    [Test]
    public void NullRealm_HasNoGrid()
    {
        // Collapses "this peer has no realm yet" into the same answer as "nobody is in that realm",
        // which is what lets interest management resolve a grid in one step.
        Assert.That(grids.GetGrid(null), Is.Null);
    }

    [Test]
    public void SetPeer_PlacesItInItsRealmsGrid()
    {
        PeerIndex peer = new (0);
        Vector3 position = new (10, 0, 10);

        grids.Set(peer, REALM_A, position);

        Assert.That(grids.PeersAt(REALM_A, position), Does.Contain(peer));
        Assert.That(grids.GetGrid(REALM_B), Is.Null);
    }

    [Test]
    public void SamePositionInTwoRealms_OccupiesTwoIndependentGrids()
    {
        PeerIndex peerA = new (0);
        PeerIndex peerB = new (1);
        Vector3 position = new (10, 0, 10);

        grids.Set(peerA, REALM_A, position);
        grids.Set(peerB, REALM_B, position);

        Assert.That(grids.PeersAt(REALM_A, position), Is.EquivalentTo(new[] { peerA }));
        Assert.That(grids.PeersAt(REALM_B, position), Is.EquivalentTo(new[] { peerB }));
    }

    [Test]
    public void MovingWithinARealm_LeavesOnlyTheNewCellOccupied()
    {
        PeerIndex peer = new (0);
        Vector3 from = new (10, 0, 10);
        Vector3 to = new (210, 0, 10);

        grids.Set(peer, REALM_A, from);
        grids.Set(peer, REALM_A, to);

        Assert.That(grids.PeersAt(REALM_A, from), Is.Null);
        Assert.That(grids.PeersAt(REALM_A, to), Does.Contain(peer));
    }

    [Test]
    public void SoloPeerMovingWithinARealm_KeepsTheRealmsGridAlive()
    {
        // The vacated cell empties the grid momentarily. Adding before removing is what keeps the
        // grid from being evicted and silently replaced mid-move.
        PeerIndex peer = new (0);
        Vector3 to = new (210, 0, 10);

        grids.Set(peer, REALM_A, new Vector3(10, 0, 10));
        SpatialGrid? before = grids.GetGrid(REALM_A);

        grids.Set(peer, REALM_A, to);

        Assert.That(grids.GetGrid(REALM_A), Is.SameAs(before));
        Assert.That(grids.PeersAt(REALM_A, to), Does.Contain(peer));
    }

    [Test]
    public void ChangingRealm_VacatesTheOldGrid()
    {
        PeerIndex peer = new (0);
        Vector3 position = new (10, 0, 10);

        grids.Set(peer, REALM_A, position);
        grids.Set(peer, REALM_B, position);

        Assert.That(grids.GetGrid(REALM_A), Is.Null, "the emptied realm must not keep a grid");
        Assert.That(grids.PeersAt(REALM_B, position), Does.Contain(peer));
    }

    [Test]
    public void ChangingRealm_LeavesOtherOccupantsOfTheOldRealmInPlace()
    {
        PeerIndex leaver = new (0);
        PeerIndex stayer = new (1);
        Vector3 position = new (10, 0, 10);

        grids.Set(leaver, REALM_A, position);
        grids.Set(stayer, REALM_A, position);
        grids.Set(leaver, REALM_B, position);

        Assert.That(grids.PeersAt(REALM_A, position), Is.EquivalentTo(new[] { stayer }));
        Assert.That(grids.PeersAt(REALM_B, position), Is.EquivalentTo(new[] { leaver }));
    }

    [Test]
    public void RemovingTheLastOccupant_DropsTheRealmsGrid()
    {
        PeerIndex peer = new (0);

        grids.Set(peer, REALM_A, new Vector3(10, 0, 10));
        grids.Remove(peer);

        Assert.That(grids.GetGrid(REALM_A), Is.Null);
    }

    [Test]
    public void RemovingOneOfTwoPeersSharingACell_KeepsTheRealmsGrid()
    {
        // The occupied-cell count backing eviction must track cells, not peers: emptying a shared cell
        // takes two removals, and decrementing on the first would evict a grid that still holds someone.
        PeerIndex first = new (0);
        PeerIndex second = new (1);
        Vector3 position = new (10, 0, 10);

        grids.Set(first, REALM_A, position);
        grids.Set(second, REALM_A, position);

        grids.Remove(first);

        Assert.That(grids.PeersAt(REALM_A, position), Is.EquivalentTo(new[] { second }));

        grids.Remove(second);

        Assert.That(grids.GetGrid(REALM_A), Is.Null);
    }

    [Test]
    public void PeerReturningToACellItLeft_DoesNotDoubleCountIt()
    {
        // A cell removed and re-created must not drift the count — otherwise a grid either never evicts
        // or evicts while occupied.
        PeerIndex peer = new (0);
        Vector3 first = new (10, 0, 10);
        Vector3 second = new (210, 0, 10);

        for (var i = 0; i < 10; i++)
        {
            grids.Set(peer, REALM_A, second);
            grids.Set(peer, REALM_A, first);
        }

        Assert.That(grids.PeersAt(REALM_A, first), Does.Contain(peer));
        Assert.That(grids.PeersAt(REALM_A, second), Is.Null);

        grids.Remove(peer);

        Assert.That(grids.GetGrid(REALM_A), Is.Null, "the count drifted, so the grid was not evicted");
    }

    [Test]
    public void Remove_IsIdempotentAndSafeForNeverPlacedPeers()
    {
        PeerIndex placed = new (0);
        PeerIndex neverPlaced = new (1);

        grids.Set(placed, REALM_A, new Vector3(10, 0, 10));

        Assert.DoesNotThrow(() =>
        {
            grids.Remove(neverPlaced);
            grids.Remove(placed);
            grids.Remove(placed);
        });

        Assert.That(grids.GetGrid(REALM_A), Is.Null);
    }

    [Test]
    public void RemovedPeerPlacedAgain_ReappearsInAFreshGrid()
    {
        // PeerIndex is a recycled ENet slot, so the per-peer bookkeeping must be genuinely cleared
        // rather than left pointing at the realm the previous occupant was in.
        PeerIndex peer = new (0);
        Vector3 position = new (10, 0, 10);

        grids.Set(peer, REALM_A, position);
        grids.Remove(peer);
        grids.Set(peer, REALM_B, position);

        Assert.That(grids.GetGrid(REALM_A), Is.Null);
        Assert.That(grids.PeersAt(REALM_B, position), Does.Contain(peer));
    }

    [Test]
    public void GetRealmGrids_EnumeratesOnlyOccupiedRealms()
    {
        grids.Set(new PeerIndex(0), REALM_A, new Vector3(10, 0, 10));
        grids.Set(new PeerIndex(1), REALM_B, new Vector3(10, 0, 10));
        grids.Set(new PeerIndex(2), "realm-c", new Vector3(10, 0, 10));
        grids.Remove(new PeerIndex(2));

        List<string> realms = [];

        foreach (RealmSpatialGrids.RealmGrid realmGrid in grids.GetRealmGrids())
            realms.Add(realmGrid.Realm);

        Assert.That(realms, Is.EquivalentTo(new[] { REALM_A, REALM_B }));
    }

    [Test]
    public void CellCoords_MatchTheCellAPeerIsIndexedIn()
    {
        PeerIndex peer = new (0);
        Vector3 position = new (-10, 0, 120);

        grids.Set(peer, REALM_A, position);
        grids.CellCoords(position, out int x, out int z);

        SpatialGrid? grid = grids.GetGrid(REALM_A);

        Assert.That(grid, Is.Not.Null);
        Assert.That(grid.GetPeers(SpatialGrid.PackKey(x, z)), Does.Contain(peer));
        Assert.That((x, z), Is.EqualTo((-1, 2)));
    }

    [Test]
    public void ManyRealmsVisitedInSequence_LeaveOneLiveGrid()
    {
        // The bound that makes client-supplied realm names safe: grids live only as long as they hold
        // a peer, so one peer touring a thousand realms can never accumulate a thousand grids.
        PeerIndex peer = new (0);

        for (var i = 0; i < 1000; i++)
            grids.Set(peer, $"realm-{i}", new Vector3(10, 0, 10));

        Assert.That(grids.LiveRealmCount(), Is.EqualTo(1));
        Assert.That(grids.GetGrid("realm-999"), Is.Not.Null);
        Assert.That(grids.GetGrid("realm-998"), Is.Null);
    }

    [Test]
    public void PeerMovingBetweenCells_IsNeverMissedByAConcurrentReader()
    {
        // Why Set adds before it vacates: at every instant the peer is in the new cell's published set
        // or still in the old one, and the realm's grid never vanishes under a reader.
        //
        // Exactly one move is in flight per round, which is what makes the check sound. A reader polls
        // the two cells one after the other, so with a writer moving repeatedly the peer could leave the
        // cell that was read first and be gone from the second by the time that read happens — a torn
        // pair of reads, not a lost peer. With a single move, either the first read precedes the vacate
        // (and sees the peer) or it follows it, in which case the add already happened and the second
        // read sees the peer. The multi-cell scan in interest management has no such bound: a subject
        // moving during a scan can be missed for one tick regardless of the write order.
        PeerIndex peer = new (0);
        Vector3 first = new (10, 0, 10);
        Vector3 second = new (210, 0, 10);

        for (var round = 0; round < 500; round++)
        {
            grids.Set(peer, REALM_A, first);

            string? failure = null;
            using var moved = new CancellationTokenSource();

            Task reader = Task.Run(() =>
            {
                while (!moved.IsCancellationRequested && failure is null)
                {
                    if (grids.GetGrid(REALM_A) is null)
                    {
                        failure = "the realm's grid was evicted while its only peer moved within it";
                        return;
                    }

                    if (grids.PeersAt(REALM_A, first)?.Contains(peer) != true
                        && grids.PeersAt(REALM_A, second)?.Contains(peer) != true)
                        failure = "the peer was in neither the old nor the new cell";
                }
            });

            grids.Set(peer, REALM_A, second);

            moved.Cancel();
            reader.Wait();

            Assert.That(failure, Is.Null, $"round {round}");
        }
    }

    [Test]
    public void PackKey_RoundTripsThroughUnpackKey()
    {
        SpatialGrid.UnpackKey(SpatialGrid.PackKey(-7, 13), out int x, out int z);

        Assert.That((x, z), Is.EqualTo((-7, 13)));
    }
}
