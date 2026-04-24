using NUnit.Framework;
using Pulse.Messaging.Hardening;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class BanListTests
{
    [Test]
    public void IsBanned_EmptyList_ReturnsFalse()
    {
        var list = new BanList();

        Assert.That(list.IsBanned("0xabc"), Is.False);
    }

    [Test]
    public void Replace_PopulatesList_IsBannedReturnsTrue()
    {
        var list = new BanList();

        list.Replace(["0xabc", "0xdef"]);

        Assert.That(list.IsBanned("0xabc"), Is.True);
        Assert.That(list.IsBanned("0xdef"), Is.True);
        Assert.That(list.IsBanned("0x123"), Is.False);
    }

    [Test]
    public void IsBanned_CaseInsensitive()
    {
        var list = new BanList();

        list.Replace(["0xABCDEF0000000000000000000000000000000000"]);

        Assert.That(list.IsBanned("0xabcdef0000000000000000000000000000000000"), Is.True);
        Assert.That(list.IsBanned("0xABCDEF0000000000000000000000000000000000"), Is.True);
        Assert.That(list.IsBanned("0xAbCdEf0000000000000000000000000000000000"), Is.True);
    }

    [Test]
    public void Replace_ReturnsOnlyNewlyAdded()
    {
        var list = new BanList();
        list.Replace(["0xabc", "0xdef"]);

        IReadOnlyCollection<string> added = list.Replace(["0xabc", "0xdef", "0x111"]);

        Assert.That(added, Is.EquivalentTo(new[] { "0x111" }));
    }

    [Test]
    public void Replace_RemovedAddresses_NoLongerBanned()
    {
        var list = new BanList();
        list.Replace(["0xabc", "0xdef"]);

        list.Replace(["0xabc"]);

        Assert.That(list.IsBanned("0xabc"), Is.True);
        Assert.That(list.IsBanned("0xdef"), Is.False,
            "Wallet removed from the upstream list must no longer be considered banned");
    }

    [Test]
    public void Replace_DiffIsCaseInsensitive()
    {
        var list = new BanList();
        list.Replace(["0xABC"]);

        IReadOnlyCollection<string> added = list.Replace(["0xabc", "0xDEF"]);

        Assert.That(added, Is.EquivalentTo(new[] { "0xDEF" }),
            "An address that differs only in casing must not be reported as newly added");
    }
}
