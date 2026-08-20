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

Two dedicated limiters in `src/DCLPulse/Messaging/Hardening/`, both inheriting from a shared
`TokenBucketRateLimiter` base:

| Component | Cap | Enforcement |
|---|---|---|
| `MovementInputRateLimiter` | Token bucket, `MaxHz` refill (default 20) + `BurstCapacity` (default 16) on `PlayerStateInput` | Burst absorbs UDP jitter (ISP/NAT/Wi-Fi clustering, worker batch drain) without false positives |
| `DiscreteEventRateLimiter` | Token bucket, `RatePerSecond` refill (default 5) + `BurstCapacity` (default 10) | Shared across emote start/stop + teleport |

Per-peer state lives on `PeerThrottleState` hanging off `PeerState` (one `(tokens, lastRefillMs)`
pair per limiter). Mutated exclusively on the owning worker thread, so no synchronisation is
required.

**Why a bucket, not a strict interval, for movement input?** UDP packets sent at uniform 50 ms
spacing at the client routinely arrive at the server in tight clusters after ISP bufferbloat,
NAT queue drains, or Wi-Fi retransmits; the owning worker also drains its incoming-event
channel in batches once per loop iteration, so two messages enqueued between drains are
handled microseconds apart regardless of wire spacing. A strict `now − last < 1000/MaxHz`
check counts these as violations and disconnects the peer. The bucket caps the long-run rate
identically while letting short bursts through.

**Violation response:** the peer is disconnected with a specific `DisconnectReason`. This is
not graceful back-pressure — clients that sustain message rates above the cap are either
buggy or malicious, and staying connected lets them keep probing.

### Config

