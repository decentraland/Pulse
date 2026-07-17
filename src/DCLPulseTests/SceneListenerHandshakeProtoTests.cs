using Decentraland.Pulse;
using Google.Protobuf;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerHandshakeProtoTests
{
    [Test]
    public void SceneListenerHandshake_RoundTripsThroughEnvelope()
    {
        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8("{}"),
            Realm = "main",
        };

        // Negative coordinates exercise the sint32 zigzag encoding.
        request.ParcelRects.Add(new ParcelRect { MinX = -150, MinZ = -2, MaxX = -140, MaxZ = 3 });
        request.ParcelRects.Add(new ParcelRect { MinX = 7, MinZ = 7, MaxX = 7, MaxZ = 7 });

        var envelope = new ClientMessage { SceneListenerHandshake = request };

        ClientMessage parsed = ClientMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.That(parsed.MessageCase, Is.EqualTo(ClientMessage.MessageOneofCase.SceneListenerHandshake));
        Assert.That(parsed.SceneListenerHandshake.Realm, Is.EqualTo("main"));
        Assert.That(parsed.SceneListenerHandshake.ParcelRects, Has.Count.EqualTo(2));
        Assert.That(parsed.SceneListenerHandshake.ParcelRects[0].MinX, Is.EqualTo(-150));
        Assert.That(parsed.SceneListenerHandshake.ParcelRects[0].MaxZ, Is.EqualTo(3));
        Assert.That(parsed.SceneListenerHandshake.ParcelRects[1].MaxX, Is.EqualTo(7));
    }
}
