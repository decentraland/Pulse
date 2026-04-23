# Hardening — DoS & Pre-Auth Abuse Defenses

Operator reference for the network-level protections built into the Pulse server. This doc
is organized by protection group; each section covers the threat, the mitigation, the config
knobs, and the metrics to watch.

---

## Group A — Pre-Auth Resource Exhaustion

### Threat model

ENet's UDP connect handshake allocates a `PeerIndex` slot (server-allocated pool, default
4095). The slot stays occupied until the client either completes the handshake
(`HandshakeRequest`) or hits the PENDING_AUTH timeout (default 30 s).

Three abuse patterns:

1. **Pre-auth squatting.** An attacker opens connections but never sends a valid handshake.
   Each connection holds a pool slot for 30 s for ~zero cost. One connection per second per
   attacker = 30 slots held steady-state. A botnet exhausts 4095 slots and locks real players
   out.
2. **Single-IP flood.** A single attacker IP opens many connections in parallel, saturating
   the pool from one source.
3. **Handshake CPU burn.** Once connected, a peer can replay `HandshakeRequest` packets. Each
   one costs the server two ECDSA recoveries (via the Rust signature verifier). Many attempts
   per peer = asymmetric cost.

### Defenses

All three are addressed by two components:

| Component | Location | What it caps |
|---|---|---|
| `PreAuthAdmission` | `src/DCLPulse/Transport/Hardening/PreAuthAdmission.cs` | Global + per-IP concurrent PENDING_AUTH connections |
| `HandshakeAttemptPolicy` | `src/DCLPulse/Messaging/Hardening/HandshakeAttemptPolicy.cs` | Handshake attempts per peer |

### `PreAuthAdmission` — how it works

Two layered checks on every ENet Connect. Both must pass to admit; either failing disconnects
the peer immediately with a specific `DisconnectReason`.

```
ENet Connect
   │
   ├─ peerIndexAllocator.TryAllocate() ─── fail → SERVER_FULL
   │
   ├─ admission.TryAdmit(peerIndex, ip) ── IpLimitExhausted → PRE_AUTH_IP_LIMIT_EXHAUSTED
   │                                    ── BudgetExhausted  → PRE_AUTH_BUDGET_EXHAUSTED
   │
   └─ normal flow (messagePipe.OnPeerConnected, worker gets lifecycle Connected)
```

Release happens on the owning worker thread at exactly two points (both guaranteed to fire
for every admitted peer):

- **Promotion**: `HandshakeHandler` calls `ReleaseOnPromotion(peerIndex)` on successful
  validation (PENDING_AUTH → AUTHENTICATED).
- **Disconnect**: `PeersManager.HandleLifeCycleEvent(Disconnected)` calls
  `ReleaseOnDisconnect(peerIndex)` regardless of state. Idempotent — after a promotion the
  lookup misses, so nothing decrements twice.

This means:

- **Per-IP quota only counts PENDING_AUTH peers.** Once a peer authenticates it no longer
  consumes a slot against its IP. Essential for NAT / CGNAT / VPN / corporate egress users
  where many legitimate players share one public IP.
- **Global budget reserves authenticated capacity.** `MaxPeers - PreAuthBudget` slots in the
  PeerIndex pool can never be held by unauthenticated peers, so a pre-auth flood cannot lock
  real players out.

### `HandshakeAttemptPolicy` — how it works

A byte counter on `PeerState.TransportState.HandshakeAttempts`. Incremented at the entry of
`HandshakeHandler.Handle`. Once it exceeds `MaxAttempts`, the handler force-disconnects the
peer with `AUTH_FAILED` and no further crypto work happens.

Because the counter lives on `PeerState`, it is scoped to the peer's lifetime — a reconnect
starts fresh (the new connection gets a new `PeerIndex` and a new `PeerState`).

### Config

`appsettings.json`:

