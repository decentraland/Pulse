namespace Pulse.Metrics.Console;

/// <summary>
///     Log level prefixes and colors matching Microsoft's default SimpleConsoleFormatter.
/// </summary>
public static class LogLevelStyle
{
    public static string GetPrefix(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "????",
        };

    /// <summary>
    ///     Markup color tag for <see cref="XenoAtom.Terminal.UI.Controls.LogControl.AppendMarkupLine" />.
    ///     Uses XenoAtom.Ansi markup syntax: basic-16 color names.
    /// </summary>
    public static string GetMarkupColor(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => "gray",
            LogLevel.Debug => "gray",
            LogLevel.Information => "green",
            LogLevel.Warning => "yellow",
            LogLevel.Error => "black on darkred",
            LogLevel.Critical => "white on darkred",
            _ => "gray",
        };

    /// <summary>
    ///     ANSI escape sequence foreground (and optional background) for console output.
    /// </summary>
    public static (string open, string close) GetAnsiEscape(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => ("\x1b[90m", "\x1b[0m"), // gray
            LogLevel.Debug => ("\x1b[90m", "\x1b[0m"), // gray
            LogLevel.Information => ("\x1b[32m", "\x1b[0m"), // green
            LogLevel.Warning => ("\x1b[33m", "\x1b[0m"), // yellow
            LogLevel.Error => ("\x1b[30;41m", "\x1b[0m"), // black on dark red
            LogLevel.Critical => ("\x1b[37;41m", "\x1b[0m"), // white on dark red
            _ => ("", ""),
        };
}
