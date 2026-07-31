---
name: stop-test-client
description: Stop running DCLPulseTestClient bots gracefully. Use when the user wants to stop, kill, disconnect, or shut down the test client / bots.
user-invocable: true
allowed-tools: Bash
---

# Stop DCLPulseTestClient

Gracefully stop running test bots so they disconnect cleanly from the server.

## Steps

1. Find the process:
   ```
   tasklist | grep -i DCLPulseTestClient 2>/dev/null || ps aux | grep DCLPulseTestClient 2>/dev/null
   ```
   If no process is found, tell the user there are no running bots.

2. Create the stop signal file. The bot polls for this file every 500ms and triggers graceful shutdown (disconnects each peer, flushes ENet, then exits):
   ```
   touch "$TMPDIR/dcl-pulse-test-client.stop" 2>/dev/null || touch "$TEMP/dcl-pulse-test-client.stop" 2>/dev/null || touch /tmp/dcl-pulse-test-client.stop
   ```

3. Wait for the process to exit gracefully:
   ```
   sleep 3
   ```

4. Check if the process is still running. If it is, force-kill as a fallback:
   ```
   tasklist | grep -i DCLPulseTestClient && taskkill //F //IM DCLPulseTestClient.exe 2>/dev/null || echo "Bots stopped gracefully."
   ```
   On macOS/Linux fallback: `pkill -9 -f DCLPulseTestClient`

## Bridge processes

A process started with `--mode=bridge` (the stub gatekeeper) **does not watch the stop file** — the file watcher belongs to the bot lifecycle, which bridge mode skips entirely. It stops on Ctrl+C or a kill.

The harness normally runs two processes from the same binary, so both match the same name. If the user wants only the bots stopped, match on the command line rather than the image name:

- Windows: `Get-CimInstance Win32_Process -Filter "Name='DCLPulseTestClient.exe'" | Where-Object { $_.CommandLine -match '--mode=bridge' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`
- macOS/Linux: `pkill -f 'DCLPulseTestClient.*--mode=bridge'`

Invert the match (`-notmatch` / `pgrep -f` and filter) to target the bots instead. Step 2's stop file only ever reaches the bots, so it is already bridge-safe.
