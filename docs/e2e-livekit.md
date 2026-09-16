# End-to-end: the LiveKit conn-string path

How to run the harness that checks Pulse's cluster feed all the way to the LiveKit connection
string a client would actually receive, on a local machine and in CI.

> **Status.** The full chain has been run end to end against a live stack — five bots on five
> wallets, one cluster, five conn strings delivered to the client. What does **not** exist yet is
> the scenario runner: the numbered regression scenarios in
> [`docs/tasks/e2e-livekit-connstring.md`](tasks/e2e-livekit-connstring.md) are still the
> intended contract rather than something you can run, so a baseline today means reading the
> output yourself. Places where the spec and the sources disagree are called out inline.

## 0. Verified baseline

Recorded from an actual run, as the shape to compare against. Five bots, `--spawn-radius=2`,
realm `main`:

```
CLUSTER_CHANGE peer.0xbc7c…9efc.cluster_change cluster=C3 realm=main          (x5, one per wallet)
HEARTBEAT      peer.0x655d…bd75.heartbeat pos=(-106.1, 0.0, 3.9)              (x5, keepalive only)
ISLAND_CHANGED engine.peer.0xbc7c…9efc.island_changed island=island-C3 from=none
                                        conn=livekit:wss://…?access_token=<redacted>   (x5)
[ws-connector] Island island-C3 (from none), 0 peer(s), connStr …              (x5, at the client)
```

Five of each, one `island_id` across all five, and the wallet set on `cluster_change` identical
to the set ws-connector welcomed. `island-C3` rather than `C3` — the prefix is real (§4).
`from=none` on a first assignment and `0 peer(s)` are both by design.

Note `C3`, not `C1`: the cluster counter is monotonic per Pulse process, so a restart of the
*bots* against a long-lived server keeps incrementing. That is the ID-reuse gap in
[clustering-on-aoi.md §7](clustering-on-aoi.md), visible in ordinary use.

**Heartbeats are not what triggers the mint here.** They carry real positions and ws-connector
republishes them to `peer.{addr}.heartbeat`, but in *this* stack nothing subscribes —
archipelago-core was removed from the repo (`archipelago-workers@ad3d007`). The mint is driven
by Pulse's `cluster_change`. That is the step that changed, and it is not yet true of what is
deployed.

### 0.1 The same client against deployed zone

The client is agnostic about which service answers: it sends heartbeats and reads
`islandChanged`, both over the one WebSocket. Pointing it at the **current, un-migrated** zone
infrastructure — zone Pulse for the game protocol, the deployed archipelago for comms —
works with no code changes and no local services:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=loadtest --bot-count=5 \
  --ip=pulse-server.decentraland.zone --port=7777 --comms-enabled \
  --comms-url=archipelago:archipelago:wss://peer.decentraland.zone/archipelago/ws
```

The `--comms-url` is a realm's `comms.adapter` pasted verbatim; `AdapterAddress` reduces it to
`wss://peer.decentraland.zone/archipelago/ws`.

> **This host is realm `artemis`, not the realm the explorer uses.** It is fine for exercising the
> harness, and wrong for reproducing what a client sees — see "Resolve the adapter from the realm
> the client uses" in §4. To match the explorer, take the adapter from
> `realm-provider-ea.decentraland.zone/main/about` instead.

Observed: 5/5 welcomed, 5/5 islands delivered, real tokens against `wss://dcl.livekit.cloud`,
nothing unredacted in the log. **In this path the mint *is* heartbeat-driven** — there is no
`cluster_change` involved at all.

**The two producers do not agree on shape, and assertions must not assume they do:**

| | Deployed archipelago (today) | comms-gatekeeper (after migration) |
| --- | --- | --- |
| `island_id` | `peer-zone1` — no prefix | `island-C3` — `island-` prefix, cluster id |
| `peers` | populated (5 peers seen) | empty by design |
| Trigger | heartbeat position over WS | Pulse `peer.{addr}.cluster_change` |
| Reassignment | islands *merge* — `from=peer-zone5`, `…4`, `…3`, `…2` all converging on `peer-zone1` | new cluster id per assignment |

So an assertion written as `island_id == "island-" + cluster_id` is **gatekeeper-specific** and
fails against current infra. Scenario assertions that must hold across the migration should key
on *relationships* — same island vs different island, count and order of reassignments — never
on the id's spelling.

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
is not. Do not "fix" either side to match the other; the two subjects are load-bearing exactly
as written, and a mismatch means nothing arrives.

Pulse publishes its subjects literally — it has no prefix knob. See
[clustering-on-aoi.md §3.6](clustering-on-aoi.md) for the full feed description and the three
subjects Pulse emits.

### Two strengths of claim, and which one you asked for

| | Default | With `--join-livekit` | unity-explorer |
| --- | --- | --- | --- |
| Success is | a conn string arrived and its claims are consistent | `room.ConnectionState == ConnConnected` | `ConnectionState == LKConnectionState.ConnConnected` |
| Reached by | reading `islandChanged` off the WebSocket | `Room.ConnectAsync` — a real LiveKit session | `TryConnectToRoomAsync` — the same |

**Without the flag, a token that is well-formed but rejected by LiveKit** — bad signature, revoked
key, room policy, expired in transit — **reads as success here and as failure in Unity.** So never
report a default run as "comms works"; report it as "a usable-looking token was delivered".

With the flag the harness makes the explorer's claim, on the same enum
(`LiveKit.Proto.ConnectionState`), and reports the room the **server** granted rather than the one
the token asked for — a stronger check than the claim, since the two can disagree. Use it whenever
the question is "does comms actually work" rather than "did something mint".

