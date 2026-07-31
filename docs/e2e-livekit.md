# End-to-end: the LiveKit conn-string path

How to run the harness that checks Pulse's cluster feed all the way to the LiveKit connection
string a client would actually receive, on a local machine and in CI.

> **Status.** The compose stack and this document describe a harness whose test-client side is
> under construction. Everything about NATS, ws-connector, comms-gatekeeper and Pulse's
> configuration below was read out of those sources. Everything about the test client's own
> flags, output and exit codes is the **intended contract** from
> [`docs/tasks/e2e-livekit-connstring.md`](tasks/e2e-livekit-connstring.md) — treat it as the
> spec it is, not as observed behaviour. Places where the two disagree are called out inline.

## 1. What the harness proves

`ClusterTracker` deciding that a peer belongs to cluster `C3` is only half the claim worth
making. The half that matters to a player is that the same wallet, on a different socket,
receives a LiveKit connection string for the room that cluster maps to. Those are two
independent channels, and until they are held by one process on one identity, checking them
means comparing two logs and hoping.

One test-client process, one account, both channels:

```
                  ENet / WebTransport            (Pulse protocol: movement, deltas, emotes)
  test client  ─────────────────────────────▶  Pulse
       │                                          │  ClusterTracker pass, 1 Hz
       │                                          ▼
       │                                     NATS  peer.{addr}.cluster_change
       │                                          │  PeerClusterChange { cluster_id, realm }
       │                                          ▼
       │                                   gatekeeper  (mints conn_str, ban check)
       │                                          │
       │                                          ▼
       │                                     NATS  engine.peer.{addr}.island_changed
       │                                          │  IslandChangedMessage { island_id, conn_str, … }
       │            WebSocket /ws                 ▼
       └────────────────────────────────────  ws-connector
                  ◀── ServerPacket{ islandChanged }
```

The identity being the same on both channels is the whole point. It turns
`peer.{addr}.cluster_change` → `engine.peer.{addr}.island_changed` into a verifiable
correspondence rather than two unrelated observations: the address in the outbound subject and
the address that welcomed the WebSocket are the same string, so a missing message is
attributable to a hop rather than to "some other bot, probably".

**The subject asymmetry is deliberate.** Pulse publishes the unprefixed
`peer.{addr}.cluster_change`. ws-connector subscribes to the `engine.`-prefixed
`engine.peer.*.island_changed` (`ws-connector/src/service.ts`). comms-gatekeeper sits between
them and publishes the `engine.`-prefixed subject explicitly, and it does so *without* applying
its own `NATS_SUBJECT_PREFIX` — the comment in
`comms-gatekeeper/src/logic/cluster-subscriber/component.ts` calls this out as intentional,
because ws-connector subscribes to the literal subject. It looks like a bug from either end. It
is not. Do not "fix" either side to match the other; the stub gatekeeper has to reproduce it
exactly or nothing arrives.

Pulse's `Nats:SubjectPrefix` does apply to what Pulse publishes, so a non-empty prefix has to be
mirrored on whatever subscribes to `cluster_change`. It must not be mirrored on
`island_changed`. See [clustering-on-aoi.md §3.6](clustering-on-aoi.md) for the full feed
description and the three subjects Pulse emits.

## 2. The stack

`docker-compose.e2e.yml` at the repo root brings up three services. The test client is not one
of them — it runs on the host, which is why every port below is published.

| Service | Image / build | Host port | Container port | Purpose |
| --- | --- | --- | --- | --- |
| `nats` | `nats:2-alpine` | `4222` | `4222` | Broker, plain pub/sub |
| `nats` | | `8222` | `8222` | Monitoring: `/healthz`, `/connz`, `/subsz` |
| `ws-connector` | build from `../archipelago-workers` | `5000` | `5000` | `/ws`, `/status`, `/metrics`, `/health/live` |
| `pulse` | build from `Dockerfile.debug` | `7777/udp` | `7777/udp` | ENet game traffic |
| `pulse` | | `5100` | `5000` | `/health`, `/about`, `/metrics` |

