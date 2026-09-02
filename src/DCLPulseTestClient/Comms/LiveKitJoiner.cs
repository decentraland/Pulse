using LiveKit.Rtc;

namespace PulseTestClient.Comms;

/// <summary>
///     Joins the LiveKit room a conn string was minted for, so a run can assert what a real client
///     asserts — a live session — instead of only that a token arrived. Those two differ exactly where
///     it matters: a token that is well-formed but rejected by LiveKit reads as success to anyone who
///     only inspects its claims.
///     <para />
///     Reports the room the server actually placed us in, which is a stronger statement than the room
///     named in the token: the claim is what was asked for, and this is what was granted.
///     <para />
///     Its own failure domain, like <see cref="CommsChannel" />: <see cref="JoinAsync" /> never throws,
///     so a room that will not accept us ends this join and nothing else.
/// </summary>
public sealed class LiveKitJoiner(string account) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new (1, 1);

    private Room? room;
    private string? latestConnStr;

    /// <summary>
    ///     Leaves the current room, if any, and joins the one <paramref name="connStr" /> was minted
    ///     for. <paramref name="expectedRoom" /> is the island the assignment named, reported as a
    ///     mismatch when the server disagrees.
    /// </summary>
    public async Task JoinAsync(string connStr, string expectedRoom, CancellationToken ct)
    {
        Volatile.Write(ref latestConnStr, connStr);

        if (!LiveKitToken.TryParse(connStr, out string url, out string token))
        {
            Report("cannot join: the conn string carries no url or no access_token");
            return;
        }

        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            // Assignments can arrive in a burst, and each join costs a full session setup. Only the
            // newest is worth connecting to, so an older one that waited here is abandoned.
            if (!ReferenceEquals(Volatile.Read(ref latestConnStr), connStr)) return;

            await DisconnectCurrentAsync();

            var joined = new Room();

            // One join attempt: a refusal is the finding this exists to surface, and retrying would
            // bury it under a delay. Not 0 — the SDK does not say whether that means no retries or no
            // attempts.
            await joined.ConnectAsync(url, token, new RoomOptions { AutoSubscribe = true, JoinRetries = 1 }, ct);

            room = joined;

            string mismatch = string.Equals(joined.Name, expectedRoom, StringComparison.Ordinal)
                ? string.Empty
                : $" MISMATCH: the server put us in '{joined.Name}', not '{expectedRoom}'";

            Report($"joined state={joined.ConnectionState} room={joined.Name} sid={joined.Sid} participants={joined.NumParticipants}{mismatch}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a fault.
        }
        catch (Exception e)
        {
            // The whole point of the flag: a token whose claims read clean can still be refused here.
            Report($"JOIN FAILED for room '{expectedRoom}': {e.Message}");
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();

        try { await DisconnectCurrentAsync(); }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    /// <summary>Caller must hold <see cref="gate" />.</summary>
    private async Task DisconnectCurrentAsync()
    {
        if (room is null) return;

        Room previous = room;
        room = null;

        try { await previous.DisconnectAsync(); }
        catch (Exception e) { Report($"could not leave room '{previous.Name}' cleanly: {e.Message}"); }
        finally { previous.Dispose(); }
    }

    private void Report(string message) =>
        Console.WriteLine($"[livekit] [{account}] {message}");
}
