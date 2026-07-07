namespace Pulse.Peers;

public sealed class SceneListenerOptions
{
    public const string SECTION_NAME = "SceneListener";

    /// <summary>
    ///     Maximum number of distinct parcels a single scene listener may announce.
    ///     A handshake exceeding this after dedup is rejected — never clamped.
    /// </summary>
    public int MaxParcels { get; set; } = 256;
}