ws-connector gets host 5000 because that is what the test client's `--comms-url` defaults to;
Pulse's HTTP service also listens on 5000 inside its own container, so it is published on 5100.
ws-connector's port is pinned explicitly in the compose file rather than inherited, because the
two `.env.default` files in the archipelago-workers monorepo disagree about it — the root says
5000, the `ws-connector/` workspace says 5001, and which one applies depends on the working
directory. Every host port is overridable — `E2E_NATS_PORT`, `E2E_NATS_MONITOR_PORT`,
`E2E_WS_CONNECTOR_PORT`, `E2E_PULSE_ENET_PORT`, `E2E_PULSE_HTTP_PORT`. On macOS, host 5000 is
taken by AirPlay Receiver unless you have disabled it (see the Troubleshooting section of the
root README); `E2E_WS_CONNECTOR_PORT=5010` plus a matching `--comms-url` is the cheaper fix.

Startup ordering is enforced with healthchecks and `depends_on: condition: service_healthy`,
not with sleeps. Both Pulse and ws-connector wait for NATS to answer `/healthz` before they
start. This matters more than it looks: Pulse's NATS feed is **fail-soft by design** — an
unreachable broker leaves the tracker running in stats-only mode and publishes nothing, with no
error. Racing the broker produces a run that looks healthy and delivers nothing.

`docker compose up --wait` blocks until all three report healthy, which is the only start
sequence worth using before launching the client.

### Notes on the two builds

**ws-connector.** The archipelago-workers monorepo has a single `Dockerfile` at its root serving
both the `ws-connector` and `stats` workspaces; its `CMD` is `node dist/index.js` relative to the
working directory. The compose file selects the workspace with `working_dir: /app/ws-connector`
rather than by overriding the command. That build also runs `yarn test` as a build step, so a
broken test in your archipelago-workers checkout fails the image build, not the run — the error
will be a jest report in `docker compose build` output.

The build context defaults to `../archipelago-workers`, resolved relative to this repo's root.
If your checkout lives elsewhere, set `E2E_ARCHIPELAGO_WORKERS_PATH`. There is deliberately no
published image reference: a local harness should track the ws-connector you actually have.

**Pulse.** Built from `Dockerfile.debug` — the SDK image that restores and builds inside the
container on every `up`, which is what you want while the server is being changed under you. The
first start takes minutes; the healthcheck's `start_period` is 240 s to match. The Release
`src/DCLPulse/Dockerfile` would start faster, but its runtime base image ships no HTTP client,
so it cannot carry a container healthcheck — swapping to it means giving up the ordering
guarantee above.

## 3. Prerequisites

- **Docker** with Compose v2 (`docker compose`, not `docker-compose`).
- **An `archipelago-workers` checkout**, by default a sibling of this repo.
- **`metaforge` on PATH, recent enough to have `account sign`.** The test client shells out to
  MetaForge for every signing operation; private keys never enter the test client. Signing the
  ws-connector challenge needs a subcommand that older builds do not have:

  ```bash
  metaforge account sign --help
  ```

  If that fails, rebuild and reinstall MetaForge. The failure mode with a stale binary is worth
  recognising: the test client surfaces the CLI's non-zero exit and its stderr, so it reads as
  *"unknown command"*, not as a rejected signature. `metaforge account chain` is the older,
  different thing — it builds the signed-fetch shape (`method:path:timestamp:metadata`) and
  cannot sign a `dcl-<hex>` challenge verbatim.
- **`Clusters:Enabled` must be on.** It ships `true` in `appsettings.json`, and the compose file
  pins `Clusters__Enabled=true` anyway so the harness does not depend on that default holding.
  With it off, the tracker never runs and nothing is ever published — silently.
