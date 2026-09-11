using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerCellMapperTests
{
    // ParcelEncoderOptions defaults: ParcelSize 16. Grid cellSize 100 → parcel (0,0) spans
    // world [0,16)² inside cell (0,0).
    private const int PARCEL_SIZE = 16;

    private RealmSpatialGrids grids;
    private SceneListenerCellMapper mapper;

    [SetUp]
    public void SetUp()
    {
        IOptions<ParcelEncoderOptions> options = Options.Create(new ParcelEncoderOptions());
        grids = new RealmSpatialGrids(100, 100);
        mapper = new SceneListenerCellMapper(grids, options);
    }

    [Test]
    public void SingleParcelInsideOneCell_CoversThatCell()
    {
        // Parcel (1,1) spans world [16,32)² — fully inside grid cell (0,0).
        HashSet<long> keys = Cover(1, 1, 1, 1);

        var peer = new PeerIndex(7);
        grids.Set(peer, "main", new Vector3(20f, 0f, 20f));

        Assert.That(keys.Any(k => grids.GetGrid("main")?.GetPeers(k)?.Contains(peer) == true), Is.True,
            "A peer standing inside the parcel must be reachable through the covering cell keys.");
    }

    [Test]
    public void ParcelStraddlingCellBoundary_CoversBothCells()
    {
        // Parcel (6,0) spans world x [96,112) — straddles the x=100 cell boundary.
        HashSet<long> keys = Cover(6, 0, 6, 0);

        var left = new PeerIndex(1);
        var right = new PeerIndex(2);
        grids.Set(left, "main", new Vector3(97f, 0f, 5f));
        grids.Set(right, "main", new Vector3(105f, 0f, 5f));

        Assert.Multiple(() =>
        {
            Assert.That(keys.Any(k => grids.GetGrid("main")?.GetPeers(k)?.Contains(left) == true), Is.True);
            Assert.That(keys.Any(k => grids.GetGrid("main")?.GetPeers(k)?.Contains(right) == true), Is.True);
        });
    }

    [Test]
    public void AdjacentParcelsInSameCell_DedupeKeys()
    {
        // Parcels (1,1)..(2,1) both live inside cell (0,0) — the cover must not multiply.
        HashSet<long> keys = Cover(1, 1, 2, 1);

        Assert.That(keys, Has.Count.LessThanOrEqualTo(4),
            "Two adjacent interior parcels must not multiply covering cells.");
    }

    [Test]
    public void RectCover_MatchesEveryParcelInsideIt()
    {
        // The cover is derived from the rect's two corners rather than from each parcel's four
        // corners. That shortcut is only valid while the two agree exactly, so pin the
        // equivalence: an 8×8 rect spans world [0,128)², crossing both cell boundaries at 100.
        HashSet<long> fromRect = Cover(0, 0, 7, 7);
        var fromParcels = new HashSet<long>();

        for (int z = 0; z <= 7; z++)
        for (int x = 0; x <= 7; x++)
        {
            float minX = x * PARCEL_SIZE;
            float minZ = z * PARCEL_SIZE;

            fromParcels.Add(grids.ComputeCellKey(minX, minZ));
            fromParcels.Add(grids.ComputeCellKey(minX + PARCEL_SIZE, minZ));
            fromParcels.Add(grids.ComputeCellKey(minX, minZ + PARCEL_SIZE));
            fromParcels.Add(grids.ComputeCellKey(minX + PARCEL_SIZE, minZ + PARCEL_SIZE));
        }

        Assert.That(fromRect, Is.EquivalentTo(fromParcels));
    }

    [Test]
    public void SeparateRects_AccumulateIntoOneCover()
    {
        var keys = new HashSet<long>();
        mapper.AddCoveringCells(keys, 0, 0, 0, 0);
        mapper.AddCoveringCells(keys, 40, 40, 40, 40);

        Assert.That(keys, Is.EquivalentTo(Cover(0, 0, 0, 0).Union(Cover(40, 40, 40, 40))),
            "Accumulating rects into a shared set must union their covers, not replace them.");
    }

    private HashSet<long> Cover(int minX, int minZ, int maxX, int maxZ)
    {
        var keys = new HashSet<long>();
        mapper.AddCoveringCells(keys, minX, minZ, maxX, maxZ);

        return keys;
    }
}
