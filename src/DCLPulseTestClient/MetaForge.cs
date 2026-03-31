using System.Diagnostics;

namespace PulseTestClient;

public static class MetaForge
{
    public static async Task<string> RunCommandAsync(string arguments, CancellationToken ct)
    {
        string fileName;
        string processArguments;

        if (OperatingSystem.IsWindows())
        {
            string metaforgePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Decentraland", "MetaForge", "bin", "metaforge.exe");

            fileName = metaforgePath;
            processArguments = arguments;
        }
        else
        {
            fileName = "/bin/zsh";
            processArguments = $"-c \"source ~/.zshrc && metaforge {arguments}\"";
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = processArguments,
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