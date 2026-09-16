#!/usr/bin/env bash
# One-wallet-two-identities takeover scenario (see docs/e2e-livekit.md "Takeover scenario").
#
# Client A (wallet W, device "a") connects, gets an island, joins its LiveKit room. Client B
# (same wallet W, device "b" — a different persisted MetaForge identity) then connects. Within
# a bounded window this script checks six legs end to end, against the real Pulse (in Docker),
# the real ws-connector (in Docker) and the real comms-gatekeeper (run from source on the host):
#
#   L1  Pulse disconnects A with reason DUPLICATE_SESSION, and A does not reconnect.
#   L2  Pulse publishes peer.{W}.cluster_change with session=S_B, displaced_session=S_A,
#       displaced_cluster_id=A's cluster.
#   L3  gatekeeper evicts A's participant from island-{displaced_cluster_id} (metric
#       dcl_gatekeeper_cluster_takeover_evicted_total or _absent_total +1, _failed_total flat),
#       mints for W, and publishes engine.peer.{W}.island_changed.{S_B}.
#   L4  ws-connector delivers to B's socket only: B logs "[ws-connector] Island …" and
#       "[livekit] … joined".
#   L5  A's OLD room holds no participant for W afterwards; A IS in the parking room
#       island-parked-<16 hex of S_A> afterwards (exactly one participant, identity W); B's room
#       holds exactly one participant for W, with a sid different from A's original one.
#   L6  A's ws-connector socket receives exactly ONE further "Island" line after L1, and it names
#       the parking room; A's LiveKitJoiner logs "SWITCHED room '<old>' -> 'island-parked-…'".
#
# L5/L6 assume the gatekeeper's takeover-parking behaviour (Task 18c: the displaced session is
# handed a private island-parked-<S_A> room instead of being left with nothing to re-join), not
# just eviction. Against a pre-parking gatekeeper both legs FAIL by design — that is the intended
# signal that the assertions bite, not a harness bug (see docs/e2e-livekit.md "Reading a failure").
#
# This script does not fix anything — it only proves which hop breaks first. See
# docs/e2e-livekit.md for how to read a failure, and .superpowers/sdd/task-16-report.md /
# task-17-report.md (in the archipelago-workers repo) for the evidence from the runs that produced
# this script and its variants.
#
# Usage: scripts/e2e/livekit-takeover.sh [--log-dir DIR] [--wait-seconds N] [--account NAME]
#                                        [--variant order1|order2|ghost|rejoin] [--ghost-attribute foreign|none|0x...]
#   --log-dir DIR      Where logs/evidence land (default: scripts/e2e/logs/<UTC timestamp>-<variant>).
#   --wait-seconds N   How long to give the takeover after B connects, before evaluating
#                      L1-L6 (default 30 — see "Expected behaviour under test" in the brief).
#   --account NAME     MetaForge account A and B share (default wtval). bot-count is always 1
#                      per client, so PulseTestClient/Program.cs uses this name verbatim
#                      (not "<name>-0") — see ClientOptions/Program.cs account naming.
#   --variant NAME     Which ordering/reproduction to run (default order1):
#                        order1  — Task 16's shape: B's ws-connector socket registers before Pulse
#                                  publishes the takeover, so the direct island_changed delivers.
#                        order2  — B runs with --comms-delay-ms=4000 (>= 2 Pulse tracker passes
#                                  after B's snapshot), forcing Pulse to publish the takeover
#                                  before B's ws-connector socket exists. The direct island_changed
#                                  is dropped; L4 depends on gatekeeper's connect re-announce.
#                        ghost   — order2, plus: as soon as the watcher sees B's takeover
#                                  cluster_change, the harness mints a LiveKit token for wallet W
#                                  with a dclsession attribute controlled by --ghost-attribute
#                                  (default "foreign": a valid-looking session that is not B's, what
#                                  a device evicted by the fixed gatekeeper carries) and joins B's
#                                  TARGET room (island-{cluster_id} from that same event) with a
#                                  third test-client instance (--join-conn-str), standing in for a
#                                  displaced device that outlived its own eviction.
#                                  --ghost-attribute foreign: the fixed gatekeeper can tell the
#                                  ghost apart from B and evicts it (reannounce_evicted_stale_total
#                                  +1), L4 PASSes. --ghost-attribute none: no attribute at all (a
#                                  pre-fix token, or a LiveKit without attribute support) — the
#                                  fixed gatekeeper still cannot tell, by design, and suppresses
#                                  (reannounce_suppressed_total +1); L4 FAILS on purpose (the script
#                                  prints an explicit note so this is not misread as a regression).
#                        rejoin  — order1, plus A's LiveKitJoiner re-joins with its ORIGINAL
#                                  connection string 2s after being removed (--rejoin-after-ms),
#                                  mirroring the Unity island room's backoff reconnect against a
#                                  token the takeover should have revoked. With parking, the parking
#                                  assignment normally arrives well inside that 2s and cancels the
#                                  probe outright ("re-join probe cancelled: newer assignment …");
#                                  if the probe fires anyway, L5's "A's old room is empty" check
#                                  still catches a token that was wrongly honoured.
#
# Env overrides, mirroring docker-compose.e2e.yml's own knobs:
#   E2E_ARCHIPELAGO_WORKERS_PATH   default ../archipelago-workers (sibling of this repo)
#   E2E_COMMS_GATEKEEPER_PATH      default ../comms-gatekeeper (sibling of this repo)
#   E2E_METAFORGE_CLI_BIN          default ../MetaForge/MetaForgeCLI/bin/Debug/net10.0/win-x64
#
# Prerequisites (not built by this script — see docs/e2e-livekit.md §3 and the task brief):
#   - metaforge built with `account sign`/`account chain --device` support, on $E2E_METAFORGE_CLI_BIN.
#   - src/DCLPulseTestClient built (`dotnet build src/DCLPulseTestClient -p:GenerateProto=false`).
#   - Docker running; the gatekeeper's own compose (Postgres + NATS on 4222) may already be up —
#     this script never touches it and does not need it stopped.
#
# Exits 1 if any of L1-L6 fails (every leg is still evaluated). Tears down everything it started
# — the compose stack, the LiveKit dev container, the host gatekeeper process, both bot
# processes, the NATS watcher, and any orphan DCLPulseTestClient dotnet process left running on
# Windows — even on failure or Ctrl+C.
#
# Bash-tool quirks this script is written around: no foreground `sleep` (uses
# `timeout N tail -f /dev/null` instead), and every long-running process is started with `exec`
# inside `( … ) &` so the captured PID is the real process, not a wrapping shell.
set -uo pipefail

# ---------- args ----------
LOG_DIR=""
WAIT_SECONDS=30
ACCOUNT=wtval
VARIANT=order1
# ghost variant: the dcl.session attribute the ghost token carries: "foreign" (a valid-looking key that
# is not B's, what a displaced device minted by the fixed gatekeeper carries), "none" (no attribute: a
# pre-fix token, or a LiveKit that drops attributes), or an explicit 0x... session key.
GHOST_ATTRIBUTE=foreign

