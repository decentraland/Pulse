namespace DCLPulseTests;

/// <summary>
///     The copied contract pack is the premise of every other iteration-2 test in this assembly, so it
///     gets a check of its own: if a copy drifts from
///     <c>archipelago-workers/docs/contracts/iteration-2</c>, this fails instead of letting the golden
///     tests assert the wrong contract.
/// </summary>
[TestFixture]
public class IterationTwoFixtureIntegrityTests
{
    [Test]
    public void CopiedFixtures_MatchTheContractPackManifest() =>
        IterationTwoFixtures.AssertMatchesManifest();

    /// <summary>
    ///     The copied bytes only survive a Windows checkout while <c>.gitattributes</c> classifies the
    ///     <c>.bin</c> fixtures as binary, and gitattributes resolves each attribute with the
    ///     <b>last</b> matching line winning — so <c>*.bin binary</c> written above
    ///     <c>* text=auto eol=lf</c> silently loses the <c>-text</c> the macro stands for. That is the
    ///     bug the pack fixed in its own copy (<c>aw-contracts 9cbf350</c>) and this copy inherited.
    ///     <para />
    ///     Eight of the ten <c>.bin</c> fixtures carry no NUL in the first 8000 bytes, so git's
    ///     <c>text=auto</c> heuristic reads them as text; none of them holds a <c>0x0D</c> byte today,
    ///     which is the only reason the ordering has cost nothing so far. The next pack refresh whose
    ///     protobuf encoding happens to put <c>0x0D 0x0A</c> in a NUL-free file loses that byte on
    ///     check-in from a CRLF checkout, and the byte tests then assert the wrong contract.
    ///     <para />
    ///     Read off the file rather than from <c>git check-attr</c>, so it holds wherever the tests run.
    /// </summary>
    [Test]
    public void TheFixtureAttributes_ClassifyTheBinFixturesAsBinary_After_TheGenericTextRule()
    {
        string[] rules = File.ReadAllLines(IterationTwoFixtures.Path(".gitattributes"))
                             .Select(static line => line.Trim())
                             .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                             .ToArray();

        int generic = Array.FindLastIndex(rules, static rule => rule.StartsWith("* ", StringComparison.Ordinal));
        int binary = Array.FindLastIndex(rules, static rule => rule.StartsWith("*.bin ", StringComparison.Ordinal));

        Assert.That(generic, Is.GreaterThanOrEqualTo(0), "the generic `* text=auto eol=lf` rule is missing");

        Assert.That(binary, Is.GreaterThan(generic),
            "the *.bin rule has to come after the generic one: in gitattributes the last matching line wins per attribute");

        Assert.That(rules[binary], Does.Contain("-text"),
            "an explicit -text is what stops text=auto from normalizing line endings inside the fixture bytes");
    }
}
