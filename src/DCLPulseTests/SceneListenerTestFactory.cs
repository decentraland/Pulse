using Microsoft.Extensions.Options;
using Pulse.InterestManagement;

namespace DCLPulseTests;

/// <summary>
///     Shared construction for the scene-listener collaborators <c>FieldValidator</c> takes,
///     for the fixtures that need one without exercising a listener announcement.
/// </summary>
internal static class SceneListenerTestFactory
{
    /// <summary>Cell mapper over a default-configured parcel grid.</summary>
    internal static SceneListenerCellMapper CellMapper() =>
        new (new RealmSpatialGrids(100, 100), Options.Create(new ParcelEncoderOptions()));
}
