using System.Diagnostics;

namespace PulseTestClient;

public static class ProcessOrchestrator
{
    public static async Task<int> RunAsync(ClientOptions options, int botsPerProcess, CancellationToken ct)
    {
        int totalBots = options.BotCount;
        int processCount = (totalBots + botsPerProcess - 1) / botsPerProcess;

        Console.WriteLine($"Spawning {totalBots} bots across {processCount} processes ({botsPerProcess} per process)..");

        // Account creation must be sequential across all bots
        for (var i = 0; i < totalBots; i++)
        {
            var accountName = $"{options.AccountPrefix}-{i}";
            Console.WriteLine($"[{accountName}] Ensuring account exists..");
            await MetaForge.RunCommandAsync($"account create {accountName} --skip-update-check --skip-auto-login", ct);
        }

        var processes = new List<Process>();

        for (var p = 0; p < processCount; p++)
        {
            int offset = p * botsPerProcess;
            int count = Math.Min(botsPerProcess, totalBots - offset);

            string childArgs = BuildChildArgs(options, offset, count, totalBots);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = childArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Console.WriteLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Console.Error.WriteLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processes.Add(process);

            Console.WriteLine($"[orchestrator] Process {p} started (PID {process.Id}): bots {offset}..{offset + count - 1}");
        }

        Console.WriteLine($"[orchestrator] All {processCount} processes running. Press q+Enter or Ctrl+C to stop.");

        // Watch for quit
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = Console.ReadLine();

                if (line is "q" or "Q" or "quit")
                {
                    Console.WriteLine("[orchestrator] Quit requested, stopping child processes..");
                    SignalStop();
                }
            }
        });

        // When parent is cancelled, signal children
        ct.Register(SignalStop);

        // Wait for all children
        Task[] tasks = processes.Select(p => p.WaitForExitAsync(CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        // Clean up the stop file after all children have exited
        CleanupStopFile();

        int failed = processes.Count(p => p.ExitCode != 0);

        if (failed > 0)
            Console.WriteLine($"[orchestrator] {failed}/{processCount} processes exited with errors.");
        else
            Console.WriteLine($"[orchestrator] All {processCount} processes exited cleanly.");

        return failed > 0 ? 1 : 0;
    }

    private static string BuildChildArgs(ClientOptions options, int offset, int count, int totalBots)
    {
        var parts = new List<string>
        {
            $"--account={options.AccountPrefix}",
            $"--bot-count={count}",
            $"--bot-offset={offset}",
            $"--total-bot-count={totalBots}",
            $"--ip={options.ServerIp}",
            $"--port={options.ServerPort}",
            $"--pos-x={options.PositionX}",
            $"--pos-y={options.PositionY}",
            $"--pos-z={options.PositionZ}",
            $"--spawn-radius={options.SpawnRadius}",
            $"--dispersion-radius={options.DispersionRadius}",
            $"--rotate-speed={options.RotateSpeed}",
        };

        return string.Join(' ', parts);
    }

    private static void SignalStop()
    {
        string stopFile = Path.Combine(Path.GetTempPath(), "dcl-pulse-test-client.stop");

        try { File.WriteAllText(stopFile, ""); }
        catch
        { /* best effort */
        }
    }

    private static void CleanupStopFile()
    {
        string stopFile = Path.Combine(Path.GetTempPath(), "dcl-pulse-test-client.stop");

        try { File.Delete(stopFile); }
        catch
        { /* best effort */
        }
    }
}
