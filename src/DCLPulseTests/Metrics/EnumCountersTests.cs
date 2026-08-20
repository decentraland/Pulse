using Decentraland.Pulse;
using Pulse.Metrics;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Pins the parameterless <see cref="EnumCounters{TEnum}" /> constructor: the bucket count is
///     derived from the enum (max defined value + 1), so it cannot go stale when the enum grows.
/// </summary>
[TestFixture]
public class EnumCountersTests
{
    private enum Sparse
    {
        First = 0,
        Last = 5,
    }

    [Test]
    public void ParameterlessCtor_SparseEnum_SizesToMaxDefinedValuePlusOne()
    {
        var counters = new EnumCounters<Sparse>();

        counters.Increment(Sparse.Last);

        Assert.That(counters.Read(Sparse.Last), Is.EqualTo(1));
    }

    [Test]
    public void ParameterlessCtor_ClientEnvelope_CoversEveryDefinedCase()
    {
        var counters = new ClientMessageCounters();

        foreach (var value in Enum.GetValues<ClientMessage.MessageOneofCase>())
        {
            counters.Increment(value);
            Assert.That(counters.Read(value), Is.EqualTo(1));
        }
    }

    [Test]
    public void ParameterlessCtor_ServerEnvelope_CoversEveryDefinedCase()
    {
        var counters = new ServerMessageCounters();

        foreach (var value in Enum.GetValues<ServerMessage.MessageOneofCase>())
        {
            counters.Increment(value);
            Assert.That(counters.Read(value), Is.EqualTo(1));
        }
    }
}
