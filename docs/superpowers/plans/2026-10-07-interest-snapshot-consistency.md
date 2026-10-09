# AoI snapshot consistency implementation history and sequence plan

> Historical record: implementation instructions below describe completed or superseded work. Use [interest snapshot consistency](../../interest-snapshot-consistency.md) for the current contract. Measurements identify their implementation variant; earlier experiments are not results for the current sequence reader.

The initial implementation returned a copy of the exact subject snapshot accepted by interest management. Its contract and results below describe commit `85476d3`. The sequence follow-up retains the accepted sequence instead, with a measured hard fallback on eviction; its plan follows the initial results.

Branch: `fix/interest-snapshot-consistency`. Baseline commit: `e42f167` (`fix: simplify realm view lifecycle`). Baseline validation: 951 tests passed, 14 skipped.

## Initial snapshot-copy contract and behavior

- `InterestEntry` contains `Subject`, `Tier`, a value copy of `PeerSnapshot` named `Snapshot`, and the existing immutable `IdentityRegistration` named `Identity`.
- `IInterestCollector.Add` takes `(PeerIndex subject, PeerViewSimulationTier tier, in PeerSnapshot snapshot, IdentityRegistration identity)`. The reusable collector accepts each subject once per query; the first accepted snapshot and registration win.
- `IAreaOfInterest` supports player queries using `in PeerSnapshot observerSnapshot` and listener queries using `SceneListenerState listener`. Both return validated snapshots through the collector.
- Grid cells supply candidates. Interest management validates realm and distance for players, and the announced realm/parcel pair for listeners, using the snapshot it returns. Tier calculation uses that same snapshot.
- Capture identity before the snapshot read and verify its reference afterward, following the existing `ClusterTracker` pattern. Simulation validates active state and registration before consumption, uses the captured wallet, and records the registration on the view. This preserves disconnect suppression and detects slot reuse even for the same wallet/session without adding shared state.
- A teleport before the subject read participates in that query. A teleport after acceptance participates in a later query. No global snapshot across workers is implied.
- Subjects excluded from interest stop receiving state and retain the existing stale-view grace before `PlayerLeft`. Remove the special immediate retirement caused only by the former second-read race. Observer realm invalidation and a visible subject changing between observed realms still produce the existing lifecycle messages.
- Historical reads may resolve earlier events and resync baselines, but cannot replace the captured target with newer state. Ring eviction falls back to the captured snapshot. Targeted resync deltas require a baseline earlier than the captured target.
- Process the target sequence directly from the captured snapshot so an evicted target teleport or stop remains available. Revalidate registration after historical reads before their results are delivered.
- Keep identity handling, profile announcements, self mirror, tier pacing, listener metrics, and disconnection behavior covered. No protocol generation, new shared boards, locks, or cross-worker state access.

## Execution

### 1 Establish failing regression cases

- [x] Interest subagent adds deterministic stale-grid/realm tests that fail on the baseline.
- [x] Simulation subagent adds collection-to-delivery interleavings for realm changes, same-realm movement, and event publication; asserts delivery uses the accepted state.
- [x] Parent runs those tests before implementation and records the failures.

### 2 Make interest management own eligibility

Owner: interest subagent. Files: `InterestManagement/IAreaOfInterest.cs`, `IInterestCollector.cs`, `SpatialHashAreaOfInterest.cs`, `NullAreaOfInterest.cs`, interest/collector tests, and directly affected spatial/listener comments.

- [x] Extend the interfaces and collector, retaining reusable buffers and deduplication.
- [x] Validate and return the snapshot already read by spatial interest collection.
- [x] Move listener collection from simulation into interest management; validate realms before parcel membership.
- [x] Cover overlapping cells, identical parcel numbers in different realms, no realm, tier consistency, and captured snapshots surviving later publication.

### 3 Consume accepted snapshots in simulation

Owner: simulation subagent. Files: `Peers/Simulation/PeerSimulation.cs`, the active-state accessor in `SnapshotBoard.cs`, `PeerToPeerView.cs`, and `PeerSimulationTests*.cs`.

- [x] Route both observer types through interest management and include the captured observer snapshot in self mirror.
- [x] Remove the second latest-snapshot read and both AoI guard helpers.
- [x] Preserve lifecycle and tier behavior; keep history and resync bounded by the captured target.
- [x] Adapt fixture callbacks to the new contract. Change existing assertions only for the documented departure timing or query contract; retain unrelated expectations.
- [x] Cover existing/new views, skipped tier ticks, pending resync, listener transitions, eviction, and post-collection disconnects.

