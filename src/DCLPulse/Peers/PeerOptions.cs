namespace Pulse.Peers;

public sealed class PeerOptions
{
    public const string SECTION_NAME = "Peers";

    public int SnapshotHistoryCapacity { get; set; } = 10;

    /// <summary>
    ///     Simulation steps in milliseconds for tiers
    /// </summary>
    public uint[] SimulationSteps { get; set; } = new[] { 50u, 100u, 200u };
}
