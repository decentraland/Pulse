# `engine.parcel_changes` — the presence feed

Pulse publishes one NATS subject that says which wallet is standing on which parcel of which realm.
It is the platform's single source of online-player information: comms-gatekeeper builds its
presence map and `/hot-scenes` from it, social-service derives ONLINE/OFFLINE from it, and
worlds-content-server and realm-provider read the [HTTP surface](openapi.yaml) that is derived from
the same clustering pass.

Subject: **`engine.parcel_changes`**
Message: **`decentraland.pulse.ParcelChangesBatch`**, from
`@dcl/protocol`'s `proto/decentraland/pulse/pulse_presence.proto`.

```protobuf
message Parcel { int32 x = 1; int32 y = 2; }

message ParcelChange {
  string address = 1;          // lowercase 0x wallet
  string realm   = 2;          // canonical lowercase
  optional Parcel parcel = 3;  // absent => this peer left this realm/instance
}

message ParcelChangesBatch {
  string server_name = 1;      // NatsOptions.ServerName; consumers key seq per server_name
  uint64 seq         = 2;      // monotonic per server_name, resets on restart (snapshot=true follows)
  bool   snapshot    = 3;      // true => full state of this server, replaces everything for server_name
  uint64 server_time = 4;      // unix ms
  repeated ParcelChange changes = 5;
}
```

Two spellings matter on the wire and are easy to get wrong:

- **`"parcel": {}` is present, at `(0,0)`** — the world origin, a placement like any other. proto3
  omits default values, so an all-zero `Parcel` encodes as an empty submessage.
- **`parcel` absent is the exit signal.** Not `(0,0)`, not an empty `realm`. In generated C# this is
  `change.Parcel is null`; in protobufjs, the field being undefined.

## What Pulse guarantees

1. **A peer's first placement is an entry.** A peer that connects, stands still and never moves is
   published once, because its previous state was "nowhere". A consumer does not have to wait for a
   peer to move to learn it exists.
2. **Every exit is exactly one `parcel`-absent entry** — precisely, one per **transport
   disconnect of a wallet's last connection**. Clean disconnect, authentication timeout,
   duplicate-session kick, ban eviction, `PeerDefense` kick — all of them are a transport
   disconnect, and every transport disconnect ends in the one `PeerSimulation` cleanup that also
   wipes `IdentityBoard` and `ProfileBoard`, so that one call site covers all of them and none is
   published twice. Exits are deliberately **not** derived from "present last pass, missing in this
   one".
   Two cases publish nothing, both for the same reason — there is no presence to withdraw:
   - a peer that timed out before it ever authenticated was never placed in a realm, so it never
     reached the feed;
   - a connection whose wallet is **already placed on a newer connection of this server**. A
     duplicate session (or a reconnect after a network blip) is accepted immediately while the
     evicted slot is cleaned up `Peers:DisconnectionCleanTimeoutMs` later, so the wallet is standing
     somewhere by then; withdrawing it would take a peer that is online offline until the next
     snapshot, and inside one batch the per-address coalescing would keep the exit and swallow the
     new placement entirely.

   The corollary is worth stating for consumers: **the guarantee is scoped to transport
   disconnects.** A client that keeps its socket alive — so the transport keeps acknowledging it —
   but stops sending input is not a disconnect, so no exit entry is published for it. `ParcelChange`
   carries no timestamp, so a feed-only consumer cannot expire such a peer; the HTTP surface shows it
   with a frozen `lastPing` (see [openapi.yaml](openapi.yaml)).
3. **A realm change is one non-null entry for the new realm.** There is no exit for the old one — a
   wallet is in one realm at a time, so the new entry is the whole of the move. A consumer keying
   presence by wallet replaces; one keying by `(realm, wallet)` must remove the wallet from every
   other realm on seeing it in a new one.
4. **Within a batch a wallet appears at most once, with its latest state.** A peer running across
   parcels costs one entry per batch interval, not one per step. This holds for a snapshot as well as
   a delta, and a snapshot is the case worth spelling out: entries are per **wallet**, not per
   connection, so a wallet briefly standing on two connections — the
   `Peers:DisconnectionCleanTimeoutMs` window after a duplicate-session kick or a fast reconnect —
   appears once, at the connection that is actually placed. Consumers may therefore apply a batch as
   a straight last-write-wins per address, in wire order, without checking for repeats.
