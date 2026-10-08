# Interest snapshot consistency

Interest management owns subject eligibility. Simulation preserves that decision by reading the exact sequence accepted by the query. A missing accepted sequence enters a measured, explicitly undesired hard fallback to the previous comparison logic.

## Accepted sequence and registration

Realm grids supply candidates. A reader can retain a cell set after a subject moves or teleports, so the grid alone cannot establish eligibility. `SpatialHashAreaOfInterest` validates one snapshot against the player realm and distance, or the listener's announced realm and parcels, and calculates the tier from that same snapshot.

`InterestEntry` retains `(Subject, Tier, uint Seq, Identity)`. `InterestCollector` uses a reusable `HashSet<uint>` to accept each subject once; the first accepted sequence, tier, and registration win. Deduplication matters when movement during enumeration exposes the same subject through old and new cells or through two listener realms. Replacing the snapshot with its sequence reduces entry copying; it preserves this deduplication contract.

`InterestSnapshotReader` reads that exact sequence for simulation and the retained-target benchmark. `SnapshotBoard.TryRead` rejects inactive slots; a post-read active-state and `IdentityRegistration` reference check rejects disconnects and slot reuse during the read. Interest already captured the registration, so an additional pre-read registration check is unnecessary. Wallet or sequence equality alone cannot identify a connection: a recycled slot can repeat both. Retained targets require no second eligibility comparison, even if the subject has since teleported. Later publications normally participate in the next query; the query does not create a global snapshot across workers.

## Why a recorded sequence can disappear

`SnapshotBoard.Publish` writes to `Seq % ringCapacity`. With consecutive publications, sequence `s + capacity` replaces the slot holding `s`. The sequence identifies a published value; recording it does not reserve its ring slot. The seqlock protects the read from a torn write, and the historical read checks the stored sequence before succeeding. It does not extend retention.

Collection and delivery run sequentially on the observer's worker, while another worker can keep publishing the subject. The gap includes the remainder of collection and delivery to earlier subjects, as well as thread scheduling delays. There is no bound that guarantees fewer than one ring's worth of publications during that gap.

Movement and discrete events have separate token buckets. A subject worker drains queued events before its simulation tick, so allowed mixed bursts can overwrite a selected slot faster than steady publishing would suggest. Resolve current capacities and rate limits from runtime configuration when investigating an occurrence; production frequency is measured by the target-eviction counter.

A retained target preserves its own state, while earlier event markers can already be overwritten. If both teleports in an A-to-B-to-A round trip disappear from history, the observer receives a delta to the retained target instead of a teleport snap. This historical loss also occurs for same-realm teleports and does not increment the target-eviction counter. A sequence gap alone cannot distinguish movement from a lost teleport.

## Eviction hard fallback

Only an exact-sequence miss with the accepted registration still active increments `PulseMetrics.Simulation.INTEREST_SNAPSHOT_EVICTED` and enters `TryResolveEvictedInterestSnapshotHardFallback`. That helper reads the latest snapshot, rechecks registration, and applies the original comparison:

| Observer | Latest snapshot accepted when |
| --- | --- |
| Player | Its realm equals the observer's realm. |
| Scene listener | Its realm is announced and its parcel belongs to that realm's parcel set. |

The player comparison checks realm only. Distance and tier remain the original interest query's decision, including on this fallback. A rejected existing view is removed and receives one `PlayerLeft`; a rejected new subject receives no `PlayerJoined`.

Resolve the target before aliasing, realm lifecycle handling, and tier pacing. An evicted target outside the observer's realm still retires its view on a skipped tier tick; inactive or recycled entries do not refresh the stale-view deadline. Comparing only the entry and view registrations before skipping would bypass these checks. History scans and resync responses remain bounded by the resolved target. A pending resync bypasses tier pacing and receives the resolved state; a skipped ordinary tier tick still stamps an existing accepted view as visible.

The fallback can also lose the accepted target's own teleport, emote event, or stop reason. Latest state cannot reconstruct that event. Keep the comparison isolated in the named hard fallback when modifying this code; it preserves the previous guard rather than re-running the full interest query.

## Eviction metric and investigation

`dcl_pulse_interest_snapshot_evicted_total` exposes hard fallback attempts. Its counting scope, exclusions, Grafana query, and interpretation are defined in [Interest Snapshot Evictions](metrics.md#interest-snapshot-evictions). The local dashboard export requires operator import; deployment of the counter alone does not install its panel.

For an increase, compare the effective history capacity with admitted publisher bursts, incoming backlog, and delay between selection and resolution. Check tick duration and scheduling pressure before choosing a larger ring. Extra capacity increases retained snapshot storage for every peer; it improves tolerance without establishing a time bound. Keep the exact-sequence path as the normal contract while measuring whether the fallback needs further work.

## Performance findings

Accepted-sequence queries add eligibility validation, registration fences, and HashSet deduplication to the legacy query. Compact entries reduce buffer traffic compared with storing full snapshots, while consumption requires an exact historical read.

[InterestSnapshotBenchmarks](../src/DCLPulseBenchmarks/InterestSnapshotBenchmarks.cs) compares query and consumption using the production reader. Targets remain retained throughout the static run; missing targets fail the benchmark instead of using fallback or recording a production metric. The run excludes tier pacing, concurrent writers, history scans, and network encoding. Ratios describe query and consumption cost; peak-density tick headroom requires a complete fan-out workload with scheduling and transport costs.

## Regression evidence

Preserve the behavioral assertions in these tests when changing the contract:

- `InterestCollectorTests` and `SpatialHashAreaOfInterestTests`: stale candidates, first acceptance across duplicates, matching tier and registration, and retained sequences after later publications.
- `PeerSimulationTests.InterestSnapshots` and `PeerSimulationTests.InterestSequenceFallback`: exact retained targets, overwritten events, fallback acceptance and rejection for both observer types, realm lifecycle ordering, skipped tiers, resync, disconnects, and same-sequence slot reuse.
- `PeerSimulationTests.ReviewRegressions`: overwritten teleport history with a retained target, registration changes during aliasing retirement before reseeding, and eviction/disconnect/recycling checks before skipped tier ticks.
- `InterestSnapshotMetricsTests`: counter aggregation, Prometheus export at zero, and absence of peer labels. Simulation cases distinguish real fallback attempts from retained targets and inactive or recycled entries.
