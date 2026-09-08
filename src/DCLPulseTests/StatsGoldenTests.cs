using Pulse;
using Pulse.Clusters;
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

    /// <summary>
    ///     The 404 body of <c>/peers/{id}</c>, asserted as the bytes on the wire rather than only
    ///     structurally. <c>peer</c> has to be <em>present</em> and null: a consumer replacing
    ///     worlds-content-server's <c>/wallet/:wallet/connected-world</c> tests <c>'peer' in body</c>
    ///     or <c>body.peer === null</c>, and the published OpenAPI declares the key required — so
    ///     omitting it turns a legitimate "not online" answer into a malformed one.
    /// </summary>
    [Test]
    public void PeersSingleNotFound_WritesTheNullPeerKey()
    {
        StatsResponse response = Request($"/peers/{StatsFixtureWorld.OFFLINE_WALLET}");

        Assert.That(response.Status, Is.EqualTo(404));
        Assert.That(Encoding.UTF8.GetString(response.Body!), Is.EqualTo("{\"ok\":false,\"peer\":null}"));
    }

    /// <summary>
    ///     The golden harness itself, because a harness that cannot see a missing key makes every
    ///     null the contract pins unasserted — which is how the <c>peer</c> key above went missing
    ///     while its golden test was green.
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

    /// <summary>
    ///     And a golden with no <c>body</c> at all still means "no body" — <c>/health</c> and the
    ///     404s that answer with nothing — which is a different statement from a null-valued key.
    /// </summary>
    [Test]
    public void GoldenHarness_StillReadsAGoldenWithoutABodyAsNoBody()
    {
        Assert.That(JsonGolden.Differences(null, null), Is.Empty);

        Assert.That(JsonGolden.Differences(null, JsonNode.Parse("""{"ok":true}""")),
            Has.Exactly(1).Contains("expected no body"));
    }

    /// <summary>
    ///     <c>lastUpdated</c> is a machine-readable timestamp, so it must not depend on the
    ///     container's locale: <c>:</c> is the culture's time separator, and under a culture that
    ///     spells it <c>.</c> the field would come out as <c>2026-09-04T09.52.47.834Z</c>, which every
    ///     JS consumer's <c>new Date(...)</c> reads as Invalid Date.
    /// </summary>
    [Test]
    [SetCulture("fi-FI")]
    public void LastUpdated_IsFormattedInvariantlyOfTheAmbientCulture()
    {
        string lastUpdated = BodyOf(Request("/realms"))!["lastUpdated"]!.GetValue<string>();

        Assert.That(lastUpdated, Is.EqualTo("2026-09-04T09:52:47.834Z"));
    }

    /// <summary>
    ///     <c>/comms/</c> is a second spelling of the paths archipelago-stats published under it and of
    ///     nothing else. Stripping it before matching every route answered <c>/comms/realms</c>,
    ///     <c>/comms/status</c>, <c>/comms/about</c> and the realm-scoped routes as well — unversioned
    ///     public surface nobody asked for, which becomes hard to withdraw once a caller depends on it.
    ///     <para />
    ///     <c>/comms/peers/{id}</c> left this list with A5: stats served that alias with a live 200, so
    ///     it is answered rather than declined. One segment deeper is still nothing.
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
    ///     The legacy set keeps working under the prefix, both exceptions included — the paths
    ///     <c>redirects.json</c> lists are exactly what <c>/comms/</c> is for: four that redirect,
    ///     <c>/comms/peers</c> carrying a query parameter, and <c>/comms/peers/{id}</c> (A5).
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
    ///     A5: <c>/comms/peers/{id}</c> is <em>served</em>, not redirected — archipelago-stats answered
    ///     that alias with a live 200 and consumers still call it, so a 308 (or the 404 it used to get
    ///     here) breaks a caller that works in production today.
    ///     <para />
    ///     Asserted as "the same answer as the unprefixed path", byte for byte, rather than against a
    ///     restatement of the golden: the contract is that the two paths are one handler, so a change to
    ///     the peer shape, the <c>realm</c> field or the 404 body cannot land on one of them only. Every
    ///     branch of the route is driven through it — a peer in Genesis City, a peer in a world, a
    ///     wallet that is offline, and a wallet spelled in another casing.
    /// </summary>
    [TestCase("0x0000000000000000000000000000000000000001", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAPeerInGenesisCity")]
    [TestCase("0x0000000000000000000000000000000000000003", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAPeerInAWorld")]
    [TestCase("0x0000000000000000000000000000000000000009", 404, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAWalletThatIsOffline")]
    [TestCase("0X0000000000000000000000000000000000000003", 200, TestName = "CommsPeersSingle_MatchesPeersSingle_ForAWalletInAnotherCasing")]
    public void CommsPeersSingle_IsServedExactlyLikePeersSingle(string wallet, int expected)
    {
        StatsResponse direct = Request($"/peers/{wallet}");
        StatsResponse aliased = Request($"/comms/peers/{wallet}");

        // Both halves of the comparison have to be a real answer, or two routes that answered nothing
        // would satisfy the equality below.
        Assert.That(direct.Status, Is.EqualTo(expected), wallet);
        Assert.That(BodyText(direct), Is.Not.Null, wallet);

        Assert.That(aliased.Status, Is.EqualTo(direct.Status), wallet);
        Assert.That(aliased.Location, Is.Null, "the alias is served, not redirected");
        Assert.That(BodyText(aliased), Is.EqualTo(BodyText(direct)), wallet);
    }

    /// <summary>
    ///     Island membership, against a naive scan of the pass it was built from. The view indexes
    ///     members per cluster in one pass instead of rescanning the whole peer array per island, and
    ///     what must not change is the answer — including the address ordering inside each island.
    /// </summary>
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

            if (entry["golden"] is { } golden)
                AssertAnsweredLikeItsGolden(golden.GetValue<string>(), path, response);
        }
    }

    /// <summary>
    ///     A row that names a golden instead of a <c>Location</c> claims the path is answered by the
    ///     same handler as that golden, so where the row <em>is</em> the golden's own request modulo the
    ///     <c>/comms/</c> prefix, the body has to be the golden's body. That is what makes the A5 rows
    ///     say something: <c>/comms/peers/0x…3</c> has to return <c>peers-single.json</c> and the
    ///     unknown wallet <c>peers-single-404.json</c>, not merely 200 and 404.
    ///     <para />
    ///     A row whose request differs from its golden's — <c>/comms/peers?id=</c> carries one id where
    ///     <c>peers-by-id.json</c> asks for three — is the same handler on a different input, and the
    ///     golden test proper already pins that input.
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
    ///     The body as the bytes on the wire, or null when there is none — so two responses compare
    ///     on what a caller actually receives, key order and number formatting included.
    /// </summary>
    private static string? BodyText(StatsResponse response) =>
        response.Body is { } body ? Encoding.UTF8.GetString(body) : null;

    /// <summary>
    ///     <paramref name="count" /> distinct wallets, none of them online, so the only thing under
    ///     test is how many ids the route accepts.
    /// </summary>
    private static string PathWithIds(int count) =>
        "/peers?" + string.Join('&', Enumerable.Range(1000, count).Select(static n => $"id={IterationTwoFixtures.Wallet(n)}"));
}
