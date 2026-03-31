---
name: run-test-client
description: Launch DCLPulseTestClient bot(s) against a Pulse server. Use when the user wants to run, start, or launch the test client / bot / load test.
user-invocable: true
allowed-tools: Bash
argument-hint: [--account=name] [--bot-count=N] [--ip=address] [--port=port] [--pos-x=X] [--pos-y=Y] [--pos-z=Z] [--rotate-speed=deg]
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
