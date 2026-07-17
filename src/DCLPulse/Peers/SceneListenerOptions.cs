namespace Pulse.Peers;

public sealed class SceneListenerOptions
{
    public const string SECTION_NAME = "SceneListener";

    /// <summary>
    ///     Nominal-area budget, in parcels, for the announced rects (Σ of rect areas).
    ///     Handshakes exceeding it are rejected — never clamped.
    /// </summary>
    public int MaxParcels { get; set; } = 4096;
}
