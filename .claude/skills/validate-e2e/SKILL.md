---
name: validate-e2e
description: Validate the full client↔server networking flow end-to-end — QUIC/UDP connect, DCL ECDSA handshake, movement, interest-managed state fan-out, and resync — by running two DCLPulseTestClient bots against a live server and cross-checking server logs, client logs, and per-transport Prometheus metrics. Covers ENet and WebTransport; WebTransport needs the extra cert/bind setup documented here. Use when asked to validate, verify, or smoke-test the end-to-end flow, especially over WebTransport.
user-invocable: true
allowed-tools: Bash, PowerShell, Grep, Read
argument-hint: [--transport=enet|webtransport] [--account=prefix]
---

# Validate the e2e networking flow

Run **two** bots that connect, authenticate, move, and observe each other, then confirm every leg of the protocol from three vantage points: the **server log**, the **client log**, and **Prometheus metrics**. Two bots (not one) are required — the interest-managed server→client fan-out only fires when a peer has someone else to observe.

## What this proves

| Leg | Channel | Where it shows up |
|---|---|---|
| QUIC/UDP connect (×2) | — | server `peer connected`; metric `active_peers{transport} == 2` |
| DCL ECDSA handshake (×2) | reliable | server `handshake accepted with wallet 0x…` |
| Client→server reliable | stream / ch0 | teleport + profile processed (server log) |
| Client→server unreliable (MovementInput) | datagram / ch1 | metric `packets_received{transport}` climbing |
| Server→client reliable | stream / ch0 | client `Player joined`, `Full state for subject` |
| Server→client unreliable (STATE_DELTA) | datagram / ch1 | client seq advancing; a `Seq gap` line proves deltas arrive |
| Resync round-trip | datagram→stream→stream | client `Seq gap … Requesting resync` → `Full state … seq=N` |
| Interest management (mutual) | — | server `Sending PlayerJoined for subject A to observer B` **both ways** |
| Per-transport metrics | — | every counter tagged `transport="enet"` / `"webtransport"` |

A `Seq gap → resync` line is **expected, not a failure**: unreliable datagrams drop/reorder, the client detects the gap and asks for a `STATE_FULL`. Seeing it means the datagram path *and* the reliable resync path both work (the "client drives resync" model — see CLAUDE.md).

## Prerequisites

- Build first: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
- `metaforge` on PATH (the client shells out to it for the auth chain): `metaforge --version`.
- Network to Catalyst (account create + profile fetch): `curl -sS -m8 https://peer.decentraland.org/about` should return HTTP 200.
- WebTransport only: the `web_transport` native ships in the RustWebTransport nupkg — restore/build copies it to `runtimes/<rid>/native/` under each project's `bin/`. If missing, connect fails at runtime (not build).

## Setup findings — READ before running

These each cost real debugging time. Bake them in:

1. **Disable the Console TUI.** In Development, `appsettings.Development.json` sets `Metrics:Type=Console`, which takes over the terminal (alternate screen) and **swallows all logs** — you'll see an empty log and think the server never started. Always pass `Metrics__Type=Prometheus` so startup + Debug logs go to stdout and `/metrics` is scrapeable.
2. **Development enables the self-signed dev cert (WebTransport).** Outside Development the server refuses to self-sign and exits by design. On self-sign it writes the cert SHA-256 (base64) to `Path.GetTempPath()/dcl-pulse-wt-cert-hash` (`%TEMP%\dcl-pulse-wt-cert-hash` on Windows); the WebTransport client reads that file to pin the cert — no manual copy.
3. **Bind is uniform on `0.0.0.0` by default** (`Transport:BindHost` and `WebTransport:BindHost`), so a `127.0.0.1` client reaches either transport on Windows and Linux. If `BindHost=::`: ENet is dual-stack everywhere, but **WebTransport's `::` is IPv6-only on Windows** — a `127.0.0.1` client then hangs at `Connect` with the server logging no peer. Keep `0.0.0.0`, or dial `--ip=::1`.
4. **Use `--bot-count=2`, never 1, for headless runs.** `bot-count=1` uses the manual-exit input reader, which calls `Console.KeyAvailable` and **throws when stdin is redirected** (any piped/headless run) right after `Ready.`. `bot-count≥2` uses the autonomous bot input and is headless-safe. With `BotsPerProcess=20` (client appsettings) two bots stay in a single process — no child-process orchestration.
5. **Small `--spawn-radius`** (e.g. `2`) keeps the two bots inside each other's AoI so the mutual `PlayerJoined` / `STATE_FULL` fan-out fires. The default `10` places 2 bots ~20 units apart — at/over the Tier-0 radius, so they may not observe each other.
6. **Debug logging** (`Logging__LogLevel__Default=Debug`) surfaces `peer connected` and `Sending PlayerJoined …` (both Debug level). At the default Warning/Info you won't see them — but `handshake accepted` is Info, so a lighter run still proves connect+auth.

## Run it — manual (two terminals)

Terminal 1 — server (WebTransport enabled, TUI off, verbose):
```
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" \
DOTNET_ENVIRONMENT=Development WebTransport__Enabled=true Metrics__Type=Prometheus Logging__LogLevel__Default=Debug \
  dotnet run --project src/DCLPulse -p:GenerateProto=false
```
Wait for `WebTransport host listening on 0.0.0.0:7443.` and `ENet host listening on 0.0.0.0:7777.`

