using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Transport.Hardening;

namespace DCLPulseTests;

/// <summary>
///     Shared construction for the scene-listener collaborators <c>FieldValidator</c> takes,
///     for the fixtures that need one without exercising a listener announcement.
/// </summary>
internal static class SceneListenerTestFactory
{
    /// <summary>Cell mapper over a default-configured parcel grid.</summary>
    internal static SceneListenerCellMapper CellMapper() =>
        new (new SpatialGrid(100, 100), Options.Create(new ParcelEncoderOptions()));

    /// <summary>
    ///     Live IP limiter exempting <paramref name="whitelist" /> — a comma-separated list in the
    ///     same form the option takes — with caps high enough that no fixture trips them. A real
    ///     limiter rather than a substitute: it is sealed, and the reservation bookkeeping an
    ///     exemption lookup reads is precisely what stubbing would erase. Bind a peer to an address
    ///     with <c>Bind</c> to make it exempt; an unbound peer never is.
    /// </summary>
    internal static IpLimiter Limiter(string whitelist = "")
    {
        IOptionsMonitor<IpLimiterOptions> monitor = Substitute.For<IOptionsMonitor<IpLimiterOptions>>();

        monitor.CurrentValue.Returns(new IpLimiterOptions
        {
            Enabled = true,
            MaxConcurrency = 64,
            SceneListenerMaxConcurrency = 64,
            Whitelist = whitelist,
        });

        monitor.OnChange(Arg.Any<Action<IpLimiterOptions, string?>>()).Returns(Substitute.For<IDisposable>());

        return new IpLimiter(monitor, Substitute.For<ILogger<IpLimiter>>());
    }
}
