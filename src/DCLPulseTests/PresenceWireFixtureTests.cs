using Decentraland.Pulse;
using Pulse;
using Pulse.Peers;
using Pulse.Presence;

namespace DCLPulseTests;

/// <summary>
///     Byte-for-byte checks against the iteration-2 contract pack's <c>parcel_changes/*.bin</c>. Every
///     batch here is assembled by the real <see cref="Pulse.Presence.ParcelChangeTracker" /> from a
///     real <c>ClusterTracker</c> pass, taken out of the real <c>NatsPublisher</c> outbox and written
///     by the serializer the publisher hands to NATS — so what these assert is the bytes a deployed
///     Pulse puts on <c>engine.parcel_changes</c>, not a hand-built message that happens to match.
///     <para />
///     Fixtures 01–06 and 08 are one continuous scenario on <c>pulse-1</c>: the sequence numbers, the
///     server times and each peer's state carry from one to the next, which is why they are produced
///     by one walk rather than per-test setups. 09 is a second instance and 10 a restart, so each has
///     its own process. 07 is the invalid fixture — a realm that is not lowercase — and the only thing
///     to assert about it is that this producer cannot emit it.
/// </summary>
[TestFixture]
public class PresenceWireFixtureTests
{
    private const long T0 = IterationTwoFixtures.T0;

    private static readonly PeerIndex W1 = new (1);
    private static readonly PeerIndex W2 = new (2);
    private static readonly PeerIndex W3 = new (3);
    private static readonly PeerIndex W4 = new (4);
    private static readonly PeerIndex W5 = new (5);
    private static readonly PeerIndex WAB = new (6);
    private static readonly PeerIndex W7 = new (7);

    private const string MAIN = "main";
    private const string COZYFARM = "cozyfarm.dcl.eth";

    private Dictionary<string, byte[]> emitted;

    /// <summary>
    ///     Walks the <c>pulse-1</c> scenario once and keeps the wire bytes of every batch it produced,
    ///     so each fixture gets its own test name while the chain that produces them stays a single
    ///     ordered story.
    /// </summary>
    [OneTimeSetUp]
    public void WalkPulseOneScenario()
    {
        var scenario = new PresenceScenario("pulse-1");
        emitted = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // 01 — the whole state of pulse-1 as its opening batch. The peers are placed before the
        // first pass, so the snapshot the publisher asked for on start is the first thing that goes
        // out (C1.4 start).
        scenario.Place(W1, IterationTwoFixtures.Wallet(1), MAIN, -1, 0);
        scenario.Place(W2, IterationTwoFixtures.Wallet(2), MAIN, 147, -3);
        scenario.Place(W3, IterationTwoFixtures.Wallet(3), COZYFARM, 0, 0);
        scenario.Place(W4, IterationTwoFixtures.Wallet(4), MAIN, -1, 0);
        scenario.Place(W5, IterationTwoFixtures.Wallet(5), MAIN, 147, -3);
        scenario.RunPass();
        Capture(scenario, "01-snapshot", T0);

        // 02 — W2 walks one parcel east.
        scenario.Move(W2, MAIN, 148, -3);
        scenario.RunPass();
        Capture(scenario, "02-delta-move", T0 + 2000);

        // 03 — W1 leaves. The exit comes from the peer-lifecycle drain, never from "missing in this
        // pass", so the pass that follows must add nothing.
        scenario.Remove(W1);
        scenario.RunPass();
        Capture(scenario, "03-exit", T0 + 4000);

        // 04 — W2 teleports to a world: one non-null entry for the new realm, the old one implied.
        scenario.Move(W2, COZYFARM, 1, 2);
        scenario.RunPass();
        Capture(scenario, "04-realm-change", T0 + 6000);

        // 05 — W3 crosses two parcels inside one batch interval; only the latest survives (C1.3).
        scenario.Move(W3, COZYFARM, 2, 2);
        scenario.RunPass();
        scenario.Move(W3, COZYFARM, 3, 4);
        scenario.RunPass();
        Capture(scenario, "05-coalesced", T0 + 8000);

        // 06 — a handshake/teleport that arrived mixed-case on both the wallet and the realm, taken
        // through the production ingest path so the canonicalizer is what lowercases them (C1.5).
        scenario.Register(WAB, "0x00000000000000000000000000000000000000AB");
        scenario.Teleport(WAB, "CozyFarm.dcl.eth", 5, 6);
        scenario.RunPass();
        Capture(scenario, "06-mixed-case", T0 + 10000);

        // Two batches that were assembled and never delivered — a broker that refused the publish.
        // seq is stamped per assembled batch and never reused, which is what turns a failed publish
        // into the real gap 08 pins. W4 steps away and back, so the state 10 restarts into is
        // unchanged.
        scenario.Move(W4, MAIN, 0, 0);
        scenario.RunPass();
        Assert.That(scenario.NextBatch(T0 + 12000)?.Seq, Is.EqualTo(7u), "the lost batch still consumes a seq");

        scenario.Move(W4, MAIN, -1, 0);
        scenario.RunPass();
        Assert.That(scenario.NextBatch(T0 + 14000)?.Seq, Is.EqualTo(8u), "and so does the second");

        // 08 — the next batch that does arrive carries seq 9.
        scenario.Move(W5, MAIN, 150, -3);
        scenario.RunPass();
        Capture(scenario, "08-gap", T0 + 16000);
    }

