// Prints every subject seen on the harness broker, one line per message, until killed.
//
// Two subjects get special treatment because a plain byte dump does not answer the question
// the takeover scenario needs answered:
//   - `peer.*.cluster_change` is decoded with the real wire schema so `session`,
//     `displaced_session` and `displaced_cluster_id` (PeerClusterChange fields 3/4/5 — see
//     docs/e2e-livekit.md) are visible instead of protobuf noise. Those three fields are the
//     only place a takeover ever states itself: L2 in the takeover scenario is "a cluster_change
//     arrived naming session=B, displaced_session=A".
//   - `peer.*.connect` payloads (the ws-connector session key, UTF-8) are printed so they can be
//     compared with the session token of the `engine.peer.*.island_changed.*` subjects.
//
// Everything else (including `engine.peer.*.island_changed[.session]`) is printed as a bare
// subject + byte length line — enough to see arrival and ordering (L4/L6 evidence) without
// pulling in a decoder for every message shape this harness does not need to inspect.
//
// Resolve `nats` and `@dcl/protocol` from comms-gatekeeper's node_modules, since that is the
// one checkout in this harness guaranteed to carry both:
//   cd <comms-gatekeeper> && NODE_PATH=<comms-gatekeeper>/node_modules node nats-watch.js [natsUrl]
const { connect } = require('nats')
const { PeerClusterChange } = require('@dcl/protocol/out-js/decentraland/pulse/pulse_clusters.gen')

const url = process.argv[2] || 'nats://127.0.0.1:4322'

function describeClusterChange(data) {
  try {
    const change = PeerClusterChange.decode(data)
    return (
      ` cluster_id=${change.clusterId} realm=${change.realm} session=${change.session} ` +
      `displaced_session=${change.displacedSession || '(none)'} displaced_cluster_id=${change.displacedClusterId || '(none)'}`
    )
  } catch (e) {
    return ` <decode failed: ${e.message}>`
  }
}

;(async () => {
  const nc = await connect({ servers: url })
  process.stdout.write(`connected ${url}\n`)
  const sub = nc.subscribe('>')

  for await (const m of sub) {
    let extra = ''

    if (m.subject.endsWith('.cluster_change')) {
      extra = describeClusterChange(m.data)
    } else if (m.subject.endsWith('.connect')) {
      extra = ` payload=${Buffer.from(m.data).toString('utf8')}`
    }

    process.stdout.write(`${new Date().toISOString()} ${m.subject} ${m.data.length}B${extra}\n`)
  }
})().catch((e) => {
  process.stderr.write(`watcher failed: ${e.message}\n`)
  process.exit(1)
})