```json
{
  "Messaging": {
    "Hardening": {
      "MovementInput": { "MaxHz": 20, "BurstCapacity": 16 },
      "DiscreteEvent": { "RatePerSecond": 5.0, "BurstCapacity": 10 }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `MovementInput.MaxHz` | 20 | Sustained `PlayerStateInput` refill rate per peer, in messages per second. Matches the base simulation tick rate — faster sends have no game-state benefit. `0` disables. |
| `MovementInput.BurstCapacity` | 16 | Burst allowance for movement input — absorbs jitter-induced packet clustering (~800 ms worth at the default 20 Hz). Stored as `byte`, clamped to 255. `0` disables. |
| `DiscreteEvent.RatePerSecond` | 5.0 | Sustained rate of discrete events per peer. `0` disables. |
| `DiscreteEvent.BurstCapacity` | 10 | Burst allowance for discrete events. Stored as `byte`, clamped to 255. |

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
(`ValidatePlayerStateInput`, `ValidateEmoteStart`, `ValidateTeleport`). Checks performed:

- Parcel-index bounds (delegated to `ParcelEncoder.IsValidIndex`).
- `Realm` string length cap. Default 255 — covers all four realm-string forms in ADR-144 (DCL World subdomain `name.dcl.eth`, ENS name, DAO catalyst friendly name, catalyst URL) and matches the ENS-label spec ceiling.
- `EmoteStart.DurationMs` upper bound.
- **Finiteness** (`float.IsFinite`) on every client-supplied float: `Position`, `Velocity`,
  `RotationY`, `MovementBlend`, `SlideBlend`, optional `HeadYaw`/`HeadPitch`,
  `TeleportRequest.Position`. Rejects NaN and ±Infinity. Optional fields (head yaw/pitch)
  are checked only when the proto's `Has*` flag is set.
- Null-guard on `Position`/`Velocity` proto sub-messages to prevent NRE on malformed input.

On any violation the peer is disconnected with a message-type-specific `DisconnectReason`.

### Config

```json
{
  "Messaging": {
    "Hardening": {
      "FieldValidator": {
        "MaxRealmLength": 255,
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
| `INVALID_EMOTE_FIELD = 12` | EmoteStart had excessive DurationMs or invalid parcel index. |
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

## Group E — Auth Hardening

### E2 — Handshake replay cache

#### Threat model

The handshake validator accepts any well-formed `connect_sig` whose `timestamp` is within
the server's 60 s anti-replay window. Within that window, a captured handshake packet can be
**replayed as many times as the attacker wants**. Capture sources:

- Passive sniffing on shared WiFi / corporate NAT / coffee-shop networks.
- Malicious router or ISP hop between client and server.
- UDP duplication from broken middleboxes (non-malicious but same effect).

An attacker who captures one successful handshake packet and replays it within 60 s is
admitted as the victim's wallet.

#### Defense

`src/DCLPulse/Messaging/Hardening/HandshakeReplayPolicy.cs` — sliding-window
`Dictionary<(wallet, timestamp), expiry>` guarded by one `Lock`, called once per handshake
after `AuthChainValidator.Validate` succeeds. On duplicate `(wallet, timestamp)` pair within
the TTL, the peer is disconnected with `HANDSHAKE_REPLAY_REJECTED` and the metric fires.

Memory footprint is bounded by handshake rate × TTL. At peak ~50 connects/s × 60 s TTL =
3000 entries, well under the 4096 hard cap.

#### Config

```json
{
  "Messaging": {
    "Hardening": {
      "HandshakeReplay": {
        "Enabled": true
      }
    }
  }
}
```

No numeric knobs — both are derived to avoid duplicated sources of truth:
- **TTL** = `PeerOptions.PendingAuthCleanTimeoutMs` (single source of truth for "how long
  we remember a PENDING_AUTH fact").
- **Memory cap** = `ENetTransportOptions.MaxPeers` (concurrent handshakes can't exceed the
  PeerIndex pool, so the cache needs no more).

`Enabled = false` disables the check (dev / load tests).

#### DisconnectReason

| Value | Meaning |
|---|---|
| `HANDSHAKE_REPLAY_REJECTED = 14` | Same (wallet, timestamp) pair was already accepted within the TTL. Terminal — legitimate clients rebuild the handshake with a fresh timestamp on every connect. |

#### Client recovery

**Terminal, not retryable.** A legitimate client will never see this code — it means either
the packet was captured and replayed from elsewhere, or the client is reusing a cached
handshake packet (bug). UI copy: "Session rejected: please sign in again." Do not auto-retry
with the same handshake; always rebuild the auth chain with a fresh timestamp.

#### Known gap — the signed payload does not bind the server instance

`SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata)` produces the
exact string the client signs:

```
connect:/:<timestamp>:<metadata>
```

The payload covers the peer's identity material only — method, path, timestamp, and
client-supplied metadata. **There is nothing server-side baked into it.** In particular,
no server instance identifier is required or checked.

Consequence: a handshake captured from instance A can be replayed to instance B of the same
fleet and, if instance B hasn't seen that `(wallet, timestamp)` pair (which it won't —
`HandshakeReplayPolicy` is per-instance, in-memory), the replay succeeds. The replay
policy in this doc blocks **same-instance** replay only.

Closing this gap requires:
1. The client including a `server_id` in the signed `metadata`, or
2. A dedicated `server_id` field in the signed payload shape (proto change), or
3. A shared replay store (Redis etc.) backing `HandshakeReplayPolicy` across the fleet.

None of these are implemented today. Tracked as a known limitation rather than a scheduled
item because single-instance deployments are the common case and multi-instance fleets
behind sticky-session load balancers are naturally immune (a victim and their attacker
don't land on different instances).

#### Metrics to watch

| Metric | Type | Signal |
|---|---|---|
| `handshake_replay_rejected` | counter | Non-zero ⇒ active replay attempt or a misbehaving client reusing packets. Forensic priority — log + investigate source IPs. |

---

## Group F — Platform Ban List

### Threat model

Decentraland moderation maintains a central list of banned wallet addresses served by
`comms-gatekeeper`. Two enforcement windows matter:

1. **New connection by a banned wallet.** A user who was banned between sessions reconnects
   with a valid auth chain — the cryptography checks out, but the wallet is on the moderation
   list. Without enforcement at the server, the user rejoins the same realm they were
   moderated out of.
2. **Wallet banned mid-session.** A user is already connected and misbehaving when a
   moderator issues the ban. If the server only checks on handshake, the banned user stays in
   place until they disconnect voluntarily.

### Defense

Two components in `src/DCLPulse/Messaging/Hardening/`:

| Component | Role |
|---|---|
| `BanList` | Shared, atomically-swappable `HashSet<string>` (case-insensitive). Readers never lock. |
| `BansPollingHttpService` | `BackgroundService` that polls `https://comms-gatekeeper.decentraland.{HttpSuffix}/bans` every `PollIntervalSeconds` with the `COMMS_MODERATOR_TOKEN` bearer, replaces the list, and evicts newly-banned connected peers via `MessagePipe.SendDisconnect(..., DisconnectReason.BANNED)`. |

The handshake path (`HandshakeHandler`) consults `BanList.IsBanned(walletAddress)` after
`AuthChainValidator.Validate` succeeds and before `HandshakeReplayPolicy.TryAdmit` — a banned
wallet never burns a replay-window slot. On a hit, the handler sends `HandshakeResponse
{ Success = false, Error = "banned" }`, flips the peer to `PENDING_DISCONNECT`, bumps the
`banned_refused` metric, and calls `transport.Disconnect(from, DisconnectReason.BANNED)`.

The poller's eviction scan runs on the poller thread and never touches any `PeerState` — it
only enqueues disconnects through `MessagePipe`, which is the documented cross-thread entry
point. The owning worker receives the lifecycle Disconnected event and performs its usual
cleanup. This preserves the worker-shard isolation rule.

### Pass-through mode (local dev / CI)

`BansPollingHttpService.ExecuteAsync` checks two conditions on startup and returns without scheduling any
work when either fails:

1. `CommsBearerToken.Value` is empty (`COMMS_MODERATOR_TOKEN` env var not set).
2. `Bans:PollIntervalSeconds` is zero.

In both cases the `BanList` singleton stays empty for the process lifetime, so the
handshake-time `IsBanned` check is a constant-time hash lookup that always returns `false`.
The feature has zero runtime cost and zero network traffic outside production deployments.

### Config

`appsettings.json`:

```json
{
  "Messaging": {
    "Hardening": {
      "Bans": {
        "PollIntervalSeconds": 30,
        "HttpTimeoutSeconds": 10
      }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `PollIntervalSeconds` | 30 | How often to refresh the ban list. `0` disables the poller. |
| `HttpTimeoutSeconds` | 10 | Per-request HTTP timeout. `0` means no timeout. |

`appsettings.Development.json` sets `PollIntervalSeconds: 0` so dev runs never hit the
gatekeeper even if a token leaks into the local env.

### Unban semantics

When a wallet is removed upstream, `BanList.Replace` drops it on the next poll cycle. No
active notification is sent — a previously-banned wallet is simply re-admitted on its next
connection attempt. This matches how every other policy in the codebase handles removal of
state: silent and eventual.

### DisconnectReason

`BANNED = 5` (already existed before this group). Used for both enforcement paths.

### Client recovery

**Terminal, not retryable.** A banned user rejoining lands on `BANNED` both at handshake and
on active-session eviction. Client UI copy should surface a moderation-specific message
("Your account has been suspended — contact support") rather than the generic
"reconnecting…" treatment used for retryable codes. Auto-retry must be suppressed — a retry
loop would hammer the gatekeeper with refused connections.

### HTTP error handling

The poller keeps the previous ban list on any transient error (non-2xx response, timeout,
malformed JSON, DNS failure). Worst case during a gatekeeper outage: the list goes stale but
continues enforcing the last known good snapshot. A first-boot failure before any successful
poll leaves the list empty — identical to pass-through mode.

### Metrics to watch

| Metric | Type | Signal |
|---|---|---|
| `banned_refused` | counter | Non-zero ⇒ at least one banned wallet attempted to connect, or was evicted mid-session. Combines both paths — a spike without corresponding handshake traffic indicates a fresh ban wave causing evictions. |

---

## Group G — Corrupted Packets

### Threat model

Two distinct trigger points produce a "this packet is garbage" outcome on the ENet thread:

1. **Oversized packets.** ENet reassembles fragmented UDP datagrams before delivering a Receive
   event, so a peer can hand the server a single logical packet much larger than the MTU. The
   Receive handler copies the payload into a fixed-size `receiveBuffer` (`Transport.BufferSize`,
   default 4096). Anything larger either crashes the ENet thread on `CopyTo`, overflows into
   adjacent memory in unsafe configurations, or wastes CPU on garbage parsing.
2. **Protobuf parse failures.** `MessagePipe.OnDataReceived` runs `ClientMessage.Parser.ParseFrom`
   on the raw bytes. A malformed payload (`Protocol message contained an invalid tag (zero)`,
   truncated frame, unknown wire types) throws `Google.Protobuf.InvalidProtocolBufferException`.
   A single corrupted packet may be a UDP middlebox glitch; a sustained rate is a fuzzer
   feeding random bytes to probe the parser.

A single corrupted packet is benign — well-formed clients still occasionally see one in the
wild after broken NAT reassembly. The expensive failure mode is **sustained** corruption from
one peer.

### Defense

A per-peer token bucket — `CorruptedPacketLimiter`
(`src/DCLPulse/Transport/Hardening/CorruptedPacketLimiter.cs`) — refills one token every
`60000 / MaxPerMinute` ms up to `BurstCapacity`. The transport calls
`RegisterAndCheckExhausted` on every corrupt packet (oversized **and** parse-failure paths
funnel through the same bucket). When the bucket exhausts, the peer is queued for disconnect
with `PACKET_CORRUPTED`.

The two call sites are:

| Site | When |
|---|---|
| `ENetHostedService.IsOversizedAndRecordCorruption` | Packet length > `Transport.BufferSize` (caught before `CopyTo`) |
| `ENetHostedService.HandleEvent` `Receive` branch | `messagePipe.OnDataReceived` returned `false` (protobuf parse failed) |

`MessagePipe.OnDataReceived` now returns `bool` — the parse result — and the transport (which
already owns the limiter) decides whether to call `RecordCorruption`. The limiter has no
dependency on `MessagePipe`; both call sites run on the ENet thread, so the limiter's per-peer
dictionary is mutated single-threaded with no locking.

Per-peer state is released in the existing Disconnect event handler via
`corruptedPacketLimiter.Release(peerIndex)`.

### Config

```json
{
  "Transport": {
    "Hardening": {
      "CorruptedPacket": {
        "MaxPerMinute": 5,
        "BurstCapacity": 5
      }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `MaxPerMinute` | 5 | Sustained corrupt-packets-per-minute per peer before disconnect (refill: one token every `60000 / MaxPerMinute` ms = 12000 ms at the default). Zero disables. |
| `BurstCapacity` | 5 | Burst allowance — number of corrupt packets a peer can send back-to-back before refill kicks in. Stored as a byte per peer (clamped to 255). Zero disables. |

### Tuning

Well-formed clients produce **zero** corrupt packets — protobuf is rigid and ENet handles its
own checksum/length on the wire. The default `5 / 5` (5 per minute, burst 5) tolerates a brief
reassembly anomaly on flaky links without nuking the session: a peer can hit 5 corrupt packets
back-to-back, then must wait 12 s for each subsequent token. A fuzzer streaming garbage at
~1 Hz exhausts the bucket within ~5 seconds. Raise both knobs together if you observe
legitimate clients churning the metric, but suspect the client first.

### DisconnectReason

| Value | Meaning |
|---|---|
| `PACKET_CORRUPTED = 16` | Peer's corrupted-packet budget was exhausted. Terminal — covers both oversized packets and protobuf parse failures. |

### Client recovery

**Terminal, not retryable.** A legitimate client encoding the protocol correctly never sustains
corrupt packets — even a full `STATE_FULL` snapshot fits well under 4 KB after quantization,
and protobuf serializers don't randomly emit invalid tags. If a client needs to send something
larger than `Transport.BufferSize` the right fix is at the protocol layer (split, chunk,
compress), not raising the buffer. Recommended client behaviour matches the other terminal
codes: log locally, surface to telemetry, do **not** auto-reconnect.

### Metrics to watch

| Metric | Type | Signal |
|---|---|---|
| `corrupted_packet` | counter | Increments on **every** corrupt packet (oversized + parse-failure), regardless of whether the bucket exhausted. Sporadic hits ⇒ benign UDP/middlebox jitter; sustained per-peer rate above `MaxPerSecond` ⇒ that peer just hit `PACKET_CORRUPTED`; cross-peer spike ⇒ coordinated fuzzing or client/server build drift. |

---

## Group H — Hard Per-IP Connection Cap

### Threat model

A single source IP opens connections without bound. Each admitted connection consumes a
`PeerIndex` from a fixed pool (`Transport:MaxPeers`, default 4095), a transport peer slot,
and — once it reaches `OnPeerConnected` — a worker's `peerStates` entry and a 30 s
`PENDING_AUTH` window. Because released slots sit in the allocator's pending-recycle grace
window, connection *churn* from one host degrades slot availability for everyone even when no
individual connection is long-lived.

The cost asymmetry is severe: the attacker spends one connect handshake (two round trips, no
crypto); the server spends a pool slot, a dictionary insert in three structures, a worker
lifecycle event, and a 30 s reservation. A defense against this belongs at the cheapest layer
available, which is why this one sits above everything in Group A.

Group A caps *pre-auth* concurrency per IP. Nothing there bounds a single IP's **total**
footprint once its connections authenticate — that is the gap this group closes.

**Scene listeners break a single shared cap.** A scene listener is a full peer: the transport
allocates its `PeerIndex` at connect and `HandshakeHandlerBase.Handle` builds the `PeerState` that
`SceneListenerHandshakeHandler.TryAuthorize` stamps its listener descriptor onto, so it consumes a
pool slot exactly like a player and must be counted. But a listener fleet runs from a
handful of egress IPs and deliberately opens many connections, so one shared per-IP cap can only
be wrong in one of two directions: low enough to protect players and it throttles the
infrastructure, high enough for the fleet and it protects nothing. The cap is therefore **per
connection class** — see the two budgets below.

### Defenses

`IpLimiter` (`src/DCLPulse/Transport/Hardening/IpLimiter.cs`) enforces a hard cap on concurrent
connections per source IP **at the top of `EventType.Connect`, before
`peerIndexAllocator.TryAllocate`**, with one budget per `ConnectionClass`
(`src/DCLPulse/Transport/Hardening/ConnectionClass.cs`).

That placement is strictly earlier than `PreAuthAdmission`, which runs *after* allocation and
then rolls back with `MarkPending` + `Release`. Refusing before allocation means a flooding IP
never touches the allocator's pending-recycle state at all, never creates a `ConnectedPeer`,
never emits `OnPeerConnected`, never reaches a worker, and never enters `PENDING_AUTH`.

Admission sequence on connect, with all rollback owned by the `ENetHostedService.Hardening.cs`
partial:

```
1. ipLimiter.TryAcquire(ip, PLAYER)      -> refuse: DisconnectNow(IP_CONNECTION_LIMIT_EXCEEDED), return
                                            (nothing allocated, nothing to undo)
2. peerIndexAllocator.TryAllocate       -> fail:   Abandon(ip, PLAYER); DisconnectNow(SERVER_FULL)
3. preAuthAdmission.TryAdmit            -> fail:   allocator rollback; Abandon(ip, PLAYER); disconnect
4. ipLimiter.Bind(peerIndex, ip, PLAYER) <- commit the reservation to the peer
5. ...existing wiring -> messagePipe.OnPeerConnected(peerIndex)
```

**Two budgets, not one.**

| Class | Cap key | Default | Counts |
|---|---|---|---|
| `PLAYER` | `MaxConcurrency` | 10 | Every connection until it announces itself something else |
| `SCENE_LISTENER` | `SceneListenerMaxConcurrency` | 2 | Connections whose `SCENE_LISTENER_HANDSHAKE` validated |

The budgets are independent in both directions: a full player budget never refuses a listener, and
a full listener budget never refuses a player. They are therefore **additive** — one IP's ceiling is
the sum of the caps, 12 at the shipped defaults; see
[the arithmetic](#how-the-limits-interact). Adding a class later costs an enum value, a label
and a cap key — not a second copy of the bookkeeping. State is one `Dictionary<string,int[]>` of
per-IP counts (one slot per class) plus a `Dictionary<PeerIndex,Reservation>` reverse index whose
`Reservation` carries the IP **and** the class, so a disconnect always credits the budget that
actually held the peer. `ip_limit_tracked_ips` stays one entry per IP no matter how its
connections split across classes, and an entry is removed only once **every** class is at zero.

**The class is decided in two phases.** The server cannot know at connect that a peer is a
listener — that is only known when `SCENE_LISTENER_HANDSHAKE` validates, on the worker thread. So
every connection is acquired against `PLAYER` at step 1 above, and the listener handshake *moves*
the reservation:

```
SCENE_LISTENER_HANDSHAKE validates (worker thread)
   │
   ├─ fieldValidator.ValidateSceneListenerHandshake  -> fail: INVALID_HANDSHAKE_FIELD
   │                                                    (a malformed announcement spends no
   │                                                     listener capacity)
   ├─ ipLimiter.TryReclassify(peer, SCENE_LISTENER)  -> fail: SCENE_LISTENER_IP_LIMIT_EXCEEDED
   │                                                    PLAYER -> SCENE_LISTENER on success
   └─ peer published, PENDING_AUTH -> AUTHENTICATED
```

`TryReclassify` is all-or-nothing: on refusal nothing is mutated, so the peer stays player-classed
and the ordinary Disconnected release frees the slot it really holds. Both gates run inside
`TryAuthorize`, which the pipeline calls **before** it publishes the peer into the worker's dict —
a refused listener therefore never reaches `AUTHENTICATED`, never registers an identity, and never
gets a listener descriptor. The same hook point as the field validation next to it, and the same
`PENDING_DISCONNECT`-then-disconnect shape (`HandshakeHandlerBase.RejectHandshake`, sibling of
`PeerDefense.Reject`).

A peer the transport could not attribute (empty `Peer.IP`, only admitted while the limiter is
disabled) **does** hold a reservation — both transports call `Bind` unconditionally, so the peer is
indexed under the empty-string key — but nothing was ever counted for it in the per-IP table. Its
promotion therefore succeeds without moving anything: there is no count to charge, and refusing on
missing bookkeeping would disconnect a peer the limiter never counted. Such a peer stays
`PLAYER`-classed for the rest of its session and its release decrements nothing, which is harmless
only because no count exists to end up in the wrong budget.

**Both transports enforce the cap against one shared counter.**
`WebTransportHostedService.HandleConnect` mirrors the sequence above, using
`ParseIp(ev.RemoteAddress)`. The two transports draw from the same `PeerIndex` pool, so ENet and
WebTransport connections from the same IP count together against the same per-class budgets.

**Release paths.** Steps 2 and 3 refuse *before* `OnPeerConnected`, so no worker lifecycle event
will ever fire for those peers — the transport thread releases them inline via `Abandon(ip, PLAYER)`.
Admitted peers release from the owning worker on the Disconnected lifecycle event
(`PeersManager`, alongside the existing `preAuthAdmission.ReleaseOnDisconnect`), keyed by
`PeerIndex` — and from whichever class the reservation currently names, so a promoted listener
credits the listener budget rather than the player one it connected under. `Release` is idempotent
via lookup-and-clear, so a duplicate call is a no-op.

Both dictionaries live under one `Lock` — required because ENet and WebTransport run on separate
threads, and `Release`/`TryReclassify` run on worker threads. Contention is bounded by connect
rate, not packet rate. Per-IP entries are removed once every class is at zero, so the table is
bounded by concurrent connections, not by distinct IPs ever seen.

> Earlier still would be ENet's `intercept` callback, which fires on raw datagram receive before
> protocol handling. The C# binding does not surface it. Out of scope; noted as a future option
> if connect-flood volume ever justifies it.

### Config

`Transport:Hardening:IpLimiter` — **unlike every other hardening knob in this document, this
section is runtime-reconfigurable.** Its keys live in `dynamicconfig.json` rather than
`appsettings.json`, and can be changed on a live server from the remote `pulse.json` document.
See [docs/feature-flags.md](feature-flags.md) for the mechanism, the type schema, and the local
dev loop.

`dynamicconfig.json`:

```json
{
  "Transport": {
    "Hardening": {
      "IpLimiter": {
        "Enabled": false,
        "MaxConcurrency": 10,
        "SceneListenerMaxConcurrency": 2,
        "Whitelist": ""
      }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. When `false`, connections are still counted but never refused. |
| `MaxConcurrency` | 10 | Max concurrent player-class connections from one source IP, across both transports. `0` disables the cap. |
| `SceneListenerMaxConcurrency` | 2 | Max concurrent scene-listener connections from one source IP — one global value applied to every IP, not a per-fleet allowance ([sizing](#sizing-scenelistenermaxconcurrency)). `0` disables the cap — it does **not** mean "no listeners". |
| `Whitelist` | `""` | Comma-separated **exact** IPs exempt from **both** caps. Whitelisted IPs are still counted. |

The cap keys stay flat, one per class, rather than nesting under a `MaxConcurrency` object: the
flat keys are the ones the live remote payload already sets, and since the key allowlist was
removed a renamed key would bind to nothing and silently revert the cap to its default. The
options type maps the flat keys onto a per-class lookup internally
(`IpLimiterOptions.MaxConcurrencyFor`).

The shipped default is `Enabled: false`, so local dev and load tests are unclamped and the
limiter is turned on per environment from the remote document. There is **no**
`appsettings.Development.json` override — these keys exist only in `dynamicconfig.json`.

`Whitelist` is a delimited string rather than a JSON array on purpose; the reasoning is in
[docs/feature-flags.md](feature-flags.md). The string format also extends to CIDR later without
a schema change.

### Sizing `SceneListenerMaxConcurrency`

A listener's reach is bounded by `SceneListener:MaxParcels` (default 4096 parcels per connection).
`ParcelEncoder` widens the `ParcelEncoderOptions` bounds of x ∈ [-150, 163], z ∈ [-150, 158] by
`Padding` (2 per side) in its constructor, and `FieldValidator.ValidateSceneListenerHandshake`
accepts rects against those *padded* bounds via `IsValidCoordinate` — so the encodable area is
x ∈ [-152, 165] (318 columns) × z ∈ [-152, 160] (313 rows) = **99,534 parcels**. One listener
connection therefore covers at most 4096 / 99,534 ≈ **4.1%** of it, and whole-map coverage needs
99,534 / 4096 ≈ 24.3 → **25** concurrent connections.

**Raising this cap is the wrong way to pay for that — whitelist the fleet instead.**
`SceneListenerMaxConcurrency` is a **global** knob: it is the per-IP listener cap applied to *every*
source address, not an allowance handed to a known fleet. Setting it to 25 grants 25 listener slots
to every IP on the internet and pushes the per-IP ceiling to `10 + 25 = 35`, past
`MaxConcurrentPreAuthPerIP` of 32. And nothing gates who may claim listener budget: there is **no
listener allowlist** — `SceneListenerHandshakeHandler` requires only a valid Decentraland auth
chain, a non-empty realm and at least one in-bounds rect, all three of which any client can
produce.

So a listener fleet that needs many connections should have its **egress IPs whitelisted**;
`Whitelist` exempts an IP from both caps, on the listener promotion path as much as at connect, and
it names the hosts that are actually entitled to the capacity. `SceneListenerMaxConcurrency` should
stay small — the shipped default of 2 is a starting point for one or two scoped scenes from an
unlisted host. A **per-wallet listener allowlist**, so listener capacity could be granted to an
identity instead of to every source IP alike, is the prerequisite for ever raising this global cap
substantially; it does not exist.

Under-setting this budget **shows up as missing coverage, not as an error**: refused listeners
retry, so the fleet stays up and simply never observes some parcels. Nothing in the listener's own
telemetry says "I was capped" — watch `ip_limit_refused{class="scene_listener"}` and the
`scene_listener_connected` gauge together, and compare the gauge against the number of connections
the fleet is configured to open.

### How the limits interact

`PreAuthAdmission.MaxConcurrentPreAuthPerIP` (Group A) and `IpLimiter.MaxConcurrency` are both
"per-IP connection caps", and they are deliberately kept as **siblings rather than merged into
one class**:

| | `PreAuthAdmission.MaxConcurrentPreAuthPerIP` | `IpLimiter.MaxConcurrency` |
|---|---|---|
| Counts | Connections in `PENDING_AUTH` only | **All** connections, authenticated included |
| Released on | Promotion **or** disconnect | Disconnect only |
| Enforced | After `TryAllocate` | Before `TryAllocate` |
| Configured | Boot-time `IOptions` | Runtime `IOptionsMonitor` |
| Whitelist | No | Yes |

Merging two counters is the right call when they move together. These do not: they count
different populations, over different lifetimes, at different points in the connect sequence,
from different config sources, and only one of them honours a whitelist. Every row of that table
is a place where a merged class would need an internal branch — and merging would also drag
`PreAuthAdmissionOptions` onto the dynamic-config path, which it is not on.

The full connect pipeline, in gate order:

| Gate | Order | Refused reason |
|---|---|---|
| Per-IP total connection cap (player class) | Before allocation | `IP_CONNECTION_LIMIT_EXCEEDED` |
| PeerIndex pool exhausted | Allocation | `SERVER_FULL` |
| Per-IP pre-auth quota | After allocation | `PRE_AUTH_IP_LIMIT_EXHAUSTED` |
| Global pre-auth budget | After allocation | `PRE_AUTH_BUDGET_EXHAUSTED` |
| Per-IP scene-listener cap | On listener-handshake validation | `SCENE_LISTENER_IP_LIMIT_EXCEEDED` |

Note the arithmetic at the shipped defaults. **The class budgets are additive: one IP's ceiling is
their sum, not the largest of them.** A promotion moves a reservation between the budgets rather
than duplicating it — `TryReclassify` credits the class it came from as it charges the target, so
every promotion hands a player slot back — which means a single IP can hold `MaxConcurrency` player
connections **and** `SceneListenerMaxConcurrency` listener connections at the same time:
`10 + 2 = 12` concurrent connections at the shipped defaults. A fleet host sitting at its listener
cap still has all 10 player slots available. There is deliberately no cross-class ceiling; that is
what "two budgets, not one" buys.

What therefore has to stay under Group A's per-IP pre-auth quota (`MaxConcurrentPreAuthPerIP`, 32)
is that **sum**, not `MaxConcurrency` alone. At the shipped defaults 12 < 32, so the total caps bind
first and the pre-auth quota is unreachable from one IP; raise either class's cap past that headroom
and the pre-auth quota becomes the gate that refuses first for connections still in `PENDING_AUTH`.
The pre-auth quota is also what protects you while the limiter is disabled.

### Runtime semantics

Because this config is live, the semantics of a mid-flight change matter. Two of them look like
bugs and are not.

- **Read per call.** `TryAcquire` reads `options.CurrentValue` at entry. Connect rate is low, so
  this is not a hot path in the CLAUDE.md sense (per-tick fan-out, per-packet parse) and the
  monitor lookup is free.
- **Counting continues while the limiter is disabled.** When `Enabled == false` or
  `MaxConcurrency == 0`, `TryAcquire` still increments and `Release`/`Abandon` still decrement —
  only the refusal branch is skipped. If counting stopped, re-enabling the limiter would resume
  from a zero baseline and over-admit until the whole connected population churned. This is a
  deliberate departure from the usual "bail out early when disabled" shape; connect-rate cost
  makes the tradeoff free.
- **A disabled limiter still reclassifies.** `TryReclassify` moves the reservation whether or not
  enforcement is on; only the refusal is skipped. A move that paused while disabled would leave
  listeners charged to the player budget, and re-enabling would then refuse players for
  connections that are not theirs.
- **Whitelisted IPs are still counted**, for the same reason: removing an IP from the whitelist
  must take effect against an accurate count immediately, not once that IP's connections have
  drained.
- **Lowering `MaxConcurrency` does not evict.** Connections already above the new cap stay put;
  the counter simply refuses new ones until it drains below the cap. Retroactive eviction would
  kick legitimate players because an operator typed a smaller number. This is a deliberate
  contrast with `BanEnforcer`, which *does* evict already-connected peers — bans are about
  identity, capacity limits are not.
- **Whitelist parsing is cached.** The parsed `HashSet<string>` is rebuilt on
  `IOptionsMonitor.OnChange` and published as an immutable snapshot — the same
  swap-a-snapshot pattern as `BanList`, so readers never lock.

### Known limitations

- **Exact IP match only.** No CIDR. Office / VPN egress ranges have to be listed individually in
  `Whitelist`.
- **IPv6 limiting is weak.** A single customer typically controls an entire /64, so a per-address
  cap is easily evaded by rotating within the prefix. Keying IPv6 by /64 prefix is the correct
  fix and is not implemented.
- **NAT / CGNAT collateral.** Every user behind one shared public IP draws from the same budget,
  so a tight cap refuses legitimate players. `MaxConcurrency: 10` is a starting point, not a
  recommendation — watch `ip_limit_refused` before tightening. Being able to correct this without
  a redeploy is precisely why the knob is runtime-adjustable.

### DisconnectReason

| Value | Meaning |
|---|---|
| `IP_CONNECTION_LIMIT_EXCEEDED = 17` | Hard per-source-IP concurrent-connection cap exceeded. Unlike `PRE_AUTH_IP_LIMIT_EXHAUSTED`, authenticated connections count against this cap, and the connection is refused before a `PeerIndex` is allocated. **Retryable** — capacity frees as other connections from the same IP close. |
| `SCENE_LISTENER_IP_LIMIT_EXCEEDED = 18` | Per-source-IP concurrent **scene-listener** cap exceeded. Deliberately distinct from `17`: the operator fix is a different knob (`SceneListenerMaxConcurrency`, not `MaxConcurrency`), and it is refused later — when the listener handshake validates, not at connect. A shared code would make the two indistinguishable in the field. **Retryable**, but see the recovery contract below. |

### Client recovery

`IP_CONNECTION_LIMIT_EXCEEDED` is **retryable transient**, in the same family as
`PRE_AUTH_IP_LIMIT_EXHAUSTED` and `SERVER_FULL`. Clients should:

1. Retry with **exponential backoff and jitter**. Jitter is mandatory, not a refinement: without
   it, every client behind one NAT re-synchronises onto the same retry instants and re-triggers
   the cap indefinitely.
2. Surface it as **"too many connections from your network"** — never as an authentication
   failure. The user's credentials are fine; their network's connection budget is full.
3. **Reuse the existing auth chain** on retry if still inside the anti-replay window, so the
   retry costs no wallet signature prompt.
4. Open a fresh connection on retry — don't try to revive the refused one.

`SCENE_LISTENER_IP_LIMIT_EXCEEDED` is retryable too, but its capacity behaves differently and the
client contract follows from that: the budget only frees when **another listener from the same IP
disconnects**, which on a steady fleet may be minutes or never. So:

1. **Long backoff with jitter** — start around 5–10 s, double, cap in the minutes, and keep
   retrying rather than giving up: a listener that stops retrying leaves its parcels unobserved
   with nothing in the logs to say why. Jitter is mandatory, for the same re-synchronisation
   reason as above; a fleet started by one orchestrator retries in lockstep otherwise.
2. **Do not re-announce a smaller parcel set** hoping to fit. The cap counts connections, not
   parcels — a narrower announcement is refused identically. Fixing it means whitelisting the egress
   IP, spreading the fleet across more egress addresses, or — knowing it widens the cap for every IP
   — raising `SceneListenerMaxConcurrency`.
3. **Surface it as a capacity/config problem, not an auth failure**, and log the reason code: this
   is the one refusal whose only other symptom is silently missing coverage.

### Metrics to watch

From `pulse.hardening.*`:

| Metric | Type | What it tells you |
|---|---|---|
| `ip_limit_refused` | counter | Connections refused by a per-IP cap, labelled `class="player"` / `class="scene_listener"` (one series per `ConnectionClass`, always emitted). `player` non-zero ⇒ some IP is at its budget — either a flood, or a CGNAT/venue population that needs a higher `MaxConcurrency`. `scene_listener` non-zero ⇒ a fleet is being capped and parcels are going unobserved; whitelist the egress IP rather than raising the global listener cap ([sizing](#sizing-scenelistenermaxconcurrency)). |
| `ip_limit_whitelist_bypass` | counter | Connections that *would have been* refused and were admitted because the IP is whitelisted — either cap, including a listener promotion. |
| `ip_limit_tracked_ips` | gauge | Distinct IPs currently holding ≥1 connection. Equals the size of the limiter's per-IP dictionary. |

The whitelist-bypass counter is the one worth a dashboard panel: it is the only signal that
separates a **load-bearing** whitelist entry (actively absorbing refusals) from a **vestigial**
one (added during an incident, never removed, doing nothing). An entry whose bypass counter is
flat zero can be deleted without effect.

---

## Shared `PeerDefense` base class

`MovementInputRateLimiter`, `DiscreteEventRateLimiter`, `FieldValidator`, and
`HandshakeAttemptPolicy` all inherit from `PeerDefense`, which provides the common
`Reject(PeerIndex, PeerState, DisconnectReason)` helper: bumps the violation counter, flips
`PeerState.ConnectionState` to `PENDING_DISCONNECT`, and calls `transport.Disconnect`.

The two rate limiters additionally share `TokenBucketRateLimiter` (subclass of `PeerDefense`),
which owns the bucket math (whole-token refill, sub-interval-remainder carry, byte-capped
debit). Subclasses provide three things: the rate/burst configuration, the disconnect reason,
and a getter/setter pair that maps to their slot on `PeerThrottleState` (input slot vs
discrete-event slot). Keeps the per-limiter classes to ~15 lines and ensures both behave
identically under jitter and overflow.

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
