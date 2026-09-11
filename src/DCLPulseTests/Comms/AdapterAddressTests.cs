using PulseTestClient;
using PulseTestClient.Comms;

namespace DCLPulseTests.Comms;

/// <summary>
///     <see cref="AdapterAddress.Refine" /> against the adapter forms a realm actually advertises.
/// </summary>
[TestFixture]
public class AdapterAddressTests
{
    // Verbatim from https://peer.decentraland.zone/about, comms.adapter. The whole point of the
    // helper is that this pastes in unchanged.
    private const string ZONE_ADAPTER = "archipelago:archipelago:wss://peer.decentraland.zone/archipelago/ws";

    [TestCase("ws://127.0.0.1:5000/ws", "ws://127.0.0.1:5000/ws")]
    [TestCase("wss://peer.decentraland.zone/archipelago/ws", "wss://peer.decentraland.zone/archipelago/ws")]
    [TestCase(ZONE_ADAPTER, "wss://peer.decentraland.zone/archipelago/ws")]
    [TestCase("archipelago:archipelago:ws://127.0.0.1:5000/ws", "ws://127.0.0.1:5000/ws")]
    [TestCase("  wss://host/ws  ", "wss://host/ws")]
    public void Refine_ReducesToTheWebSocketUrl(string input, string expected) =>
        Assert.That(AdapterAddress.Refine(input), Is.EqualTo(expected));

    [Test]
    public void Refine_PreservesCasing_BecauseOnlyTheSchemeIsMatchedCaseInsensitively() =>
        Assert.That(AdapterAddress.Refine("WSS://HOST/ws"), Is.EqualTo("WSS://HOST/ws"));

    // explorer routes these to a different room type instead. Here there is no other room type, and
    // resolving quietly to one that never delivers is the silent no-delivery failure the harness
    // exists to catch — so they have to be loud.
    [TestCase("https://peer.decentraland.org", TestName = "Refine_Rejects_FixedAdapter")]
    [TestCase("archipelago:archipelago:https://archipelago-ea-stats.decentraland.zone", TestName = "Refine_Rejects_HttpsArchipelagoAdapter")]
    [TestCase("offline:offline", TestName = "Refine_Rejects_OfflineAdapter")]
    [TestCase("", TestName = "Refine_Rejects_Empty")]
    [TestCase("   ", TestName = "Refine_Rejects_Whitespace")]
    public void Refine_RejectsAnythingThatIsNotAWebSocketUrl(string input) =>
        Assert.Throws<PulseException>(() => AdapterAddress.Refine(input));

    [Test]
    public void Refine_NamesTheOriginalValue_SoAMisconfiguredFlagIsObvious()
    {
        var e = Assert.Throws<PulseException>(() => AdapterAddress.Refine("offline:offline"));
        Assert.That(e!.Message, Does.Contain("offline:offline"));
    }
}
