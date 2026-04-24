# Metrics & Dashboard

Pulse includes a built-in analytics layer with a real-time terminal dashboard for development. Metrics are collected via `System.Diagnostics.Metrics` and shared atomic counters, aggregated by `MetricsCollector` every 500ms, and displayed via XenoAtom.Terminal.UI.

## Enabling the dashboard

Set `Dashboard:Enabled` to `true` in `appsettings.json` (or `appsettings.Development.json`):

```json
{
  "Dashboard": {
    "Enabled": true
  }
}
```

When enabled, the dashboard takes over the terminal (fullscreen mode). Logs are captured by a custom `ILoggerProvider` and displayed in an embedded panel at the bottom. When disabled, ZLogger writes to stdout as usual and a no-op `NullDashboard` is used.

## Architecture

```
Hot path threads            MetricsCollector (500ms timer)        ConsoleDashboard (UI thread)
─────────────────           ──────────────────────────────        ────────────────────────────
ENet thread                 MeterListener callbacks               onUpdate callback (~66 Hz)
  PulseMetrics.Add() ──────► Interlocked accumulation             reads MetricsSnapshot
  queue depth counters        RateTracker → rate + P95/P99        updates State<T> + sparklines
Worker threads                PercentileBuffer → gauge stats      renders via XenoAtom.Terminal.UI
  EnumCounters.Increment()    builds MetricsSnapshot
  PulseMetrics.Add()          pushes to IDashboard.Update()
```

Each metric shows: current value, P95, P99, and a sparkline of the last 60 seconds (120 samples at 500ms).

---

## Transport metrics

### Active Peers

Current number of connected ENet peers.

| Signal | Meaning |
|---|---|
| Stable | Normal operation |
| Sudden drop | Mass disconnect — check network, server error logs, or timeout configuration |
| Gradual decline | Peers timing out — may indicate server stalling (long ticks) |

### Total Connected / Total Disconnected

Cumulative counters since server start. The difference equals current active peers. Useful for detecting churn — if both grow fast but active peers stays flat, peers are connecting and disconnecting rapidly (auth failures, network instability, ghost connections).

### Bytes In/s, Bytes Out/s

Throughput in bytes per second, with human-readable formatting (B/s, KB/s, MB/s).

| Signal | Meaning |
|---|---|
| Bytes Out >> Bytes In | Normal — server fans out state to N observers per subject |
| Bytes In spike | Burst of client input — many peers moved simultaneously |
| P95/P99 much higher than current | Periodic traffic bursts (e.g. tick-aligned input batches) |

**Expected ratio**: Bytes Out is roughly `N * (N-1) * delta_size * tick_rate` for N peers in mutual visibility. With 100 peers at 20 ticks/sec and ~50-byte deltas, expect ~10 MB/s outbound.

### Packets In/s, Packets Out/s

Packet count per second (each packet = one protobuf message). Complements bytes metrics — a high packet rate with low byte rate means many small messages (typical for position updates). A low packet rate with high byte rate means large messages (full snapshots, profiles).

### Unauth Skipped

Messages dropped because the sending peer was not yet authenticated (`PENDING_AUTH` state). Recorded in `RuntimePacketHandlerBase.SkipFromUnauthorizedPeer()`.

| Signal | Meaning |
|---|---|
| Zero | Normal — peers complete handshake before sending game messages |
| Occasional spikes on connect | Normal — a few messages may arrive during the handshake window |
| Sustained non-zero | Problem — clients sending data before auth completes, or handshake is too slow |
| Growing | Possible attack — unauthenticated peers flooding the server |

### Incoming Queue Depth

Pending messages in the incoming channel (`MessagePipe.incomingChannel`) waiting for workers to process. The ENet thread writes parsed `ClientMessage` envelopes; the router reads and fans out to worker channels.

| Signal | Meaning |
|---|---|
| Zero or near-zero | Workers are keeping up with inbound traffic |
| Stable low value | Normal — small transient buildup between router reads |
| Growing over time | Workers are saturated — consider increasing `MaxWorkerThreads` or optimizing handler logic |
| Spikes that recover | Burst of client input (e.g. all peers emoting simultaneously) |

### Outgoing Queue Depth