while [[ $# -gt 0 ]]; do
  case "$1" in
    --log-dir) LOG_DIR="$2"; shift 2 ;;
    --wait-seconds) WAIT_SECONDS="$2"; shift 2 ;;
    --account) ACCOUNT="$2"; shift 2 ;;
    --variant) VARIANT="$2"; shift 2 ;;
    --ghost-attribute) GHOST_ATTRIBUTE="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$VARIANT" in
  order1|order2|ghost|rejoin) ;;
  *) echo "Unknown --variant '$VARIANT' (expected order1, order2, ghost or rejoin)" >&2; exit 2 ;;
esac

# Per-variant knobs, threaded to the test client (see docs/e2e-livekit.md "Takeover scenario").
# B_COMMS_DELAY_MS forces "Order 2" (Pulse's direct publish races ahead of B's ws-connector
# socket); A_REJOIN_AFTER_MS is the `rejoin` variant's post-eviction self-rejoin probe.
B_COMMS_DELAY_MS=0
A_REJOIN_AFTER_MS=0
case "$VARIANT" in
  order2|ghost) B_COMMS_DELAY_MS=4000 ;;
  rejoin) A_REJOIN_AFTER_MS=2000 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/takeover-assertions.sh"
PULSE_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
AW="${E2E_ARCHIPELAGO_WORKERS_PATH:-$PULSE_ROOT/../archipelago-workers}"
GK="${E2E_COMMS_GATEKEEPER_PATH:-$PULSE_ROOT/../comms-gatekeeper}"
MF_BIN="${E2E_METAFORGE_CLI_BIN:-$PULSE_ROOT/../MetaForge/MetaForgeCLI/bin/Debug/net10.0/win-x64}"
TESTCLIENT_BIN="$PULSE_ROOT/src/DCLPulseTestClient/bin/Debug/net10.0"

TS="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_MARKER="pulse-e2e-takeover-${TS}-$$"
LOG_DIR="${LOG_DIR:-$SCRIPT_DIR/logs/$TS-$VARIANT}"
mkdir -p "$LOG_DIR"
# Absolute from here on: several steps below `cd` into another repo (comms-gatekeeper) in a
# subshell before redirecting into a file under $LOG_DIR, and a relative --log-dir would then
# resolve against THAT directory instead of the one this script started in.
LOG_DIR="$(cd "$LOG_DIR" && pwd)"

NATS_PORT=4322
NATS_MONITOR_PORT=8322            # gatekeeper's own compose already owns 4222/8222 — leave it alone.
WS_CONNECTOR_PORT=5000
PULSE_HTTP_PORT=5100
PULSE_ENET_PORT=7777
GK_HTTP_PORT=3100
LIVEKIT_WS_PORT=7880
LIVEKIT_TCP_PORT=7881
LIVEKIT_API_KEY=devkey
LIVEKIT_API_SECRET=secret
LIVEKIT_CONTAINER=pulse-e2e-livekit-takeover

A_LOG="$LOG_DIR/bot-a.log"
B_LOG="$LOG_DIR/bot-b.log"
GHOST_LOG="$LOG_DIR/bot-ghost.log"
GK_LOG="$LOG_DIR/gatekeeper.log"
NATS_LOG="$LOG_DIR/nats.log"

wait_s() { timeout "$1" tail -f /dev/null >/dev/null 2>&1 || true; }
# Sub-second poll tick, for windows too tight to spend a whole wait_s() on (e.g. the ghost
# variant's race to inject before B's delayed ws-connector handshake catches up).
poll_tick() { timeout 0.25 tail -f /dev/null >/dev/null 2>&1 || true; }
log() { echo "[$(date -u +%T)] $*"; }

log "variant=$VARIANT (comms-delay-ms for B: $B_COMMS_DELAY_MS, rejoin-after-ms for A: $A_REJOIN_AFTER_MS)"

if [[ ! -f "$TESTCLIENT_BIN/DCLPulseTestClient.dll" ]]; then
  echo "Missing $TESTCLIENT_BIN/DCLPulseTestClient.dll — build it first:" >&2
  echo "  dotnet build src/DCLPulseTestClient -p:GenerateProto=false" >&2
  exit 2
fi
if [[ ! -x "$MF_BIN/metaforge.exe" && ! -f "$MF_BIN/metaforge.exe" ]]; then
  echo "Missing $MF_BIN/metaforge.exe — build MetaForge first:" >&2
  echo "  dotnet build MetaForgeCLI/MetaForgeCLI.csproj -c Debug   (in the MetaForge repo)" >&2
  exit 2
fi

# ---------- teardown ----------
PIDS=()
CLEANED_UP=0
cleanup() {
  [[ "$CLEANED_UP" == 1 ]] && return
  CLEANED_UP=1
  log "tearing down.."

  for pid in "${PIDS[@]:-}"; do
    [[ -n "${pid:-}" ]] && kill "$pid" 2>/dev/null || true
  done
  wait_s 2
  for pid in "${PIDS[@]:-}"; do
    [[ -n "${pid:-}" ]] && kill -9 "$pid" 2>/dev/null || true
  done

  docker rm -f "$LIVEKIT_CONTAINER" >/dev/null 2>&1 || true

  ( cd "$PULSE_ROOT" && \
    E2E_NATS_URL=nats://nats:4222 E2E_NATS_PORT=$NATS_PORT E2E_NATS_MONITOR_PORT=$NATS_MONITOR_PORT \
    E2E_WS_CONNECTOR_PORT=$WS_CONNECTOR_PORT E2E_PULSE_HTTP_PORT=$PULSE_HTTP_PORT \
    E2E_PULSE_ENET_PORT=$PULSE_ENET_PORT E2E_ARCHIPELAGO_WORKERS_PATH="$AW" \
    docker compose -f docker-compose.e2e.yml down --timeout 10 \
  ) >"$LOG_DIR/teardown-compose.log" 2>&1 || true

  # A Windows child may outlive its MSYS parent. Only sweep bots carrying this run
  # marker; other test-client processes belong to the user or another harness.
  powershell -NoProfile -Command \
    "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*--harness-run-marker=$RUN_MARKER *' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }" \
    >/dev/null 2>&1 || true

  log "teardown done. Logs and evidence are under $LOG_DIR"
}
trap cleanup EXIT INT TERM

# ---------- 1. bring up NATS + Pulse + ws-connector ----------
log "removing any stale host ws-connector/stats dist (COPY . . in the Dockerfile would pick it up)"
rm -rf "$AW/ws-connector/dist" "$AW/stats/dist"

log "docker compose up --build --wait (NATS on $NATS_PORT, Pulse ENet on $PULSE_ENET_PORT, ws-connector on $WS_CONNECTOR_PORT)"
( cd "$PULSE_ROOT" && \
  E2E_NATS_URL=nats://nats:4222 E2E_NATS_PORT=$NATS_PORT E2E_NATS_MONITOR_PORT=$NATS_MONITOR_PORT \
  E2E_WS_CONNECTOR_PORT=$WS_CONNECTOR_PORT E2E_PULSE_HTTP_PORT=$PULSE_HTTP_PORT \
  E2E_PULSE_ENET_PORT=$PULSE_ENET_PORT E2E_ARCHIPELAGO_WORKERS_PATH="$AW" \
  docker compose -f docker-compose.e2e.yml up --build --wait \
) >"$LOG_DIR/compose-up.log" 2>&1
COMPOSE_STATUS=$?
if [[ $COMPOSE_STATUS -ne 0 ]]; then
  echo "docker compose up failed (exit $COMPOSE_STATUS); tail of $LOG_DIR/compose-up.log:" >&2
  tail -n 60 "$LOG_DIR/compose-up.log" >&2
  exit 1
