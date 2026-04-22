using Pulse.Transport;

namespace DCLPulseTests;

[TestFixture]
public class ENetTransportOptionsTests
{
    [Test]
    public void EffectiveMaxConcurrentConnections_FallsBackToMaxPeers_WhenUnset()
    {
        var options = new ENetTransportOptions { MaxPeers = 4095 };

        Assert.That(options.EffectiveMaxConcurrentConnections, Is.EqualTo(4095));
    }

    [Test]
    public void EffectiveMaxConcurrentConnections_FallsBackToMaxPeers_WhenExplicitlyZero()
    {
        var options = new ENetTransportOptions { MaxPeers = 4095, MaxConcurrentConnections = 0 };

        Assert.That(options.EffectiveMaxConcurrentConnections, Is.EqualTo(4095));
    }

    [Test]
    public void EffectiveMaxConcurrentConnections_UsesExplicitValue_WhenSet()
    {
        var options = new ENetTransportOptions { MaxPeers = 4095, MaxConcurrentConnections = 4000 };

        Assert.That(options.EffectiveMaxConcurrentConnections, Is.EqualTo(4000));
    }
}