Pending messages in the outgoing channel (`MessagePipe.outgoingChannel`) waiting for the ENet thread to flush. Worker threads enqueue `ServerMessage` envelopes; the ENet thread drains and sends via `FlushOutgoing()`.

| Signal | Meaning |
|---|---|
| Zero or near-zero | ENet thread is easily keeping up |
| Stable 50-500 | **Normal for 50-200 peers** — workers produce bursts of deltas per tick, ENet drains them in the next loop iteration |
| Growing over time | **Bad** — ENet thread is falling behind, memory will grow unbounded |
| Thousands, stable | Borderline — ENet is saturated but not falling behind |
| Thousands, growing | **Critical** — reduce fan-out, optimize serialization, or reduce tick rate |

**The key signal is the trend, not the absolute value.** A flat sparkline at 200 with 100 peers is healthy. A sparkline trending upward is a problem regardless of the absolute value.

**Why it's bursty**: Simulation ticks fire on worker threads and produce N*(N-1) outgoing deltas in a burst. The ENet thread, running at ~1000 iterations/sec (1ms service timeout), drains the queue between bursts. The queue depth reflects the burst size, not sustained backlog.

---

## Hardening metrics

Pre-auth admission control and handshake throttling — see `docs/hardening.md` for the full threat model.

### Pre-Auth In-Flight

Gauge of connections currently in `PENDING_AUTH`. Read from `PreAuthAdmission.InFlight`.

| Signal | Meaning |
|---|---|
| Stable low | Normal — peers complete handshake within ~1 s and drop out of pending |
| Climbing toward `PreAuthBudget` | Flash crowd (event start) or pre-auth flood — watch `Pre-Auth Refused` |
| Near or at `PreAuthBudget` | Budget is saturated — further legitimate connections are being refused |
| Stuck high after admission traffic stops | Likely a counter leak — file a bug |

### Pre-Auth Refused

Counter of connections refused by the global `PreAuthBudget` check. `dcl_pulse_pre_auth_refused_total` in Prometheus.

| Signal | Meaning |
|---|---|
| Zero | Budget is not hit |
| Rare bursts during events | Normal flash-crowd back-pressure — clients retry with jitter |
| Sustained non-zero | Budget is undersized for peak connect rate, or a genuine pre-auth flood is active |

### Per-IP Limit Refused

Counter of connections refused by the per-source-IP pre-auth cap. `dcl_pulse_pre_auth_ip_limit_refused_total`.

| Signal | Meaning |
|---|---|
| Zero | No IP is saturating its quota |
| Sporadic from known CGNAT / corporate egress | Legitimate IP is shared by many users — consider raising `MaxConcurrentPreAuthPerIP` |
| High rate from one IP | Single-IP pre-auth flood; infrastructure-level block is probably warranted |

### Handshake Attempts Exceeded

Counter of peers disconnected after burning their handshake attempt budget. `dcl_pulse_handshake_attempts_exceeded_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — legitimate clients succeed on the first attempt |
| Sporadic | Clients with a bug in auth-chain generation, or transient MetaForge issues |
| Spiking | Targeted replay attempt or broken client build |

### Input Rate Throttled

Counter of `PlayerStateInput` messages dropped because the peer exceeded `MovementInput.MaxHz`. The peer is also disconnected with `INPUT_RATE_EXCEEDED`. `dcl_pulse_input_rate_throttled_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — legitimate Unity clients send at the server tick rate or slower |
| Sporadic | A client briefly burst before the rate check fired; may indicate a bug in the client's send loop |
| Sustained | Targeted input flood or a broken client build sending every frame |

### Discrete Event Throttled

Counter of emote/teleport messages dropped because the peer exceeded the token bucket. The peer is also disconnected with `DISCRETE_EVENT_RATE_EXCEEDED`. `dcl_pulse_discrete_event_throttled_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — real players do not chain emotes at >5 Hz sustained |
| Sporadic | One user triggering many emotes in a burst (likely a bug in emote UI, rarely an attack) |
| Sustained | Scripted client spamming emotes to amplify fan-out load |

### Handshake Replay Rejected

Counter of handshakes rejected because the `(wallet, timestamp)` pair had already been accepted within the anti-replay TTL. The peer is disconnected with `HANDSHAKE_REPLAY_REJECTED`. `dcl_pulse_handshake_replay_rejected_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — legitimate clients rebuild the handshake each time |
| Non-zero | Replay attack or buggy client reusing cached packets; treat as forensic priority |