    [TestCase("01-snapshot")]
    [TestCase("02-delta-move")]
    [TestCase("03-exit")]
    [TestCase("04-realm-change")]
    [TestCase("05-coalesced")]
    [TestCase("06-mixed-case")]
    [TestCase("08-gap")]
    public void PulseOne_EmitsTheContractBytes(string fixture)
    {
        Assert.That(emitted, Does.ContainKey(fixture), "the scenario produced no batch at this step");

        AssertBytes(fixture, emitted[fixture]);
    }

    /// <summary>
    ///     A second instance announces itself with its own snapshot at seq 1: <c>seq</c> is per
    ///     <c>server_name</c>, so nothing about pulse-1's stream is implied by it.
    /// </summary>
    [Test]
    public void SecondInstance_EmitsItsOwnOpeningSnapshot()
    {
        var scenario = new PresenceScenario("pulse-2");

        scenario.Place(W7, IterationTwoFixtures.Wallet(7), MAIN, 0, 0);
        scenario.RunPass();

        AssertBytes("09-second-server", scenario.NextBatchBytes(T0 + 17000)!);
    }

    /// <summary>
    ///     A restart is a new process: <c>seq</c> is back to 1 and the batch is a snapshot, which is
    ///     what tells a consumer to replace everything it holds for this <c>server_name</c> rather
    ///     than to treat the drop as a gap (C1.4 start).
    /// </summary>
    [Test]
    public void Restart_EmitsASnapshotAtSeqOne()
    {
        var scenario = new PresenceScenario("pulse-1");

        scenario.Place(W2, IterationTwoFixtures.Wallet(2), COZYFARM, 1, 2);
        scenario.Place(W4, IterationTwoFixtures.Wallet(4), MAIN, -1, 0);
        scenario.RunPass();

        AssertBytes("10-snapshot-restart", scenario.NextBatchBytes(T0 + 20000)!);
    }

    /// <summary>
    ///     07 is the fixture a conforming producer cannot emit. The realm is canonicalized at ingest,
    ///     so a peer that handshakes into "Main" is placed in "main" and the feed says "main" — there
    ///     is no path from a mixed-case realm on the wire in to a mixed-case realm on the wire out.
    ///     Asserted against the invalid bytes themselves, so the test fails if a future ingest path
    ///     ever lets one through.
    /// </summary>
    [Test]
    public void MixedCaseRealm_IsUnreachable_SoFixture07CannotBeProduced()
    {
        var scenario = new PresenceScenario("pulse-1");

        scenario.Register(W1, IterationTwoFixtures.Wallet(1));
        scenario.Teleport(W1, "Main", -1, 0);
        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatch(T0)!;

        Assert.That(batch.Changes.Select(static change => change.Realm), Is.EqualTo(new[] { "main" }),
            "a mixed-case realm off the wire must reach the feed lowercase");

        Assert.That(PresenceScenario.Serialize(batch),
            Is.Not.EqualTo(IterationTwoFixtures.Bytes("parcel_changes/07-invalid-mixed-case-realm.bin")),
            "07 is the contract violation; this producer must have no way to emit it");
    }

    /// <summary>
    ///     The exit is published once, by the lifecycle drain alone. If exits were also derived from
    ///     "present last pass, missing in this one", the pass after a removal would emit a second
    ///     one — so a batch here at all is the failure.
    /// </summary>
    [Test]
    public void PassAfterARemoval_EmitsNothing()
    {
        var scenario = new PresenceScenario("pulse-1");

        scenario.Place(W1, IterationTwoFixtures.Wallet(1), MAIN, -1, 0);
        scenario.RunPass();
        scenario.NextBatch(T0);

        scenario.Remove(W1);
        scenario.RunPass();

        ParcelChangesBatch exit = scenario.NextBatch(T0 + 2000)!;

        Assert.That(exit.Changes, Has.Count.EqualTo(1), "exactly one entry for the exit");
        Assert.That(exit.Changes[0].Parcel, Is.Null, "and it is the parcel-absent one");

        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 4000), Is.Null, "the exit must not be emitted a second time");
    }

    private void Capture(PresenceScenario scenario, string fixture, long serverTimeMs)
    {
        byte[]? bytes = scenario.NextBatchBytes(serverTimeMs);

        if (bytes is not null)
            emitted[fixture] = bytes;
    }

    /// <summary>
    ///     Compares against the pack's bytes, and on a mismatch prints both messages decoded so the
    ///     failure names the field that drifted instead of an offset.
    /// </summary>
    private static void AssertBytes(string fixture, byte[] actual)
    {
        byte[] expected = IterationTwoFixtures.Bytes($"parcel_changes/{fixture}.bin");

        if (actual.AsSpan().SequenceEqual(expected)) return;

        Assert.Fail(
            $"{fixture}.bin mismatch\nexpected: {ParcelChangesBatch.Parser.ParseFrom(expected)}\nactual:   {ParcelChangesBatch.Parser.ParseFrom(actual)}");
    }
}
