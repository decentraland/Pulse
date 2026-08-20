using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerCellMapperTests
{
    // ParcelEncoderOptions defaults: MinParcelX/Z=-150, Padding=2, ParcelSize=16 → minX=minZ=-152.
    // Grid cellSize 100 → parcel (0,0) spans world [0,16)² inside cell (0,0).
    private SpatialGrid grid;
    private ParcelEncoder encoder;
    private SceneListenerCellMapper mapper;

    [SetUp]
    public void SetUp()
    {
        IOptions<ParcelEncoderOptions> options = Options.Create(new ParcelEncoderOptions());
        grid = new SpatialGrid(100, 100);
        encoder = new ParcelEncoder(options);
        mapper = new SceneListenerCellMapper(encoder, grid, options);
    }

    [Test]
    public void SingleParcelInsideOneCell_CoversThatCell()
    {
        // Parcel (1,1) spans world [16,32)² — fully inside grid cell (0,0).
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(1, 1) });

        var peer = new PeerIndex(7);
        grid.Set(peer, new Vector3(20f, 0f, 20f));

        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(peer) == true), Is.True,
            "A peer standing inside the parcel must be reachable through the covering cell keys.");
    }

    [Test]
    public void ParcelStraddlingCellBoundary_CoversBothCells()
    {
        // Parcel (6,0) spans world x [96,112) — straddles the x=100 cell boundary.
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(6, 0) });

        var left = new PeerIndex(1);
        var right = new PeerIndex(2);
        grid.Set(left, new Vector3(97f, 0f, 5f));
        grid.Set(right, new Vector3(105f, 0f, 5f));

        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(left) == true), Is.True);
        Assert.That(keys.Any(k => grid.GetPeersByCell(k)?.Contains(right) == true), Is.True);
    }

    [Test]
    public void AdjacentParcelsInSameCell_DedupeKeys()
    {
        // Parcels (1,1) and (2,1) both live inside cell (0,0) — keys must be deduped.
        long[] keys = mapper.ComputeCellKeys(new[] { encoder.Encode(1, 1), encoder.Encode(2, 1) });

        Assert.That(keys, Is.Unique);
        Assert.That(keys.Length, Is.LessThanOrEqualTo(4),
            "Two adjacent interior parcels must not multiply covering cells.");
    }
}