fi

# The HTTP healthcheck compose just waited on says nothing about the NATS client, which
# connects/reconnects independently and a moment later — so this is retried for a few seconds
# rather than checked once, to avoid a false "stats-only mode" warning on an otherwise-healthy
# stack that just hasn't finished its first NATS handshake yet.
PULSE_NATS_OK=0
for i in $(seq 1 10); do
  if curl -s -m5 "http://127.0.0.1:$PULSE_HTTP_PORT/metrics" 2>/dev/null | grep -q "^dcl_pulse_nats_connected 1"; then
    PULSE_NATS_OK=1
    break
  fi
  wait_s 1
done
[[ "$PULSE_NATS_OK" == 1 ]] || log "WARNING: Pulse does not report dcl_pulse_nats_connected=1 after 10s — its NATS feed may be in stats-only mode"

# ---------- 2. LiveKit dev server ----------
log "starting livekit-server --dev on $LIVEKIT_WS_PORT/$LIVEKIT_TCP_PORT (devkey/secret)"
docker rm -f "$LIVEKIT_CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$LIVEKIT_CONTAINER" \
  -p "$LIVEKIT_WS_PORT:7880" -p "$LIVEKIT_TCP_PORT:7881" -p "$LIVEKIT_TCP_PORT:7881/udp" \
  livekit/livekit-server:latest --dev --bind 0.0.0.0 \
  >"$LOG_DIR/livekit-container-id.txt" 2>"$LOG_DIR/livekit-run.log"
if [[ ! -s "$LOG_DIR/livekit-container-id.txt" ]]; then
  echo "livekit-server failed to start; see $LOG_DIR/livekit-run.log" >&2
  cat "$LOG_DIR/livekit-run.log" >&2
  exit 1
fi

# ---------- 3. comms-gatekeeper from source ----------
if [[ ! -f "$GK/dist/index.js" ]] || find "$GK/src" -name "*.ts" -newer "$GK/dist/index.js" 2>/dev/null | grep -q .; then
  log "comms-gatekeeper dist is missing or stale, rebuilding.."
  ( cd "$GK" && rm -rf dist && yarn build ) >"$LOG_DIR/gatekeeper-build.log" 2>&1
  if [[ ! -f "$GK/dist/index.js" ]]; then
    echo "comms-gatekeeper build failed; see $LOG_DIR/gatekeeper-build.log" >&2
    exit 1
  fi
else
  log "comms-gatekeeper dist is up to date, skipping build"
fi

log "starting comms-gatekeeper on :$GK_HTTP_PORT against nats://127.0.0.1:$NATS_PORT"
( cd "$GK" && exec env \
    CLUSTER_SUBSCRIBER_ENABLED=true \
    NATS_URL="nats://127.0.0.1:$NATS_PORT" \
    NATS_SUBJECT_PREFIX= \
    COMMS_GATEKEEPER_AUTH_TOKEN=e2e-placeholder \
    HTTP_SERVER_PORT=$GK_HTTP_PORT \
    PROD_LIVEKIT_HOST="ws://127.0.0.1:$LIVEKIT_WS_PORT" PROD_LIVEKIT_API_KEY=$LIVEKIT_API_KEY PROD_LIVEKIT_API_SECRET=$LIVEKIT_API_SECRET \
    PREVIEW_LIVEKIT_HOST="ws://127.0.0.1:$LIVEKIT_WS_PORT" PREVIEW_LIVEKIT_API_KEY=$LIVEKIT_API_KEY PREVIEW_LIVEKIT_API_SECRET=$LIVEKIT_API_SECRET \
    node dist/index.js >"$GK_LOG" 2>&1 ) & GK_PID=$!
PIDS+=("$GK_PID")

for i in $(seq 1 40); do
  grep -qa "Cluster subscriber started" "$GK_LOG" 2>/dev/null && break
  kill -0 "$GK_PID" 2>/dev/null || break
  wait_s 1
done
if ! grep -qa "Cluster subscriber started" "$GK_LOG" 2>/dev/null; then
  echo "comms-gatekeeper did not start its cluster subscriber; tail of $GK_LOG:" >&2
  tail -n 40 "$GK_LOG" >&2
  exit 1
fi
log "comms-gatekeeper subscriber up"

curl -s -m5 "http://127.0.0.1:$GK_HTTP_PORT/metrics" -o "$LOG_DIR/gk-metrics-before.txt" || true
curl -s -m5 "http://127.0.0.1:$WS_CONNECTOR_PORT/metrics" -o "$LOG_DIR/ws-metrics-before.txt" || true

# ---------- 4. NATS watcher ----------
log "starting NATS watcher"
( cd "$GK" && exec env NODE_PATH="$GK/node_modules" node "$SCRIPT_DIR/nats-watch.js" "nats://127.0.0.1:$NATS_PORT" \
    >"$NATS_LOG" 2>&1 ) & WATCH_PID=$!
PIDS+=("$WATCH_PID")
wait_s 1

# ---------- 5. RoomService helper (tiny node script, generated — not a repo deliverable) ----------
cat >"$LOG_DIR/list-participants.js" <<'EOF'
// Prints ParticipantInfo[] for one LiveKit room as JSON: [{identity, sid, state}, ...], or
// {"error": "..."} if the room does not exist or the call fails. Args: apiHost apiKey apiSecret roomName.
const { RoomServiceClient } = require('livekit-server-sdk')
const [, , apiHost, apiKey, apiSecret, roomName] = process.argv
;(async () => {
  const svc = new RoomServiceClient(apiHost, apiKey, apiSecret)
  try {
    const participants = await svc.listParticipants(roomName)
    process.stdout.write(JSON.stringify(participants.map((p) => ({ identity: p.identity, sid: p.sid, state: p.state }))))
  } catch (e) {
    process.stdout.write(JSON.stringify({ error: e.message }))
  }
})()
EOF

list_participants() {
  # list_participants <roomName> <outFile>
  ( cd "$GK" && exec env NODE_PATH="$GK/node_modules" node "$LOG_DIR/list-participants.js" \
      "http://127.0.0.1:$LIVEKIT_WS_PORT" "$LIVEKIT_API_KEY" "$LIVEKIT_API_SECRET" "$1" ) >"$2" 2>>"$LOG_DIR/list-participants.err.log"
}

# `ghost` variant only: mints a bare LiveKit token — identity=wallet, room=the room named on the
# command line, ttl 5 min, roomJoin — and deliberately NO attributes (no `dcl.session`), so the
# participant it creates looks exactly like a displaced device that predates the session-attribute
# stamp: the one thing the connect re-announce's stale-participant check keys on. Mirrors
# comms-gatekeeper's own generateCredentials() shape (src/adapters/livekit.ts) minus that one field.
cat >"$LOG_DIR/mint-ghost-token.js" <<'EOF'
// Prints a LiveKit access token (JWT) on stdout, or nothing + a message on stderr on failure.
// Args: apiKey apiSecret identity roomName.
const { AccessToken } = require('livekit-server-sdk')
const [, , apiKey, apiSecret, identity, roomName, sessionAttribute] = process.argv
;(async () => {
  const at = new AccessToken(apiKey, apiSecret, { identity, ttl: 5 * 60 })
  at.addGrant({ roomJoin: true, room: roomName, canPublish: true, canSubscribe: true })
  if (sessionAttribute) at.attributes = { dclsession: sessionAttribute }
  process.stdout.write(await at.toJwt())
})().catch((e) => {
  process.stderr.write(`mint failed: ${e.message}\n`)
  process.exit(1)
})
EOF

