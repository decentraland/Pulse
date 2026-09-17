using Pulse;
using Pulse.Clusters;
using Pulse.Stats;
using System.Text;
using System.Text.Json.Nodes;

namespace DCLPulseTests;

/// <summary>
///     Every route of the stats surface (iteration-2 C2) against the contract pack's
///     <c>http/*.json</c> goldens. Each golden carries the request that produced it, so the test
///     issues that request rather than a restatement — a golden regenerated for another path cannot
///     keep passing. The world is one real clustering pass over the pack's five peers
///     (<see cref="StatsFixtureWorld" />).
/// </summary>
[TestFixture]
public class StatsGoldenTests
{
    private PresenceScenario world;
    private StatsRouter router;

    [OneTimeSetUp]
    public void BuildWorld()
    {
        world = StatsFixtureWorld.Build();
        router = StatsFixtureWorld.RouterOver(world);
    }

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

    /// <summary>Byte-exact: <c>peer</c> has to be present <em>and</em> null, as the OpenAPI requires.</summary>
    [Test]
    public void PeersSingleNotFound_WritesTheNullPeerKey()
    {
        StatsResponse response = Request($"/peers/{StatsFixtureWorld.OFFLINE_WALLET}");

        Assert.That(response.Status, Is.EqualTo(404));
        Assert.That(Encoding.UTF8.GetString(response.Body!), Is.EqualTo("{\"ok\":false,\"peer\":null}"));
    }

    /// <summary>
    ///     The harness itself: a comparison blind to a missing key makes every null the contract
    ///     pins vacuous — which is how the <c>peer</c> key above went missing with its test green.
    /// </summary>
    [Test]
    public void GoldenHarness_FailsWhenAKeyTheGoldenPinsAsNullIsAbsent()
    {
        List<string> differences = JsonGolden.Differences(
            JsonNode.Parse("""{"ok":false,"peer":null}"""),
            JsonNode.Parse("""{"ok":false}"""));

        Assert.That(differences, Has.Exactly(1).Contains("$.peer: missing"));
    }

    [Test]
    public void GoldenHarness_AcceptsTheKeyPresentWithNull()
    {
        Assert.That(
            JsonGolden.Differences(
                JsonNode.Parse("""{"ok":false,"peer":null}"""),
                JsonNode.Parse("""{"ok":false,"peer":null}""")),
            Is.Empty);
    }

    /// <summary>A golden with no <c>body</c> is a different statement from one pinning a null value.</summary>
    [Test]
    public void GoldenHarness_StillReadsAGoldenWithoutABodyAsNoBody()
    {
        Assert.That(JsonGolden.Differences(null, null), Is.Empty);

        Assert.That(JsonGolden.Differences(null, JsonNode.Parse("""{"ok":true}""")),
            Has.Exactly(1).Contains("expected no body"));
    }

    /// <summary>
    ///     <c>:</c> is the culture's time separator, so without invariant formatting <c>fi-FI</c>
    ///     would spell this timestamp <c>2026-09-04T09.52.47.834Z</c>.
    /// </summary>
    [Test]
    [SetCulture("fi-FI")]
    public void LastUpdated_IsFormattedInvariantlyOfTheAmbientCulture()
    {
        string lastUpdated = BodyOf(Request("/realms"))!["lastUpdated"]!.GetValue<string>();

        Assert.That(lastUpdated, Is.EqualTo("2026-09-04T09:52:47.834Z"));
    }

    /// <summary>
    ///     <c>/comms/</c> is a second spelling of the paths archipelago-stats published under it and
    ///     of nothing else; stripping it before routing would republish the whole surface unversioned.
    ///     <c>/comms/peers/{id}</c> left this list with A5 — one segment deeper is still nothing.
    /// </summary>
    [TestCase("/comms/realms")]
    [TestCase("/comms/status")]
    [TestCase("/comms/about")]
    [TestCase("/comms/health")]
    [TestCase("/comms/realms/main/peers")]
    [TestCase("/comms/realms/main/islands")]
    [TestCase("/comms/peers/0x0000000000000000000000000000000000000001/extra")]
    [TestCase("/comms/parcels/extra")]
    [TestCase("/comms/metrics")]
    [TestCase("/comms")]
    public void CommsPrefix_IsNotASecondSpellingOfEveryRoute(string path)
    {
        StatsResponse response = Request(path);

        Assert.That(response.Status, Is.EqualTo(404), path);
        Assert.That(response.Body, Is.Null, path);
    }

