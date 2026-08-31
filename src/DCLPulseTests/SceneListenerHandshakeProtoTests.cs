using Decentraland.Pulse;
using Google.Protobuf;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerHandshakeProtoTests
{
    [Test]
    public void SceneListenerHandshake_RoundTripsThroughEnvelope()
    {
        var request = new SceneListenerHandshakeRequest { AuthChain = ByteString.CopyFromUtf8("{}") };

        var main = new SceneListenerAoi { Realm = "main" };
        // Negative coordinates exercise the sint32 zigzag encoding.
        main.ParcelRects.Add(new ParcelRect { MinX = -150, MinZ = -2, MaxX = -140, MaxZ = 3 });
        main.ParcelRects.Add(new ParcelRect { MinX = 7, MinZ = 7, MaxX = 7, MaxZ = 7 });

        // A second realm at the same parcels as the first — two cohosted worlds both starting
        // at 0,0 is the shape the per-realm AoI exists for, so the envelope must carry it.
        var world = new SceneListenerAoi { Realm = "world.dcl.eth" };
        world.ParcelRects.Add(new ParcelRect { MinX = 7, MinZ = 7, MaxX = 7, MaxZ = 7 });

        request.Aoi.Add(main);
        request.Aoi.Add(world);

        var envelope = new ClientMessage { SceneListenerHandshake = request };

        ClientMessage parsed = ClientMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.That(parsed.MessageCase, Is.EqualTo(ClientMessage.MessageOneofCase.SceneListenerHandshake));
        Assert.That(parsed.SceneListenerHandshake.Aoi, Has.Count.EqualTo(2));
        Assert.That(parsed.SceneListenerHandshake.Aoi[0].Realm, Is.EqualTo("main"));
        Assert.That(parsed.SceneListenerHandshake.Aoi[0].ParcelRects, Has.Count.EqualTo(2));
        Assert.That(parsed.SceneListenerHandshake.Aoi[0].ParcelRects[0].MinX, Is.EqualTo(-150));
        Assert.That(parsed.SceneListenerHandshake.Aoi[0].ParcelRects[0].MaxZ, Is.EqualTo(3));
        Assert.That(parsed.SceneListenerHandshake.Aoi[0].ParcelRects[1].MaxX, Is.EqualTo(7));
        Assert.That(parsed.SceneListenerHandshake.Aoi[1].Realm, Is.EqualTo("world.dcl.eth"));
        Assert.That(parsed.SceneListenerHandshake.Aoi[1].ParcelRects[0].MaxX, Is.EqualTo(7));
    }
}