### 4 Integrate and measure

Owner: parent, with a benchmark/documentation subagent after implementation stabilizes.

- [x] Build and run focused tests, then the full suite once integration passes.
- [x] Update benchmark implementations for the contract and measure collection plus snapshot consumption. Account for larger collector entries and removal of the second board read; avoid allocations after warmup and unnecessary struct copies.
- [x] Update the architecture documentation to describe candidates, accepted snapshots, and departure timing.

### 5 Review and finish

- [x] Independent subagent reviews the diff against this plan, including concurrency, identity lifetime, historical reads, deduplication, and test strength.
- [x] Fix actionable findings and repeat relevant checks; obtain a final independent review.
- [x] Record verification and any material benchmark limits below. Commit the completed AoI work separately from the baseline commit. Do not push or create a PR.

## Verification

Use the installed .NET 10 SDK on this Windows host. Serialize builds and tests across agents. Never regenerate protocol files.

```powershell
dotnet test src/DCLPulseTests/DCLPulseTests.csproj --configuration Release --no-restore -p:GenerateProto=false -p:FetchGeoDb=false --verbosity minimal
dotnet build src/DCLPulse/DCLPulse.sln --configuration Release --no-restore -p:GenerateProto=false -p:FetchGeoDb=false --verbosity minimal
git diff --check
```

## Results

Baseline RED evidence: all three initial interest regressions failed (stale cell realm mismatch and duplicate collection). Initial simulation interleavings had 12 failures and two passes; all four additional slot-reuse cases failed. Failures include a destination realm announced through old interest, delivery of newer poses/events, an evicted teleport becoming a delta, and same-wallet reuse producing a reversed sequence delta.

Completed with separate interest and simulation implementers, an independent design/diff reviewer, and parent-run verification. Final review found no actionable defects.

- Final full suite: 992 passed, 14 skipped, 0 failed. Focused integration: 246 passed; after the collector optimization, 229 affected tests passed.
- Release solution build succeeded; whitespace checks passed. Existing nullable warnings and the benchmark project's Newtonsoft.Json advisory remain outside this change.
- Regression cases cover stale cell references, post-collection realm/parcel/movement/events, captured target eviction including Completed stop reason, pending resync, tier pacing, inactive subjects, and recycled slots with the same or different wallet.
- Lifetime fences after historical reads were reviewed but are not forced by a dedicated deterministic test; no test-only read hooks were introduced.

The warmed dense-grid query/consumption benchmark used three warmups, eight 200 ms iterations, and one launch. Results after inlining and narrower deduplication keys:

| Peer count | Legacy query and consumption | Accepted snapshot query and consumption | Reported ratio |
| --- | --- | --- | --- |
| 128 | 1.106 us | 3.915 us | 3.55 |
| 512 | 4.696 us | 15.514 us | 3.31 |
| 4095 | 46.370 us | 150.435 us | 3.24 |

Captured entries are 192 bytes versus 12 bytes for the legacy ID/tier entry. Both buffers are reused; the captured buffer also retains deduplication storage. MemoryDiagnoser reported zero bytes per query at 128/512 peers and 1–3 bytes at 4095 in this short in-process run, which does not prove absolute zero allocation. The first captured implementation measured 4.127/16.526/158.787 us respectively; the second run is modestly lower but does not isolate each optimization's effect.

This benchmark excludes concurrent writers, scheduling, historical scans, profile lookups, and network encoding. It measures the cost of the changed interest contract, not complete simulation throughput. The snapshot capture and identity fences improve consistency at a measured CPU and retained-buffer cost.

Benchmark command:

```powershell
dotnet run --project src/DCLPulseBenchmarks/DCLPulseBenchmarks.csproj --configuration Release --no-build --no-restore -- --filter '*InterestSnapshotBenchmarks*' --warmupCount 3 --iterationCount 8 --launchCount 1 --iterationTime 200
```

The implementation is committed separately from the baseline; no push or PR is part of this task.

## Bitmap experiment (reverted)

A reusable bitmap sized from `SnapshotBoard.MaxPeers` was tested as a replacement for the collector's `HashSet<uint>`. Each slot occupied one bit; `Clear` reset the bitmap for the next query. First acceptance retained the snapshot, tier, identity, and transport tag. At 4,095 slots the bitmap payload was 512 bytes. The bitmap, capacity wiring, dedicated tests, and benchmark variants were reverted at the user's request in favor of the simpler HashSet implementation. The measurements below record the experiment.

