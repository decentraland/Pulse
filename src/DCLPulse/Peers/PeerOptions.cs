using Pulse.Peers.Simulation;

namespace Pulse.Peers;

public sealed class PeerOptions
{
    public const string SECTION_NAME = "Peers";

    public int SnapshotHistoryCapacity { get; set; } = 10;

    /// <summary>
    ///     Maximum number of worker threads. Defaults to <see cref="Environment.ProcessorCount" />.
    ///     Useful for limiting thread count on machines with many cores.
    /// </summary>
    public int MaxWorkerThreads { get; set; }

    /// <summary>
    ///     Simulation steps in milliseconds for tiers
    /// </summary>
    public uint[] SimulationSteps { get; set; } = new[] { 50u, 100u, 200u };

    /// <summary>
    ///     When enabled, each peer receives its own state updates as if from another peer
    ///     identified by <see cref="PeerSimulation.SELF_MIRROR_WALLET_ID" />.
    /// </summary>
    public bool SelfMirrorEnabled { get; set; }

    /// <summary>
    ///     Tier index (0, 1, 2) used for self-mirror updates, controlling update frequency and field detail.
    /// </summary>
    public int SelfMirrorTier { get; set; }

    /// <summary>
    ///     When enabled, resync responses attempt a targeted delta from the client's
    ///     known baseline before falling back to STATE_FULL.
    /// </summary>
    public bool ResyncWithDelta { get; set; }

    /// <summary>
    ///     How long a peer remains in DISCONNECTING before <c>CleanupDisconnectedPeer</c> fires,
    ///     wipes every per-peer board, and releases the slot back to
    ///     <see cref="PeerIndexAllocator" />. This is the single clock governing slot reuse — the
    ///     allocator has no independent timer, so its pending-recycle state and the simulation's
    ///     cleanup state cannot drift.
    ///     <para />
    ///     Ordering against <c>PlayerLeft</c>: the <c>Disconnected</c> lifecycle event already
    ///     cleared the peer from <see cref="SnapshotBoard" /> and the <c>SpatialGrid</c>, so no
    ///     observer collects it again and its views are swept — emitting <c>PlayerLeft</c> — within
    ///     <c>(VIEW_STALE_TICKS + SWEEP_CHECK_INTERVAL) × BaseTickMs</c>, 4000 ms against this
    ///     5000 ms deadline at the defaults. That is a real margin at the default configuration
    ///     under nominal tick pacing, but not a guarantee: the sweep is counted in simulation
    ///     ticks while this timeout is wall clock, and the worker loop never skips ticks to catch
    ///     up, so the effective tick period is <c>max(BaseTickMs, actual tick duration)</c>.
    ///     Sustained tick overrun beyond ~62.5 ms per pass, or a <c>Peers:SimulationSteps[0]</c>
    ///     above 62 ms, stretches the sweep past this deadline and inverts the ordering. The
    ///     guarantee of last resort when they do cross is
    ///     <c>PeerSimulation.DetectAndHandleAliasing</c>.
    ///     <para />
    ///     The auth-timeout path funnels through the same sequence — a PENDING_AUTH peer that
    ///     times out is transport-disconnected, which triggers the ENet disconnect event and the
    ///     same DISCONNECTING → cleanup → release flow.
    /// </summary>
    public uint DisconnectionCleanTimeoutMs { get; set; } = 5000;

    /// <summary>
    ///     Maximum time a peer may stay in PENDING_AUTH before being force-disconnected. After
    ///     force-disconnect, the slot still follows the normal disconnect cleanup path governed
    ///     by <see cref="DisconnectionCleanTimeoutMs" />.
    /// </summary>
    public uint PendingAuthCleanTimeoutMs { get; set; } = 30000;
}
