using System.Diagnostics;

namespace PulseTestClient;

public static class MetaForge
{
    public static async Task<string> RunCommandAsync(string arguments, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "metaforge",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                Environment = {["NO_COLOR"] = "1"}
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        // Spectre.Console wraps long lines at terminal width, breaking JSON string
        // values across multiple lines. Collapse to single-line JSON to fix parsing.
        return output.Replace("\r", "").Replace("\n", "");
    }
}