Two things that are *not* the difference, checked so they are not re-investigated:

- **Parsing is equivalent.** The explorer uses `new Uri(connStr)` +
  `HttpUtility.ParseQueryString(uri.Query)["access_token"]`
  (`Connections/Credentials/ConnectionStringCredentials.cs`) and strips `livekit:` off the URL by
  splitting on `?`. Verified against zone's exact shape — `livekit:wss://host?access_token=…`,
  with and without a trailing slash, with an extra query parameter — and .NET's `Uri` resolves the
  query for the `livekit:` scheme in all three. It agrees with `LiveKitToken`'s substring
  extraction.
- **The identity and room are checked.** `[livekit]` prints `room=`, `identity=`, expiry, and
  `publish=`, and flags `MISMATCH` when the room is not the island id or the identity is not the
  bot's wallet.

**Watch the TTL against the explorer's retry policy.** Zone mints tokens that expire in ~5
minutes. The explorer retries a *cached* conn string with `RECONNECT_BACKOFF = 5s` and only forces
a fresh handshake after `MAX_RECONNECT_ATTEMPTS_BEFORE_FRESH_HANDSHAKE = 3`
(`Archipelago/Rooms/ArchipelagoIslandRoom.cs`) — its own comment names the failure as "its token
expired during a long outage". A short TTL plus any stall is a real way for a token that this
harness called valid to be dead by the time the client uses it. For contrast, MetaForge's MoB
mints 24-hour tokens.

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

  **As of this writing that command is unreleased**, so an installed MetaForge does not have it —
  it exits 127 with `Unknown command 'sign'`. Until it ships, build the CLI and put its output
  first on PATH:

  ```bash
  dotnet build ../MetaForge/MetaForgeCLI/MetaForgeCLI.csproj
  ```

  then prefix runs with `PATH="../MetaForge/MetaForgeCLI/bin/Debug/net10.0/<rid>:$PATH"`.

  The failure mode with a stale binary is worth recognising: the test client surfaces the CLI's
  non-zero exit and its stderr, so it reads as *"unknown command"*, not as a rejected signature.
  The e2e fixture probes for it in `OneTimeSetUp` and fails before opening a socket, because
  otherwise the run dies mid-handshake against a real server and looks like a protocol fault.
  `metaforge account chain` is the older, different thing — it builds the signed-fetch shape
  (`method:path:timestamp:metadata`) and cannot sign a `dcl-<hex>` challenge verbatim.

  The harness also needs a MetaForge whose `account sign` and `account chain` reuse one persisted
  identity per account, because the backend session key is the auth chain's ephemeral address and
  the Pulse handshake and the ws-connector challenge must carry the same one. A MetaForge that
  mints a fresh identity per call makes the session-addressed `island_changed` miss the bot's
  socket — symptom: bots log `Welcome received` but never an `[ws-connector] Island` line, and the
  ws-connector metric `dcl_ws_connector_island_changed_no_session_socket_total` and the gatekeeper
  metric `dcl_gatekeeper_cluster_reannounce_skipped_other_session_total` count up. Note that the
  persisted identity still has the class-default validity window, so a soak run longer than that
  window will rotate a bot's ephemeral mid-run the same way a never-persisted one always did —
  this only fixes the two-handshakes-per-bot mismatch, not long-run rotation.
- **`Clusters:Enabled` must be on.** It ships `true` in `appsettings.json`, and the compose file
  pins `Clusters__Enabled=true` anyway so the harness does not depend on that default holding.
  With it off, the tracker never runs and nothing is ever published — silently.