# ---------- 6. Client A ----------
log "starting Client A ($ACCOUNT, device=a)"
( cd "$TESTCLIENT_BIN" && exec env DOTNET_ENVIRONMENT=Development PATH="$MF_BIN:$PATH" \
    dotnet DCLPulseTestClient.dll --harness-run-marker="$RUN_MARKER" --account="$ACCOUNT" --bot-count=1 --spawn-radius=2 \
      --comms-enabled --join-livekit --device=a --rejoin-after-ms="$A_REJOIN_AFTER_MS" \
    </dev/null >"$A_LOG" 2>&1 ) & A_PID=$!
PIDS+=("$A_PID")

log "waiting for A to join its LiveKit room.."
for i in $(seq 1 60); do
  grep -qaE "^\[livekit\] \[$ACCOUNT\] joined " "$A_LOG" 2>/dev/null && break
  kill -0 "$A_PID" 2>/dev/null || break
  wait_s 1
done

A_JOIN_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] joined " "$A_LOG" 2>/dev/null | tail -1)"
if [[ -z "$A_JOIN_LINE" ]]; then
  curl -s -m5 "http://127.0.0.1:$GK_HTTP_PORT/metrics" -o "$LOG_DIR/gk-metrics-failed-setup.txt" || true
  curl -s -m5 "http://127.0.0.1:$NATS_MONITOR_PORT/subsz?subs=1" -o "$LOG_DIR/nats-subscriptions-failed-setup.json" || true
  echo "Client A never reported a LiveKit join; tail of $A_LOG:" >&2
  tail -n 40 "$A_LOG" >&2
  exit 1
fi

A_ROOM="$(echo "$A_JOIN_LINE" | grep -oE 'room=[^ ]+' | cut -d= -f2)"
A_CLUSTER="${A_ROOM#island-}"
WALLET="$(grep -aoE '\[ws-connector\] Welcome received, peer id 0x[0-9a-f]+' "$A_LOG" | grep -oE '0x[0-9a-f]+' | head -1)"
log "A joined room=$A_ROOM (cluster=$A_CLUSTER), wallet=${WALLET:0:8}…"

list_participants "$A_ROOM" "$LOG_DIR/participants-before.json"

# ---------- 7. Client B ----------
log "starting Client B ($ACCOUNT, device=b) — same wallet, different identity"
B_START_EPOCH=$(date +%s)
( cd "$TESTCLIENT_BIN" && exec env DOTNET_ENVIRONMENT=Development PATH="$MF_BIN:$PATH" \
    dotnet DCLPulseTestClient.dll --harness-run-marker="$RUN_MARKER" --account="$ACCOUNT" --bot-count=1 --spawn-radius=2 \
      --comms-enabled --join-livekit --device=b --comms-delay-ms="$B_COMMS_DELAY_MS" \
    </dev/null >"$B_LOG" 2>&1 ) & B_PID=$!
PIDS+=("$B_PID")

# ---------- 7b. ghost variant only: inject the displaced-device ghost participant ----------
# Races B's own (delayed) ws-connector handshake: must land before B's `peer.*.connect` fires, or
# the whole point (the re-announce seeing identity W already in the room) never gets exercised.
GHOST_CLUSTER=""
GHOST_ROOM=""
GHOST_JOIN_LINE=""
GHOST_JOIN_TS=""
GHOST_PARTICIPANTS_BEFORE_COUNT=-1
if [[ "$VARIANT" == "ghost" ]]; then
  log "ghost: watching NATS for B's takeover cluster_change to learn its target room.."
  GHOST_DEADLINE=$(( $(date +%s) + 15 ))
  while [[ $(date +%s) -lt $GHOST_DEADLINE ]]; do
    GHOST_CC_LINE="$(grep -aE "peer\.${WALLET}\.cluster_change [0-9]+B .*displaced_session=0x" "$NATS_LOG" 2>/dev/null | tail -1)"
    if [[ -n "$GHOST_CC_LINE" ]]; then
      # Not `grep -oE 'cluster_id=...'`: that pattern also matches inside "displaced_cluster_id=",
      # which is the OTHER cluster on this same line (A's, already evicted) — the char
      # immediately before the standalone field is a space, never `_`, so `[^_]cluster_id=`
      # picks out only the one this variant actually needs: B's own target cluster.
      GHOST_CLUSTER="$(echo "$GHOST_CC_LINE" | sed -E 's/^.*[^_]cluster_id=([^ ]+).*$/\1/')"
      break
    fi
    poll_tick
  done

  if [[ -z "$GHOST_CLUSTER" ]]; then
    log "WARNING: ghost did not see a takeover cluster_change for $WALLET within 15s — no ghost participant will be injected; this run will not reproduce anything"
  else
    GHOST_ROOM="island-$GHOST_CLUSTER"
    case "$GHOST_ATTRIBUTE" in
      none) GHOST_SESSION="" ;;
      foreign) GHOST_SESSION="0x00000000000000000000000000000000000000d1" ;;
      *) GHOST_SESSION="$GHOST_ATTRIBUTE" ;;
    esac
    log "ghost target room is $GHOST_ROOM (from: $GHOST_CC_LINE); minting a ghost token (dcl.session=${GHOST_SESSION:-<none>})"
    GHOST_TOKEN="$( ( cd "$GK" && exec env NODE_PATH="$GK/node_modules" node "$LOG_DIR/mint-ghost-token.js" \
        "$LIVEKIT_API_KEY" "$LIVEKIT_API_SECRET" "$WALLET" "$GHOST_ROOM" "$GHOST_SESSION" ) 2>"$LOG_DIR/mint-ghost-token.err.log" )"

    if [[ -z "$GHOST_TOKEN" ]]; then
      log "WARNING: minting the ghost token failed; see $LOG_DIR/mint-ghost-token.err.log"
    else
      GHOST_CONN_STR="livekit:ws://127.0.0.1:$LIVEKIT_WS_PORT?access_token=$GHOST_TOKEN"
      ( cd "$TESTCLIENT_BIN" && exec env DOTNET_ENVIRONMENT=Development \
          dotnet DCLPulseTestClient.dll --harness-run-marker="$RUN_MARKER" --join-conn-str="$GHOST_CONN_STR" \
          </dev/null >"$GHOST_LOG" 2>&1 ) & GHOST_PID=$!
      PIDS+=("$GHOST_PID")

      for i in $(seq 1 20); do
        grep -qaE '^\[livekit\] \[ghost\] joined ' "$GHOST_LOG" 2>/dev/null && break
        kill -0 "$GHOST_PID" 2>/dev/null || break
        poll_tick
      done
      GHOST_JOIN_LINE="$(grep -aE '^\[livekit\] \[ghost\] joined ' "$GHOST_LOG" 2>/dev/null | tail -1)"
      if [[ -n "$GHOST_JOIN_LINE" ]]; then
        log "ghost joined: $GHOST_JOIN_LINE"
      else
        log "WARNING: ghost did not report joining within 5s; tail of $GHOST_LOG:"
        tail -n 20 "$GHOST_LOG" | sed 's/access_token=[^ &"'"'"']*/access_token=<redacted>/g' >&2 || true
      fi
    fi
  fi

  if [[ -n "$GHOST_ROOM" && -n "$GHOST_JOIN_LINE" ]]; then
    list_participants "$GHOST_ROOM" "$LOG_DIR/participants-ghost-room-before-b.json"
    GHOST_PARTICIPANTS_BEFORE_COUNT=$(jq --arg w "${WALLET,,}" '[.[]? | select(.identity == $w)] | length' "$LOG_DIR/participants-ghost-room-before-b.json" 2>/dev/null || echo -1)
    GHOST_JOIN_TS="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
    printf '%s participant_count=%s\n' "$GHOST_JOIN_TS" "$GHOST_PARTICIPANTS_BEFORE_COUNT" >"$LOG_DIR/ghost-joined.marker"
  fi
