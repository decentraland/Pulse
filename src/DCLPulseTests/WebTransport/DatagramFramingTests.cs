using System;
using Pulse.Transport.WebTransport;

namespace DCLPulseTests.WebTransport;

[TestFixture]
public class DatagramFramingTests
{
    [Test]
    public void FrameThenParse_RoundTrips()
    {
        byte[] framed = DatagramFraming.Frame(channelId: 1, seq: 0x01020304, payload: new byte[] { 7, 7, 7 });

        Assert.That(DatagramFraming.TryParse(framed, out byte ch, out uint seq, out ReadOnlySpan<byte> payload), Is.True);
        Assert.That(ch, Is.EqualTo(1));
        Assert.That(seq, Is.EqualTo(0x01020304u));
        Assert.That(payload.ToArray(), Is.EqualTo(new byte[] { 7, 7, 7 }));
    }

    [Test]
    public void TryParse_ShorterThanHeader_ReturnsFalse()
    {
        Assert.That(DatagramFraming.TryParse(new byte[] { 1, 2, 3 }, out _, out _, out _), Is.False);
    }

    [Test]
    public void Sequencer_IncrementsPerChannelIndependently()
    {
        var seq = new DatagramSequencer();

        Assert.That(seq.Next(1), Is.EqualTo(0u));
        Assert.That(seq.Next(1), Is.EqualTo(1u));
        Assert.That(seq.Next(2), Is.EqualTo(0u)); // channel 2 has its own counter
        Assert.That(seq.Next(1), Is.EqualTo(2u));
    }

    [Test]
    public void Deduper_AcceptsFirstAndNewer_DropsDuplicateAndStale()
    {
        var dedup = new DatagramDeduper();

        Assert.That(dedup.ShouldAccept(1, 5), Is.True);  // first on the channel
        Assert.That(dedup.ShouldAccept(1, 6), Is.True);  // newer
        Assert.That(dedup.ShouldAccept(1, 6), Is.False); // duplicate
        Assert.That(dedup.ShouldAccept(1, 4), Is.False); // reordered straggler
        Assert.That(dedup.ShouldAccept(1, 7), Is.True);  // newer again
    }

    [Test]
    public void Deduper_TracksChannelsIndependently()
    {
        var dedup = new DatagramDeduper();

        Assert.That(dedup.ShouldAccept(1, 10), Is.True);
        Assert.That(dedup.ShouldAccept(2, 1), Is.True);  // low seq on a fresh channel is still accepted
        Assert.That(dedup.ShouldAccept(2, 1), Is.False); // duplicate on channel 2
    }

    [Test]
    public void Deduper_SurvivesSequenceWraparound()
    {
        var dedup = new DatagramDeduper();

        Assert.That(dedup.ShouldAccept(1, uint.MaxValue - 1), Is.True);
        Assert.That(dedup.ShouldAccept(1, uint.MaxValue), Is.True);
        Assert.That(dedup.ShouldAccept(1, 0), Is.True); // wrapped past max — still "newer"
        Assert.That(dedup.ShouldAccept(1, 1), Is.True);
        Assert.That(dedup.ShouldAccept(1, uint.MaxValue), Is.False); // now stale relative to the wrapped value
    }
}
