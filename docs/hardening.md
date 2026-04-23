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