5. **`realm` and `address` are lowercase**, canonicalized at ingest (handshake and teleport), and
   `server_name` is the same string for the life of the process — **and unique per replica**, which
   the `pulse-<hostname>` default gives you per *host* rather than per process, see
   [Configuration](#configuration).

## Cadence

| When | What |
|---|---|
| every `Presence:BatchIntervalMs` (default 2000) | a delta batch, if anything changed. An empty delta is not published — "no peer moved" is not news |
| on publisher start | `snapshot=true` (`reason=start`) |
| when the broker connection is rebuilt | `snapshot=true` (`reason=start` — as far as the wire is concerned it is a fresh stream) |
| every `Presence:SnapshotIntervalMs` (default 60000) | `snapshot=true` (`reason=interval`) |
| after an outbox eviction | `snapshot=true` (`reason=eviction`), on the next batch turn, coalesced to at most one per quarter of `Presence:SnapshotIntervalMs` |

A snapshot is a batch with the flag set, not a separate stream: it takes the next `seq` like any
other. It carries one non-null entry per active **wallet** with a known realm and parcel, and an **empty**
snapshot is meaningful — it says this server holds nobody, which a consumer has no other way to
learn.

The interval is a **recovery deadline**, not a refresh rate: it bounds how long a consumer that
missed a delta serves stale state before it is corrected. The eviction trigger exists because the
outbox is the one place a change can be genuinely lost — see `dcl_pulse_nats_dropped_total` — and a
consumer must never be left running on a delta stream that is known to be incomplete. Repeated
evictions inside one window are covered by the snapshot the first one raised, so they are coalesced:
under sustained loss the feed would otherwise send its largest possible message every batch interval,
at exactly the moment the broker is already dropping messages.

**A snapshot never discards a pending change.** Whatever was waiting when the snapshot was collected
is published first, under its own `seq`, and the snapshot takes the next one — so a batch turn can
carry two messages, a delta and then the snapshot. This is what makes an exit survive a snapshot: the
snapshot lists live peers, and a peer that has just left is not one of them, so folding its exit into
the snapshot would lose it entirely. Anything raised *after* the snapshot was collected is newer than
it and follows it, on the next turn.

## The consumer rule

Keep state per `server_name`. There are several Pulse instances, each with its own `seq`.

```
on batch B for server S:
    if B.snapshot:
        replace everything known for S with B.changes   # and only for S
        expected[S] = B.seq + 1
        return

    if B.seq != expected[S]:
        # a gap, or a restart that has not announced itself yet.
        # Keep serving what you have and wait for the next snapshot — which is at most
        # Presence:SnapshotIntervalMs away. Do NOT clear your map.
        log the gap; expected[S] = B.seq + 1
        return

    apply B.changes            # parcel present => place; parcel absent => remove
    expected[S] = B.seq + 1
```

Three things this rule is protecting:

- **A gap is not a reason to drop state.** Losing one batch loses the peers that moved in it;
  clearing the map loses every peer. Hold what you have — it is stale for at most one snapshot
  interval.
- **`seq` going backwards means that server restarted**, and the restart's first batch is a
  snapshot, which repairs it. Nothing else resets `seq`.
- **A snapshot replaces the state of its own `server_name` only.** A snapshot from `pulse-2` says
  nothing about the peers `pulse-1` reported.

Two more rules for the values themselves:

- **Compare realms and addresses case-insensitively anyway.** Pulse canonicalizes at ingest, but a
  non-lowercase value on the wire is a contract violation to log, not a reason to drop state — the
  contract pack ships `07-invalid-mixed-case-realm.bin` for exactly this test.
- **Tolerate an address you have never seen leaving.** Exits are idempotent; a removal of something
  you do not hold is not an error.

## Configuration

| Key | Env var | Default | Notes |
|---|---|---|---|
| `Nats:ServerName` | `Nats__ServerName` | `pulse-<hostname>` | The `server_name` on every batch. **Two replicas must never share it** — see below |
| `Presence:Enabled` | `Presence__Enabled` | `true` | Rollback switch for this feed alone. False leaves clustering, `engine.islands` and the HTTP surface untouched |
| `Presence:BatchIntervalMs` | `Presence__BatchIntervalMs` | `2000` | Also the window changes coalesce over. A non-positive value disables the feed |
| `Presence:SnapshotIntervalMs` | `Presence__SnapshotIntervalMs` | `60000` | Recovery deadline. Non-positive disables the periodic snapshot; start and eviction still fire |

`Presence:Enabled` defaults to true, but the feed follows the existing NATS gating: with `Nats:Url`
unset there is no broker and nothing is published, exactly as before this feed existed. **A deploy
that changes no configuration behaves as it did before**, and a deployment already pointing Pulse at
a broker gets the feed.

### `server_name` must be unique per replica

Consumers key their state by `server_name` and a `snapshot=true` batch replaces *everything they
hold for that name*. Two replicas publishing the same value therefore delete each other's whole
population from every consumer's presence map once per snapshot interval, and their interleaved delta
streams read as a permanent `seq` gap that freezes consumers in between — all of it silent on the
Pulse side, because both instances look perfectly healthy.

So `Nats:ServerName` is left blank in `appsettings.json` and defaults to `pulse-` plus
`Environment.MachineName` — the pod name under default Kubernetes networking, the container id under
plain Docker. The value the process resolved is logged at startup on the `NATS publisher started`
line, which is the one place to read it back.

**That default is unique per host, not per process.** Wherever the hostname is not the pod's own it
is the node's: `hostNetwork: true` — a plausible choice for a UDP game server — gives every pod on a
node the node name, a deployment-wide `spec.hostname` gives every pod the same name everywhere, and
two Pulse processes on one machine share it by definition. In any of those, set `Nats:ServerName`
explicitly, to something already unique per replica (never a deployment-wide literal). Nothing
detects the collision for you: the startup line prints the same value on each replica and looks
correct on both, and the symptom shows up only at the consumer, as the mutual deletion above.

`Nats:ServerName` is not presence-only. The same value is the `server_name` on the
`engine.discovery` heartbeat and the NATS client's connection name — what `/connz` and
`nats server report connections` display. `appsettings.json` shipped the literal `pulse` before this
feed existed, so a deploy that changes no configuration moves all three onto `pulse-<hostname>`: an
operator artefact matching the old literal (a `/connz` filter, a Grafana rule or dashboard variable
pinned to `server_name="pulse"`) stops matching after the rollout, silently. Nothing in the platform
reads it — archipelago-stats' discovery consumer takes `currentTime` and `userCount` and never looks
at `server_name` — but an operator's own tooling might.

Batch size is bounded by `Nats:ChannelCapacity` (default 1024) for a delta, and by the peers this
server holds for a snapshot.

## Metrics

| Metric | Type | Notes |
|---|---|---|
| `dcl_pulse_presence_batch_size` | histogram | Entries per published batch, snapshots included. The observation count is the batch count, so `rate(dcl_pulse_presence_batch_size_count[5m])` is the feed's cadence |
| `dcl_pulse_presence_snapshots_total` | counter, `reason="start"\|"interval"\|"eviction"` | A steady trickle of `interval` is the healthy shape. **Any** rate of `eviction` means the outbox is losing changes — raise `Nats:ChannelCapacity` |
| `dcl_pulse_nats_dropped_total` | counter | Shared with the cluster feed — a dropped *cluster assignment* forces a presence snapshot too, since either loss means the broker is behind. Coalesced to one snapshot per quarter of `Presence:SnapshotIntervalMs`, so this counter can climb faster than `snapshots_total{reason="eviction"}` |
| `dcl_pulse_nats_published_total` / `_publish_failed_total` | counters | Shared with the cluster feed. A publish that failed is a real `seq` gap on this subject |

## Reading the feed by hand

There is no `nats` CLI on the development machines; a few lines of Node with the `nats` package do
the job.

```js
const { connect } = require('nats')
const protobuf = require('protobufjs')

const root = await protobuf.load('proto/decentraland/pulse/pulse_presence.proto')
const Batch = root.lookupType('decentraland.pulse.ParcelChangesBatch')
const nc = await connect({ servers: 'nats://localhost:4222' })

for await (const m of nc.subscribe('engine.parcel_changes')) {
  const b = Batch.decode(m.data)
  console.log(b.snapshot ? 'SNAPSHOT' : 'delta', b.serverName, 'seq', b.seq,
    b.changes.map(c => `${c.address} ${c.realm} ${c.parcel ? `${c.parcel.x || 0},${c.parcel.y || 0}` : 'LEFT'}`))
}
```

## Contract fixtures

`archipelago-workers/docs/contracts/iteration-2/parcel_changes/` holds the wire bytes for each
guarantee, the canonical JSON beside them, and `replay.json` — the ordered consumer scenario with
the expected presence map after every step. Producers compare **bytes**; consumers decode the bytes
and compare **objects**, then run the replay through their state machine. Copy the files into your
own test tree (CI has no sibling checkout) and check them against the pack's `manifest.json`.

Pulse's own copies live in `src/DCLPulseTests/Fixtures/iteration-2/`, driven by
`PresenceWireFixtureTests` (bytes) and `PresenceGuaranteeTests` (the five guarantees above).
