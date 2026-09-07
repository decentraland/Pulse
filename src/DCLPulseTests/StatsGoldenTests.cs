using Pulse.Stats;
using System.Text;
using System.Text.Json.Nodes;

namespace DCLPulseTests;

/// <summary>
///     Every route of the stats surface (iteration-2 C2) against the contract pack's
///     <c>http/*.json</c> goldens. Each golden carries the request that produced it, so the tests
///     issue <em>that</em> request rather than a restatement of it — a golden regenerated for a
///     different path cannot silently keep passing.
///     <para />
///     The world is one real clustering pass over the pack's five peers
///     (<see cref="StatsFixtureWorld" />), so what is compared is what a running Pulse would answer
///     from its own boards.
/// </summary>
[TestFixture]
public class StatsGoldenTests
{
    private StatsRouter router;

    [OneTimeSetUp]
    public void BuildWorld() =>
        router = StatsFixtureWorld.RouterOver(StatsFixtureWorld.Build());

    [TestCase("realms.json")]
    [TestCase("realms-main-peers.json")]
    [TestCase("realms-cozyfarm-peers.json")]
    [TestCase("realms-unknown-peers.json")]
    [TestCase("realms-main-parcels.json")]
    [TestCase("realms-cozyfarm-parcels.json")]
    [TestCase("realms-main-islands.json")]
    [TestCase("realms-cozyfarm-islands.json")]
    [TestCase("realms-main-islands-C1.json")]
    [TestCase("realms-main-islands-404.json")]
    [TestCase("peers-by-id.json")]
    [TestCase("peers-all.json")]
    [TestCase("peers-single.json")]
    [TestCase("peers-single-404.json")]
    [TestCase("status.json")]
    [TestCase("health.json")]
    public void Route_AnswersTheContractGolden(string golden)
    {
        JsonNode fixture = IterationTwoFixtures.Json($"http/{golden}");

        StatsResponse response = Request(RequestPathOf(fixture));

        Assert.That(response.Status, Is.EqualTo(fixture["status"]!.GetValue<int>()), golden);

        JsonGolden.AssertMatches(fixture["body"], BodyOf(response), golden);
    }

    /// <summary>
    ///     <c>/about</c> is the one response that is a superset of the golden: Pulse reports the
    ///     feature-flag overrides this task is running with, which archipelago-stats had no
    ///     equivalent of. The contract's two fields must match exactly; the extra key is named here
    ///     so a third one could not appear unnoticed.
    /// </summary>
    [Test]
    public void About_AnswersTheContractGolden_PlusPulsesFeatureFlagOverrides()
    {
        JsonNode fixture = IterationTwoFixtures.Json("http/about.json");

        StatsResponse response = Request("/about");

        Assert.That(response.Status, Is.EqualTo(200));

        JsonGolden.AssertMatches(fixture["body"], BodyOf(response), "about.json", "featureFlagOverrides");
    }

    /// <summary>
    ///     The cap on <c>/peers?id=</c>. Its golden cannot carry 201 wallets in its request field, so
    ///     this is the one request built here rather than read from the pack — and the boundary is
    ///     checked from both sides, since an off-by-one would either reject a legal page or leave the
    ///     bound unenforced.
    /// </summary>
    [Test]
    public void PeersById_RefusesMoreIdsThanTheCap()
    {
        JsonNode fixture = IterationTwoFixtures.Json("http/peers-by-id-too-many.json");

        StatsResponse refused = Request(PathWithIds(StatsRouter.MAX_IDS + 1));

        Assert.That(refused.Status, Is.EqualTo(fixture["status"]!.GetValue<int>()));
        JsonGolden.AssertMatches(fixture["body"], BodyOf(refused), "peers-by-id-too-many.json");

        Assert.That(Request(PathWithIds(StatsRouter.MAX_IDS)).Status, Is.EqualTo(200),
            "the cap is inclusive — a request of exactly MAX_IDS is legal");
    }

    /// <summary>
    ///     The legacy table: the unscoped paths and their <c>/comms/</c> copies answer 308 into
    ///     <c>/realms/main/…</c> with the query string intact, except the two that a query parameter
    ///     turns into all-realms lookups. Driven from <c>redirects.json</c> so the table cannot drift
    ///     from the one every consumer was given.
    /// </summary>
    [Test]
    public void LegacyPaths_FollowTheContractRedirectTable()
    {
        JsonArray cases = IterationTwoFixtures.Json("http/redirects.json")["cases"]!.AsArray();

        Assert.That(cases, Is.Not.Empty);

        foreach (JsonNode? entry in cases)
        {
            string path = entry!["path"]!.GetValue<string>();
            int status = entry["status"]!.GetValue<int>();

            // /metrics is the one route this surface does not own: it keeps its bearer token and is
            // answered by HttpService before the router is reached, which is why the router declines
            // it rather than serving it unauthenticated.
            if (path == "/metrics")
            {
                Assert.That(Request(path).Status, Is.EqualTo(404),
                    "/metrics must not be served by the unauthenticated surface");

                continue;
            }

            StatsResponse response = Request(path);

            Assert.That(response.Status, Is.EqualTo(status), path);

            if (entry["location"] is { } location)
                Assert.That(response.Location, Is.EqualTo(location.GetValue<string>()), path);
            else
                Assert.That(response.Location, Is.Null, $"{path} is answered directly, not redirected");
        }
    }