fi

B_ELAPSED=$(( $(date +%s) - B_START_EPOCH ))
B_REMAIN=$(( WAIT_SECONDS - B_ELAPSED ))
if [[ $B_REMAIN -gt 0 ]]; then
  log "waiting ${B_REMAIN}s more for the takeover to complete (${WAIT_SECONDS}s total since B started, ${B_ELAPSED}s already spent)…"
  wait_s "$B_REMAIN"
fi

# ---------- 8. scrape + snapshot ----------
curl -s -m5 "http://127.0.0.1:$WS_CONNECTOR_PORT/metrics" -o "$LOG_DIR/ws-metrics.txt" || true
curl -s -m5 "http://127.0.0.1:$PULSE_HTTP_PORT/metrics" -o "$LOG_DIR/pulse-metrics.txt" || true
curl -s -m5 "http://127.0.0.1:$GK_HTTP_PORT/metrics" -o "$LOG_DIR/gk-metrics-after.txt" || true

B_JOIN_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] joined " "$B_LOG" 2>/dev/null | tail -1)"
B_ROOM="$(echo "$B_JOIN_LINE" | grep -oE 'room=[^ ]+' | cut -d= -f2)"

# The takeover's parking assignment (gatekeeper Task 18c) sends A's displaced session, S_A, to a
# private room named island-parked-<first 16 hex of S_A> — computed once here from the watcher's own
# cluster_change line (never from A's own log) so L2 and L6 below share one source of truth, and so
# the snapshot below is taken regardless of variant. S_A empty (no takeover cluster_change seen at
# all) means L2 itself is already failing; downstream reads of an empty EXPECTED_PARK_ROOM are
# guarded, not skipped, so a missing displaced_session still shows up as an explicit L6 failure.
L2_LINE="$(grep -aE "\.cluster_change [0-9]+B .*displaced_session=0x" "$NATS_LOG" 2>/dev/null | tail -1)"
S_A="$(echo "$L2_LINE" | grep -oE 'displaced_session=0x[0-9a-fA-F]+' | cut -d= -f2)"
TAKEOVER_CLUSTER="$(echo "$L2_LINE" | sed -n 's/.* cluster_id=\([^ ]*\).*/\1/p')"
EXPECTED_B_ROOM=""
[[ -n "$TAKEOVER_CLUSTER" ]] && EXPECTED_B_ROOM="island-$TAKEOVER_CLUSTER"
S_A_LC="${S_A,,}"
EXPECTED_PARK_ROOM=""
[[ -n "$S_A_LC" ]] && EXPECTED_PARK_ROOM="island-parked-${S_A_LC:2:16}"

list_participants "$A_ROOM" "$LOG_DIR/participants-after-a-room.json"
if [[ -n "$B_ROOM" && "$B_ROOM" != "$A_ROOM" ]]; then
  list_participants "$B_ROOM" "$LOG_DIR/participants-after-b-room.json"
fi
if [[ -n "$EXPECTED_PARK_ROOM" ]]; then
  list_participants "$EXPECTED_PARK_ROOM" "$LOG_DIR/participants-after-parked-room.json"
fi
if [[ -n "$GHOST_ROOM" && "$GHOST_ROOM" != "$A_ROOM" && "$GHOST_ROOM" != "$B_ROOM" && "$GHOST_ROOM" != "$EXPECTED_PARK_ROOM" ]]; then
  list_participants "$GHOST_ROOM" "$LOG_DIR/participants-after-ghost-room.json"
fi

# ---------- 9. evaluate L1-L6 ----------
metric() { # metric <file> <name>
  awk -v m="$2" '$1==m {print $2; f=1} END{if(!f) print 0}' "$1" 2>/dev/null
}

RESULT_FILE="$LOG_DIR/results.txt"
: >"$RESULT_FILE"
overall=0
evaluate() { # evaluate <leg> <pass:0|1> <evidence...>
  local leg=$1 pass=$2; shift 2
  local status="FAIL"; [[ "$pass" == 1 ]] && status="PASS"
  [[ "$pass" != 1 ]] && overall=1
  printf '%s\t%s\t%s\n' "$leg" "$status" "$*" >>"$RESULT_FILE"
}

# L1: Pulse disconnected A with DUPLICATE_SESSION, and A made no further connect attempt.
L1_DISCONNECT="$(grep -aE 'disconnected by server: DUPLICATE_SESSION' "$A_LOG" | tail -1)"
A_CONNECT_ATTEMPTS="$(grep -acE "^\[$ACCOUNT\] Connecting to " "$A_LOG")"
if [[ -n "$L1_DISCONNECT" && "$A_CONNECT_ATTEMPTS" -le 1 ]]; then
  evaluate L1 1 "$L1_DISCONNECT (connect attempts: $A_CONNECT_ATTEMPTS)"
else
  evaluate L1 0 "no DUPLICATE_SESSION disconnect seen for A in $A_LOG (connect attempts: $A_CONNECT_ATTEMPTS)"
fi

# L2: cluster_change for W naming a non-empty displaced_session, whose displaced_cluster_id
# matches A's own cluster (extracted from A's join line above). L2_LINE/S_A were already computed
# in step 8 (needed there for the parked-room snapshot); reused here rather than re-grepped.
if [[ -n "$L2_LINE" ]] && echo "$L2_LINE" | grep -qE "displaced_cluster_id=${A_CLUSTER}([[:space:]]|$)"; then
  evaluate L2 1 "$L2_LINE"
elif [[ -n "$L2_LINE" ]]; then
  evaluate L2 0 "cluster_change with a displaced_session arrived but names a different displaced_cluster_id than A's ($A_CLUSTER): $L2_LINE"
else
  evaluate L2 0 "no cluster_change with a non-empty displaced_session seen on peer.*.cluster_change in $NATS_LOG"
fi

