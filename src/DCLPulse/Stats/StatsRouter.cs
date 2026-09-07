using Pulse.Clusters;
using Pulse.FeatureFlags;
using Pulse.InterestManagement;
using Pulse.Peers.Simulation;

namespace Pulse.Stats;

/// <summary>
///     The read-only stats surface (iteration-2 C2): the routes archipelago-stats used to answer,
///     re-sourced from Pulse's own boards and scoped by realm. Unauthenticated, exactly like
///     <c>/about</c> — the only thing here is who is standing where, which every client learns from
///     the comms feed anyway — while <c>/metrics</c> keeps its bearer token and stays with
///     <see cref="HttpService" />.
///     <para />
///     A router rather than another arm of <c>HttpService</c>'s switch, and separate from the listener
///     entirely, for two reasons: the paths now carry a variable segment (<c>/realms/{realm}/…</c>)
///     that a switch cannot express, and every response can then be asserted against the contract
///     goldens without an <c>HttpListener</c> in the test.
///     <para />
///     Three rules run through the whole surface:
///     <list type="bullet">
///         <item>
///             a realm path segment matches case-insensitively and the response carries the canonical
///             lowercase name, because a realm typed by a user or read off a scene deployment is the
///             same realm however it was spelled;
///         </item>
///         <item>
///             an unknown realm is an empty realm — 200 with an empty list, never 404. A realm exists
///             exactly as long as someone is in it, so "nobody is there" and "there is no such place"
///             are the same fact, and a caller polling a world that has just emptied should not have
///             to treat that as an error;
///         </item>
///         <item>
///             the legacy unscoped paths answer 308 into <c>/realms/main/…</c> with the query string
///             intact, so a caller written against archipelago-stats keeps working while it is
///             updated.
///         </item>
///     </list>
/// </summary>
public sealed class StatsRouter(
    ClusterBoard clusterBoard,
    SnapshotBoard snapshotBoard,
    ParcelEncoder parcelEncoder,
    ITimeProvider timeProvider,
    ServiceIdentity identity,
    PulseFlagsConfigurationProvider featureFlags)
{
    /// <summary>
    ///     The realm the legacy unscoped paths redirect to: Genesis City, which is what every one of
    ///     them meant when there was only one realm to mean.
    /// </summary>
    public const string DEFAULT_REALM = "main";

    /// <summary>
    ///     Cap on <c>/peers?id=</c>. The route exists so a consumer can resolve a page of wallets in
    ///     one call; without a bound, one request could ask this server to scan its whole population
    ///     per id, and a caller that wants everything has <c>?all=true</c>.
    /// </summary>
    public const int MAX_IDS = 200;

    /// <summary>
    ///     Answers <paramref name="absolutePath" />, or reports 404 with no body for a path this
    ///     surface does not own — which is what <see cref="HttpService" /> answered before these
    ///     routes existed and what its remaining routes fall through to.
    /// </summary>
    public StatsResponse Handle(string absolutePath, StatsQuery query)
    {
        string[] segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The /comms/ prefix is a second spelling of the same legacy paths, so it is stripped before
        // matching and the redirect it produces points at the unprefixed canonical route.
        if (segments is ["comms", ..])
            segments = segments[1..];

        return segments switch
        {
            ["health"] => new StatsResponse(200),
            ["about"] => About(),
            ["status"] => Status(),

            ["realms"] => Realms(),
            ["realms", var realm, "peers"] => RealmPeers(realm),
            ["realms", var realm, "parcels"] => RealmParcels(realm),
            ["realms", var realm, "islands"] => RealmIslands(realm),
            ["realms", var realm, "islands", var island] => RealmIsland(realm, island),

            // All-realms peer lookups. They share a path with the legacy redirect, and the query
            // decides which it is: an `id` or `all` parameter is a caller that already knows about
            // this route, so it is answered rather than redirected.
            ["peers"] when query.Has("id") => PeersByIds(query.All("id")),
            ["peers"] when query.Has("all") => AllPeers(),
            ["peers", var id] => SinglePeer(id),

            ["peers"] or ["parcels"] or ["islands"] or ["islands", _] => RedirectToDefaultRealm(segments, query),

            _ => StatsResponse.NotFound(),
        };
    }

    private StatsResponse Realms()
    {
        StatsBoardView view = Read();

        return StatsResponse.Ok(new RealmsResponse(view.Realms(), IsoUtcMs(view.TakenAtUnixMs)));
    }

    private StatsResponse RealmPeers(string realm)
    {
        string canonical = CanonicalName.Of(realm);

        return StatsResponse.Ok(new PeersResponse(Ok: true, Read().PeersIn(canonical), canonical));
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
    ///     One island, as the body itself rather than in an envelope — the shape stats answered, kept
    ///     unchanged. Ids are unique across realms, so an id that exists in another realm is a 404
    ///     here: the answer to "is this island in this realm" is no.
    /// </summary>
    private StatsResponse RealmIsland(string realm, string islandId)
    {
        IslandResult? island = Read().IslandIn(CanonicalName.Of(realm), islandId);

        return island is null ? StatsResponse.NotFound() : StatsResponse.Ok(island);
    }

    private StatsResponse PeersByIds(IReadOnlyList<string> ids)
    {
        if (ids.Count > MAX_IDS)
            return StatsResponse.Json(400, new ErrorResponse(Ok: false, $"too many ids (max {MAX_IDS})"));

        return StatsResponse.Ok(new PeersResponse(Ok: true, Read().PeersMatching(ids)));
    }

    private StatsResponse AllPeers() =>
        StatsResponse.Ok(new PeersResponse(Ok: true, Read().AllPeers()));

    /// <summary>
    ///     One wallet across every realm. 404 with <c>{"ok":false,"peer":null}</c> rather than an
    ///     empty body: the caller asked a yes/no question, and a body that says "no" is easier to
    ///     handle than a status code alone.
    /// </summary>
    private StatsResponse SinglePeer(string id)
    {
        PeerResult? peer = Read().Peer(id);

        return peer is null
            ? StatsResponse.Json(404, new PeerResponse(Ok: false, Peer: null))
            : StatsResponse.Ok(new PeerResponse(Ok: true, peer));
    }

    /// <summary>
    ///     <c>version</c> is kept because the Godot client reads it; <c>currentTime</c> is now rather
    ///     than the pass time, since a client using it to check its own clock wants this server's
    ///     clock, not the age of its clustering.
    /// </summary>
    private StatsResponse Status()
    {
        StatsBoardView view = Read();

        return StatsResponse.Ok(new StatusResponse(
            identity.Version,
            timeProvider.UnixTimeMs,
            identity.CommitHash,
            view.Realms().Select(static realm => new RealmPeerCount(realm.Name, realm.Peers)).ToArray()));
    }

    /// <summary>
    ///     Built per request rather than cached: the feature-flag overrides change whenever a new
    ///     remote document is applied, and the point of reporting them is to show what this task is
    ///     running right now.
    /// </summary>
    private StatsResponse About() =>
        StatsResponse.Ok(new AboutResponse(identity.CommitHash, Read().UserCount, featureFlags.AppliedOverrides));

    private static StatsResponse RedirectToDefaultRealm(string[] segments, StatsQuery query)
    {
        var location = $"/realms/{DEFAULT_REALM}/{string.Join('/', segments)}";

        return StatsResponse.MovedPermanently(
            query.Raw.Length == 0 ? location : $"{location}?{query.Raw}");
    }

    private StatsBoardView Read() =>
        StatsBoardView.Read(clusterBoard, snapshotBoard, parcelEncoder, timeProvider);

    /// <summary>
    ///     ISO-8601 UTC with milliseconds and a <c>Z</c> — what <c>new Date(ms).toISOString()</c>
    ///     produces, since every consumer of this field parses it with a JavaScript <c>Date</c>.
    /// </summary>
    private static string IsoUtcMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