- Optional but worth having: the [`nats` CLI](https://github.com/nats-io/natscli), for watching
  subjects directly. Section 6 gives a container-based alternative if you would rather not
  install it.

Outbound internet is nice to have. ws-connector fetches
`https://config.decentraland.org/denylist.json` at handshake time (cached 5 minutes) and fails
open on error, logging it — so the harness works offline, but with an error line per cache miss.

## 4. The conn-string source

Between `peer.{addr}.cluster_change` and `engine.peer.{addr}.island_changed` sits
comms-gatekeeper, and **the harness expects the real one**. The test client is a client: it
speaks Pulse's protocol and ws-connector's, and it holds no broker connection of its own. It
does not mint conn strings, does not subscribe to `cluster_change`, and cannot stand in for a
service. Nothing arrives on the comms channel unless something else is doing the translation.

An earlier revision carried a stub gatekeeper inside the test client behind `--mode=bridge`,
publishing to the broker itself. That was removed: a client that publishes on a server's subject
is not a client, and a harness whose observations come from its own writes proves less than it
appears to. If you want that code back as a standalone tool, it is in git history at `2f9233e`
under `src/DCLPulseTestClient/Bridge/`.

**No real LiveKit credentials are needed, and this surprised us.** `generateCredentials`
(`comms-gatekeeper/src/adapters/livekit.ts`) constructs an `AccessToken` and calls `addGrant` —
it signs a JWT offline and never contacts the LiveKit host. So gatekeeper mints and publishes a
well-formed conn string with *any* key/secret pair. The token will not open a room, which is
irrelevant: the assertion that matters is that a valid conn string arrived for the right wallet,
not that LiveKit accepted it.

That keeps the harness credential-free and CI-able, satisfying the task spec's acceptance
criterion 4 through the real gatekeeper rather than a stub. Only `--join-livekit` (D6, out of
scope) would need a real host.

Postgres is still required to start, but its ban check and deny-list lookup **fail open**, so
neither needs to be populated.

### Resolve the adapter from the realm the client uses — not from `peer.<domain>`

**The single most expensive mistake available here.** A domain hosts several realms on separate
stacks at different points in the migration, and `peer.<domain>` is usually *not* the one the
explorer is on. Testing the wrong one produces a confident green result about a stack nobody uses.

unity-explorer resolves Genesis from `realm-provider-ea.<domain>/main`
(`DecentralandUrlsSource.cs`, `DecentralandUrl.Genesis`), then `RealmController.ResolveCommsAdapter`
takes `about.comms.adapter` — unless the `COMMS_ADAPTER` app arg overrides it — and
`RefinedAdapterAddresses` strips the `archipelago:archipelago:` prefix. So the adapter a client
dials is:

```bash
curl -s https://realm-provider-ea.decentraland.zone/main/about | jq '{healthy, acceptingUsers, realm: .configurations.realmName, comms}'
```

Observed on 2026-09-02, the two zone realms had diverged completely:

| | `realm-provider-ea/main` (what the explorer uses) | `peer.decentraland.zone` |
| --- | --- | --- |
| realmName | `main` | `artemis` |
| adapter | `wss://archipelago-ea-ws-connector.decentraland.zone/ws` | `wss://peer.decentraland.zone/archipelago/ws` |
| comms build | `081ac634` — `chore/decommission-archipelago-core` | `e320cd00` — June |
| core in that build | **absent** | **present** |
| `healthy` / `acceptingUsers` | `false` / `false` | `true` / `true` |
| A bot there | welcome, then silence forever | island + a token that joins |

Both stacks answer the handshake identically, so the failure is invisible until you notice that
nothing follows the welcome. `healthy: false` and `acceptingUsers: false` on the realm are the
cheap tell — check them before blaming a client.

The refinement itself is not a difference: `RefinedAdapterAddresses.AdapterUrlAsync` and this
harness's `AdapterAddress.Refine` were traced against each other and agree on every real adapter
shape. Only the *input realm* differed.

### Who mints the token

Whoever assigns the island. Minting is not a separate service — it is a step inside island
assignment, done by the assigner with a LiveKit API key it holds directly. Two implementations of
the same step exist:

- **archipelago-core** (`core/src/components.ts`, `createLivekitTransport`) reads
  `LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET` / `LIVEKIT_HOST` from its own env, vendors a copy of
  LiveKit's signer (`core/src/logic/livekit.ts`), and calls `mintToken(userId, roomId)` from
  `getConnectionStrings(userIds, roomId)` as it forms an island.
- **comms-gatekeeper** (`src/adapters/livekit.ts`, `generateCredentials`) does the same thing for
  the cluster subscriber, at `component.ts` step "Mint a LiveKit token for the cluster's island
  room" — `livekit.getIslandRoomName(clusterId)` then `generateCredentials(wallet, room, …)`.

Both mint with `ttl: 5 * 60`, `roomJoin: true`, `canPublish: true`, `room = the island id`, and
`identity = the wallet`. So the token's claims tell you the *island* and the *wallet*, and the
5-minute TTL, but they **do not identify which service signed it** — the shape is house
convention, not a fingerprint. The only claim that differs in practice is the API key (`iss`),
which is per-deployment rather than per-service.

Two consequences worth holding on to:

- **Killing the assigner kills the tokens.** There is no standalone minter to keep working. So a
  token arriving *is* evidence that some assigner is alive — which is what makes the negative
  check meaningful.
- **The 5-minute TTL is theirs, not ours.** Nothing in Pulse or in this harness shortens it, and
  it is the same on both paths.

### Which producer is live, and how to tell

Three things can put an `island_changed` on the wire, and they are told apart by the shape of what
arrives — not by asking a status endpoint.

Read it off the **room name** in the `[livekit]` line. The two island producers do not collide,
because gatekeeper prefixes and core does not (`ISLAND_ROOM_PREFIX = 'island-'`,
`src/adapters/livekit.ts`):

| `room=` | Producer | Requires |
| --- | --- | --- |
| `island-C3` | **comms-gatekeeper**'s cluster subscriber | `CLUSTER_SUBSCRIBER_ENABLED=true`, NATS configured, **and** Pulse publishing `peer.{addr}.cluster_change` |
| `peer-zone4` — a bare island name, unprefixed | the old **archipelago-core** | the `archipelago-ea-core` service still running; it is deployed separately from ws-connector and nothing in CI removes it |
| `<COMMS_ROOM_PREFIX>…`, or `…realm:sceneId` | gatekeeper's **scene-adapter** path — unrelated to clustering | nothing; it is pull-based and always on |
| nothing arrives, `Welcome received` still logged | no island producer | — |

Corroborating signals, weaker than the room name: core populates `peers` and merges islands
(`from=peer-zone5`), while the cluster subscriber leaves `peers` empty and issues one room per
cluster.

**Gatekeeper being deployed does not mean islands work.** It mints on eight independent paths and
only one of them involves Pulse:

| Path | Trigger | Room | Needs Pulse |
| --- | --- | --- | --- |
| `POST /get-scene-adapter`, `/get-server-scene-adapter` | HTTP from client, scene or authoritative server | scene or world room | no |
| `GET /private-messages/token` | HTTP from explorer | private-messages room | no |
| private + community voice chat | HTTP from the social service | `voice-chat-private-…`, `voice-chat-community…` | no |
| cast streamer / watcher / presentation-bot | HTTP from the cast app | scene room | no |
| cluster subscriber | NATS `peer.*.cluster_change` | `island-{clusterId}` | **yes** |

The non-Pulse paths are **pull, not push**, and their rooms are statically derivable — a scene room
is a pure function of `(realm, sceneId)`, both of which the caller already knows, so it asks and
gets a token. Nothing has to work out who is standing near whom. The island path is the only one
that is pushed and the only one whose room name the client cannot derive, which is exactly the part
Pulse supplies. With no feed, `start()` logs `disabled (CLUSTER_SUBSCRIBER_ENABLED is not "true")`
or `enabled but NATS is not configured, staying idle` and mints nothing there — while serving every
other path normally.

**No status endpoint answers this, because core has none that is reachable.** `archipelago-core`
is its own service (`archipelago-ea-core`), separate from ws-connector, and it publishes to NATS
rather than serving clients. The two endpoints that look like they should answer do not:

- `/archipelago/status` is served by **ws-connector**
  (`ws-connector/src/controllers/handlers/status-handler.ts`), so its `commitHash` dates
  ws-connector's build and says nothing about whether core is running beside it.
- `/core-status` is served by the **stats** service and reflects whether stats still sees
  `engine.discovery`. Stats can be rolled forward independently, in which case
  `{"healthy":false,"userCount":0}` is an artifact of stats being decommissioned, not evidence
  about the service minting for your bots. This misled a whole investigation once.

**So identify the producer from what arrives, using the room name above.** A bare unprefixed island
id is core, and it is the only positive proof that core is alive.

The commit hashes are still worth collecting — they date the surrounding stack and reveal an
inconsistent rollout — just not as an answer about core:

```bash
curl -s https://peer.decentraland.zone/about | jq .comms   # ws-connector's build + the adapter clients use
curl -s https://comms-gatekeeper.decentraland.zone/status  # gatekeeper's build

cd ../archipelago-workers && git fetch --all
git log -1 --format='%ad %s' --date=short <commitHash>
```

Observed on 2026-09: ws-connector on `e320cd00` (2026-06-18), stats on `081ac634`
(`chore/decommission-archipelago-core`), gatekeeper on `feat/cluster-livekit-subscriber` — three
services, three different points in the migration, and core still minting behind all of them.

**Nothing redeploys core, so it will not go away on its own.** The removal commit deleted core's
jobs from `docker-next.yml`, `docker-release.yml` and `manual-deploy.yml`, so CI no longer touches
that service and the running task keeps its last image indefinitely. Retiring it is an infra
action, not a deploy — see `archipelago-workers/docs/core-decommission-runbook.md`, precondition 3.
Until then, expect core to keep assigning islands regardless of what is deployed elsewhere, and
expect **two** `island_changed` messages per wallet during any window where Pulse's feed and
gatekeeper's subscriber are both live — the runbook calls this the dual-publish flap.

Also worth knowing: ws-connector's own comment says "the publishers are out of this repo —
comms-gatekeeper mints and publishes `island_changed`" (`ws-connector/src/logic/nats.ts`). That
describes the *target* architecture. It is not evidence about what is running today.

### A negative check needs a positive precondition

"No token was minted" is only meaningful if the run got far enough to have received one. The
precondition is the line `[ws-connector] Welcome received, peer id 0x…`: the handshake completed
and the wallet is in ws-connector's registry, so a mint for that wallet would have been delivered.
Without that line, an absent token says nothing — it could be a failed handshake, the wrong
adapter, or a kicked session.

`--expect-conn-string-within` is **parsed and not acted on**, so the client never fails on a
missing conn string. A negative result is something you read from the log, not an exit code.

### The room name is not the cluster id

The single most transferable detail. The real gatekeeper names the room
`islandRoomName(clusterId)` = `` `${ISLAND_ROOM_PREFIX}${clusterId}` `` with
`ISLAND_ROOM_PREFIX = 'island-'`
(`comms-gatekeeper/src/adapters/livekit.ts`, used by
`comms-gatekeeper/src/logic/cluster-subscriber/rooms.ts`), and puts that in
`IslandChangedMessage.island_id`. So for cluster `C3` on an unsharded cluster the client sees
`island-C3`, not `C3`.

Assertions are therefore written as `island_id == "island-" + cluster_id`. Worth knowing even
though the harness now only ever talks to the real gatekeeper: it is the detail that makes
`island_id` look wrong the first time you compare it against what Pulse published.

> **Conflicts with the task spec.** D4 in
> [`docs/tasks/e2e-livekit-connstring.md`](tasks/e2e-livekit-connstring.md) writes
> `IslandId = cluster_id`, and scenario 1's assertion is phrased as "`island_id` matches the
> `cluster_id`". Against the gatekeeper source that is wrong by a prefix. The prefix wins; the
> spec text is the thing to correct.

Two further details that explain the shape of `IslandChangedMessage`: `peers` is left empty by
design (unity-explorer reads only `conn_str`), and `from_island_id` is *omitted* rather than set
to `""` when there is no previous room.

### Running comms-gatekeeper

It is not in `docker-compose.e2e.yml` — it lives in its own repo, and its config surface is
large enough that duplicating it here would rot. Bring up its Postgres first
(`comms-gatekeeper/docker-compose.yml`, which also starts a NATS this harness can share), then
run it from its own checkout against the same broker.

Everything else it needs is already in its committed `.env.default`; only these have to be
overridden, and process environment beats `.env.default`:

| Variable | Value | Note |
| --- | --- | --- |
| `CLUSTER_SUBSCRIBER_ENABLED` | `true` | Compared against the literal string `"true"`; anything else means it starts and does nothing |
| `NATS_URL` | `nats://127.0.0.1:4222` | The same broker Pulse publishes to |
| `NATS_SUBJECT_PREFIX` | empty | Pulse publishes literal subjects, so the subscription must be unprefixed |
| `COMMS_GATEKEEPER_AUTH_TOKEN` | any placeholder | Required at startup; only guards its inbound HTTP API, which this harness never calls |
| `PROD_LIVEKIT_HOST` / `_API_KEY` / `_API_SECRET` | any placeholder | Minting is offline JWT signing — see above. `PREVIEW_` triple must also be set |

This is the exact set that was verified working; anything missing fails fast at startup naming
the variable, so there is nothing to guess.

It subscribes to `${prefix}peer.*.cluster_change` and publishes the unprefixed
`engine.peer.{wallet}.island_changed`. Its ban check and deny-list lookup both fail open, so an
unreachable Postgres degrades to "everyone allowed" rather than to silence. Wait for
`Cluster subscriber started` in its log before running bots — the HTTP server logs `Listening`
about a second earlier, and that line does **not** mean the subscription is up.

## 5. Running it

Bring the stack up and wait for all three healthchecks:

```bash
docker compose -f docker-compose.e2e.yml up --wait
```

> **If you run Pulse on the host instead of in compose**, its health server takes port **5000**,
> which is the port ws-connector defaults to and the port `--comms-url` points at. ws-connector
> then dies with `Failed to listen on 0.0.0.0:5000`. Give it another port and match `--comms-url`
> to it — `HTTP_SERVER_PORT=5010` and `--comms-url=ws://127.0.0.1:5010/ws`. Compose sidesteps this
> by publishing Pulse's HTTP on 5100. Note also that a host-run Pulse answers `/metrics` only on
> `localhost`; `127.0.0.1` returns HTTP 400.

Confirm Pulse actually connected to the broker rather than falling into stats-only mode:

```bash
curl -s http://127.0.0.1:5100/metrics | grep dcl_pulse_nats_connected
```

Start comms-gatekeeper against the same broker (§4) — without it the bots connect to
ws-connector and then sit there receiving nothing, which looks identical to a healthy idle run.

Run one bot holding both channels:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=e2e-bot --comms-enabled
```

Two bots, which needs two accounts — see §6 on `KR_NEW_SESSION`:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=e2e-bot --bot-count=2 --comms-enabled
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
| `--comms-url=<url>` | `ws://127.0.0.1:5000/ws` | ws-connector endpoint. Also accepts a realm's raw adapter string (`archipelago:archipelago:wss://host/ws`), so a value copied from `/about` works unchanged |
| `--join-livekit` | off | Also **join** each room the bot is given a token for, and report `ConnectionState`. Opens a real WebRTC session per bot; accepted bare or as `--join-livekit=true` |
| `--expect-conn-string-within=<s>` | `15` | **Parsed but not yet acted on.** Reserved for the regression scenarios |

There is deliberately no broker flag. The client never connects to NATS.

Two parsing details that will cost you a run each:

- **`--flag=value`, not `--flag value`.** `FromArgs` matches on the `--name=` prefix; a
  space-separated value is silently ignored and the default is used — so `--comms-url ws://…`
  leaves you pointed at the default endpoint with no warning.
- **`--comms-enabled` and `--join-livekit` are the exceptions**, accepted both bare and as `--comms-enabled=true`.

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
| 4 | Gatekeeper minted | `nats sub "engine.peer.*.island_changed"` | comms-gatekeeper is not running, is on a different broker, has `CLUSTER_SUBSCRIBER_ENABLED` unset, or could not mint (missing LiveKit credential) |

Hop 3 present and hop 4 absent isolates comms-gatekeeper. Hop 4 present and the client silent
isolates ws-connector's registry — which is almost always an address or session problem, below.

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
`engine.peer.*.cluster_change` gets you a gatekeeper that never fires. *Tell it apart:*
`/subsz?subs=1` lists the literal strings; the pair should read `peer.*.cluster_change` on
gatekeeper's side and `engine.peer.*.island_changed` on ws-connector's.

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
logs `empty clusterId` and skips it. Were that guard ever to regress, the run would have every
bot agreeing on the same island for the wrong reason — passing scenario 2 and failing scenario
3. *Tell it apart:* the `island_id` is exactly `island-`.

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
- **Nothing secret in source, config or fixtures.** The LiveKit host, key and secret belong to
  comms-gatekeeper and are supplied to it from the host environment at run time. The test client
  never holds them. They are never written to a fixture, a committed config file, or a recorded
  expectation.
- **`access_token` is redacted in output.** A conn string is
  `livekit:<url>?access_token=<jwt>`, and the JWT is a live credential for the duration of its
  validity. Anything that prints a conn string — log lines, assertion failure messages, scenario
  reports — must redact the token, not the URL. `ConnStringRedaction` is the single choke point,
  and `ConnStringListener` runs every observed conn string through it before logging.
- **Private keys stay in MetaForge.** The test client shells out to `metaforge account sign` and
  handles only the resulting auth chain. It never reads a key.

Signing is the one place where an unredacted value is *supposed* to appear: the auth chain is
public by construction — a signature over `dcl-<hex>` is what you send over the wire. Do not
confuse it with a secret and redact it; it is the thing being tested.

## 8. Takeover scenario

One wallet, two identities, both live: client A holds a session (Pulse + ws-connector +
LiveKit) and client B connects on the *same wallet* with a *different* signed-in identity.
Everything above proves the conn-string path for one identity at a time; this scenario proves
what happens when a second identity shows up for a wallet that already has one — the case where
dev has been observed to half-work: Pulse evicts A but A's LiveKit session stays alive, and B
never gets a room.

`scripts/e2e/livekit-takeover.sh` (+ `scripts/e2e/nats-watch.js`) runs it against the real Pulse,
the real ws-connector (both from `docker-compose.e2e.yml`, same as above) and the real
comms-gatekeeper (built and run from source on the host, unlike sections 1-7 where it is assumed
already running) plus a disposable local `livekit-server:latest --dev` container — no cloud
LiveKit project needed, since minting is offline JWT signing (§4) and `--dev` answers the
RoomService calls the scenario itself makes to inspect participants.

### What "identity" means here

Two identities on one wallet is two different **ephemeral keys** delegated by the same wallet
signature, each with its own auth chain — not two wallets, and not two processes racing the same
key. `DCLPulseTestClient` gets a second identity through `--device=<label>`, threaded to every
MetaForge call (`Auth/MetaForgeAuthenticator.cs`): `--device=a` and `--device=b` each resolve,
mint and persist their own ephemeral key under the one account (`metaforge`'s
`AccountRecord.DeviceIdentities`, keyed by device label — see the MetaForge repo's own history
for `AccountIdentity`/`AccountStore`). Two processes running `--account=wtval --device=a` reuse
the same ephemeral; `--device=b` is a different one. Neither call needs a wallet signature after
the first mint — MetaForge's persisted-identity locking (§3) applies per device, not just per
account.

### The six legs

In order, because each depends on the previous one having actually happened — the first leg that
has no evidence is where the chain actually breaks, regardless of which legs after it also fail:

| # | What must be true | Where it shows up |
| --- | --- | --- |
| L1 | Pulse disconnects A with reason `DUPLICATE_SESSION`, and A does not reconnect | `[peer N] disconnected by server: DUPLICATE_SESSION (4).` in A's log (`ENetTransport.HandleEvent`); no second `Connecting to` line follows — the test client never retries a dropped Pulse session, matching `PulseMultiplayerBus.Disconnects.cs` in unity-explorer (reconnection is refused for every reason except the handful it lists, and `DUPLICATE_SESSION` is not one of them) |
| L2 | Pulse publishes `peer.{W}.cluster_change` naming `session=S_B`, `displaced_session=S_A`, `displaced_cluster_id=`A's cluster | decoded by `nats-watch.js` off the wire (`PeerClusterChange` fields 3/4/5 — see `clustering-on-aoi.md`) |
| L3 | gatekeeper evicts A's participant from `island-{displaced_cluster_id}`, mints for W, publishes `engine.peer.{W}.island_changed.{S_B}` | `dcl_gatekeeper_cluster_takeover_evicted_total` or `_absent_total` +1 with `_failed_total` flat, on `/metrics`; the session-addressed subject on the wire (`src/logic/cluster-subscriber/component.ts`, `evictDisplacedSession`) |
| L4 | ws-connector delivers to B's socket only | B's log: `[ws-connector] Island …` then `[livekit] … joined` |
| L5 | A's OLD room holds no participant for W afterwards; A IS in the parking room `island-parked-<16 hex of S_A>` afterwards (exactly one participant, identity W); B's room holds exactly one participant for W, with a `sid` different from A's original one | A's log: `[livekit] [account] LEFT room '…': disconnected, reason=…`; RoomService `ListParticipants` snapshots of A's old room, the parking room and B's room, taken before B connects and again after the wait |
| L6 | A's ws-connector socket receives exactly ONE further `Island` line after L1, and it names the parking room | A's log: `[ws-connector] Island island-parked-… …`, then `[livekit] [account] SWITCHED room '…' -> 'island-parked-…'` (`Comms/LiveKitJoiner.cs`); the gatekeeper's `dcl_gatekeeper_cluster_takeover_parked_total` +1 |

L5/L6 assume the gatekeeper's takeover-**parking** behaviour (see "Variants" below and the plan's
F4): the displaced session is hard-evicted *and* handed a private `island-parked-<S_A>` room to
follow, rather than being left with nothing to re-join. Against a pre-parking gatekeeper — plain
eviction only — both legs FAIL by design: there is no further `Island` line for A at all (L6) and
no parked-room snapshot to find A in (L5). That is the intended signal that the assertions bite,
not a harness bug — see "Reading a failure" below.

