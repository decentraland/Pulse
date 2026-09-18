using Pulse.Clusters;
using Pulse.FeatureFlags;
using System.Globalization;
using System.Net;
using Pulse.InterestManagement;
using Pulse.Peers.Simulation;

namespace Pulse.Stats;

/// <summary>
///     The read-only stats surface (iteration-2 C2): the routes archipelago-stats used to answer,
///     re-sourced from Pulse's own boards and scoped by realm. Unauthenticated, like <c>/about</c>;
///     <c>/metrics</c> keeps its bearer token and stays with <see cref="HttpService" />.
///     <para />
///     Three rules run through it: a realm segment matches case-insensitively and the response
///     carries the canonical lowercase name; an unknown realm is an empty realm — 200 and an empty
///     list, never 404; and the legacy unscoped paths answer 308 into <c>/realms/main/…</c> with the
///     query intact, except the all-realms lookups <c>/peers?id|all</c> and <c>/peers/{id}</c>.
/// </summary>
public sealed class StatsRouter(
    ClusterBoard clusterBoard,
    SnapshotBoard snapshotBoard,
    ParcelEncoder parcelEncoder,
    ITimeProvider timeProvider,
    ServiceIdentity identity,
    PulseFlagsConfigurationProvider featureFlags)
{
    /// <summary>Genesis City: the one realm the legacy unscoped paths were written against.</summary>
    public const string DEFAULT_REALM = "main";

    /// <summary>
    ///     Cap on <c>/peers?id=</c>, so one request cannot drive a scan per id. Everything at once is
    ///     <c>?all=true</c> instead.
    /// </summary>
    public const int MAX_IDS = 200;

    /// <summary>
    ///     Answers <paramref name="absolutePath" />, or 404 with no body for a path not owned here.
    /// </summary>
    public StatsResponse Handle(string absolutePath, StatsQuery query)
    {
        string[] segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The /comms/ prefix is a second spelling of the legacy paths and of nothing else — stripped
        // before matching, but only for those, so the rest do not become new public surface.
        if (segments is ["comms", ..])
        {
            segments = segments[1..];

            if (!IsLegacyPath(segments)) return StatsResponse.NotFound();
        }

        return segments switch
        {
            ["health"] => new StatsResponse(HttpStatusCode.OK),
            ["about"] => About(),
            ["status"] => Status(),

            ["realms"] => Realms(),
            ["realms", var realm, "peers"] => RealmPeers(realm),
            ["realms", var realm, "parcels"] => RealmParcels(realm),
            ["realms", var realm, "islands"] => RealmIslands(realm),
            ["realms", var realm, "islands", var island] => RealmIsland(realm, island),

            // All-realms lookups share a path with the legacy redirect below; the query decides
            // which it is, since `id` or `all` is a caller that already knows this route.
            ["peers"] when query.Has("id") => PeersByIds(query.All("id")),
            ["peers"] when query.Has("all") => AllPeers(),
            ["peers", var id] => SinglePeer(id),

            ["peers"] or ["parcels"] or ["islands"] or ["islands", _] => RedirectToDefaultRealm(segments, query),

            _ => StatsResponse.NotFound(),
        };
    }

    /// <summary>
    ///     The paths archipelago-stats answered under the <c>/comms/</c> prefix. Membership decides
    ///     only whether the prefix is accepted — the switch above still decides the answer, so both
    ///     spellings are one route. <c>/peers/{id}</c> is in the set because stats served
    ///     <c>/comms/peers/{id}</c> live (iteration-2 amendment A5), and a 308 cannot stand in: it
    ///     would point at <c>/realms/main/peers/{id}</c>, which is not a route.
    /// </summary>
    private static bool IsLegacyPath(string[] segments) =>
        segments is ["peers"] or ["peers", _] or ["parcels"] or ["islands"] or ["islands", _];

    private StatsResponse Realms()
    {
        // Off the pass alone: the peer projection and its sort answer nothing this route asks.
        ClusterPass pass = clusterBoard.Current;

        return StatsResponse.Ok(new RealmsResponse(
            StatsBoardView.RealmsOf(pass), IsoUtcMs(pass.TakenAtUnixMs)));
    }

    private StatsResponse RealmPeers(string realm)
    {
        string canonical = CanonicalName.Of(realm);

        return StatsResponse.Ok(new PeersResponse(Ok: true, canonical, Read().PeersIn(canonical)));
    }

    private StatsResponse RealmParcels(string realm)
    {
        string canonical = CanonicalName.Of(realm);

        return StatsResponse.Ok(new ParcelsResponse(canonical, Read().ParcelsIn(canonical)));
    }

    private StatsResponse RealmIslands(string realm)
    {
        string canonical = CanonicalName.Of(realm);

        return StatsResponse.Ok(new IslandsResponse(Ok: true, canonical, Read().IslandsIn(canonical)));
    }

    /// <summary>
    ///     One island, as the body itself rather than in an envelope — the shape stats answered. Ids
    ///     are unique across realms, so an id that lives in another realm is a 404 here.
    /// </summary>
    private StatsResponse RealmIsland(string realm, string islandId)
    {
        IslandResult? island = Read().IslandIn(CanonicalName.Of(realm), islandId);

        return island is null ? StatsResponse.NotFound() : StatsResponse.Ok(island);
    }

    private StatsResponse PeersByIds(IReadOnlyList<string> ids)
    {
        if (ids.Count > MAX_IDS)
            return StatsResponse.Json(
                HttpStatusCode.BadRequest, new ErrorResponse(Ok: false, $"too many ids (max {MAX_IDS})"));

        return StatsResponse.Ok(new PeersResponse(Ok: true, Realm: null, Read().PeersMatching(ids)));
    }

    private StatsResponse AllPeers() =>
        StatsResponse.Ok(new PeersResponse(Ok: true, Realm: null, Read().AllPeers()));

    /// <summary>
    ///     One wallet across every realm. Not found is 404 with <c>{"ok":false,"peer":null}</c>, not
    ///     an empty body — the shape the contract pins.
    /// </summary>
    private StatsResponse SinglePeer(string id)
    {
        PeerResult? peer = Read().Peer(id);

        return peer is null
            ? StatsResponse.Json(HttpStatusCode.NotFound, new PeerResponse(Ok: false, Peer: null))
            : StatsResponse.Ok(new PeerResponse(Ok: true, peer));
    }

    /// <summary>
    ///     <c>currentTime</c> is now, not the pass time — this server's clock rather than the age of
    ///     its clustering.
    /// </summary>
    private StatsResponse Status()
    {
        return StatsResponse.Ok(new StatusResponse(
            identity.Version,
            timeProvider.UnixTimeMs,
            identity.CommitHash,
            StatsBoardView.RealmsOf(clusterBoard.Current)
                          .Select(static realm => new RealmPeerCount(realm.Name, realm.Peers))
                          .ToArray()));
    }

    /// <summary>Built per request: the feature-flag overrides change as remote documents apply.</summary>
    private StatsResponse About() =>
        StatsResponse.Ok(new AboutResponse(
            identity.CommitHash, clusterBoard.Current.Peers.Count, featureFlags.AppliedOverrides));

    private static StatsResponse RedirectToDefaultRealm(string[] segments, StatsQuery query)
    {
        var location = $"/realms/{DEFAULT_REALM}/{string.Join('/', segments)}";

        return StatsResponse.PermanentRedirect(
            query.Raw.Length == 0 ? location : $"{location}?{query.Raw}");
    }

    private StatsBoardView Read() =>
        StatsBoardView.Read(clusterBoard, snapshotBoard, parcelEncoder, timeProvider);

    /// <summary>
    ///     ISO-8601 UTC with milliseconds and a <c>Z</c> — what <c>new Date(ms).toISOString()</c>
    ///     produces. <see cref="CultureInfo.InvariantCulture" /> is load-bearing: <c>:</c> in a custom
    ///     format string is the culture's time separator, so under <c>LANG=fi_FI.UTF-8</c> this would
    ///     emit <c>2026-09-04T09.52.47.834Z</c>. The explicit pattern, not <c>"o"</c>, which writes
    ///     seven fractional digits where the contract pins three.
    /// </summary>
    private static string IsoUtcMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                      .UtcDateTime
                      .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
