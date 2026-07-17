using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     Exercises the rect announcement pipeline against a real, complex scene footprint
///     (TestData/scene-emblem.parcels — 311 parcels in a 20×20 bounding box with an intricate
///     interior). The scene is decomposed into disjoint rects the way a client SDK would
///     (greedy largest-rectangle cover) and pushed through
///     <see cref="FieldValidator.ValidateSceneListenerHandshake" />; the server-side expansion
///     must reproduce the original parcel set exactly — no cell lost, none invented.
/// </summary>
[TestFixture]
public class SceneListenerRealSceneTests
{
    private ParcelEncoder encoder;
    private FieldValidator validator;
    private PeerState state;

    [SetUp]
    public void SetUp()
    {
        encoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));

        validator = new FieldValidator(
            Options.Create(new FieldValidatorOptions { MaxRealmLength = 255, MaxEmoteDurationMs = 60_000 }),
            Options.Create(new SceneListenerOptions()),
            encoder,
            Substitute.For<ITransport>());

        state = new PeerState(PeerConnectionState.PENDING_AUTH);
    }

    [Test]
    public void RealScene_RectCover_ExpandsToExactParcelSet()
    {
        HashSet<(int X, int Z)> scene = LoadScene();
        Assert.That(scene, Has.Count.EqualTo(311), "Fixture sanity: the emblem scene holds 311 distinct parcels.");

        List<ParcelRect> rects = GreedyRectCover(scene);

        long sumArea = rects.Sum(r => (long)(r.MaxX - r.MinX + 1) * (r.MaxZ - r.MinZ + 1));
        Assert.That(sumArea, Is.EqualTo(scene.Count),
            "The cover must be disjoint and exact — Σ of rect areas equals the parcel count.");
        Assert.That(sumArea, Is.LessThanOrEqualTo(new SceneListenerOptions().MaxParcels),
            "A real scene of this size must fit the default nominal-area budget.");

        var request = new SceneListenerHandshakeRequest { Realm = "main" };
        request.ParcelRects.AddRange(rects);

        bool ok = validator.ValidateSceneListenerHandshake(new PeerIndex(1), state, request, out HashSet<int>? parcels);

        Assert.That(ok, Is.True, "A well-formed real-scene announcement must be accepted.");

        HashSet<int> expected = scene.Select(c => encoder.Encode(c.X, c.Z)).ToHashSet();
        Assert.That(parcels, Is.EquivalentTo(expected),
            "Server-side expansion must reproduce the announced footprint exactly.");
    }

    private static HashSet<(int X, int Z)> LoadScene()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "scene-emblem.parcels");
        var cells = new HashSet<(int X, int Z)>();

        foreach (string token in File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(',');
            cells.Add((int.Parse(parts[0]), int.Parse(parts[1])));
        }

        return cells;
    }

    /// <summary>
    ///     Greedy maximal-rectangle cover: repeatedly take the largest axis-aligned rectangle
    ///     fully inside the uncovered set (largest-rectangle-in-histogram per row). Produces a
    ///     disjoint, exact cover — the shape a client SDK would reasonably announce.
    /// </summary>
    private static List<ParcelRect> GreedyRectCover(HashSet<(int X, int Z)> cells)
    {
        int minX = cells.Min(c => c.X), maxX = cells.Max(c => c.X);
        int minZ = cells.Min(c => c.Z), maxZ = cells.Max(c => c.Z);
        int width = maxX - minX + 1, height = maxZ - minZ + 1;

        var remaining = new bool[width, height];

        foreach ((int x, int z) in cells)
            remaining[x - minX, z - minZ] = true;

        var rects = new List<ParcelRect>();
        var heights = new int[width];

        while (true)
        {
            var bestArea = 0;
            int bestX1 = 0, bestZ1 = 0, bestX2 = 0, bestZ2 = 0;
            Array.Clear(heights);

            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                    heights[x] = remaining[x, z] ? heights[x] + 1 : 0;

                var stack = new Stack<int>();

                for (var x = 0; x <= width; x++)
                {
                    int current = x == width ? 0 : heights[x];

                    while (stack.Count > 0 && heights[stack.Peek()] >= current)
                    {
                        int barHeight = heights[stack.Pop()];
                        int left = stack.Count == 0 ? 0 : stack.Peek() + 1;
                        int area = barHeight * (x - left);

                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestX1 = left;
                            bestZ1 = z - barHeight + 1;
                            bestX2 = x - 1;
                            bestZ2 = z;
                        }
                    }

                    stack.Push(x);
                }
            }

            if (bestArea == 0)
                return rects;

            for (int z = bestZ1; z <= bestZ2; z++)
                for (int x = bestX1; x <= bestX2; x++)
                    remaining[x, z] = false;

            rects.Add(new ParcelRect
            {
                MinX = bestX1 + minX,
                MinZ = bestZ1 + minZ,
                MaxX = bestX2 + minX,
                MaxZ = bestZ2 + minZ,
            });
        }
    }
}