**L5 does not assume B lands in A's own room.** Pulse's cluster tracker only groups peers it is
currently tracking, and A's eviction can be observed by the tracker before its next pass runs for
B — in every local run so far B formed a new, separate cluster instead of joining A's. The
script handles both shapes for A's *old* room vs. B's room: if B's room differs from A's, A's old
room must end up with zero participants for the wallet (not just "someone else moved in") and B's
own room must hold exactly one; if B lands in A's old room, that one room must show exactly one
participant (B's) with a `sid` different from A's original one. Either way, the *parking* room is a
separate, third room, always checked on its own — A is never expected back in its old room. Read
`island_id == "island-" + cluster_id` per wallet-visit, not "same room" per wallet.

### Running it

```bash
scripts/e2e/livekit-takeover.sh [--log-dir DIR] [--wait-seconds N] [--account NAME]
```

Defaults: a fresh `scripts/e2e/logs/<UTC timestamp>/` directory, a 30 s post-connect window for
B (generous — every local run so far completed L1-L6 within about two seconds of B connecting;
`Clusters:DwellPasses`/`PassIntervalMs` in appsettings.json bound how long a cluster reassignment
can take to debounce), and account `wtval` (bot-count is always 1 per client process, so
`Program.cs`'s account-naming rule uses this name verbatim — see §"Flags" above — and A and B
share it on purpose: same account, same wallet, different device).

It brings up `docker-compose.e2e.yml` on NATS port 4322 (not the default 4222 — a comms-gatekeeper
checkout's own compose, Postgres + NATS on 4222, is left running untouched if it already is),
starts a disposable `livekit-server:latest --dev` container, builds and runs comms-gatekeeper
from `$E2E_COMMS_GATEKEEPER_PATH` (default `../comms-gatekeeper`, rebuilt only if `dist/` is
missing or older than `src/`), runs client A to a confirmed LiveKit join, snapshots A's room,
runs client B, waits, scrapes all three services' `/metrics`, snapshots the room(s) again, prints
a PASS/FAIL table with one line of evidence per leg, and tears down everything it started —
compose stack, LiveKit container, gatekeeper process, both bots, the NATS watcher, and (Windows
only) orphaned `DCLPulseTestClient` `dotnet` processes carrying this run's unique marker. Exits 1 if any leg fails; every leg is still evaluated first.

`MetaForge` (built with `--device` support) and `src/DCLPulseTestClient`
(`dotnet build src/DCLPulseTestClient -p:GenerateProto=false`) must already be built — this
script does not build either, matching §3's treatment of them as prerequisites.

### Variants: forcing the ordering that fails on dev

The default run (`--variant order1`) is the common ordering: B's ws-connector socket registers
before Pulse publishes B's assignment, so the direct `island_changed` reaches B. Dev has been seen
to fail in the other ordering, so the script can force it and can stand in the one condition that
turns it into a permanent loss.

**The attribute key is `dclsession`, no separators.** LiveKit camel-cases a token attribute key
that contains `.`/`_`/`-` (`dcl.session`, `dcl_session` and `dcl-session` all come back as
`dclSession` on `listParticipants`), so a key with no separators is the only one that round-trips
intact. Both the ghost token minter (`mint-ghost-token.js`) and the gatekeeper's own
`ISLAND_SESSION_ATTRIBUTE` use `dclsession` for exactly this reason — a lookup for the wrong key
is indistinguishable from "no attribute at all", which is the fail-closed "cannot tell" case below.

**Measured fact behind `rejoin` and the parking legs (L5/L6):** on livekit-server v1.13.6
(`--dev`), `RemoveParticipant` with a `revokeTokenTs` stamp removes the participant, but a fresh
join with the SAME token is accepted afterwards regardless of the stamp's value. Eviction alone
therefore cannot stop a displaced device from coming back on its own backoff reconnect — which is
why the gatekeeper additionally **parks** the displaced session in a private
`island-parked-<16 hex of S_A>` room it publishes to that session specifically: every client honours
its latest island assignment (the Unity island room included), so a client that has been told to
go somewhere else has nothing left to rejoin its old room with, independent of whether the server
actually enforces the revocation.

| Variant | What it does | What it proves |
| --- | --- | --- |
| `order1` | as above | the takeover works when the direct publish finds B's socket |
| `order2` | B runs with `--comms-delay-ms=4000`, so Pulse publishes the takeover before B's socket exists; the direct `island_changed` is dropped by ws-connector (no socket for that session yet) | B gets its island **only** through gatekeeper's connect re-announce (`dcl_gatekeeper_cluster_reannounce_attempted_total` +1; a re-announced `island_changed` addressed to B's session is timestamped after B's `Welcome received`; the earlier direct publish can still appear on NATS) |
| `ghost` | `order2`, plus: the moment the watcher sees B's takeover `cluster_change`, the harness mints a dev-key token for wallet W and joins B's target room with a third test-client instance (`--join-conn-str`) — a displaced device that outlived its eviction. `--ghost-attribute foreign` (default: a valid-looking `dclsession` that is not B's, what a device evicted by the fixed gatekeeper carries) or `none` (no attribute at all: a pre-fix token, or a LiveKit without attribute support) | whether the connect re-announce can tell the displaced participant from B. `foreign`: the fixed gatekeeper evicts the stale participant (`dcl_gatekeeper_cluster_reannounce_evicted_stale_total` +1) and B is re-announced — **L4 passes**. `none`: the fixed gatekeeper still cannot tell — by design, so it does not wrongly evict a live device — and suppresses (`dcl_gatekeeper_cluster_reannounce_suppressed_total` +1); **L4 fails on purpose** (the script prints `ghost(none): suppressed as designed (cannot tell)` so this is not misread as a regression) |
| `rejoin` | `order1`, plus A's `LiveKitJoiner` re-joins with its ORIGINAL connection string 2 s after being removed (`--rejoin-after-ms`), as the Unity island room does after its backoff | with parking, the parking assignment normally arrives well inside that 2 s window and cancels the probe outright (A's log: `re-join probe cancelled: newer assignment 'island-parked-…' received`); if the probe fires anyway, L5's "A's old room is empty" check still catches a token that was wrongly honoured (the measured fact above) |

Confirmed 2026-09-11 against the gatekeeper commit deployed to dev (`105b845`, run from a
separate worktree via `E2E_COMMS_GATEKEEPER_PATH`, predating the parking fix): `order2` passes
twice with B delivered only by the re-announce; `ghost --ghost-attribute none` fails at L4 with
`reannounce_suppressed_total` 0→1 while L1-L3 pass — Pulse and the takeover eviction did their
part, the wallet-keyed "already in the room" check then silenced the only path left to B. Against
that same pre-parking commit, L5 and L6 fail unconditionally under every variant — there is no
`island-parked-…` room to find A in and no further `Island` line to switch to — which is the
intended signal that the new assertions bite, not a harness bug (see "Reading a failure"). With the
session-aware re-announce and parking (gatekeeper: participants carrying another valid
`dclsession` are evicted with the takeover's revocation stamp before B is re-announced;
participants with no attribute are "cannot tell" and still suppress; the displaced session is
additionally parked before eviction) `ghost` with `foreign` must pass with
`dcl_gatekeeper_cluster_reannounce_evicted_stale_total` +1 and L5/L6 passing on A's side, and
`ghost` with `none` must show `reannounce_suppressed_total` +1 and no eviction (L4 fails by design,
independent of L5/L6).

Evidence for a `ghost` run lives next to the other logs: `bot-ghost.log`, `mint-ghost-token.*`,
`participants-ghost-room-before-b.json`, `participants-after-parked-room.json`, and the usual
`gk-metric-deltas.txt`. Note that L5's "B's room holds exactly one participant" reading is not
meaningful when L4 already failed (B never joined), so read a `ghost` failure from L4 and the
`suppressed` delta, not from L5.

### Reading a failure

Read the table top to bottom; the first `FAIL` names the hop, and every leg after it is
suspect-but-uninformative rather than independently broken. A few shapes worth knowing in
advance:

- **L1 fails, L2-L6 never had a chance.** Pulse itself did not treat the second identity as a
  takeover of the first — check `Clusters:Enabled`, and that both `--device` identities really
  did resolve to different ephemeral keys (a MetaForge that mints fresh per call, or one where
  `--device` silently fell through to the default slot, gives A and B the same session and Pulse
  has nothing to disambiguate).
- **L1 passes, L2 fails.** Pulse disconnected A, but never told gatekeeper why — look for a gap
  between "evict on duplicate `player_id`" (generic, already existed) and "publish
  `displaced_session`/`displaced_cluster_id`" (specific to this feature); the two do not have to
  ship together.
- **L2 passes, L3 fails.** gatekeeper received the displacement but could not act on it — check
  `gatekeeper.log` for `Cannot evict displaced session …` (LiveKit RoomService error) and the
  `_failed_total` metric; a stale gatekeeper checkout without the cluster-subscriber's takeover
  code reads the same as a wire-format mismatch, so confirm the subject actually decodes (nats.log
  shows `displaced_session=(none)` if the code that would have populated it is not there at all).
- **L3 passes, L4 fails.** The mint and publish happened but never reached B — this is the
  session-addressed-subject class of bug in §6's "Half a session"/"`engine.` prefix" causes,
  applied to `.island_changed.{session}` rather than the legacy four-token subject. In the `ghost`
  variant with `--ghost-attribute none` this is EXPECTED (see the variants table above) — check the
  script's own `ghost(none): suppressed as designed` note before treating it as a bug.
- **L4 passes, L5/L6 both fail with no parked-room evidence at all** (no further `Island` line for
  A, no `island-parked-…` snapshot to find A in). This is the pre-parking shape: the gatekeeper
  evicted A (L3 already showed that) but never sent it anywhere to go — check for
  `dcl_gatekeeper_cluster_takeover_parked_total` staying flat across the run, which means this
  gatekeeper checkout predates Task 18c's parking behaviour (see the "Measured fact" note above:
  eviction alone does not stop a re-join on this LiveKit build, which is exactly why parking
  exists). Confirmed against `105b845` (the commit on dev as of 2026-09-11): both legs fail this
  way under every variant.
- **L6 passes but L5's parked-room count is wrong (0 or >1).** The parking message itself arrived
  and A switched rooms (L6 is about the *message*), but something in the room membership disagrees
  with expectations — a race between the parking mint and a delayed eviction, or (in the `rejoin`
  variant) a wrongly-honoured self-rejoin landing in the OLD room rather than the parked one; check
  `A_SELF_REJOIN_LINE` and `A_REJOIN_PROBE_CANCELLED_LINE` in the evidence for that variant.
- **A's own LiveKit client (or the LiveKit dev server) is the remaining suspect for L5 when
  everything else lines up.** Check A's `reason=` on its `LEFT room` line against LiveKit's
  `DisconnectReason` enum (`ParticipantRemoved` is the one an admin-initiated `removeParticipant`
  should produce; `UnknownReason` has also been observed against the local dev server and is not
  itself a failure signal — the room-membership snapshot is the authority, not the reason string).

Masking follows §7: wallets and sessions are truncated to 8 characters and conn strings/tokens
are redacted in the table and in this doc; the script's own log files under `--log-dir` keep full
values, since they never leave the machine.

### Takeover harness validation

The additional `V1` result verifies that `order2` and `ghost` actually exercised the delayed
connection: the takeover precedes B's connect, with a connect re-announce metric and a B-session
delivery afterward. A direct publish before B connects is allowed; it can be dropped by the socket
router. L4 also checks that B's assigned and joined room matches the takeover's `cluster_id`.

A ghost run requires a successful join and a RoomService snapshot containing the wallet, timestamped
before B connects (`ghost-joined.marker`). Missing or late injection fails V1. A foreign-session ghost
requires a stale-eviction metric; an unidentified ghost requires suppression without stale eviction.
The latter remains an expected nonzero run because B does not receive a room (L4 fails).

The rejoin variant requires a cancellation line or an actual probe outcome. Final room membership
remains authoritative if an original token was briefly accepted before the parking assignment.
The harness sets compose's NATS URL to its local broker and tags every bot with an ignored per-run
argument, so inherited broker settings and unrelated Windows bots cannot affect cleanup.

Run the evidence-check regression cases without services:

```bash
bash scripts/e2e/takeover-assertions.test.sh
```

Validated locally on 2026-09-14 with LiveKit v1.13.7 and the sibling gatekeeper checkout's
session-aware re-announce and parking changes: `order1`, `order2`, `ghost --ghost-attribute foreign`,
and `rejoin` passed; `ghost --ghost-attribute none` showed the expected suppression, L4/L5 failures,
and nonzero exit. One initial `order2` attempt stopped during A's initial room setup; the retry
passed. The non-E2E .NET suite passed 822 tests and the offline harness suite passed 12 cases.
The reconnect regression test was also verified to fail with the post-lock assignment guard removed.
