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
}
