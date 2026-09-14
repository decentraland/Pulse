using System.Reflection;
using PulseTestClient.Comms;

namespace DCLPulseTests.Comms;

[TestFixture]
[NonParallelizable]
public class LiveKitJoinerTests
{
    [Test]
    public async Task RejoinProbe_RechecksAssignmentAfterWaitingForGate()
    {
        await using var joiner = new LiveKitJoiner("race", rejoinAfterMs: 0);
        Type joinerType = typeof(LiveKitJoiner);
        var gate = (SemaphoreSlim)RequiredField(joinerType, "gate").GetValue(joiner)!;
        FieldInfo latestConnStr = RequiredField(joinerType, "latestConnStr");
        FieldInfo latestIslandId = RequiredField(joinerType, "latestIslandId");
        MethodInfo rejoin = joinerType.GetMethod("RejoinAfterDelayAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        const string originalConnStr = "livekit:wss://old.example?access_token=old";
        const string newerConnStr = "livekit:wss://new.example?access_token=new";
        using var probeCts = new CancellationTokenSource();
        using var joinCts = new CancellationTokenSource();
        using var output = new StringWriter();
        TextWriter previousOutput = Console.Out;
        Task? probe = null;
        bool gateHeld = false;

        try
        {
            try
            {
                await gate.WaitAsync();
                gateHeld = true;
                latestConnStr.SetValue(joiner, originalConnStr);
                latestIslandId.SetValue(joiner, "old-room");
                Console.SetOut(output);

                probe = (Task)rejoin.Invoke(joiner,
                    ["invalid://old.example", "old-token", originalConnStr, joinCts.Token, probeCts])!;

                Assert.That(probe.IsCompleted, Is.False, "the probe must be waiting for the held room gate");

                latestConnStr.SetValue(joiner, newerConnStr);
                latestIslandId.SetValue(joiner, "new-room");
            }
            finally
            {
                if (gateHeld) gate.Release();
            }

            try
            {
                await probe!.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                await joinCts.CancelAsync();
                await probe!.WaitAsync(TimeSpan.FromSeconds(2));
            }

            string log = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain("re-join probe cancelled: newer assignment 'new-room' received"));
                Assert.That(log, Does.Not.Contain("RE-JOIN ACCEPTED"));
                Assert.That(log, Does.Not.Contain("RE-JOIN REJECTED"));
            });
        }
        finally
        {
            Console.SetOut(previousOutput);
        }
    }

    private static FieldInfo RequiredField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new AssertionException($"Expected private field '{name}' was not found.");
}
