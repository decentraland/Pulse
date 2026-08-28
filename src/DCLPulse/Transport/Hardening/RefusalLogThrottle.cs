namespace Pulse.Transport.Hardening;

/// <summary>
///     Emission gate for a log record whose rate an attacker controls. Formatting a structured
///     record is the most expensive thing left on the connect path, and a refusal happens once per
///     connect attempt: one host at 10 000 connects/s against a cap of 10 would emit 10 000
///     records/s. Dropping the record instead is no better — <c>ip_limit_refused</c> carries the
///     volume but no address, so the log is the only thing naming <em>which</em> IP is refused.
///     This keeps it at Warning and bounds its rate to one per <paramref name="intervalMs" />,
///     handing back the count skipped since the last so the suppressed scale is still reported. A
///     suppressed occurrence costs one clock read, one comparison and one increment.
///     <para />
///     Not thread-safe by design: each transport service owns an instance and calls it only from
///     its own transport thread, so the two transports emit at most one record each per interval —
///     the intent, since the two refusals are independent events.
/// </summary>
public sealed class RefusalLogThrottle(long intervalMs)
{
    private long nextEmitTick;
    private long suppressed;

    /// <summary>
    ///     Whether a record should be emitted for the occurrence being reported now.
    ///     <paramref name="suppressedSinceLast" /> counts occurrences gated out since the previous
    ///     emission, and is meaningful only when this returns <c>true</c>.
    /// </summary>
    public bool ShouldEmit(out long suppressedSinceLast)
    {
        long now = Environment.TickCount64;

        if (now < nextEmitTick)
        {
            suppressed++;
            suppressedSinceLast = 0;
            return false;
        }

        nextEmitTick = now + intervalMs;
        suppressedSinceLast = suppressed;
        suppressed = 0;
        return true;
    }
}
