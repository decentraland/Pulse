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
}
