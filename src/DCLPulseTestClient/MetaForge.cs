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
                FileName = "/bin/zsh",
                Arguments = $"-c \"source ~/.zshrc && metaforge {arguments}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                Environment = {["NO_COLOR"] = "1"}
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }
}