Bitmap verification: 228 affected tests passed; the full suite passed with 999 tests and 14 skipped. An initial full run encountered an unrelated HTTP test endpoint port collision and passed on retry. Release solution build and whitespace checks passed.

The experimental benchmark compared concrete collector calls with identical entries and consumption, avoiding AoI interface dispatch differences between collector implementations. Collector-only means:

| Peer count | Bitmap collection and consumption | HashSet collection and consumption |
| --- | --- | --- |
| 128 | 2.797 us | 2.849 us |
| 512 | 11.736 us | 12.469 us |
| 4095 | 92.462 us | 98.680 us |

Means were 2–6% lower with the bitmap; the 4,095-peer HashSet confidence interval was wide, so that case does not establish a precise speedup. Complete accepted query and consumption measured 3.848/15.326/143.803 us, versus legacy 1.097/4.723/47.893 us. A longer rerun with five warmups and twelve 250 ms iterations still measured ratios of 3.64/3.18/3.03. The bitmap reduced deduplication storage but did not materially resolve the overall slowdown. Earlier benchmark limits still apply.

## Accepted-sequence follow-up

The initial read-only evaluation proposed deferring an evicted target rather than replacing it. The chosen implementation uses the previous comparison logic as an explicitly undesired hard fallback, with an exported counter to measure whether it occurs. It retains the simpler HashSet and is committed separately from the snapshot-copy implementation.

### Contract

