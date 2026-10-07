# AoI snapshot consistency implementation plan

Interest management will return the exact subject snapshot it accepted. Simulation will deliver that snapshot without reading the subject's latest state again or repeating realm and parcel eligibility checks. This closes the gap between interest selection and state delivery while retaining the existing worker isolation and snapshot history.

Branch: `fix/interest-snapshot-consistency`. Baseline commit: `e42f167` (`fix: simplify realm view lifecycle`). Baseline validation: 951 tests passed, 14 skipped.

## Contract and behavior

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
