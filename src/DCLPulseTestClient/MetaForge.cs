using System.Diagnostics;

namespace PulseTestClient;

public static class MetaForge
{
    /// <param name="throwOnNonZeroExit">
    ///     Leave set for commands whose output the caller parses. Clear it for idempotent
    ///     "ensure this exists" calls, where MetaForge reports the already-satisfied case as a
    ///     failure exit — <c>account create</c> returns 2 for an account that is already there,
    ///     which is the normal path on every re-run.
    /// </param>
    public static async Task<string> RunCommandAsync(string arguments, CancellationToken ct, bool throwOnNonZeroExit = true)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "metaforge",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment = {["NO_COLOR"] = "1"}
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new PulseException(
                "Could not launch 'metaforge'. Install it and make sure it is on PATH.", e);
        }

        // Both streams are read before waiting: a child that fills the stderr pipe while nothing
        // drains it blocks forever, and WaitForExitAsync would never return.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(ct);

        // Without this the caller sees an empty string and fails inside a JSON parse, which points
        // at the wrong thing entirely — an outdated metaforge missing a subcommand reads as
        // malformed output rather than "rebuild metaforge".
        if (throwOnNonZeroExit && process.ExitCode != 0)
        {
            string detail = stderr.Result.Trim();
            if (detail.Length == 0) detail = stdout.Result.Trim();

            throw new PulseException(
                $"'metaforge {arguments}' exited with {process.ExitCode}." +
                (detail.Length > 0 ? $" {detail}" : string.Empty));
        }

        // Spectre.Console wraps long lines at terminal width, breaking JSON string
        // values across multiple lines. Collapse to single-line JSON to fix parsing.
        return stdout.Result.Replace("\r", "").Replace("\n", "");
    }
}