    /// <summary>
    ///     Realm segments match case-insensitively and the response carries the canonical lowercase
    ///     name, so a caller that got its realm from a scene deployment or a user does not have to
    ///     normalise it first — and every response says the same thing about which realm it describes.
    /// </summary>
    [TestCase("/realms/CozyFarm.dcl.eth/peers")]
    [TestCase("/realms/COZYFARM.DCL.ETH/peers")]
    [TestCase("/realms/cozyfarm.dcl.eth/peers")]
    public void RealmSegment_MatchesCaseInsensitively_AndAnswersWithTheCanonicalName(string path)
    {
        JsonNode? body = BodyOf(Request(path));

        Assert.That(body!["realm"]!.GetValue<string>(), Is.EqualTo("cozyfarm.dcl.eth"));
        Assert.That(body["peers"]!.AsArray(), Has.Count.EqualTo(1));
    }

    /// <summary>
    ///     A realm nobody is in is an empty realm on every route, not a 404: a realm exists exactly as
    ///     long as it has peers, so a caller polling a world that has just emptied must not have to
    ///     treat that as an error.
    /// </summary>
    [TestCase("/realms/nosuchrealm/peers")]
    [TestCase("/realms/nosuchrealm/parcels")]
    [TestCase("/realms/nosuchrealm/islands")]
    public void UnknownRealm_IsAnEmptyRealm(string path)
    {
        StatsResponse response = Request(path);

        Assert.That(response.Status, Is.EqualTo(200));

        JsonNode body = BodyOf(response)!;

        Assert.That(body["realm"]!.GetValue<string>(), Is.EqualTo("nosuchrealm"));

        JsonArray list = (body["peers"] ?? body["parcels"] ?? body["islands"])!.AsArray();

        Assert.That(list, Is.Empty);
    }

    /// <summary>
    ///     An id that exists in another realm is not found under this one. Cluster ids come from one
    ///     global counter, so they are unique platform-wide and this is the difference between "not
    ///     here" and "nowhere" — which a caller holding a stale island id needs to be able to tell.
    /// </summary>
    [Test]
    public void IslandFromAnotherRealm_IsNotFound()
    {
        Assert.That(Request("/realms/main/islands/C3").Status, Is.EqualTo(404));
        Assert.That(Request("/realms/cozyfarm.dcl.eth/islands/C3").Status, Is.EqualTo(200));
        Assert.That(Request("/realms/main/islands/C99").Status, Is.EqualTo(404));
    }

    /// <summary>
    ///     A path this surface does not own is a 404 with no body, which is what <c>HttpService</c>
    ///     answered before these routes existed.
    /// </summary>
    [TestCase("/")]
    [TestCase("/realms/main")]
    [TestCase("/realms/main/peers/extra")]
    [TestCase("/nope")]
    public void UnknownPath_IsNotFound(string path)
    {
        StatsResponse response = Request(path);

        Assert.That(response.Status, Is.EqualTo(404));
        Assert.That(response.Body, Is.Null);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private StatsResponse Request(string pathAndQuery)
    {
        int split = pathAndQuery.IndexOf('?');

        return split < 0
            ? router.Handle(pathAndQuery, StatsQuery.Parse(null))
            : router.Handle(pathAndQuery[..split], StatsQuery.Parse(pathAndQuery[(split + 1)..]));
    }

    /// <summary>
    ///     The path from a golden's <c>request</c> field, which reads <c>"GET /realms/main/peers"</c>.
    /// </summary>
    private static string RequestPathOf(JsonNode golden)
    {
        string request = golden["request"]!.GetValue<string>();

        return request[(request.IndexOf(' ') + 1)..];
    }

    private static JsonNode? BodyOf(StatsResponse response) =>
        response.Body is { } body ? JsonNode.Parse(Encoding.UTF8.GetString(body)) : null;

    /// <summary>
    ///     <paramref name="count" /> distinct wallets, none of them online, so the only thing under
    ///     test is how many ids the route accepts.
    /// </summary>
    private static string PathWithIds(int count) =>
        "/peers?" + string.Join('&', Enumerable.Range(1000, count).Select(static n => $"id={IterationTwoFixtures.Wallet(n)}"));
}