# L3: takeover metric moved (evicted or absent, not failed) AND island_changed addressed to
# B's own session (the second peer.{W}.connect payload — A's device connected first).
EVICTED_BEFORE=$(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_takeover_evicted_total)
EVICTED_AFTER=$(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_takeover_evicted_total)
ABSENT_BEFORE=$(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_takeover_absent_total)
ABSENT_AFTER=$(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_takeover_absent_total)
FAILED_BEFORE=$(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_takeover_failed_total)
FAILED_AFTER=$(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_takeover_failed_total)
TAKEOVER_METRIC_EVIDENCE="evicted ${EVICTED_BEFORE}->${EVICTED_AFTER}, absent ${ABSENT_BEFORE}->${ABSENT_AFTER}, failed ${FAILED_BEFORE}->${FAILED_AFTER}"

B_SESSION=""
if [[ -n "$WALLET" ]]; then
  # Two `.connect` payloads are expected for this wallet: A's first, B's second.
  B_SESSION="$(grep -aE "peer\.${WALLET}\.connect .*payload=" "$NATS_LOG" 2>/dev/null | sed -E 's/^.*payload=(0x[0-9a-f]+)$/\1/' | sed -n '2p')"
fi
ISLAND_CHANGED_FOR_B="$([[ -n "$B_SESSION" ]] && grep -aF "engine.peer.${WALLET}.island_changed.${B_SESSION}" "$NATS_LOG" | tail -1)"

if [[ $((EVICTED_AFTER - EVICTED_BEFORE + ABSENT_AFTER - ABSENT_BEFORE)) -ge 1 && $((FAILED_AFTER - FAILED_BEFORE)) -eq 0 && -n "$ISLAND_CHANGED_FOR_B" ]]; then
  evaluate L3 1 "$TAKEOVER_METRIC_EVIDENCE; $ISLAND_CHANGED_FOR_B"
else
  evaluate L3 0 "$TAKEOVER_METRIC_EVIDENCE; island_changed for B's session (${B_SESSION:-unknown}): ${ISLAND_CHANGED_FOR_B:-not seen}"
fi

# L4: B logs both the ws-connector island line and a LiveKit join.
B_WS_ISLAND="$(grep -aE '^\[ws-connector\] Island ' "$B_LOG" | tail -1)"
B_WS_ROOM="$(echo "$B_WS_ISLAND" | awk '{print $3}')"
if [[ -n "$B_WS_ISLAND" && -n "$B_JOIN_LINE" && -n "$EXPECTED_B_ROOM" && "$B_ROOM" == "$EXPECTED_B_ROOM" && "$B_WS_ROOM" == "$EXPECTED_B_ROOM" ]]; then
  evaluate L4 1 "$B_WS_ISLAND | $B_JOIN_LINE"
else
  evaluate L4 0 "expected room=${EXPECTED_B_ROOM:-unknown}, joined room=${B_ROOM:-none}; ws-connector Island line: ${B_WS_ISLAND:-none}; livekit joined line: ${B_JOIN_LINE:-none}"
fi

# Variant preconditions are assertions, not merely descriptive evidence.
B_CONNECT_LINE="$(grep -aE "peer\.${WALLET}\.connect .*payload=" "$NATS_LOG" 2>/dev/null | sed -n '2p')"
B_CONNECT_TS="$(echo "$B_CONNECT_LINE" | awk '{print $1}')"
TAKEOVER_TS="$(echo "$L2_LINE" | awk '{print $1}')"
ISLAND_B_AFTER_CONNECT_COUNT=0
while IFS= read -r ib_line; do
  [[ -z "$ib_line" ]] && continue
  ib_ts="$(echo "$ib_line" | awk '{print $1}')"
  if [[ -n "$B_CONNECT_TS" && ! "$ib_ts" < "$B_CONNECT_TS" ]]; then
    ISLAND_B_AFTER_CONNECT_COUNT=$((ISLAND_B_AFTER_CONNECT_COUNT + 1))
  fi
done < <(grep -aF "engine.peer.${WALLET}.island_changed.${B_SESSION}" "$NATS_LOG" 2>/dev/null)
REANNOUNCE_ATTEMPTED_DELTA=$(( $(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_reannounce_attempted_total) - $(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_reannounce_attempted_total) ))
REANNOUNCE_SUPPRESSED_DELTA=$(( $(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_reannounce_suppressed_total) - $(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_reannounce_suppressed_total) ))
REANNOUNCE_EVICTED_STALE_DELTA=$(( $(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_reannounce_evicted_stale_total) - $(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_reannounce_evicted_stale_total) ))
VARIANT_VALID=0
if VARIANT_EVIDENCE="$(assert_takeover_variant)"; then
  VARIANT_VALID=1
  evaluate V1 1 "$VARIANT_EVIDENCE"
else
  evaluate V1 0 "$VARIANT_EVIDENCE"
fi

# L5: A's OLD room holds no participant for the wallet afterwards; A IS in the parked room
# afterwards (exactly one participant, identity W); B's room holds exactly one participant for the
# wallet, with a sid different from A's original one. This replaced the pre-parking leg (which only
# checked "A's old room is empty, B's room has one") — parking means A always ends up in a THIRD
# room, island-parked-<S_A>, never back in its own old room, so that room is asserted directly
# regardless of whether B happens to land in A's old cluster or a new one.
A_LEFT_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] LEFT room " "$A_LOG" | tail -1)"
A_REJOIN_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] RE-JOINED room " "$A_LOG" | tail -1)"
A_SWITCHED_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] SWITCHED room " "$A_LOG" | tail -1)"

# `rejoin` variant only (A_REJOIN_AFTER_MS > 0): the SELF-initiated probe using A's ORIGINAL
# (revoked-by-the-takeover) connection string — distinct from A_REJOIN_LINE above, which is the
# LiveKit SDK's own automatic reconnect. "ACCEPTED" here is the notable finding the brief calls
# out: it means the local LiveKit did not honour the revocation stamp. With parking, the expected
# outcome is that the probe never fires at all — the parking assignment arrives first and cancels
# it (see LiveKitJoiner's "re-join probe cancelled" log) — but if it DOES fire, L5's "A's old room
# is empty" check below still catches a token that was wrongly honoured.
A_SELF_REJOIN_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] RE-JOIN (ACCEPTED|REJECTED)" "$A_LOG" | tail -1)"
A_REJOIN_PROBE_CANCELLED_LINE="$(grep -aE "^\[livekit\] \[$ACCOUNT\] re-join probe cancelled: " "$A_LOG" | tail -1)"

REJOIN_EVIDENCE_OK=1
if [[ "$VARIANT" == "rejoin" && -z "$A_SELF_REJOIN_LINE" && -z "$A_REJOIN_PROBE_CANCELLED_LINE" ]]; then
  REJOIN_EVIDENCE_OK=0
fi
WALLET_LC="${WALLET,,}"
BEFORE_SID=$(jq --arg w "$WALLET_LC" -r '[.[]? | select(.identity == $w)][0].sid // empty' "$LOG_DIR/participants-before.json" 2>/dev/null)

if [[ -z "$B_ROOM" ]]; then
  # B never joined LiveKit (L4 already failed): there is no "B's participant" to compare against.
  evaluate L5 0 "not evaluable: B never joined LiveKit (see L4); A left line: ${A_LEFT_LINE:-none}; A re-joined line: ${A_REJOIN_LINE:-none}"
elif [[ -z "$EXPECTED_PARK_ROOM" ]]; then
  evaluate L5 0 "not evaluable: no displaced_session seen for A (see L2), so the expected parked room is unknown"
