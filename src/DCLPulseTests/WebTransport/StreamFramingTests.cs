using System;
using System.Collections.Generic;
using System.IO;
using Pulse.Transport.WebTransport;

namespace DCLPulseTests.WebTransport;

[TestFixture]
public class StreamFramingTests
{
    [Test]
    public void Frame_PrependsFourByteBigEndianLength()
    {
        byte[] framed = StreamFraming.Frame(new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.That(framed.Length, Is.EqualTo(7));
        Assert.That(framed[..4], Is.EqualTo(new byte[] { 0, 0, 0, 3 }));
        Assert.That(framed[4..], Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC }));
    }

    [Test]
    public void Reader_ReadsSingleFrame_ThenReportsEmpty()
    {
        var reader = new StreamFrameReader(1024);
        reader.Append(StreamFraming.Frame(new byte[] { 1, 2, 3, 4 }));

        Assert.That(reader.TryRead(out byte[] msg), Is.True);
        Assert.That(msg, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(reader.TryRead(out _), Is.False);
    }

    [Test]
    public void Reader_ReassemblesFrameDeliveredOneByteAtATime()
    {
        var reader = new StreamFrameReader(1024);
        byte[] framed = StreamFraming.Frame(new byte[] { 9, 8, 7, 6, 5 });

        for (var i = 0; i < framed.Length - 1; i++)
        {
            reader.Append(new[] { framed[i] });
            Assert.That(reader.TryRead(out _), Is.False, "must not yield until the whole frame arrives");
        }

        reader.Append(new[] { framed[^1] });
        Assert.That(reader.TryRead(out byte[] msg), Is.True);
        Assert.That(msg, Is.EqualTo(new byte[] { 9, 8, 7, 6, 5 }));
    }

    [Test]
    public void Reader_SplitsMultipleFramesCoalescedInOneChunk()
    {
        var reader = new StreamFrameReader(1024);
        var combined = new List<byte>();
        combined.AddRange(StreamFraming.Frame(new byte[] { 1 }));
        combined.AddRange(StreamFraming.Frame(new byte[] { 2, 2 }));
        combined.AddRange(StreamFraming.Frame(new byte[] { 3, 3, 3 }));
        reader.Append(combined.ToArray());

        Assert.That(reader.TryRead(out byte[] m1), Is.True);
        Assert.That(m1, Is.EqualTo(new byte[] { 1 }));
        Assert.That(reader.TryRead(out byte[] m2), Is.True);
        Assert.That(m2, Is.EqualTo(new byte[] { 2, 2 }));
        Assert.That(reader.TryRead(out byte[] m3), Is.True);
        Assert.That(m3, Is.EqualTo(new byte[] { 3, 3, 3 }));
        Assert.That(reader.TryRead(out _), Is.False);
    }

    [Test]
    public void Reader_HandlesEmptyPayloadFrame()
    {
        var reader = new StreamFrameReader(1024);
        reader.Append(StreamFraming.Frame(Array.Empty<byte>()));

        Assert.That(reader.TryRead(out byte[] msg), Is.True);
        Assert.That(msg, Is.Empty);
    }

    [Test]
    public void Reader_LengthExceedingCap_Throws()
    {
        var reader = new StreamFrameReader(maxMessageLength: 4);
        reader.Append(new byte[] { 0, 0, 0, 5 }); // declares length 5 > cap 4

        Assert.That(() => reader.TryRead(out _), Throws.TypeOf<InvalidDataException>());
    }
}
