using System.Collections.Concurrent;
using XenoAtom.Terminal.UI.Controls;

namespace Pulse.Dashboard;

/// <summary>
///     <see cref="ILoggerProvider" /> that buffers log entries and drains them on the UI thread.
///     <see cref="Log" /> is called from arbitrary threads (ENet, workers, etc.) and enqueues
///     into a <see cref="ConcurrentQueue{T}" />. <see cref="ConsoleDashboard" /> calls
///     <see cref="DrainTo" /> from the onUpdate callback (UI thread) to flush into
///     <see cref="LogControl" />.
///     Prefixes and colors match the default Microsoft SimpleConsoleFormatter.
/// </summary>
public sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> pendingEntries = new ();

    public ILogger CreateLogger(string categoryName) =>
        new DashboardLogger(this, categoryName);

    /// <summary>
    ///     Flush all buffered entries into <paramref name="logControl" />.
    ///     Must be called on the UI thread.
    /// </summary>
    public void DrainTo(LogControl logControl)
    {
        while (pendingEntries.TryDequeue(out LogEntry entry))
        {
            logControl.AppendMarkupLine($"[{entry.Color}]{entry.Prefix}[/]: {entry.Category}");
            logControl.AppendLine($"      {entry.Message}");

            if (entry.Exception is not null)
                logControl.AppendLine(entry.Exception);
        }
    }

    public void Dispose() { }

    private readonly record struct LogEntry(
        string Prefix,
        string Color,
        string Category,
        string Message,
        string? Exception);

    private sealed class DashboardLogger(DashboardLoggerProvider provider, string categoryName) : ILogger
    {
        private readonly string shortCategory = GetShortCategory(categoryName);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None;

        public IDisposable? BeginScope<TState>(TState state) where TState: notnull =>
            null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            provider.pendingEntries.Enqueue(new LogEntry(
                LogLevelStyle.GetPrefix(logLevel),
                LogLevelStyle.GetMarkupColor(logLevel),
                shortCategory,
                formatter(state, exception),
                exception?.ToString()));
        }

        private static string GetShortCategory(string category)
        {
            int lastDot = category.LastIndexOf('.');
            return lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }
    }
}
