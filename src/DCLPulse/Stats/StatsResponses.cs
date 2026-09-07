using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Stats;

/// <summary>
///     What a stats route answered: a status, an optional JSON body and, for the legacy paths, the
///     <c>Location</c> a 308 points at. A null <see cref="Body" /> is an empty response body —
///     <c>/health</c>, and the 404s that archipelago-stats answers with nothing — not a JSON
///     <c>null</c>.
/// </summary>
public readonly record struct StatsResponse(int Status, byte[]? Body = null, string? Location = null)
{
    public static StatsResponse NotFound() =>
        new (404);

    public static StatsResponse Ok(object body) =>
        new (200, StatsJson.Serialize(body));

    public static StatsResponse Json(int status, object body) =>
        new (status, StatsJson.Serialize(body));

    /// <summary>
    ///     A permanent redirect that preserves the method and the query string — 308 rather than 301
    ///     because the legacy callers are scripts and services whose requests must not be rewritten
    ///     to GET by an intermediary.
    /// </summary>
    public static StatsResponse MovedPermanently(string location) =>
        new (308, Location: location);
}

/// <summary>
///     One JSON configuration for the whole stats surface: camelCase members, and proto3-style
///     omission is <em>not</em> wanted here — a peer with no realm is not a peer, and every field in
///     these shapes is one archipelago-stats always sent, so nothing is dropped for being default.
///     <para />
///     Nulls are omitted, which is what lets one <see cref="PeerResult" /> serve both the
///     realm-scoped routes (no <c>realm</c> per entry — the envelope carries it) and the all-realms
///     ones. A shape that has to write its null says so per property with
///     <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>; see <see cref="PeerResponse" />.
/// </summary>
public static class StatsJson
{
    public static readonly JsonSerializerOptions OPTIONS = new ()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize(object body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, OPTIONS);
}

/// <summary>
///     The archipelago-stats peer shape, unchanged, plus the <c>realm</c> the all-realms routes carry
///     (<see cref="Realm" /> is null on the realm-scoped routes, where it would only repeat the
///     envelope). <see cref="Id" /> duplicates <see cref="Address" /> because both were in the
///     response every client reads today.
/// </summary>
public sealed record PeerResult(
    string Id,
    string Address,
    long LastPing,
    int[] Parcel,
    float[] Position,
    string? Realm = null);

public sealed record RealmSummary(string Name, int Peers, int Clusters);

public sealed record RealmPeerCount(string Name, int Peers);

public sealed record RealmsResponse(IReadOnlyList<RealmSummary> Realms, string LastUpdated);

/// <summary>
///     A peers list. <see cref="Realm" /> is declared before <see cref="Peers" /> so the envelope
///     reads before the payload on the wire — the shape the contract documents — and is null on the
///     all-realms routes, where each entry carries its own realm instead.
/// </summary>
public sealed record PeersResponse(bool Ok, string? Realm, IReadOnlyList<PeerResult> Peers);

/// <summary>
///     <c>/peers/{id}</c>. <c>peer</c> is written even when it is null, against
///     <see cref="StatsJson.OPTIONS" />' omission of nulls: the 404 body is
///     <c>{"ok":false,"peer":null}</c> per the contract and this repo's own <c>openapi.yaml</c>, and a
///     consumer that tests <c>'peer' in body</c> or validates the published schema reads a body
///     without the key as malformed rather than as "not online".
/// </summary>
public sealed record PeerResponse(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    PeerResult? Peer);

public sealed record ErrorResponse(bool Ok, string Error);

public sealed record ParcelCount(int PeersCount, ParcelPoint Parcel);

public sealed record ParcelPoint(int X, int Y);

public sealed record ParcelsResponse(string Realm, IReadOnlyList<ParcelCount> Parcels);

/// <summary>
///     A cluster in archipelago's island shape. <c>maxPeers</c> is zero because Pulse caps cluster
///     size nowhere, and reporting a bound it does not enforce would be a lie the client could act
///     on.
/// </summary>
public sealed record IslandResult(
    string Id,
    int MaxPeers,
    float[] Center,
    float Radius,
    IReadOnlyList<PeerResult> Peers);

public sealed record IslandsResponse(bool Ok, string Realm, IReadOnlyList<IslandResult> Islands);

public sealed record StatusResponse(
    string Version,
    long CurrentTime,
    string CommitHash,
    IReadOnlyList<RealmPeerCount> Realms);

/// <summary>
///     <c>/about</c>. <c>userCount</c> is what archipelago-stats reported and what realm-provider
///     reads; <c>featureFlagOverrides</c> is Pulse's own addition and is reported verbatim — the
///     remote document may set any configuration key, so whatever it sets appears here, on an
///     endpoint that takes no bearer token.
/// </summary>
public sealed record AboutResponse(
    string CommitHash,
    int UserCount,
    IReadOnlyDictionary<string, string?> FeatureFlagOverrides);
