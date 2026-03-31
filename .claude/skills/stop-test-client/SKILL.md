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