else
  AFTER_PARKED_JSON="$(cat "$LOG_DIR/participants-after-parked-room.json" 2>/dev/null || echo '{}')"
  AFTER_PARKED_COUNT=$(echo "$AFTER_PARKED_JSON" | jq --arg w "$WALLET_LC" '[.[]? | select(.identity == $w)] | length' 2>/dev/null || echo -1)
  AFTER_PARKED_SID=$(echo "$AFTER_PARKED_JSON" | jq --arg w "$WALLET_LC" -r '[.[]? | select(.identity == $w)][0].sid // empty' 2>/dev/null)
  PARKED_EVIDENCE="parked room $EXPECTED_PARK_ROOM afterwards: $AFTER_PARKED_COUNT participant(s) for the wallet (want 1), sid=${AFTER_PARKED_SID:-none}"
  RESTART_EVIDENCE=""
  [[ "$VARIANT" == "rejoin" ]] && RESTART_EVIDENCE="; self-initiated rejoin (original conn string): ${A_SELF_REJOIN_LINE:-not attempted}; probe cancellation: ${A_REJOIN_PROBE_CANCELLED_LINE:-not cancelled}"

  if [[ "$B_ROOM" != "$A_ROOM" ]]; then
    AFTER_A_COUNT=$(jq --arg w "$WALLET_LC" '[.[]? | select(.identity == $w)] | length' "$LOG_DIR/participants-after-a-room.json" 2>/dev/null || echo -1)
    AFTER_B_COUNT=$(jq --arg w "$WALLET_LC" '[.[]? | select(.identity == $w)] | length' "$LOG_DIR/participants-after-b-room.json" 2>/dev/null || echo -1)
    AFTER_B_SID=$(jq --arg w "$WALLET_LC" -r '[.[]? | select(.identity == $w)][0].sid // empty' "$LOG_DIR/participants-after-b-room.json" 2>/dev/null)
    EVIDENCE="A's old room $A_ROOM afterwards: $AFTER_A_COUNT participant(s) for the wallet (want 0); $PARKED_EVIDENCE; B's room $B_ROOM afterwards: $AFTER_B_COUNT (want 1), sid $BEFORE_SID -> ${AFTER_B_SID:-none}$RESTART_EVIDENCE"

    if [[ "$REJOIN_EVIDENCE_OK" == 1 && "$AFTER_A_COUNT" == "0" && "$AFTER_PARKED_COUNT" == "1" && "$AFTER_B_COUNT" == "1" && -n "$AFTER_B_SID" && "$AFTER_B_SID" != "$BEFORE_SID" ]]; then
      evaluate L5 1 "$A_LEFT_LINE; B took a different cluster (A's $A_ROOM -> B's $B_ROOM); $EVIDENCE"
    else
      evaluate L5 0 "A left line: ${A_LEFT_LINE:-none}; A re-joined line: ${A_REJOIN_LINE:-none}; $EVIDENCE"
    fi
  else
    AFTER_SHARED_COUNT=$(jq --arg w "$WALLET_LC" '[.[]? | select(.identity == $w)] | length' "$LOG_DIR/participants-after-a-room.json" 2>/dev/null || echo -1)
    AFTER_SHARED_SID=$(jq --arg w "$WALLET_LC" -r '[.[]? | select(.identity == $w)][0].sid // empty' "$LOG_DIR/participants-after-a-room.json" 2>/dev/null)
    EVIDENCE="shared room $A_ROOM afterwards: $AFTER_SHARED_COUNT participant(s) for the wallet (want 1, B's), sid $BEFORE_SID -> ${AFTER_SHARED_SID:-none}; $PARKED_EVIDENCE$RESTART_EVIDENCE"

    if [[ "$REJOIN_EVIDENCE_OK" == 1 && "$AFTER_SHARED_COUNT" == "1" && -n "$AFTER_SHARED_SID" && "$AFTER_SHARED_SID" != "$BEFORE_SID" && "$AFTER_PARKED_COUNT" == "1" ]]; then
      evaluate L5 1 "$A_LEFT_LINE; B took over A's own cluster $A_ROOM; $EVIDENCE"
    else
      evaluate L5 0 "A left line: ${A_LEFT_LINE:-none}; A re-joined line: ${A_REJOIN_LINE:-none}; $EVIDENCE"
    fi
  fi
fi

# L6: A's socket receives exactly ONE further "[ws-connector] Island" line after its
# DUPLICATE_SESSION disconnect, and it is the parking room (island-parked-<16 hex of S_A>); A's
# LiveKitJoiner logs the matching SWITCHED line, and the gatekeeper's takeover_parked_total moved.
TAKEOVER_PARKED_BEFORE=$(metric "$LOG_DIR/gk-metrics-before.txt" dcl_gatekeeper_cluster_takeover_parked_total)
TAKEOVER_PARKED_AFTER=$(metric "$LOG_DIR/gk-metrics-after.txt" dcl_gatekeeper_cluster_takeover_parked_total)
TAKEOVER_PARKED_DELTA=$((TAKEOVER_PARKED_AFTER - TAKEOVER_PARKED_BEFORE))

if [[ -z "$L1_DISCONNECT" ]]; then
  evaluate L6 0 "cannot evaluate: L1's disconnect line was never seen"
elif [[ -z "$EXPECTED_PARK_ROOM" ]]; then
  evaluate L6 0 "cannot evaluate: no displaced_session seen for A (see L2), so the expected parked room is unknown"
else
  DISCONNECT_LINE_NO=$(grep -naE 'disconnected by server: DUPLICATE_SESSION' "$A_LOG" | tail -1 | cut -d: -f1)
  ISLAND_AFTER_LINES="$(awk -v n="$DISCONNECT_LINE_NO" 'NR>n && /^\[ws-connector\] Island /' "$A_LOG")"

  if [[ -z "$ISLAND_AFTER_LINES" ]]; then
    ISLAND_AFTER_COUNT=0
  else
    ISLAND_AFTER_COUNT=$(printf '%s\n' "$ISLAND_AFTER_LINES" | wc -l | tr -d ' ')
  fi
  LAST_ISLAND_AFTER="$(printf '%s\n' "$ISLAND_AFTER_LINES" | tail -1)"
  EVIDENCE="expected parked room $EXPECTED_PARK_ROOM (from S_A=${S_A:0:8}…); saw $ISLAND_AFTER_COUNT further Island line(s) after line $DISCONNECT_LINE_NO: ${LAST_ISLAND_AFTER:-none}; ${A_SWITCHED_LINE:-no SWITCHED line}; takeover_parked_total ${TAKEOVER_PARKED_BEFORE}->${TAKEOVER_PARKED_AFTER}"

  if [[ "$ISLAND_AFTER_COUNT" == 1 \
        && "$LAST_ISLAND_AFTER" == *"Island ${EXPECTED_PARK_ROOM} "* \
        && -n "$A_SWITCHED_LINE" && "$A_SWITCHED_LINE" == *"-> '${EXPECTED_PARK_ROOM}'"* \
        && "$TAKEOVER_PARKED_DELTA" -ge 1 ]]; then
    evaluate L6 1 "$EVIDENCE"
  else
    evaluate L6 0 "$EVIDENCE"
  fi
fi

