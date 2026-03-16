using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void ProfileAnnouncement_SentWhenProfileRecentlyUpdated()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // First tick: subject joins
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject updates profile
        PublishSnapshot(subject, seq: 2);
        profileBoard.Set(subject, version: 5);

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage announcement = messages.First(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced);

        Assert.That(announcement.To, Is.EqualTo(observer));
        Assert.That(announcement.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(announcement.Message.PlayerProfileVersionAnnounced.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(announcement.Message.PlayerProfileVersionAnnounced.Version, Is.EqualTo(5));
    }

    [Test]
    public void ProfileAnnouncement_NotSentWhenProfileNotRecentlyUpdated()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        // First tick: subject joins
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Tick without profile change
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced), Is.False);
    }

    [Test]
    public void ProfileAnnouncement_NotSentOnPlayerJoined()
    {
        // Profile is set before the subject is visible — announcement flag is active
        profileBoard.Set(subject, version: 3);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> messages = DrainAllMessages();

        PlayerJoined joinedMessage = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined).Message.PlayerJoined;

        Assert.That(joinedMessage.ProfileVersion, Is.EqualTo(3));
        Assert.That(messages.Any(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced), Is.False);
    }

    [Test]
    public void ProfileAnnouncement_ClearedAfterTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Profile update triggers announcement on tick 1
        PublishSnapshot(subject, seq: 2);
        profileBoard.Set(subject, version: 7);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Tick 2: no new profile change — announcement should not repeat
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced), Is.False);
    }
}