Terminal 2 — two WebTransport bots:
```
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" \
  dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- \
  --transport=webtransport --ip=127.0.0.1 --port=7443 --bot-count=2 --spawn-radius=2 --account=wtval
```
For **ENet**: drop `--transport` and the cert concerns, use `--port=7777` (the default). Everything else — bots, logs, metrics — is identical.

Let it run ~15–30s, check the three vantage points below, then Ctrl+C both.

## Run it — automated / headless (single script)

For agent/CI runs with no interactive terminal. Build first, then run from the repo root. (Notes reflect Claude-Code Bash-tool quirks: no foreground `sleep`; launch each process with `exec` inside a subshell so `$!` is the real `dotnet` PID and stays killable — a `timeout`-wrapped `dotnet` orphans its child.)

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
ROOT="$(pwd)"; LOG="$(mktemp -d)"
SDIR="$ROOT/src/DCLPulse/bin/Debug/net10.0"; CDIR="$ROOT/src/DCLPulseTestClient/bin/Debug/net10.0"
wait_s(){ timeout "$1" tail -f /dev/null >/dev/null 2>&1 || true; }   # foreground-sleep substitute

# server: cd into bin so appsettings.json + the native load; exec so $! is dotnet
( cd "$SDIR" && exec env DOTNET_ENVIRONMENT=Development WebTransport__Enabled=true \
    Metrics__Type=Prometheus Logging__LogLevel__Default=Debug dotnet DCLPulse.dll > "$LOG/server.log" 2>&1 ) & SERVER=$!
trap 'kill $SERVER $CLIENT 2>/dev/null' EXIT
for i in $(seq 1 30); do grep -qa "WebTransport host listening" "$LOG/server.log" && break; wait_s 1; done

( cd "$CDIR" && exec env DOTNET_ENVIRONMENT=Development dotnet DCLPulseTestClient.dll \
    --transport=webtransport --ip=127.0.0.1 --port=7443 --bot-count=2 --spawn-radius=2 --account=wtval \
    < /dev/null > "$LOG/client.log" 2>&1 ) & CLIENT=$!
for i in $(seq 1 50); do grep -qa "Starting simulation" "$LOG/client.log" && break; wait_s 1; done
wait_s 8                                                    # let traffic flow while both are connected
curl -s -m5 http://localhost:5000/metrics -o "$LOG/metrics.txt"
kill $CLIENT; wait_s 2; kill $SERVER

echo "== server =="; grep -aiE "peer connected|handshake accepted|Sending PlayerJoined" "$LOG/server.log"
echo "== client =="; grep -aiE "Player joined|Full state|Seq gap|Resync" "$LOG/client.log"
echo "== metrics =="; grep -aE 'transport="webtransport"' "$LOG/metrics.txt" | grep -aiE "active_peers|packets_|bytes_"
```
Swap the two `--transport`/`--port` values (`enet` / `7777`) to validate ENet. `curl /metrics` needs no auth when no `MetricsBearerToken` is configured (the dev default).

## Success criteria

All of:
- [ ] server: two `peer connected` and two `handshake accepted with wallet 0x…`
- [ ] server: `Sending PlayerJoined for subject A to observer B` in **both** directions
- [ ] client: each bot logs `Player joined:` (the other bot) and `Full state for subject …`
- [ ] metrics: `active_peers{transport=…} 2`, and both `packets_received` and `packets_sent` non-zero for that transport
- [ ] no unexpected exceptions (a `Seq gap → Requesting resync` line is normal — see above)

## Cleanup

A `timeout`-killed wrapper can orphan the `dotnet` child, and the server holds ports 7777/7443. Kill leftovers:
- Windows: `Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -match 'DCLPulse(TestClient)?\.dll' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`
- Linux/macOS: `pkill -f 'DCLPulse(TestClient)?\.dll'`

## Troubleshooting

- **Client hangs at `Connecting to …`, server logs no peer** → WebTransport bound `[::]` on Windows (IPv6-only) with a `127.0.0.1` client. Use `WebTransport__BindHost=0.0.0.0` (the default) or `--ip=::1`.
- **Empty server log / never see `listening`** → the Console TUI is on. Add `Metrics__Type=Prometheus`.
- **`Console.KeyAvailable` throw right after `Ready.`** → `bot-count=1` under redirected stdin. Use `--bot-count=2`.
- **`web-transport: failed to connect`** → server not up yet, wrong port, or a stale cert-hash file. Ensure the server logged `listening` first; delete `%TEMP%\dcl-pulse-wt-cert-hash` and let the server rewrite it on the next start.
- **Server refuses to start / throws on cert** → not in Development. Set `DOTNET_ENVIRONMENT=Development` (self-sign is Development-only by design).
- **`metrics curl` returns 401 / empty** → a `MetricsBearerToken` is configured; send `Authorization: Bearer <token>`, or just rely on the logs.
- **Bots never reach `Ready.`** → metaforge/Catalyst issue. Verify `metaforge --version` and Catalyst reachability; `metaforge account remove wtval-0 wtval-1` then retry to force fresh accounts.
- **Only one bot observed / no `PlayerJoined`** → bots out of AoI. Lower `--spawn-radius` (e.g. `2`).