# ---------- 9b. order2/ghost only: which path delivered B's island ----------
# Not a leg — L4 already covers whether B got an island at all. This says which of the two
# delivery paths comms-gatekeeper actually used, per docs/e2e-livekit.md's own "mechanism worth
# knowing about": the direct publish (Pulse's cluster_change -> immediate mint) races B's
# ws-connector handshake, and gatekeeper's connect re-announce is what recovers a dropped direct
# publish. ws-connector's own connect NATS event is used as B's "Welcome received" instant — it
# fires immediately after ws-connector sends the Welcome to the client (ws-handler.ts), so the two
# are the same moment for this purpose. Ordering, not wall-clock precision (docs/e2e-livekit.md §6).
DELIVERY_PATH_SUMMARY=""
if [[ "$VARIANT" == "order2" || "$VARIANT" == "ghost" ]]; then
  B_CONNECT_LINE="$(grep -aE "peer\.${WALLET}\.connect .*payload=" "$NATS_LOG" 2>/dev/null | sed -n '2p')"
  B_CONNECT_TS="$(echo "$B_CONNECT_LINE" | awk '{print $1}')"
  ISLAND_B_LINES="$(grep -aF "engine.peer.${WALLET}.island_changed.${B_SESSION}" "$NATS_LOG" 2>/dev/null)"
  ISLAND_B_COUNT=0
  DIRECT_COUNT=0
  REANNOUNCE_COUNT=0
  while IFS= read -r ib_line; do
    [[ -z "$ib_line" ]] && continue
    ISLAND_B_COUNT=$((ISLAND_B_COUNT + 1))
    ib_ts="$(echo "$ib_line" | awk '{print $1}')"
    if [[ -n "$B_CONNECT_TS" && "$ib_ts" < "$B_CONNECT_TS" ]]; then
      DIRECT_COUNT=$((DIRECT_COUNT + 1))
    else
      REANNOUNCE_COUNT=$((REANNOUNCE_COUNT + 1))
    fi
  done <<<"$ISLAND_B_LINES"

  if [[ $ISLAND_B_COUNT -eq 0 ]]; then
    DELIVERY_PATH_SUMMARY="none — no island_changed.{B's session} seen on NATS at all (L4 evidence above should already show this as a FAIL)"
  elif [[ $DIRECT_COUNT -gt 0 && $REANNOUNCE_COUNT -eq 0 ]]; then
    DELIVERY_PATH_SUMMARY="direct publish before B's connect only — no post-connect delivery observed; see L4/V1 for recovery outcome"
  elif [[ $DIRECT_COUNT -eq 0 && $REANNOUNCE_COUNT -gt 0 ]]; then
    DELIVERY_PATH_SUMMARY="re-announce only — the direct publish (if it happened at all) was dropped; every island_changed for B's session arrived at/after B's connect (~Welcome)"
  else
    DELIVERY_PATH_SUMMARY="both — a direct publish before B's connect AND a delivery at/after it; V1 checks recovery evidence"
  fi

  {
    echo "island_changed.{B's session} on NATS: $ISLAND_B_COUNT total ($DIRECT_COUNT before B's connect~Welcome, $REANNOUNCE_COUNT at/after)"
    echo "B's connect (~Welcome received) at: ${B_CONNECT_TS:-not seen} — $B_CONNECT_LINE"
    echo "$ISLAND_B_LINES"
    echo "Conclusion: $DELIVERY_PATH_SUMMARY"
  } >"$LOG_DIR/delivery-path.txt"
fi

# ---------- 9c. gatekeeper reannounce_*/takeover_* metric deltas ----------
{
  for m in dcl_gatekeeper_cluster_reannounce_unresolved_total \
           dcl_gatekeeper_cluster_reannounce_skipped_other_session_total \
           dcl_gatekeeper_cluster_reannounce_check_failed_total \
           dcl_gatekeeper_cluster_reannounce_suppressed_total \
           dcl_gatekeeper_cluster_reannounce_attempted_total \
           dcl_gatekeeper_cluster_reannounce_evicted_stale_total \
           dcl_gatekeeper_cluster_reannounce_stale_evict_failed_total \
           dcl_gatekeeper_cluster_reannounce_parked_total \
           dcl_gatekeeper_cluster_takeover_evicted_total \
           dcl_gatekeeper_cluster_takeover_absent_total \
           dcl_gatekeeper_cluster_takeover_failed_total \
           dcl_gatekeeper_cluster_takeover_parked_total \
           dcl_gatekeeper_cluster_takeover_skipped_live_total \
           dcl_ws_connector_island_changed_no_session_socket_total; do
    before=$(metric "$LOG_DIR/gk-metrics-before.txt" "$m")
    after=$(metric "$LOG_DIR/gk-metrics-after.txt" "$m")
    # The one ws-connector metric in this list lives on the other service's /metrics.
    if [[ "$m" == dcl_ws_connector_* ]]; then
      before=$(metric "$LOG_DIR/ws-metrics-before.txt" "$m")
      after=$(metric "$LOG_DIR/ws-metrics.txt" "$m")
    fi
    printf '%-55s %s -> %s\n' "$m" "$before" "$after"
  done
} >"$LOG_DIR/gk-metric-deltas.txt"

# ---------- 9d. ghost(none) only: this run's L4 FAIL is by design, not a regression ----------
# --ghost-attribute none mints a ghost with no dclsession attribute at all — the "cannot tell"
# case the fix deliberately keeps fail-closed (a missing attribute is what a pre-attribute token,
# an older Pulse/ws-connector, or a LiveKit without attribute support all produce too, so treating
# it as stale would evict live devices on every reconnect). Printed unconditionally for this
# combination so the PASS/FAIL table above is never misread as "the fix regressed".
GHOST_NOTE=""
if [[ "$VARIANT" == "ghost" && "$GHOST_ATTRIBUTE" == "none" && "$VARIANT_VALID" == 1 ]]; then
  GHOST_NOTE="ghost(none): suppressed as designed (cannot tell) — L4 FAIL and reannounce_suppressed_total +1 here are the expected result, not a regression"
fi

# ---------- 10. report ----------
mask() { sed -E 's/0x[0-9a-fA-F]{8}[0-9a-fA-F]{32}/0x…/g; s/access_token=[^ &"]+/access_token=<redacted>/g'; }

echo
echo "== PASS/FAIL (variant=$VARIANT) =="
printf '%-4s %-5s %s\n' "Leg" "" "Evidence"
while IFS=$'\t' read -r leg status evidence; do
  printf '%-4s %-5s %s\n' "$leg" "$status" "$evidence"
done <"$RESULT_FILE" | mask

if [[ -n "$DELIVERY_PATH_SUMMARY" ]]; then
  echo
  echo "== Delivery path for B (variant=$VARIANT) =="
  cat "$LOG_DIR/delivery-path.txt" | mask
fi

if [[ -n "$GHOST_NOTE" ]]; then
  echo
  echo "== Note (variant=$VARIANT --ghost-attribute=$GHOST_ATTRIBUTE) =="
  echo "$GHOST_NOTE"
fi

echo
echo "== Gatekeeper reannounce_*/takeover_* metric deltas (variant=$VARIANT) =="
cat "$LOG_DIR/gk-metric-deltas.txt"

echo
echo "logs and evidence in $LOG_DIR"

exit $overall
