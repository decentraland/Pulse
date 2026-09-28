namespace DCLPulseTests;

/// <summary>
///     The copied pack is the premise of every other iteration-2 test here, so a copy that drifts
///     from <c>archipelago-workers/docs/contracts/iteration-2</c> fails here rather than quietly
///     turning the golden tests into assertions about the wrong contract.
/// </summary>
[TestFixture]
public class IterationTwoFixtureIntegrityTests
{
    [Test]
    public void CopiedFixtures_MatchTheContractPackManifest() =>
        IterationTwoFixtures.AssertMatchesManifest();

    /// <summary>
    ///     The copied bytes survive a Windows checkout only while <c>.gitattributes</c> classifies
    ///     the <c>.bin</c> fixtures as binary — and gitattributes resolves each attribute by
    ///     <b>last</b> matching line, so <c>*.bin binary</c> written above <c>* text=auto eol=lf</c>
    ///     silently loses the <c>-text</c> the macro stands for (<c>aw-contracts 9cbf350</c> fixed
    ///     that in the pack). Latent so far only because the eight NUL-free fixtures, which
    ///     <c>text=auto</c> reads as text, happen to hold no <c>0x0D</c> byte. Read off the file
    ///     rather than <c>git check-attr</c>, so it holds wherever the tests run.
    /// </summary>
    [Test]
    public void TheFixtureAttributes_ClassifyTheBinFixturesAsBinary_After_TheGenericTextRule() =>
        AssertBinFixturesResolveToTextUnset(
            File.ReadAllLines(IterationTwoFixtures.Path(".gitattributes")));

    /// <summary>
    ///     What the check above requires is the <em>resolved</em> attribute, not one spelling of it:
    ///     <c>binary</c> is a built-in macro for <c>-text -diff</c>, so after the generic rule it
    ///     leaves <c>text</c> unset exactly as the explicit form does.
    /// </summary>
    [TestCase("*.bin -text -diff -merge", TestName = "TheFixtureAttributes_AcceptTheExplicitSpelling")]
    [TestCase("*.bin binary", TestName = "TheFixtureAttributes_AcceptTheBinaryMacro")]
    public void EitherSpellingOfTheBinRule_IsAccepted_WhenItComesAfterTheGenericRule(string binRule) =>
        AssertBinFixturesResolveToTextUnset(["# a comment", "* text=auto eol=lf", "", binRule]);

    /// <summary>
    ///     A generic rule exists, the <c>.bin</c> rule comes after it, and that rule really does turn
    ///     <c>text</c> off — in either of the spellings that do.
    /// </summary>
    private static void AssertBinFixturesResolveToTextUnset(IEnumerable<string> gitattributes)
    {
        string[] rules = gitattributes
                        .Select(static line => line.Trim())
                        .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                        .ToArray();

        int generic = Array.FindLastIndex(rules, static rule => rule.StartsWith("* ", StringComparison.Ordinal));
        int binary = Array.FindLastIndex(rules, static rule => rule.StartsWith("*.bin ", StringComparison.Ordinal));

        Assert.That(generic, Is.GreaterThanOrEqualTo(0), "the generic `* text=auto eol=lf` rule is missing");

        Assert.That(binary, Is.GreaterThan(generic),
            "the *.bin rule has to come after the generic one: in gitattributes the last matching line wins per attribute");

        string[] attributes = rules[binary].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1..];

        Assert.That(attributes.Contains("-text") || attributes.Contains("binary"), Is.True,
            $"the *.bin rule has to resolve to `text: unset`, which is what stops text=auto from "
          + $"normalizing line endings inside the fixture bytes — either an explicit `-text` or the "
          + $"`binary` macro that expands to it. Got: `{rules[binary]}`");
    }
}
