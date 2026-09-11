using System.Numerics;
using PulseTestClient.Comms;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;

namespace PulseTestClient;

/// <summary>
///     One island assignment as this bot observed it, with the time it arrived.
/// </summary>
/// <remarks>
///     Only what the ws-connector channel actually carries. The upstream <c>cluster_id</c> and
///     <c>realm</c> live on <c>peer.{addr}.cluster_change</c>, which this channel never sees — a
///     scenario asserting the correspondence has to observe the broker directly.
/// </remarks>
public sealed record ObservedAssignment(
    string IslandId,
    string ConnStr,
    string? FromIslandId,
    DateTimeOffset ObservedAt);

public class BotSession
{
    public required string AccountName { get; init; }
    public required Profile Profile { get; init; }
    public required MessagePipe Pipe { get; init; }
    public required PulseMultiplayerService Service { get; init; }
    public required IInputReader InputReader { get; init; }
    public InputState InputCollector { get; } = new ();
    public required Vector3 SpawnOrigin { get; init; }
    public Vector3 Position { get; set; }
    public float RotationY { get; set; }
    public float GroundY { get; set; }
    public float VerticalVelocity { get; set; }
    public bool Airborne { get; set; }
    public int JumpCount { get; set; }
    public uint LastFrameTick { get; set; }
    public uint NextTickMs { get; set; }
    public Dictionary<uint, uint> KnownSeqBySubject { get; } = new ();
    public Dictionary<uint, uint> PendingResyncs { get; } = new ();
    public Dictionary<uint, Web3Address> PeerAddresses { get; } = new ();

    /// <summary>The wallet this bot authenticated with, lowercased — the key every subject is built from.</summary>
    public required string WalletAddress { get; init; }

    /// <summary>The ws-connector channel, when <c>--comms-enabled</c> is set.</summary>
    public CommsChannel? Comms { get; set; }

    /// <summary>
    ///     Every assignment observed, in arrival order. A list rather than just the latest because the
    ///     assertions that matter are about ordering and count — "exactly one reassignment", "no repeat".
    /// </summary>
    public List<ObservedAssignment> Assignments { get; } = [];

    public ObservedAssignment? LatestAssignment =>
        Assignments.Count > 0 ? Assignments[^1] : null;
}
