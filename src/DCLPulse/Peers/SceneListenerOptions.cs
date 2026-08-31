namespace Pulse.Peers;

public sealed class SceneListenerOptions
{
    public const string SECTION_NAME = "SceneListener";

    /// <summary>
    ///     Single cumulative budget for a scene-listener announcement, in parcels: Σ of nominal
    ///     rect areas across every realm, plus a fixed charge per announced realm for the overhead
    ///     a parcel count cannot see (see <c>FieldValidator.REALM_BUDGET_COST</c>). Announcements
    ///     exceeding it are rejected — never clamped — on both the handshake and
    ///     <c>SceneListenerUpdate</c>.
    /// </summary>
    public int MaxParcels { get; set; } = 4096;
}