    /// <summary>
    ///     The other side of that: exactly what <c>redirects.json</c> lists — four redirects, the
    ///     query-parameter forms of <c>/comms/peers</c>, and <c>/comms/peers/{id}</c> (A5).
    /// </summary>
    [TestCase("/comms/peers", 308)]
    [TestCase("/comms/parcels", 308)]
    [TestCase("/comms/islands", 308)]
    [TestCase("/comms/islands/C1", 308)]
    [TestCase("/comms/peers?id=0x0000000000000000000000000000000000000001", 200)]
    [TestCase("/comms/peers?all=true", 200)]
    [TestCase("/comms/peers/0x0000000000000000000000000000000000000003", 200)]
    [TestCase("/comms/peers/0x0000000000000000000000000000000000000009", 404)]
    public void CommsPrefix_StillAnswersTheLegacyPaths(string path, int status)
    {
        Assert.That(Request(path).Status, Is.EqualTo(status), path);
    }

    /// <summary>
    ///     A5: the alias is <em>served</em>, not redirected. Compared byte for byte against the
    ///     unprefixed path rather than a restated golden, so the two cannot diverge on the peer
    ///     shape, the <c>realm</c> field or the 404 body.
    /// </summary>
    [TestCase("0x0000000000000000000000000000000000000001", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAPeerInGenesisCity")]
    [TestCase("0x0000000000000000000000000000000000000003", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAPeerInAWorld")]
    [TestCase("0x0000000000000000000000000000000000000009", 404, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAWalletThatIsOffline")]
    [TestCase("0X0000000000000000000000000000000000000003", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAWalletInAnotherCasing")]
    public void CommsPeersSingle_IsServedExactlyLikePeersSingle(string wallet, int expected)
    {
        StatsResponse direct = Request($"/peers/{wallet}");
        StatsResponse aliased = Request($"/comms/peers/{wallet}");

        // Without this, two routes that both answered nothing would satisfy the equality below.
        Assert.That(direct.Status, Is.EqualTo(expected), wallet);
        Assert.That(BodyText(direct), Is.Not.Null, wallet);

        Assert.That(aliased.Status, Is.EqualTo(direct.Status), wallet);
        Assert.That(aliased.Location, Is.Null, "the alias is served, not redirected");
        Assert.That(BodyText(aliased), Is.EqualTo(BodyText(direct)), wallet);
    }

    /// <summary>The view's per-cluster index against a naive scan of the same pass, ordering included.</summary>
    [Test]
    public void Islands_ListExactlyTheirMembers_HoweverTheViewIndexesThem()
    {
        StatsBoardView view = StatsBoardView.Read(
            world.ClusterBoard, world.SnapshotBoard, world.ParcelEncoder, world.Clock);

        ClusterPass pass = world.ClusterBoard.Current;

        Assert.That(pass.Clusters, Is.Not.Empty);

        foreach (ClusterInfo cluster in pass.Clusters)
        {
            string[] expected = pass.Peers
                                    .Where(peer => string.Equals(peer.ClusterId, cluster.Id, StringComparison.Ordinal))
                                    .Select(static peer => CanonicalName.Of(peer.Wallet))
                                    .OrderBy(static address => address, StringComparer.Ordinal)
                                    .ToArray();

            IslandResult island = view.IslandIn(cluster.Realm, cluster.Id)!;

            Assert.That(island.Peers.Select(static peer => peer.Address).ToArray(), Is.EqualTo(expected), cluster.Id);
            Assert.That(expected, Is.Not.Empty, cluster.Id);
        }
    }

    /// <summary>The one response that is a superset of its golden — the extra key must be named here.</summary>
    [Test]
    public void About_AnswersTheContractGolden_PlusPulsesFeatureFlagOverrides()
    {
        JsonNode fixture = IterationTwoFixtures.Json("http/about.json");

        StatsResponse response = Request("/about");

        Assert.That(response.Status, Is.EqualTo(200));

        JsonGolden.AssertMatches(fixture["body"], BodyOf(response), "about.json", "featureFlagOverrides");
    }

    /// <summary>
    ///     The one request built here rather than read from the pack — a golden cannot carry 201
    ///     wallets in its <c>request</c> field. Checked from both sides of the boundary.
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

    /// <summary>Driven from <c>redirects.json</c>, so the table cannot drift from the published one.</summary>
    [Test]
    public void LegacyPaths_FollowTheContractRedirectTable()
    {
        JsonArray cases = IterationTwoFixtures.Json("http/redirects.json")["cases"]!.AsArray();

        Assert.That(cases, Is.Not.Empty);

        foreach (JsonNode? entry in cases)
        {
            string path = entry!["path"]!.GetValue<string>();
            int status = entry["status"]!.GetValue<int>();

            // /metrics is the one route this surface does not own: HttpService answers it behind a
            // bearer token, so the router has to decline rather than serve it unauthenticated.
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

            if (entry["golden"] is { } golden)
                AssertAnsweredLikeItsGolden(golden.GetValue<string>(), path, response);
        }
    }

    /// <summary>
    ///     A row naming a golden instead of a <c>Location</c> claims that path is answered by the
    ///     golden's handler, so where the row <em>is</em> that golden's own request modulo the
    ///     <c>/comms/</c> prefix, the body has to match too — that is what makes the A5 rows say
    ///     something. A row on a different input (<c>/comms/peers?id=</c> carries one id where its
    ///     golden asks three) is skipped; the golden test proper already pins that input.
    /// </summary>
    private static void AssertAnsweredLikeItsGolden(string golden, string path, StatsResponse response)
    {
        // "peers-single.json (same handler as /peers/:id — ...)": the note after the filename is prose.
        string file = golden.Split(' ')[0];

        JsonNode fixture = IterationTwoFixtures.Json($"http/{file}");

        string unprefixed = path.StartsWith("/comms/", StringComparison.Ordinal)
            ? path["/comms".Length..]
            : path;

        if (!string.Equals(RequestPathOf(fixture), unprefixed, StringComparison.Ordinal)) return;

        Assert.That(response.Status, Is.EqualTo(fixture["status"]!.GetValue<int>()), path);

        JsonGolden.AssertMatches(fixture["body"], BodyOf(response), $"{file} via {path}");
    }

    [TestCase("/realms/CozyFarm.dcl.eth/peers")]
    [TestCase("/realms/COZYFARM.DCL.ETH/peers")]
    [TestCase("/realms/cozyfarm.dcl.eth/peers")]
    public void RealmSegment_MatchesCaseInsensitively_AndAnswersWithTheCanonicalName(string path)
    {
        JsonNode? body = BodyOf(Request(path));

        Assert.That(body!["realm"]!.GetValue<string>(), Is.EqualTo("cozyfarm.dcl.eth"));
        Assert.That(body["peers"]!.AsArray(), Has.Count.EqualTo(1));
    }

    /// <summary>A realm exists exactly as long as it has peers, so one nobody is in is empty, not a 404.</summary>
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
    ///     Cluster ids come from one global counter, so <c>C3</c> is a real id under the wrong realm
    ///     — a different case from <c>C99</c>, which exists nowhere.
    /// </summary>
    [Test]
    public void IslandFromAnotherRealm_IsNotFound()
    {
        Assert.That(Request("/realms/main/islands/C3").Status, Is.EqualTo(404));
        Assert.That(Request("/realms/cozyfarm.dcl.eth/islands/C3").Status, Is.EqualTo(200));
        Assert.That(Request("/realms/main/islands/C99").Status, Is.EqualTo(404));
    }

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

    /// <summary>The path out of a golden's <c>request</c> field — <c>"GET /realms/main/peers"</c>.</summary>
    private static string RequestPathOf(JsonNode golden)
    {
        string request = golden["request"]!.GetValue<string>();

        return request[(request.IndexOf(' ') + 1)..];
    }

    private static JsonNode? BodyOf(StatsResponse response) =>
        response.Body is { } body ? JsonNode.Parse(Encoding.UTF8.GetString(body)) : null;

    /// <summary>The raw body text, so comparisons cover key order and number formatting too.</summary>
    private static string? BodyText(StatsResponse response) =>
        response.Body is { } body ? Encoding.UTF8.GetString(body) : null;

    /// <summary><paramref name="count" /> wallets, none online: only the id count is under test.</summary>
    private static string PathWithIds(int count) =>
        "/peers?" + string.Join('&', Enumerable.Range(1000, count).Select(static n => $"id={IterationTwoFixtures.Wallet(n)}"));
}
