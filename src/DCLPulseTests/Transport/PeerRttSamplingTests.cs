using Pulse.Transport;
using Pulse.Transport.Geo;
using System.Diagnostics.Metrics;

namespace DCLPulseTests.Transport;

[TestFixture]
public class PeerRttSamplingTests
{
    private static (MeterListener Listener, List<long> Values) Capture(string instrumentName)
    {
        var values = new List<long>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "DCLPulse" && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.Start();
        return (listener, values);
    }

    [Test]
    public void Rtt_records_on_the_continent_instrument()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.transport.peer_rtt_sa_ms");
        using MeterListener _ = listener;

        ENetHostedService.RecordPeerRtt(Continent.SOUTH_AMERICA, 123);

        Assert.That(values, Is.EqualTo(new[] { 123L }));
    }

    [Test]
    public void Out_of_range_continent_clamps_to_unknown()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.transport.peer_rtt_unknown_ms");
        using MeterListener _ = listener;

        ENetHostedService.RecordPeerRtt((Continent)200, 77);

        Assert.That(values, Is.EqualTo(new[] { 77L }));
    }
}