```json
{
  "Transport": {
    "Hardening": {
      "PreAuth": {
        "PreAuthBudget": 512,
        "MaxConcurrentPreAuthPerIP": 32
      }
    }
  },
  "Messaging": {
    "Hardening": {
      "Handshake": {
        "MaxAttempts": 2
      }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `PreAuthBudget` | 512 | Max concurrent PENDING_AUTH peers across the server. `0` disables. |
| `MaxConcurrentPreAuthPerIP` | 32 | Max concurrent PENDING_AUTH peers per source IP. `0` disables. |
| `MaxAttempts` | 2 | Max `HandshakeRequest` packets per peer before force-disconnect. `0` disables. |

`appsettings.Development.json` disables all three (`0`) so local load tests don't clamp.

### How the limits interact

A new connection walks this pipeline. Each gate refuses with a distinct `DisconnectReason`:

| Gate | Refused reason | Operator action |
|---|---|---|
| PeerIndex pool exhausted | `SERVER_FULL` | Raise `Transport:MaxPeers` or shorten grace window |
| Per-IP pre-auth quota hit | `PRE_AUTH_IP_LIMIT_EXHAUSTED` | Raise `MaxConcurrentPreAuthPerIP` if flash-crowd from shared IPs is legitimate |
| Global pre-auth budget hit | `PRE_AUTH_BUDGET_EXHAUSTED` | Raise `PreAuthBudget` (costs authenticated headroom) or investigate flood source |
| 30 s PENDING_AUTH deadline | `AUTH_TIMEOUT` | Indicates slow/broken client or attacker squatting |
| Handshake attempts exceeded | `AUTH_FAILED` | Client bug or replay attempt |

### Tuning `PreAuthBudget`

Two inputs:

- **`T`** — how long a peer stays in PENDING_AUTH. Worst case is `PendingAuthCleanTimeoutMs`
  (30 s) for an attacker who never sends handshake. Legitimate: ~1 s.
- **`R_legit`** — peak legitimate connect rate (joins/sec during an event rush).

At steady state, legitimate peers occupy ≈ `R_legit × 1 s` slots. So:

```
PreAuthBudget ≥ R_legit                                (don't block real bursts)
PreAuthBudget < MaxPeers                               (leave authenticated headroom)
attacker_steady_slots ≤ PreAuthBudget                  (bounded by the budget itself)
attacker_throughput ≈ PreAuthBudget / 30 s             (pre-auth slots recycled per second)
```

Defaults: budget 512, pool 4095 → authenticated-reserved 3583. An attacker filling the budget
cycles ~17 pre-auth slots/sec, irrelevant next to the reserved capacity.

### Tuning `MaxConcurrentPreAuthPerIP`

Only counts PENDING_AUTH, so once a user authenticates they free up their slot for the next
user on the same IP. Effective throughput per IP ≈ cap peers/sec.

- Residential home: 1-5 users — default 32 is fine.
- Small office / VPN: few-to-dozens of users — default 32 handles bursts.
- CGNAT / mobile carrier / large corp: hundreds+ users — raise to 64 or 128 if you see
  `PRE_AUTH_IP_LIMIT_REFUSED` traffic from known-good providers.
- Event venue / LAN: set a per-deployment override.

### Client recovery

Both `PRE_AUTH_IP_LIMIT_EXHAUSTED` and `PRE_AUTH_BUDGET_EXHAUSTED` are **retryable transient**
conditions — not auth failures. Clients should:

1. Treat alongside `SERVER_FULL` and `AUTH_TIMEOUT` as "retry with backoff", not terminal.
2. Use **exponential backoff with jitter** (initial 0.5–2 s, doubling, cap ~30 s, give up after
   ~2 min). Jitter is mandatory — without it, all users behind a CGNAT re-synchronise and
   trigger the cap forever.
3. **Reuse the existing `connectSig`** on retry if still within the 60 s anti-replay window.
   Rebuilding the auth chain costs a wallet signature on some wallets.
4. **Distinct UI copy**: "Reconnecting…" for retryable codes, not "Authentication failed".
5. Open a fresh ENet connection on retry — don't try to revive a disconnected peer.

Terminal codes that must NOT auto-retry: `AUTH_FAILED`, `DUPLICATE_SESSION`, `KICKED`.

### Metrics to watch

From `pulse.hardening.*`:

| Metric | Type | What it tells you |
|---|---|---|
| `pre_auth_in_flight` | gauge | Current size of the PENDING_AUTH pool. Should sit low; spikes are flash crowds or floods. |
| `pre_auth_refused` | counter | Admissions refused by the global budget. Non-zero ⇒ budget hit. |
| `pre_auth_ip_limit_refused` | counter | Admissions refused by the per-IP quota. Non-zero ⇒ single IP saturated (may be CGNAT or flood). |
| `handshake_attempts_exceeded` | counter | Peers that burned their attempt budget. Non-zero ⇒ buggy client or attacker. |

Alert rules to consider:

- `rate(pre_auth_refused) > 0 for 1m` — server is hitting its global budget; investigate.
- `rate(pre_auth_ip_limit_refused) > 10/s for 5m` — CGNAT/VPN block, or targeted single-IP flood.
- `pre_auth_in_flight / PreAuthBudget > 0.8` — near saturation, consider raising the budget.

---

## Group B — Post-Auth Message Rate Limiting

### Threat model

Once authenticated, a peer can flood the server with protocol messages for which the cost
asymmetry favours the attacker:

1. **Input flood.** `PlayerStateInput` at 1000+ Hz — each message triggers snapshot publish,
   spatial-grid update, and per-observer diff work on the simulation tick. One peer can
   saturate a worker thread.
2. **Discrete-event fan-out.** `EmoteStart`, `EmoteStop`, `TeleportRequest` each cause
   O(observers) reliable broadcasts. A peer spamming emote starts multiplies their send rate
   by the observer count in their interest set.

### Defenses

Two dedicated limiters in `src/DCLPulse/Messaging/Hardening/`:

| Component | Cap | Enforcement |
|---|---|---|
| `MovementInputRateLimiter` | `MaxHz` (default 20) on `PlayerStateInput` | Interval-based — min `1000/MaxHz` ms between accepted inputs |
| `DiscreteEventRateLimiter` | Token bucket, `RatePerSecond` refill (default 5) + `BurstCapacity` (default 10) | Shared across emote start/stop + teleport |

Per-peer state lives on `PeerThrottleState` hanging off `PeerState`. Mutated exclusively on
the owning worker thread, so no synchronisation is required.

**Violation response:** the peer is disconnected with a specific `DisconnectReason`. This is
not graceful back-pressure — clients that sustain message rates above the cap are either
buggy or malicious, and staying connected lets them keep probing.

### Config

```json
{
  "Messaging": {
    "Hardening": {
      "MovementInput": { "MaxHz": 20 },
      "DiscreteEvent": { "RatePerSecond": 5.0, "BurstCapacity": 10 }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `MovementInput.MaxHz` | 20 | Max `PlayerStateInput` messages per peer per second. Matches the base simulation tick rate — faster sends have no game-state benefit. `0` disables. |
| `DiscreteEvent.RatePerSecond` | 5.0 | Sustained rate of discrete events per peer. `0` disables. |
| `DiscreteEvent.BurstCapacity` | 10 | Burst allowance. Stored as `byte`, clamped to 255. |

Dev (`appsettings.Development.json`) sets all to `0` so load tests aren't throttled.

### DisconnectReason values

| Value | Meaning |
|---|---|
| `INPUT_RATE_EXCEEDED = 9` | Peer sent `PlayerStateInput` faster than `MaxHz`. |
| `DISCRETE_EVENT_RATE_EXCEEDED = 10` | Peer exceeded the token bucket for discrete events. |

### Client recovery

Both codes are **terminal, not retryable**. A well-behaved Unity client sending at the server
tick rate will never trigger them; seeing one means the client has a bug or is compromised.

Recommended client behaviour:
- **Do not auto-reconnect.** Retry would hit the same cap and the server would disconnect
  again, creating a reconnect loop that looks like a different attack.
- Log the reason locally and surface it to telemetry; the server did its job, the client is
  wrong.
- UI copy: "Connection closed: the client sent data faster than the server allows. Please
  restart the game or contact support."

### Metrics to watch

| Metric | Type | Signal |
|---|---|---|
| `input_rate_throttled` | counter | Non-zero ⇒ at least one client is bursting `PlayerStateInput`. Investigate per-client traffic before suspecting the cap is too tight. |
| `discrete_event_throttled` | counter | Non-zero ⇒ a peer is spamming emote/teleport. Almost always a buggy client; rarely a real attacker. |

---

## Group B (field validation) — Post-Auth Input Sanitisation

### Threat model

Authenticated peers can embed oversized strings, out-of-range indices, or absurd durations in
game messages. The server stores them in snapshots and re-broadcasts to every observer in the
AoI, so one bad packet costs the attacker O(1) and the server O(observers). Parcel indices
that fall outside the encoder's grid produce garbage global positions downstream.

### Defense

`src/DCLPulse/Messaging/Hardening/FieldValidator.cs` — one class, three per-message methods
(`ValidatePlayerStateInput`, `ValidateEmoteStart`, `ValidateTeleport`). Parcel bounds come
from the existing `ParcelEncoderOptions` (`ParcelEncoder.IsValidIndex`). On any violation the
peer is disconnected with a message-type-specific `DisconnectReason`.

### Config

```json
{
  "Messaging": {
    "Hardening": {
      "FieldValidator": {
        "MaxEmoteIdLength": 64,
        "MaxRealmLength": 128,
        "MaxEmoteDurationMs": 60000
      }
    }
  }
}
```

Zero disables each individual check; parcel-index validation is always on (its bounds are the
server's own configured realm size, not a per-defense knob).

### DisconnectReason values

| Value | Meaning |
|---|---|
| `INVALID_INPUT_FIELD = 11` | PlayerStateInput carried an out-of-range parcel index. |
| `INVALID_EMOTE_FIELD = 12` | EmoteStart had an oversized EmoteId, excessive DurationMs, or invalid parcel index. |
| `INVALID_TELEPORT_FIELD = 13` | TeleportRequest had an empty or oversized Realm, or invalid parcel index. |

### Client recovery

All three are **terminal, not retryable** — a well-formed client never produces invalid
fields. Same client guidance as the rate-limit codes: do not auto-reconnect, log the reason,
surface to telemetry.

### Metrics to watch

| Metric | Type | Signal |
|---|---|---|
| `field_validation_failed` | counter | Non-zero ⇒ a client is sending malformed fields. Check server logs for the specific DisconnectReason per peer. |

---

## Shared `PeerDefense` base class

`MovementInputRateLimiter`, `DiscreteEventRateLimiter`, `FieldValidator`, and
`HandshakeAttemptPolicy` all inherit from `PeerDefense`, which provides the common
`Reject(PeerIndex, PeerState, DisconnectReason)` helper: bumps the violation counter, flips
`PeerState.ConnectionState` to `PENDING_DISCONNECT`, and calls `transport.Disconnect`.

The `PENDING_DISCONNECT` state closes the window between "server decided to disconnect" and
"ENet's Disconnect event actually fires" — during that window, subsequent queued messages from
the peer fail `SkipFromUnauthorizedPeer` (which only lets `AUTHENTICATED` through), so no
handler work runs, no metrics inflate, and no further redundant disconnect envelopes are
enqueued.

`PreAuthAdmission` is not a `PeerDefense`: it returns an `AdmitResult` enum, runs on the ENet
thread, and uses `DisconnectNow` rather than the queued `Disconnect` path.

---

## Resync-request AoI invariant

`RESYNC_REQUEST` could in principle be used to reconnoitre peers outside the observer's AoI
("send me a full snapshot of subject X"). The handler itself does no visibility check —
validation happens in `PeerSimulation.ProcessVisibleSubjects`, which only consumes resync
entries for subjects in the per-tick visible collector. Entries for non-visible subjects are
discarded by the end-of-tick `ResyncRequests.Clear()` without ever producing a `STATE_FULL`.

Pinned by `PeerSimulationTests.Resync_ForNonVisibleSubject_ProducesNoStateFull` and
`Resync_ForNonVisibleSubject_ClearedOnTick`. A future batched `RESYNC_REQUEST` (multiple
subject IDs per packet) will supersede the per-peer dict and let us enforce a single
per-packet cap; no handler-time defense needed in the meantime.
