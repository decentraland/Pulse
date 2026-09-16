---
name: run-test-client
description: Launch DCLPulseTestClient bot(s) against a Pulse server. Use when the user wants to run, start, or launch the test client / bot / load test.
user-invocable: true
allowed-tools: Bash
argument-hint: [--account=name] [--bot-count=N] [--ip=address] [--port=port] [--pos-x=X] [--pos-y=Y] [--pos-z=Z] [--rotate-speed=deg] [--comms-enabled]
---

# Launch DCLPulseTestClient

Run one or more headless test bots that connect to a Pulse game server.

## Prerequisites check

Before launching, verify:
1. MetaForge CLI is available: run `metaforge --version`. If it fails, tell the user to install MetaForge and add it to PATH.
2. A Pulse game server must be running at the target address. Remind the user if they haven't started one.

## Launch command

```
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- $ARGUMENTS
```

If no arguments are provided, use the defaults (account `enetclient-test`, 1 bot, server `127.0.0.1:7777`, spawn at Genesis Plaza `-104, 0, 5`).

## Available arguments

| Argument | Default | Description |
|---|---|---|
| `--account=<name>` | `enetclient-test` | MetaForge account name (or prefix when `--bot-count` > 1) |
| `--bot-count=<N>` | `1` | Number of bots to spawn in the same process |
| `--ip=<address>` | `127.0.0.1` | Server IP |
| `--port=<port>` | `7777` | Server UDP port |
| `--pos-x=<float>` | `-104` | Spawn X |
| `--pos-y=<float>` | `0` | Spawn Y |
| `--pos-z=<float>` | `5` | Spawn Z |
| `--spawn-radius=<float>` | `10` | Radius of the circle bots spawn on |
| `--dispersion-radius=<float>` | `20` | Max wander distance from spawn origin |
| `--rotate-speed=<deg/s>` | `90` | Idle rotation speed |

### Conn-string harness arguments

Off by default — omit these and the run behaves exactly as before.

| Argument | Default | Description |
|---|---|---|
| `--comms-enabled` | off | Each bot also opens a ws-connector session on its own wallet and records the LiveKit conn strings it receives |
| `--comms-url=<url>` | `ws://127.0.0.1:5000/ws` | ws-connector endpoint. Also accepts a realm's raw adapter string (`archipelago:archipelago:wss://host/ws`); anything that isn't `ws://`/`wss://` after refinement is rejected loudly |
| `--expect-conn-string-within=<seconds>` | `15` | **Parsed but not yet acted on.** Reserved for the regression scenarios; passing it today changes nothing |

**Argument parsing is `--name=value` only.** `ClientOptions.FromArgs` matches on the `--name=` prefix, so a space-separated `--comms-url ws://…` sets nothing and silently leaves the default in place. `--comms-enabled` is the sole exception — bare or `=true` both work.

## The client does not mint conn strings

The test client is a client: Pulse over ENet/WebTransport, ws-connector over WebSocket, and no broker connection at all. It has **no `--nats-url` and no bridge mode** — an earlier revision had a stub gatekeeper behind `--mode=bridge` and it was removed on purpose.

So `--comms-enabled` on its own produces a bot that connects to ws-connector and then receives nothing, which looks exactly like a healthy idle run. Something has to translate `peer.{addr}.cluster_change` into `engine.peer.{addr}.island_changed`, and that something is **comms-gatekeeper**, run separately against the same broker with `CLUSTER_SUBSCRIBER_ENABLED=true`. It needs Postgres and a LiveKit host/key/secret. See `docs/e2e-livekit.md` section 4.

## Multi-bot mode

When `--bot-count` > 1:
- Accounts are named `<account>-0`, `<account>-1`, ..., `<account>-{N-1}` (each auto-created via MetaForge)
- Bots spawn in a circle around the initial position
- Keyboard input is disabled; use Ctrl+C to stop all bots
- All bots share one ENet Host (one UDP socket, one service thread)

When `--bot-count=1` (default): account name is used as-is, ESC key quits.

## Execution

Run the command in the foreground so the user can see the bot console output (peer joins/leaves, emote events, resync requests). Each log line is prefixed with `[accountName]`.

If the user passes custom arguments via `$ARGUMENTS`, forward them as-is after `--`. If they describe what they want in natural language (e.g. "run 5 bots on 10.0.0.5"), translate to the appropriate CLI flags.

## Spawn location shortcuts

When the user mentions a location by name, translate to position flags:

| Location | Flags |
|---|---|
| "genesis plaza" (default) | `--pos-x=-104 --pos-y=0 --pos-z=5` |
| "world" or "realm" | `--pos-x=0 --pos-y=0 --pos-z=0` |

## Troubleshooting

- **"ENet library failed to initialize"** — the native library did not load. It is **committed**
  under `src/DCLPulseTestClient/runtimes/` (`win-x64/enet.dll`, `linux-x64/libenet.so`,
  `macos-arm64/libenet.dylib`) and the csproj copies all three to the output root, so nothing needs
  installing on Windows, Linux x64 or Apple Silicon — suspect a stale `bin/` and rebuild. The one
  genuine gap is **Intel macOS**: only an arm64 dylib ships, so there `brew install enet` is the
  workaround.
- **Handshake failed** — the server rejected the auth chain. Check that the server is running and the account's ephemeral key hasn't expired (25h lifetime). Try `metaforge account remove <name>` then re-run to create a fresh account.
- **Connection timeout** — verify the server IP/port and that UDP traffic is not blocked by a firewall.
- **`--comms-enabled` set but no `[ws-connector]` lines** — ws-connector isn't up at `--comms-url`, or the flag was passed space-separated. A comms failure is deliberately non-fatal to the Pulse session and reports on the `[comms]` prefix, so the run otherwise looks healthy.
- **Bot connects to ws-connector but no island ever arrives** — nothing is minting conn strings. Start comms-gatekeeper against the same broker. `docs/e2e-livekit.md` covers the rest of the silent-no-delivery causes.
- **Second bot on the same account kicks the first** — ws-connector allows one session per wallet and kicks the previous with `KR_NEW_SESSION`. Two bots need two accounts, which `--bot-count` > 1 already gives.
