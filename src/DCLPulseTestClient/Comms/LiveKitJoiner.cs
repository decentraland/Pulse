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
/// <param name="rejoinAfterMs">
///     Debug-only, off (0) by default. When positive, a room disconnect schedules one further
///     self-initiated join attempt after this many milliseconds, using the SAME url/token this joiner
///     last connected with (not whatever was most recently assigned) — mirroring the Unity island
///     room's backoff reconnect against a conn string that may have since been revoked. See
///     <see cref="ClientOptions.RejoinAfterMs" />.
/// </param>
public sealed class LiveKitJoiner(string account, int rejoinAfterMs = 0) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new (1, 1);

    private Room? room;
    private string? latestConnStr;

    // The islandId that came with latestConnStr, kept only so a cancellation log line can name the
    // assignment that superseded the probe (latestConnStr itself is the value actually compared).
    private string? latestIslandId;

    // The url/token this joiner last actually connected with — deliberately NOT updated by the
    // scheduled rejoin itself, so a second disconnect schedules another attempt with the same
    // original pair rather than compounding onto whatever the rejoin used.
    private string? lastUrl;
    private string? lastToken;

    // The connStr behind lastUrl/lastToken — the identity of "the assignment this room was joined
    // under", compared against latestConnStr to tell whether a probe would still be reconnecting to
    // the bot's current assignment or to one it has since moved on from.
    private string? lastConnStr;

    // At most one self-initiated rejoin per joiner: this is a debug probe of a single revocation,
    // not a reconnect loop, so a rejoin that succeeds (or fails) is not itself retried.
    private bool rejoinScheduled;

    // Set only while a rejoin probe (see RejoinAfterDelayAsync) is waiting out its delay. A newer
    // island assignment arriving in that window means the takeover has already told this identity
    // where to go next — the Unity island room follows the latest assignment rather than a stale
    // reconnect, so the probe is cancelled rather than left to fire against a room this bot is
    // about to leave anyway.
    private CancellationTokenSource? rejoinProbeCts;

    /// <summary>
    ///     Leaves the current room, if any, and joins the one <paramref name="connStr" /> was minted
    ///     for. <paramref name="expectedRoom" /> is the island the assignment named, reported as a
    ///     mismatch when the server disagrees.
    /// </summary>
    public async Task JoinAsync(string connStr, string expectedRoom, CancellationToken ct)
    {
        Volatile.Write(ref latestConnStr, connStr);
        Volatile.Write(ref latestIslandId, expectedRoom);

        // A newer island assignment just arrived — any pending self-rejoin probe against the OLD
        // room is now moot; this identity is about to follow the new assignment instead, exactly
        // like the Unity island room does.
        CancellationTokenSource? pendingProbe = Interlocked.Exchange(ref rejoinProbeCts, null);

        if (pendingProbe is not null)
        {
            pendingProbe.Cancel();
            pendingProbe.Dispose();
            Report($"re-join probe cancelled: newer assignment '{expectedRoom}' received");
        }

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

            // Captured before the disconnect below clears/replaces `room` — this is what makes a
            // takeover's parking assignment (a different island than the one this bot was just
            // removed from) show up as an explicit SWITCHED line rather than just another "joined".
            string? previousRoomName = room?.Name;

            await DisconnectCurrentAsync();

            var joined = new Room();

            // Wired before ConnectAsync so a disconnect/reconnect racing the connect itself is never
            // missed. Purely observational — a takeover scenario needs to see whether the evicted side
            // comes back on its own, not just that it left.
            joined.Disconnected += (_, reason) =>
            {
                Report($"LEFT room '{joined.Name}': disconnected, reason={reason}");
                ScheduleRejoinIfEnabled(ct);
            };
            joined.Reconnecting += (_, _) => Report($"reconnecting to room '{joined.Name}'..");
            joined.Reconnected += (_, _) => Report($"RE-JOINED room '{joined.Name}' sid={joined.Sid}");

            // One join attempt: a refusal is the finding this exists to surface, and retrying would
            // bury it under a delay. Not 0 — the SDK does not say whether that means no retries or no
            // attempts.
            await joined.ConnectAsync(url, token, new RoomOptions { AutoSubscribe = true, JoinRetries = 1 }, ct);

            room = joined;
            lastUrl = url;
            lastToken = token;
            lastConnStr = connStr;

            // Unknown ("") only from the ghost debug path (--join-conn-str), which has no assignment to
            // compare against — nothing to mismatch, so nothing to report.
            string mismatch = string.IsNullOrEmpty(expectedRoom) || string.Equals(joined.Name, expectedRoom, StringComparison.Ordinal)
                ? string.Empty
                : $" MISMATCH: the server put us in '{joined.Name}', not '{expectedRoom}'";

            Report($"joined state={joined.ConnectionState} room={joined.Name} sid={joined.Sid} participants={joined.NumParticipants}{mismatch}");

            // A held room replaced by a DIFFERENT one, in one join call — the takeover-parking case
            // (and, generally, any island reassignment while already connected somewhere). Distinct
            // from the "joined" line above so a scan for room transitions does not have to diff two
            // "joined" lines against each other.
            if (previousRoomName is not null && !string.Equals(previousRoomName, joined.Name, StringComparison.Ordinal))
                Report($"SWITCHED room '{previousRoomName}' -> '{joined.Name}'");
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

    /// <summary>
    ///     Fires (at most once per joiner) from the <c>Disconnected</c> event. Debug-only — see
    ///     <see cref="ClientOptions.RejoinAfterMs" />; a no-op unless the joiner was constructed with a
    ///     positive <c>rejoinAfterMs</c> and a previous join actually recorded a url/token to retry.
    /// </summary>
    private void ScheduleRejoinIfEnabled(CancellationToken ct)
    {
        if (rejoinAfterMs <= 0 || lastUrl is null || lastToken is null || lastConnStr is null || rejoinScheduled) return;

        rejoinScheduled = true;

        // This disconnect can itself be the side effect of a NEWER assignment's own JoinAsync already
        // running (it writes latestConnStr/latestIslandId before it calls DisconnectCurrentAsync,
        // which is what raises the Disconnected event that reaches here) — so JoinAsync's own
        // cancel-pending-probe check already ran and found nothing armed yet to cancel. Re-check here,
        // against the room this probe would actually reconnect to, before arming at all: if a
        // different assignment is already the latest one, this probe is moot and must never start.
        string armingConnStr = lastConnStr;

        if (!string.Equals(Volatile.Read(ref latestConnStr), armingConnStr, StringComparison.Ordinal))
        {
            Report($"re-join probe cancelled: newer assignment '{Volatile.Read(ref latestIslandId)}' received");
            return;
        }

        var probeCts = new CancellationTokenSource();
        rejoinProbeCts = probeCts;
        _ = RejoinAfterDelayAsync(lastUrl, lastToken, armingConnStr, ct, probeCts);
    }

    /// <summary>
    ///     Waits <see cref="rejoinAfterMs" />, then makes exactly one further join attempt with the SAME
    ///     url/token the joiner was last connected with (deliberately not re-parsed from a fresh
    ///     assignment) — the point being to find out whether that specific, possibly-since-revoked
    ///     token is still honoured, mirroring the Unity island room's backoff reconnect. Never throws;
    ///     both outcomes (accepted or rejected) are the finding, and either is only ever a log line.
    ///     Abandoned without attempting anything if <paramref name="probeCts" /> is cancelled first —
    ///     see the cancellation in <see cref="JoinAsync" />.
    /// </summary>
    private async Task RejoinAfterDelayAsync(string url, string token, string originalConnStr, CancellationToken ct, CancellationTokenSource probeCts)
    {
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);
            try { await Task.Delay(rejoinAfterMs, linked.Token); }
            catch (OperationCanceledException) { return; }
        }
        finally
        {
            // Only clears the field if it still points at THIS probe — a JoinAsync race that already
            // swapped it for null (or, in principle, a later probe) is left alone.
            Interlocked.CompareExchange(ref rejoinProbeCts, null, probeCts);
            probeCts.Dispose();
        }

        // The delay elapsed without the CTS-based cancellation above ever firing — that only catches
        // an assignment that arrives WHILE this probe is armed. Re-check directly against the latest
        // assignment before touching any room: this is what makes the arm-time check in
        // ScheduleRejoinIfEnabled (and this one) sufficient even for the race where the newer
        // assignment became latest before the probe was ever armed. Never disconnect/rejoin a room the
        // bot currently holds under a newer assignment.
        if (!string.Equals(Volatile.Read(ref latestConnStr), originalConnStr, StringComparison.Ordinal))
        {
            Report($"re-join probe cancelled: newer assignment '{Volatile.Read(ref latestIslandId)}' received");
            return;
        }

        Report($"attempting a self-initiated re-join after {rejoinAfterMs} ms, using its ORIGINAL connection string..");

        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            // A newer assignment may have arrived while this probe was waiting for the room lock.
            // Re-check after acquiring it so the stale probe cannot disconnect the current room and
            // replace the newer assignment with its original connection string.
            if (!string.Equals(Volatile.Read(ref latestConnStr), originalConnStr, StringComparison.Ordinal))
            {
                Report($"re-join probe cancelled: newer assignment '{Volatile.Read(ref latestIslandId)}' received");
                return;
            }
            // The stale Room from the disconnect this rejoin is reacting to is still sitting in `room`
            // (a Disconnected event does not clear it) — dispose it before trying a fresh one.
            await DisconnectCurrentAsync();

            var rejoined = new Room();
            rejoined.Disconnected += (_, reason) => Report($"LEFT room '{rejoined.Name}' again: disconnected, reason={reason}");

            await rejoined.ConnectAsync(url, token, new RoomOptions { AutoSubscribe = true, JoinRetries = 1 }, ct);

            room = rejoined;

            // Accepted is the notable outcome here: the whole point of this probe is that the original
            // token is expected to have been revoked by the takeover's eviction stamp.
            Report($"RE-JOIN ACCEPTED using its original connection string: state={rejoined.ConnectionState} room={rejoined.Name} sid={rejoined.Sid} — the token was NOT revoked (or the revocation was not honoured)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a fault.
        }
        catch (Exception e)
        {
            Report($"RE-JOIN REJECTED using its original connection string: {e.Message}");
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