- Optional but worth having: the [`nats` CLI](https://github.com/nats-io/natscli), for watching
  subjects directly. Section 6 gives a container-based alternative if you would rather not
  install it.

Outbound internet is nice to have. ws-connector fetches
`https://config.decentraland.org/denylist.json` at handshake time (cached 5 minutes) and fails
open on error, logging it — so the harness works offline, but with an error line per cache miss.

## 4. The two bridge modes

Between `peer.{addr}.cluster_change` and `engine.peer.{addr}.island_changed` sits
comms-gatekeeper. The real one needs Postgres, LiveKit credentials and a deny-list lookup before
it will mint a token. That is three external dependencies and a secret, which is why it cannot
gate CI. The test client therefore carries a **stub gatekeeper** that does the same subject
translation with nothing behind it.

| `--bridge-mode` | What runs | Credentials | Use |
| --- | --- | --- | --- |
| `synthetic` (default) | Stub in the test client; `conn_str` is a fixed non-routable string | None | CI, and every ordinary local run |
| `livekit` | Stub in the test client; mints a real token from host environment credentials | LiveKit host, key, secret | Checking that a real token is well-formed, or feeding `--join-livekit` |
| `off` | Nothing; expects a real comms-gatekeeper subscribed to the same broker | Whatever gatekeeper needs | Validating against the production implementation |

The stub is not a second implementation of the contract — it is deliberately the *same* subject
pair and the *same* room naming, so an assertion written against it keeps holding when you flip
to `off`.

### The room name is not the cluster id

The single most transferable detail. The real gatekeeper names the room
`islandRoomName(clusterId)` = `` `${ISLAND_ROOM_PREFIX}${clusterId}` `` with
`ISLAND_ROOM_PREFIX = 'island-'`
(`comms-gatekeeper/src/adapters/livekit.ts`, used by
`comms-gatekeeper/src/logic/cluster-subscriber/rooms.ts`), and puts that in
`IslandChangedMessage.island_id`. So for cluster `C3` on an unsharded cluster the client sees
`island-C3`, not `C3`.

The stub emits `island-{clusterId}` for exactly this reason. Assertions are therefore written as
`island_id == "island-" + cluster_id`, and they transfer to the real gatekeeper unchanged.

> **Conflicts with the task spec.** D4 in
> [`docs/tasks/e2e-livekit-connstring.md`](tasks/e2e-livekit-connstring.md) writes
> `IslandId = cluster_id`, and scenario 1's assertion is phrased as "`island_id` matches the
> `cluster_id`". Against the gatekeeper source that is wrong by a prefix. The prefix wins; the
> spec text is the thing to correct.

Two further details the real gatekeeper has that the stub does not need to reproduce, but which
explain the shape of `IslandChangedMessage`: `peers` is left empty by design (unity-explorer
reads only `conn_str`), and `from_island_id` is *omitted* rather than set to `""` when there is
no previous room. Gatekeeper also shards oversized clusters into `island:{id}:{shard}` rooms;
the stub does not, and the harness does not exercise sharding.

### `livekit` mode credentials

`livekit` mode mints a real token, so it needs a LiveKit host, API key and API secret supplied
from the host environment at run time.

> **Gap, stated rather than guessed.** The exact environment variable names the test client
> reads for this are set by the bridge implementation, which is not written at the time of
> writing, so they are not documented here. For reference, comms-gatekeeper reads
> `PROD_LIVEKIT_HOST` / `PROD_LIVEKIT_API_KEY` / `PROD_LIVEKIT_API_SECRET` (and a `PREVIEW_`
> triple). Whoever lands the bridge should either match those names or record the ones chosen
> here.

None of these belong in `docker-compose.e2e.yml`: the bridge runs on the host, not in a
container. The compose file needs no credentials at all.

### `off` mode

Run comms-gatekeeper yourself, pointed at the same broker, with at least:

| Variable | Value | Note |
| --- | --- | --- |
| `CLUSTER_SUBSCRIBER_ENABLED` | `true` | Compared against the literal string `"true"`; anything else means the subscriber starts and does nothing |
| `NATS_URL` | `nats://127.0.0.1:4222` | Or the container address if you put it on the compose network |
| `NATS_SUBJECT_PREFIX` | empty | Must match Pulse's `Nats:SubjectPrefix` |
| `NATS_QUEUE_GROUP` | `comms-gatekeeper-cluster` | Default; queue-grouped so N replicas mint once, not N times |

It subscribes to `${prefix}peer.*.cluster_change` and publishes the unprefixed
`engine.peer.{wallet}.island_changed`. Its ban check and deny-list lookup both fail open, so an
unreachable Postgres degrades to "everyone allowed" rather than to silence — but a *missing*
LiveKit credential does not: `generateCredentials` is on the path to publishing, so a
credentials failure means no `island_changed` at all.

## 5. Running it

Bring the stack up and wait for all three healthchecks:

```bash
docker compose -f docker-compose.e2e.yml up --wait
```

Confirm Pulse actually connected to the broker rather than falling into stats-only mode:

```bash
curl -s http://127.0.0.1:5100/metrics | grep dcl_pulse_nats_connected
```

Run one bot holding both channels, default synthetic bridge:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=e2e-bot --comms-enabled --nats-url=nats://127.0.0.1:4222
```

Two bots, which needs two accounts — see §6 on `KR_NEW_SESSION`:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=e2e-bot --bot-count=2 --comms-enabled --nats-url=nats://127.0.0.1:4222
```

Tear down:

```bash
docker compose -f docker-compose.e2e.yml down
```

Rebuild after changing Pulse or ws-connector source:

```bash
docker compose -f docker-compose.e2e.yml up --build --wait
```

### Flags

Read from `src/DCLPulseTestClient/ClientOptions.cs`. Defaults are the ones in `FromArgs`.

| Flag | Default | Meaning |
| --- | --- | --- |
| `--comms-enabled` | off | Open a ws-connector session per bot on the bot's own wallet. Everything below is inert without it |
| `--comms-url=<url>` | `ws://127.0.0.1:5000/ws` | ws-connector endpoint |
| `--nats-url=<url>` | `nats://127.0.0.1:4222` | Broker the stub gatekeeper bridges over. Empty disables the bridge |
| `--bridge-mode=<mode>` | `synthetic` | `synthetic`, `livekit` or `off` |
| `--expect-conn-string-within=<s>` | `15` | Deadline for a conn string after a bot connects |
| `--mode=<mode>` | `bots` | `bots` drives the simulation; `bridge` runs the stub gatekeeper alone |

Two parsing details that will cost you a run each:

- **`--flag=value`, not `--flag value`.** `FromArgs` matches on the `--name=` prefix; a
  space-separated value is silently ignored and the default is used. Both the task spec and
  informal notes write `--bridge-mode synthetic`, which parses as *nothing set* and leaves you
  on the default. It happens to be the same value, which is worse, because
  `--bridge-mode livekit` also silently means `synthetic`.
- **`--comms-enabled` is the exception**, accepted both bare and as `--comms-enabled=true`.

`--mode=bridge` is useful when you want the bridge to outlive individual client runs: start it
once in its own process, then run bots with the bridge disabled in-process.

## 6. Reading a failure

The characteristic failure of this harness is **silent no-delivery**. Nothing arrives, nothing
errors, every process stays up, and every log looks like a healthy idle system. Almost every
cause below presents identically at the client. The way out is not to stare at the client log;
it is to walk the hops and find the first one where the message is absent.

### Walk the hops

Four observation points, in order. The first one that is empty is your answer.

| # | Hop | How to observe | Absent means |
| --- | --- | --- | --- |
| 1 | Pulse decided | `curl -s http://127.0.0.1:5100/metrics \| grep dcl_pulse_cluster` | Tracker not running: `Clusters:Enabled` off, or no peers with fresh snapshots |
| 2 | Pulse published | `dcl_pulse_nats_connected`, `dcl_pulse_nats_published_total` on the same endpoint | Feed disabled (`Nats:Url` empty) or the broker was unreachable at startup |
| 3 | On the broker | `nats sub "peer.*.cluster_change"` | Publish failed (`dcl_pulse_nats_publish_failed_total`) or was dropped (`dcl_pulse_nats_dropped_total`) |
| 4 | Bridged | `nats sub "engine.peer.*.island_changed"` | The bridge is not running, is on a different broker, or produced the wrong subject |

Hop 3 present and hop 4 absent isolates the bridge. Hop 4 present and the client silent isolates
ws-connector's registry — which is almost always an address or session problem, below.

Watch the two subjects from the host, in two terminals. Upstream:

```bash
nats sub -s nats://127.0.0.1:4222 "peer.*.cluster_change"
```

Downstream:

```bash
nats sub -s nats://127.0.0.1:4222 "engine.peer.*.island_changed"
```

Without installing the CLI, open a shell on the compose network and run the same two commands
inside it with `-s nats://nats:4222`:

```bash
docker run --rm -it --network pulse-e2e_default natsio/nats-box
```

The compose project is named `pulse-e2e`, so its default network is `pulse-e2e_default`;
`docker network ls` confirms it if you have overridden the project name.

Both payloads are protobuf, so the body prints as noise. That is fine — you are reading the
**subject line and the arrival**, which is exactly what distinguishes the hops.

Ask the broker who is actually subscribed to what. This is the fastest way to settle a prefix
argument, because it shows the literal subscription strings:

```bash
curl -s "http://127.0.0.1:8222/subsz?subs=1"
```

### The causes, and how to tell them apart

**Address casing.** ws-connector's registry keys on `normalizeAddress(address)`, which is
`address.toLowerCase()`, and the welcome message returns that lowercased address as `peer_id`.
Pulse lowercases the wallet before building the subject, for the same reason
(clustering-on-aoi.md §3.6). gatekeeper lowercases the token it extracts from the subject. Every
hop agrees — until something introduces a checksum-cased address, at which point
`engine.peer.0xAbC….island_changed` is published, ws-connector's wildcard subscription matches
it, `peersRegistry.getPeerWs('0xAbC…')` returns nothing, and the message is dropped with no log
line at all. *Tell it apart:* hop 4 shows a message whose subject contains uppercase hex.
Compare the subject against the `peer_id` in the welcome.

**The `engine.` prefix.** Publishing `peer.{addr}.island_changed` instead of
`engine.peer.{addr}.island_changed` gives you hop 3 and hop 4 both looking populated if you are
subscribed with a loose wildcard, and nothing at the client. Conversely, subscribing to
`engine.peer.*.cluster_change` gets you a bridge that never fires. *Tell it apart:*
`/subsz?subs=1` lists the literal strings; the pair should read `peer.*.cluster_change` on the
bridge side and `engine.peer.*.island_changed` on ws-connector's. If a `Nats:SubjectPrefix` is
set on Pulse, the first gains that prefix and the second must not.

**Half a session.** A wallet with a Pulse session but no ws-connector session gets an
`island_changed` that nobody forwards — harmless, invisible. A wallet with a ws-connector
session but no Pulse session never appears in a cluster pass, so nothing is ever published for
it. This is a documented consequence of the split (clustering-on-aoi.md §3.6). In this harness
it is usually a partial failure: `--comms-enabled` was omitted, or the comms channel failed
while the Pulse channel stayed up, which is by design a separate failure domain. *Tell it
apart:* `curl -s http://127.0.0.1:5000/status` reports ws-connector's `userCount`; compare it
against the bot count. Zero with bots running means no comms sessions at all.

**`idleTimeout: 90`.** ws-connector's `/ws` socket is configured with a 90 s idle timeout. A
client that completes the handshake and then stops sending is closed. A bot that is paused in a
debugger, or whose heartbeat pump died while the rest of the process lived, disappears from the
registry, and from that moment `island_changed` messages for it are dropped silently. Heartbeats
here are keepalive only — Pulse derives position itself, so the heartbeat position is no longer
the clustering input. *Tell it apart:* `userCount` on `/status` drops without the bot exiting;
ws-connector logs `Websocket closed`.

**`KR_NEW_SESSION`.** A second handshake on a wallet that already has a session kicks the
*previous* socket with `KickedReason.KR_NEW_SESSION` and closes it. The new session is the one
that survives. Two bots therefore need two accounts — `--bot-count=2` derives `<account>-0` and
`<account>-1`, which is correct; reusing one account name across two processes is not. Note also
that a *banned* wallet is rejected with the same `KR_NEW_SESSION` code, because the protocol has
no `KR_BANNED`, so the reason alone does not distinguish "someone reconnected as me" from "I am
banned". *Tell it apart:* the kicked client is the one that was working a moment ago; the ban
case never gets a working session at all.

**Timing.** `Clusters:DwellPasses` is 3 and `Clusters:PassIntervalMs` is 1000, so a reassignment
is published only after three consecutive passes agree — seconds after the movement that caused
it, not milliseconds. Two bypasses skip the debounce: a realm change, and a best-effort teleport
check. Assert on **ordering and count**, never on wall-clock precision:
`--expect-conn-string-within` is a generous deadline for "did it arrive at all", not a latency
measurement. A test that fails intermittently at 15 s is not measuring timing, it is measuring
your CI runner. Two consequences worth asserting on directly: a bot idling inside one cluster
must produce *no* repeat assignment (the debounce and the outbox's latest-wins), and a bot
walking from one cluster to another must produce *exactly one*.

**The broker was not there at startup.** Pulse's feed is publish-only, config-gated and
fail-soft: an empty or unreachable `Nats:Url` leaves clustering running and publishes nothing,
by design, because a broker outage must never stall the simulation. It also means a typo in the
URL is indistinguishable from a healthy idle server unless you look.
`dcl_pulse_nats_connected` and the startup log line are the only signals. This is the reason for
`--wait` and the healthchecks; it is also the reason `Metrics__Type` is pinned to `Prometheus`
in the compose file, so that gauge is actually scrapeable.

**Empty `cluster_id`.** Protobuf decodes an absent `cluster_id` as `""`, and unguarded that
dumps every affected peer into one shared `island-` room. The real gatekeeper checks for it and
logs `empty clusterId`. A stub that does not check will produce a run where every bot agrees on
the same island for the wrong reason — which passes scenario 2 and fails scenario 3. *Tell it
apart:* the `island_id` is exactly `island-`.

### Logs worth tailing

```bash
docker compose -f docker-compose.e2e.yml logs -f ws-connector
```

ws-connector's main logger is created without a config component, which leaves it at level
`ALL` — its handshake tracing (`Generating challenge`, `Authentication successful`,
`publishing island change for …`) is on by default and is the best per-wallet trace available.
Its NATS logger is separately pinned to `WARN`, so broker chatter does not drown it.

```bash
docker compose -f docker-compose.e2e.yml logs -f pulse
```

The compose file raises Pulse's own categories to `Information` while leaving
`NATS.Client.Core` at the `Warning` floor `appsettings.json` sets — below that, the client
re-dumps server info on every reconnect and a flapping broker floods the log.

## 7. Credential hygiene

The default path needs no secret of any kind. That is a property to preserve, not a coincidence:
it is what lets these scenarios run on every PR.

- **Nothing secret in the compose file.** `docker-compose.e2e.yml` contains no credential and no
  placeholder for one. The only value that could ever carry one is `E2E_NATS_URL`, which is read
  from the host environment; its committed default is the local broker, which needs no auth.
- **Nothing secret in source, config or fixtures.** `livekit` bridge mode takes its LiveKit host,
  key and secret from the host environment at run time. They are never written to a fixture, a
  committed config file, or a recorded expectation.
- **`access_token` is redacted in output.** A conn string is
  `livekit:<url>?access_token=<jwt>`, and the JWT is a live credential for the duration of its
  validity. Anything that prints a conn string — log lines, assertion failure messages, scenario
  reports — must redact the token, not the URL. In `synthetic` mode the token is a fixed
  non-routable placeholder and there is nothing to leak, which is exactly why `synthetic` is the
  default rather than an option.
- **Private keys stay in MetaForge.** The test client shells out to `metaforge account sign` and
  handles only the resulting auth chain. It never reads a key.

Signing is the one place where an unredacted value is *supposed* to appear: the auth chain is
public by construction — a signature over `dcl-<hex>` is what you send over the wire. Do not
confuse it with a secret and redact it; it is the thing being tested.
