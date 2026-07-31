---
name: run-test-client
description: Launch DCLPulseTestClient bot(s) against a Pulse server. Use when the user wants to run, start, or launch the test client / bot / load test.
user-invocable: true
allowed-tools: Bash
argument-hint: [--account=name] [--bot-count=N] [--ip=address] [--port=port] [--pos-x=X] [--pos-y=Y] [--pos-z=Z] [--rotate-speed=deg] [--comms-enabled] [--mode=bridge]
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

Off by default — omit all of these and the run behaves exactly as before.

| Argument | Default | Description |
|---|---|---|
| `--comms-enabled` | off | Each bot also opens a ws-connector session on its own wallet and records the LiveKit conn strings it receives |
| `--comms-url=<url>` | `ws://127.0.0.1:5000/ws` | ws-connector endpoint. Also accepts a realm's raw adapter string (`archipelago:archipelago:wss://host/ws`); anything that isn't `ws://`/`wss://` after refinement is rejected loudly |
| `--mode=<bots\|bridge>` | `bots` | `bridge` runs the stub gatekeeper **alone** — no bots, no accounts, no Pulse connection |
| `--bridge-mode=<synthetic\|livekit\|off>` | `synthetic` | `synthetic` needs no credentials; `livekit` mints a real token from `LIVEKIT_HOST`/`LIVEKIT_API_KEY`/`LIVEKIT_API_SECRET`; `off` expects a real comms-gatekeeper on the broker |
| `--nats-url=<url>` | `nats://127.0.0.1:4222` | Broker the bridge subscribes to |
| `--expect-conn-string-within=<seconds>` | `15` | **Parsed but not yet acted on.** Reserved for the regression scenarios (three `DwellPasses` at 1 Hz plus slack); passing it today changes nothing |

**Argument parsing is `--name=value` only.** `ClientOptions.FromArgs` matches on the `--name=` prefix, so a space-separated `--bridge-mode livekit` sets nothing and silently falls back to the default. `--comms-enabled` is the sole exception — bare or `=true` both work. Always emit the `=` form.

## Running the bridge

The stub gatekeeper is the same binary, and it is a separate process from the bots — run both:

```
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --mode=bridge --nats-url=nats://127.0.0.1:4222
```

On start it prints the subject it subscribed to, the broker, and the mode. Those three lines are the diagnostic — silent no-delivery is this system's characteristic failure, so check them before anything else. See `docs/e2e-livekit.md` for the full harness.

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

- **"ENet library failed to initialize"** — the ENet native library is missing. On macOS: `brew install enet`. On Windows: ensure `enet.dll` is in the output directory.
- **Handshake failed** — the server rejected the auth chain. Check that the server is running and the account's ephemeral key hasn't expired (25h lifetime). Try `metaforge account remove <name>` then re-run to create a fresh account.
- **Connection timeout** — verify the server IP/port and that UDP traffic is not blocked by a firewall.
- **`--comms-enabled` set but no `[ws-connector]` lines** — ws-connector isn't up at `--comms-url`, or the flag was passed space-separated. A comms failure is deliberately non-fatal to the Pulse session and reports on the `[comms]` prefix, so the run otherwise looks healthy.
- **Bot connects to ws-connector but no island ever arrives** — nothing between Pulse and ws-connector is minting conn strings. Either start the bridge (`--mode=bridge`) or a real comms-gatekeeper. `docs/e2e-livekit.md` covers the rest of the silent-no-delivery causes.
- **Second bot on the same account kicks the first** — ws-connector allows one session per wallet and kicks the previous with `KR_NEW_SESSION`. Two bots need two accounts, which `--bot-count` > 1 already gives.