The current contract, overwrite analysis, fallback limits, and regression obligations are defined in [Interest snapshot consistency](../../interest-snapshot-consistency.md). This plan retains implementation history and measured results. Operator metric semantics remain in [Interest Snapshot Evictions](../../metrics.md#interest-snapshot-evictions).

### Execution

- [x] Add regression tests first and run them against the snapshot-copy implementation. RED: 14 failed and 4 passed across 16 simulation cases and 2 metric cases; add one further multi-realm listener lifecycle case.
- [x] Interest subagent migrates the contract and retained-sequence tests; simulation subagent implements exact resolution, hard fallback, and lifecycle/resync regression cases.
- [x] Dashboard-curator subagent wires the unlabelled counter through collection and Prometheus export, documents it, and updates the ignored local Grafana dashboard. Dashboard lint: zero errors and warnings.
- [x] Parent migrates benchmarks and architecture documentation. Focused integration: 240 passed. Release solution build passed.
- [x] Run the full suite and benchmark the healthy retained-target path against the existing legacy reference; record entry sizes and benchmark limits.
- [x] Independent subagent reviews simulation, metrics, tests, and benchmark fidelity; no actionable findings.
- [x] Commit the sequence follow-up after user review and authorization. The dashboard export remains ignored and requires operator import into Grafana.

### Follow-up verification

Full suite: 1,011 passed, 14 skipped, zero failed. Focused integration: 240 passed. Release solution build succeeded; existing nullable warnings and the benchmark dependency advisory remain outside this change. Dashboard lint reports zero errors and warnings; whitespace checks pass. Independent review covered exact resolution, registration fences, hard fallback, bounded history/resync, changed eviction expectations, metric export, and benchmark fidelity.

Measured entry size: 24 bytes, down from 192 bytes for the copied snapshot. HashSet deduplication remains unchanged. The warmed query/consumption benchmark used five warmups and twelve 250 ms iterations with one launch:

| Peer count | Legacy query and consumption | Accepted sequence query and consumption | Reported ratio |
| --- | --- | --- | --- |
| 128 | 1.129 us | 2.029 us | 1.80 |
| 512 | 4.665 us | 8.293 us | 1.78 |
| 4095 | 46.421 us | 86.899 us | 1.87 |

The static single-threaded run retains every target and measures neither fallback frequency/cost nor full simulation throughput. The 4,095-peer accepted mean has a 99.9% confidence interval of 78.975–94.823 us. MemoryDiagnoser reports zero bytes at 128/512 peers and 1 byte per operation for both paths at 4,095, so this run does not prove absolute zero allocation. Results are saved locally under `BenchmarkDotNet.Artifacts/results/DCLPulseBenchmarks.InterestSnapshotBenchmarks-report-github.md`.

```powershell
dotnet run --project src/DCLPulseBenchmarks/DCLPulseBenchmarks.csproj --configuration Release --no-build --no-restore -- --filter '*InterestSnapshotBenchmarks.*QueryAndConsume*' --warmupCount 5 --iterationCount 12 --launchCount 1 --iterationTime 250
```

## PR review follow-up

Review on 2026-10-08 identified ten non-blocking findings. The follow-up removes the redundant observer-mode argument, marks the announced wallet as diagnostic, shares the production accepted-sequence reader with the benchmark, and removes benchmark fallback/counter side effects. Clustering comments now describe first-collected grid attribution under weakly consistent reads. The standing consistency document describes the current contract without branch/date or development-history dependencies.

Historical event loss is distinct from accepted-target eviction. The existing subject round-trip case already covers both teleports being overwritten; additional same-realm and round-trip cases assert that the retained target becomes a delta and records no target eviction. A sequence gap does not prove a teleport, so the implementation does not invent a snap from missing history.

The reseeding registration guard remains: alias retirement emits a message and calls the logger between target resolution and seeding. Two deterministic disconnect/recycle cases passed with the guard and failed with an extra stale `PlayerJoined` when it was temporarily removed. The post-history and resync-baseline fences protect separate shared reads. The explicitly requested hard fallback retains its previous realm-only player or realm/parcel listener check; using the full interest eligibility predicate would change the player distance/tier contract. HashSet deduplication remains the chosen implementation.

Dashboard-curator confirmed its original create flow and reviewed panel 97, `Interest Snapshot Evictions`, against the actual local export and PR-head formatter. Strict lint reports zero errors and warnings; live import remains an operator step.

Verification: 244 focused cases and the full suite passed (1,015 passed, 14 skipped). Release solution build, documentation links, and whitespace checks passed. The benchmark rerun used the same five warmups and twelve 250 ms iterations:

| Peer count | Legacy query and consumption | Accepted sequence query and consumption | Reported ratio |
| --- | --- | --- | --- |
| 128 | 1.072 us | 1.863 us | 1.74 |
| 512 | 4.497 us | 7.732 us | 1.72 |
| 4095 | 48.820 us | 76.727 us | 1.59 |

These ratios mean roughly 59–74% more query/consumption time than legacy, not a throughput improvement. The 4,095-peer accepted mean has a 99.9% confidence interval of 71.226–82.227 us; the baseline is also variable. Cross-run differences do not isolate the effect of extraction or inlining. MemoryDiagnoser again reports zero bytes at 128/512 peers and 1 byte for both paths at 4,095. This retained-target microbenchmark excludes full fan-out, encoding, transport, concurrent writers and scheduling; peak-density tick-budget headroom remains unestablished.

## Second review follow-up

The exact-sequence reader now checks the captured registration only after `SnapshotBoard.TryRead`. The board rejects inactive slots, and the post-read active/identity veto rejects disconnects and recycling even when sequence numbers repeat. Latest-read collectors still capture identity before reading; their discovery step differs from consumption of an already accepted registration.

Resolution remains before tier pacing. Four new cases cover TIER_1/TIER_2 eviction outside the observer realm and disconnect/recycling on a skipped tick. All four passed with the existing ordering and failed when the proposed same-registration shortcut was temporarily moved ahead of resolution: it missed `PlayerLeft`/the eviction metric and refreshed stale views. Preserving the explicitly requested fallback avoids this change in behavior.

The protocol contract now describes subject A-to-B-to-A retention and overwritten teleport markers. Health counters have their own metrics section, the plan is marked historical, and benchmark documentation records the reverted bitmap experiment and excludes tier pacing from its scope. Optional wallet/logging, send-context, and cross-component helper refactors remain outside this follow-up; the wallet field is diagnostic, send bypasses can combine, and clustering also verifies the wallet's live binding. The eviction counter remains available in Prometheus/Grafana; the console omission is documented.

Verification: Release solution build and all tests passed (1,019 passed, 14 skipped); documentation links and whitespace checks passed. The same five-warmup, twelve-iteration benchmark produced:

| Peer count | Legacy query and consumption | Accepted sequence query and consumption | Reported ratio |
| --- | --- | --- | --- |
| 128 | 1.161 us | 1.823 us | 1.58 |
| 512 | 4.833 us | 7.269 us | 1.51 |
| 4095 | 46.511 us | 72.755 us | 1.57 |

This run measures roughly 51–58% more query/consumption time than legacy. The 4,095-peer accepted mean has a 99.9% confidence interval of 66.328–79.181 us. Cross-run differences do not isolate the removed check's benefit. Allocation results and workload limits are unchanged; peak-density tick headroom remains unverified.
