using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Stats;

/// <summary>
///     What a stats route answered: a status, an optional JSON body and, for the legacy paths, the
///     <c>Location</c> a 308 points at. A null <see cref="Body" /> is an empty body, not JSON null.
/// </summary>
public readonly record struct StatsResponse(int Status, byte[]? Body = null, string? Location = null)
{
    public static StatsResponse NotFound() =>
        new (404);

    public static StatsResponse Ok<T>(T body) =>
        new (200, StatsJson.Serialize(body));

    public static StatsResponse Json<T>(int status, T body) =>
        new (status, StatsJson.Serialize(body));

    /// <summary>
    ///     308, not 301: preserves the method, so no intermediary rewrites a legacy request to GET.
    /// </summary>
    public static StatsResponse MovedPermanently(string location) =>
        new (308, Location: location);
}

/// <summary>
///     One JSON configuration for the whole stats surface: camelCase, nulls omitted — which is what
///     lets one <see cref="PeerResult" /> serve both the realm-scoped routes (no <c>realm</c> per
///     entry) and the all-realms ones — and defaults kept, since every field in these shapes is one
///     archipelago-stats always sent. A shape that must write its null opts out per property; see
///     <see cref="PeerResponse" />.
/// </summary>
public static class StatsJson
{
    public static byte[] Serialize<T>(T body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, typeof(T), StatsJsonContext.Default);
}

/// <summary>
///     Source-generated metadata for the stats shapes, as every other <c>System.Text.Json</c> call
///     site in this repo uses. Reflection-based serialization fails silently under trimming — a
///     property simply stops being written — and no test would catch it.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RealmsResponse))]
[JsonSerializable(typeof(PeersResponse))]
[JsonSerializable(typeof(PeerResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ParcelsResponse))]
[JsonSerializable(typeof(IslandsResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(AboutResponse))]
internal partial class StatsJsonContext : JsonSerializerContext;

/// <summary>
///     The archipelago-stats peer shape, unchanged, plus the <c>realm</c> the all-realms routes
///     carry (null on the realm-scoped routes, where it would repeat the envelope).
///     <see cref="Id" /> duplicates <see cref="Address" />: both are in the frozen shape.
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
///     A peers list. <see cref="Realm" /> is null on the all-realms routes, where the entries carry
///     their own. Property order is not part of the contract — <c>openapi.yaml</c> pins array element
///     order only, and the golden harness compares keys rather than sequence.
/// </summary>
public sealed record PeersResponse(bool Ok, string? Realm, IReadOnlyList<PeerResult> Peers);

/// <summary>
///     <c>/peers/{id}</c>. <c>peer</c> is written even when null, against
///     <see cref="StatsJsonContext" />'s omission of nulls: the 404 body is
///     <c>{"ok":false,"peer":null}</c> per <c>openapi.yaml</c>.
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
///     size nowhere, and reporting a bound it does not enforce would be a lie.
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
///     <c>/about</c>. <c>featureFlagOverrides</c> is reported verbatim, and the remote document may
///     set any configuration key — so whatever it sets is public, on an endpoint with no token.
/// </summary>
public sealed record AboutResponse(
    string CommitHash,
    int UserCount,
    IReadOnlyDictionary<string, string?> FeatureFlagOverrides);