### Banned Refused

Counter of handshake rejections and active-peer evictions caused by the platform ban list. Combines two enforcement paths: a banned wallet attempting a fresh handshake (rejected with `BANNED`) and a connected peer whose wallet appears in a newly-polled ban list (evicted via `MessagePipe.SendDisconnect`). `dcl_pulse_banned_refused_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — nobody on the platform ban list is trying to connect. |
| Occasional | Banned user attempted to reconnect; moderation working as intended. |
| Bursts matching a ban wave | Expected immediately after moderators ban many wallets at once — each connected victim triggers one increment per poll. |
| Sustained high | Banned user auto-reconnecting; check client is honouring the terminal-code guidance. |

### Field Validation Failed

Counter of post-auth messages rejected for invalid fields (oversized `EmoteId`/`Realm`, excessive `DurationMs`, out-of-range `ParcelIndex`). The offending peer is disconnected with a message-type-specific reason (`INVALID_INPUT_FIELD`, `INVALID_EMOTE_FIELD`, `INVALID_TELEPORT_FIELD`). `dcl_pulse_field_validation_failed_total`.

| Signal | Meaning |
|---|---|
| Zero | Normal — well-formed clients don't produce invalid fields |
| Sporadic | Specific buggy client build; check server logs for the DisconnectReason per peer |
| Spiking | Coordinated fuzz / exploit probing |

---

## Incoming Messages

Per-message-type rates for `ClientMessage` variants. Shows how many of each message type the server processes per second.

| Message | Source | Expected rate |
|---|---|---|
| **Handshake** | Auth flow | One per peer connection — should match connect rate |
| **Input** | Movement updates | Highest volume — ~20-30 per peer per second while moving, 0 while emoting |
| **Resync** | Gap recovery | Should be near zero — non-zero means clients are detecting sequence gaps |
| **ProfileAnnouncement** | Profile version | One per peer after auth, occasional updates |
| **EmoteStart** | Emote trigger | Depends on player behavior — bots emit every ~5s |
| **EmoteStop** | Emote cancel | Only for looping emotes cancelled by the client |
| **Teleport** | Client teleport | Rare — triggered by game logic |

**Key signals**:
- **Resync rate**: should be near zero. Sustained non-zero means the unreliable channel is losing too many packets or the server is sending deltas with gaps. Check network quality and tick consistency.
- **Input rate**: should be roughly `active_peers * tick_rate`. If significantly lower, peers may be idle or disconnected without the server knowing.

---

## Outgoing Messages

Per-message-type rates for `ServerMessage` variants. Shows the server's output composition.

| Message | Destination | Expected rate |
|---|---|---|
| **Handshake** | Auth response | Matches incoming handshake rate |
| **PlayerStateFull** | Full snapshot | On zone entry or resync — should be low |
| **PlayerStateDelta** | Incremental update | Highest volume — `observers * visible_subjects * tick_rate` |
| **PlayerJoined** | New peer notification | Matches connect rate × observer count |
| **PlayerLeft** | Peer departure | Matches disconnect rate × observer count |
| **ProfileVersion** | Profile update | Low — once per peer per observer on first sight + updates |
| **EmoteStarted** | Emote broadcast | Matches incoming EmoteStart × observer count |
| **EmoteStopped** | Emote end | Matches emote completions × observer count |
| **Teleported** | Teleport broadcast | Rare |

**Key signals**:
- **PlayerStateDelta dominance**: this should be the overwhelming majority of outgoing messages. If PlayerStateFull is a significant fraction, peers are resyncing too often.
- **Fan-out ratio**: outgoing messages = incoming messages × average observer count. With 100 peers all visible to each other, one Input generates ~99 PlayerStateDeltas.

---

## Adding new metrics

See the `/add-metric` skill (`/.claude/skills/add-metric/SKILL.md`) for step-by-step instructions covering three patterns:
- **Pattern A**: Counter-based (System.Diagnostics.Metrics) — for hot-path values
- **Pattern B**: Sampled (direct read) — for queue depths and gauges
- **Pattern C**: Per-enum collection — for counting by message type or enum variant
