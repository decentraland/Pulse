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

        request.ParcelIndices.AddRange(new[] { 100, 101, 417 });

        var envelope = new ClientMessage { SceneListenerHandshake = request };

        ClientMessage parsed = ClientMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.That(parsed.MessageCase, Is.EqualTo(ClientMessage.MessageOneofCase.SceneListenerHandshake));
        Assert.That(parsed.SceneListenerHandshake.Realm, Is.EqualTo("main"));
        Assert.That(parsed.SceneListenerHandshake.ParcelIndices, Is.EqualTo(new[] { 100, 101, 417 }));
    }
}
