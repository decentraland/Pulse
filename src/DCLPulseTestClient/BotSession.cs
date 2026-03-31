using System.Numerics;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;

namespace PulseTestClient;

public class BotSession
{
    public required string AccountName { get; init; }
    public required Profile Profile { get; init; }
    public required MessagePipe Pipe { get; init; }
    public required PulseMultiplayerService Service { get; init; }
    public required IInputReader InputReader { get; init; }
    public InputState InputCollector { get; } = new ();
    public Vector3 Position { get; set; }
    public float RotationY { get; set; }
    public uint LastFrameTick { get; set; }
    public Dictionary<uint, uint> KnownSeqBySubject { get; } = new ();
    public Dictionary<uint, Web3Address> PeerAddresses { get; } = new ();
}
