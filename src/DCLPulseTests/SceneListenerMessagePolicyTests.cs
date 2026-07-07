using Decentraland.Pulse;
using Pulse.Peers;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerMessagePolicyTests
{
    private static PeerState Listener() =>
        new (PeerConnectionState.AUTHENTICATED)
        {
            SceneListener = new SceneListenerState("main", new HashSet<int> { 1 }, new long[] { 0L }),
        };

    private static Dictionary<PeerIndex, PeerState> Peers(PeerIndex peer, PeerState state) =>
        new () { [peer] = state };

    [TestCase(ClientMessage.MessageOneofCase.Input)]
    [TestCase(ClientMessage.MessageOneofCase.EmoteStart)]
    [TestCase(ClientMessage.MessageOneofCase.EmoteStop)]
    [TestCase(ClientMessage.MessageOneofCase.Teleport)]
    [TestCase(ClientMessage.MessageOneofCase.ProfileAnnouncement)]
    [TestCase(ClientMessage.MessageOneofCase.Handshake)]
    [TestCase(ClientMessage.MessageOneofCase.SceneListenerHandshake)]
    public void ForbiddenCases_AreDropped(ClientMessage.MessageOneofCase messageCase)
    {
        var peer = new PeerIndex(1);
        ClientMessage message = BuildMessage(messageCase);

        Assert.That(PeersManager.IsForbiddenForSceneListener(Peers(peer, Listener()), peer, message), Is.True);
    }

    [Test]
    public void Resync_IsAllowed()
    {
        var peer = new PeerIndex(1);
        var message = new ClientMessage { Resync = new ResyncRequest() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(Peers(peer, Listener()), peer, message), Is.False);
    }

    [Test]
    public void PlayerPeer_IsNeverGated()
    {
        var peer = new PeerIndex(1);
        var message = new ClientMessage { Input = new PlayerStateInput() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(
            Peers(peer, new PeerState(PeerConnectionState.AUTHENTICATED)), peer, message), Is.False);
    }

    [Test]
    public void UnknownPeer_IsNeverGated()
    {
        var message = new ClientMessage { Input = new PlayerStateInput() };

        Assert.That(PeersManager.IsForbiddenForSceneListener(
            new Dictionary<PeerIndex, PeerState>(), new PeerIndex(1), message), Is.False);
    }

    private static ClientMessage BuildMessage(ClientMessage.MessageOneofCase messageCase) =>
        messageCase switch
        {
            ClientMessage.MessageOneofCase.Input => new ClientMessage { Input = new PlayerStateInput() },
            ClientMessage.MessageOneofCase.EmoteStart => new ClientMessage { EmoteStart = new EmoteStart() },
            ClientMessage.MessageOneofCase.EmoteStop => new ClientMessage { EmoteStop = new EmoteStop() },
            ClientMessage.MessageOneofCase.Teleport => new ClientMessage { Teleport = new TeleportRequest() },
            ClientMessage.MessageOneofCase.ProfileAnnouncement => new ClientMessage { ProfileAnnouncement = new ProfileVersionAnnouncement() },
            ClientMessage.MessageOneofCase.Handshake => new ClientMessage { Handshake = new HandshakeRequest() },
            ClientMessage.MessageOneofCase.SceneListenerHandshake => new ClientMessage { SceneListenerHandshake = new SceneListenerHandshakeRequest() },
            _ => throw new ArgumentOutOfRangeException(nameof(messageCase)),
        };
}
