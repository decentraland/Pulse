using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DCLPulseTests;

/// <summary>
///     Access to the iteration-2 contract fixture pack, copied verbatim into
///     <c>Fixtures/iteration-2</c> from <c>archipelago-workers/docs/contracts/iteration-2</c>. Copied
///     rather than referenced because CI has no sibling checkout — and checked against the pack's own
///     <c>manifest.json</c> by <see cref="AssertMatchesManifest" />, so a copy that drifts from the
///     contract fails a test instead of quietly asserting the wrong thing.
/// </summary>
internal static class IterationTwoFixtures
{
    private const string ROOT = "Fixtures/iteration-2";

    /// <summary>T0 from the pack's peer set — 2026-09-04T09:52:47.804Z, the first snapshot batch.</summary>
    public const long T0 = 1788515567804;

    public static string Path(string relativePath) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, ROOT, relativePath);

    public static byte[] Bytes(string relativePath) =>
        File.ReadAllBytes(Path(relativePath));

    public static JsonNode Json(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path(relativePath)))
        ?? throw new InvalidOperationException($"{relativePath} is not JSON");

    /// <summary>
    ///     The wallet the pack's peer set uses for peer <paramref name="n" /> — <c>0x000…000N</c>.
    /// </summary>
    public static string Wallet(int n) =>
        "0x" + n.ToString("x").PadLeft(40, '0');

    /// <summary>
    ///     Fails when any copied file's sha256 differs from the pack's manifest. One test calls this;
    ///     every other fixture test can then trust the bytes it reads.
    /// </summary>
    public static void AssertMatchesManifest()
    {
        JsonObject files = Json("manifest.json")["files"]!.AsObject();
        var checkedFiles = 0;

        foreach ((string relativePath, JsonNode? sha256) in files)
        {
            string full = Path(relativePath);

            // The pack carries more than Pulse consumes — the consumer scenarios, the hot-scenes
            // reference, today's probes. Only what was copied is checked.
            if (!File.Exists(full)) continue;

            Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))),
                Is.EqualTo(sha256!.GetValue<string>()),
                $"{relativePath} differs from the contract pack — re-copy it rather than editing it");

            checkedFiles++;
        }

        Assert.That(checkedFiles, Is.GreaterThan(20),
            "the fixture pack copy is missing: check Fixtures/iteration-2 is copied to the output directory");
    }
}
