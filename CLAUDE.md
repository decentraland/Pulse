# Pulse agent instructions

Pulse is a .NET 10 Generic Host for avatar synchronization and realm-scoped clustering. It relays validated client state; scene simulation and LiveKit token issuance belong to other services.

## Read by task

| Before changing | Read |
| --- | --- |
| Authentication, peer lifecycle, synchronization or scene listeners | [Architecture](docs/ai-agent-context.md) |
| Interest eligibility, sequence resolution, eviction or deduplication | [Interest snapshot consistency](docs/interest-snapshot-consistency.md) |
| Cluster topology, sticky IDs, debounce or feed delivery | [Clustering](docs/clustering-on-aoi.md) |
| Assignment lookup, recovery hints, takeover recovery or deployment overlap | [Assignment recovery](docs/cluster-assignment-recovery.md) |
| NATS request/reply, broker permissions or delivery guarantees | [NATS use](docs/nats-usage.md) |
| Admission, validation, rate limits, bans or client rejection handling | [Hardening](docs/hardening.md) |
| Dynamic IP limits or Unleash configuration | [Feature flags](docs/feature-flags.md) |
| Metrics or dashboard panels | [Metrics](docs/metrics.md) and the dashboard rule below |
| Builds, native dependencies, bots or deployment | [README](README.md); [LiveKit harness](docs/e2e-livekit.md), [debugging](docs/debugging.md) or [deploy canvas](docs/slack-canvas.md) as needed |

## Ownership and design

- Workers own `peerStates` and `observerViews`, sharded by `PeerIndex.Value % workerCount`. Route cross-worker decisions through `MessagePipe.incomingChannel` -> `PeersManager` -> the owning worker's channel. Shared transport/allocator coordination stays at that layer; peer rekeying or direct worker-state migration is unsupported.
- Fence slot-based state with `IdentityRegistration`; wallet and sequence equality cannot detect a same-wallet, same-session reconnect. Include recycled-slot cases in lifecycle tests.
- Handlers publish through `PeerSnapshotPublisher`, which owns sequence numbering, decoding, ledger stamping and spatial-index updates. Extend the `PeerSnapshot` ledger for worker-written shared state; a separate board must justify a different lifecycle or read pattern.
- Add abstractions for reuse, polymorphism or a test seam. Merge components that have one consumer and no independent behavior. Pass dependencies as objects rather than per-field delegates; use existing snapshot and quantization primitives.
- Keep `PeerSimulation` orchestration as named private calls, with each new behavior in a focused method.
- Give retries, resyncs and sweeps a bounded termination condition. Components consume injected dependencies; composition stays in `Program.cs`.

## Code and tests

Use `DCLPulse.sln.DotSettings` and nearby files for style. Prefer primary constructors for trivial DI, file-scoped namespaces and `var` when the type is clear. Tests use NUnit and NSubstitute, fixtures named `{Feature}Tests`, and behavior-named methods with arrange/act/assert structure.

- **Hot paths:** per-tick fan-out and per-packet parsing/serialization stay allocation-free. Use loops, spans and reusable buffers; avoid LINQ, boxing, captures and string building. Mark hot lambdas/local functions `static`. Keep debug-only work behind `#if DEBUG` or a config-gated cold path. Pair every rent with release and dispose owned resources.
- **Async:** suffix awaitables `Async`; dispose owned cancellation sources. Background/channel loops handle cancellation as shutdown and log other failures. Use cancellation checks in hot loops; throw in awaited flows that handle them.
- **Nullability:** express absence as `T?` and trust non-null annotations. Fix warnings at their source; null-forgiving `!` is allowed only on NSubstitute proxies in tests. Keep NRT enabled.
- **Comments:** document public types and non-obvious public members. State what the annotated code guarantees, with sentence case and a period. Omit line numbers, commented-out code and block comments.
- **Ordering:** enums/delegates -> fields -> properties -> events -> methods -> nested types; within groups, public -> internal -> protected -> private. Fields: constants/static readonly -> static -> readonly -> public -> private. Methods: constructor -> Dispose -> public API -> helpers after their callers.

## Verification

Use the host's installed .NET 10 SDK and pass the solution explicitly:

```sh
dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Keep `GenerateProto=false` unless regeneration is requested. Schema sources live in the sibling protocol repository; generated C# is committed here. Native-package restore and version pinning are documented in the README.

Benchmarks belong in `src/DCLPulseBenchmarks`. Run Release builds and select suites through `BenchmarkSwitcher`, rather than editing `Program.cs`. Record reproducible measurements and rejected optimizations in the benchmark documentation.

Changes to the restore/build pipeline, package references or `packages/` ignore rules require building all three images; selective pre-restore COPY lists must include new inputs:

```sh
docker build -f src/DCLPulse/Dockerfile -t pulse-prod-test .
docker build -f src/DCLPulse/Dockerfile.dev-debug -t pulse-dev-debug-test .
docker build -f Dockerfile.debug -t pulse-debug-test .
```

## Dashboard completion

An added, renamed or relabelled exported series is complete when the `dashboard-curator` agent (`.claude/agents/dashboard-curator.md`) has updated the panels and `python scripts/dashboard-lint.py` reports zero errors. Use that agent for dashboard reviews and consolidation too.

The local `pulse-server-dashboard.json` export stays gitignored and requires operator import into Grafana. Deployment names, hostnames and datasource IDs stay in that JSON; this public repository's docs, PR text and reports must omit them.
