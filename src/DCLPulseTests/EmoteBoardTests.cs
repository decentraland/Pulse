using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class EmoteBoardTests
{
    private const int MAX_PEERS = 10;
    private EmoteBoard board;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        board = new EmoteBoard(MAX_PEERS);
        peer = new PeerIndex(0);
    }

    [Test]
    public void Get_ReturnsNull_WhenNothingRegistered()
    {
        Assert.That(board.Get(peer), Is.Null);
    }

    [Test]
    public void Start_StoresEmoteState()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);

        EmoteState? state = board.Get(peer);
        Assert.That(state, Is.Not.Null);
        Assert.That(state!.EmoteId, Is.EqualTo("wave"));
        Assert.That(state.StartTick, Is.EqualTo(1000u));
        Assert.That(state.DurationMs, Is.EqualTo(2000u));
        Assert.That(state.StopTick, Is.Null);
        Assert.That(state.StopReason, Is.Null);
    }

    [Test]
    public void Start_LoopingEmote_StoresNullDuration()
    {
        board.Start(peer, "dance", serverTick: 1000, durationMs: null);

        EmoteState? state = board.Get(peer);
        Assert.That(state!.DurationMs, Is.Null);
    }

    [Test]
    public void IsEmoting_ReturnsTrueAfterStart()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);
        Assert.That(board.IsEmoting(peer), Is.True);
    }

    [Test]
    public void IsEmoting_ReturnsFalseAfterStop()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);
        board.Stop(peer, serverTick: 1500);
        Assert.That(board.IsEmoting(peer), Is.False);
    }

    [Test]
    public void IsEmoting_ReturnsFalseWhenNothingRegistered()
    {
        Assert.That(board.IsEmoting(peer), Is.False);
    }

    [Test]
    public void Stop_SetsStopTickAndCancelledReason()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);
        board.Stop(peer, serverTick: 1500);

        EmoteState? state = board.Get(peer);
        Assert.That(state!.EmoteId, Is.Null);
        Assert.That(state.StopTick, Is.EqualTo(1500u));
        Assert.That(state.StopReason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void TryComplete_ExpiresOneShotEmote_WhenDurationElapsed()
    {
        board.Start(peer, "clap", serverTick: 1000, durationMs: 500);

        board.TryComplete(peer, now: 1500);

        EmoteState? state = board.Get(peer);
        Assert.That(state!.EmoteId, Is.Null);
        Assert.That(state.StopTick, Is.EqualTo(1500u));
        Assert.That(state.StopReason, Is.EqualTo(EmoteStopReason.Completed));
    }

    [Test]
    public void TryComplete_DoesNotExpire_WhenDurationNotElapsed()
    {
        board.Start(peer, "clap", serverTick: 1000, durationMs: 500);

        board.TryComplete(peer, now: 1499);

        Assert.That(board.IsEmoting(peer), Is.True);
    }

    [Test]
    public void TryComplete_SkipsLoopingEmote()
    {
        board.Start(peer, "dance", serverTick: 1000, durationMs: null);

        board.TryComplete(peer, now: 999999);

        Assert.That(board.IsEmoting(peer), Is.True);
    }

    [Test]
    public void TryComplete_SkipsAlreadyStoppedEmote()
    {
        board.Start(peer, "clap", serverTick: 1000, durationMs: 500);
        board.Stop(peer, serverTick: 1200);

        board.TryComplete(peer, now: 2000);

        EmoteState? state = board.Get(peer);
        Assert.That(state!.StopReason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void Remove_ClearsState()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);
        board.Remove(peer);
        Assert.That(board.Get(peer), Is.Null);
    }

    [Test]
    public void Start_OverridesPreviousEmote()
    {
        board.Start(peer, "wave", serverTick: 1000, durationMs: 2000);
        board.Start(peer, "dance", serverTick: 2000, durationMs: null);

        EmoteState? state = board.Get(peer);
        Assert.That(state!.EmoteId, Is.EqualTo("dance"));
        Assert.That(state.StartTick, Is.EqualTo(2000u));
    }
}