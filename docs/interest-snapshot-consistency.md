# Interest snapshot consistency

Interest management owns subject eligibility. Simulation preserves that decision by reading the exact sequence accepted by the query. A missing accepted sequence enters a measured, explicitly undesired hard fallback to the previous comparison logic.

These findings describe the sequence implementation on `fix/interest-snapshot-consistency`, verified on 2026-10-07. The [implementation history](superpowers/plans/2026-10-07-interest-snapshot-consistency.md) records the earlier snapshot-copy implementation and verification results.

## Accepted sequence and registration

Realm grids supply candidates. A reader can retain a cell set after a subject moves or teleports, so the grid alone cannot establish eligibility. `SpatialHashAreaOfInterest` validates one snapshot against the player realm and distance, or the listener's announced realm and parcels, and calculates the tier from that same snapshot.

`InterestEntry` retains `(Subject, Tier, uint Seq, Identity)`. `InterestCollector` uses a reusable `HashSet<uint>` to accept each subject once; the first accepted sequence, tier, and registration win. Deduplication matters when movement during enumeration exposes the same subject through old and new cells or through two listener realms. Replacing the snapshot with its sequence reduces entry copying; it preserves this deduplication contract.

`PeerSimulation.TryResolveInterestSnapshot` reads that exact sequence. Active-state and `IdentityRegistration` reference checks fence the read. Wallet or sequence equality alone cannot identify a connection: a recycled slot can repeat both. Retained targets require no second eligibility comparison, even if the subject has since teleported. Later publications normally participate in the next query; the query does not create a global snapshot across workers.

## Why a recorded sequence can disappear

`SnapshotBoard.Publish` writes to `Seq % ringCapacity`. With consecutive publications, sequence `s + capacity` replaces the slot holding `s`. The sequence identifies a published value; recording it does not reserve its ring slot. The seqlock protects the read from a torn write, and the historical read checks the stored sequence before succeeding. It does not extend retention.

Collection and delivery run sequentially on the observer's worker, while another worker can keep publishing the subject. The gap includes the remainder of collection and delivery to earlier subjects, as well as thread scheduling delays. There is no bound that guarantees fewer than one ring's worth of publications during that gap.

At the configuration inspected during this investigation, a 20-entry ring covers roughly one second only under steady movement publishing at 20 Hz. Movement and discrete events have separate token buckets, each configured for a burst of 16. A subject worker drains queued events before its simulation tick, so allowed mixed bursts can overwrite a selected slot much faster than one second. These facts establish a possible interleaving; they do not establish its production frequency. Resolve current capacities and rate limits from runtime configuration when investigating an occurrence.

## Eviction hard fallback

Only an exact-sequence miss with the accepted registration still active increments `PulseMetrics.Simulation.INTEREST_SNAPSHOT_EVICTED` and enters `TryResolveEvictedInterestSnapshotHardFallback`. That helper reads the latest snapshot, rechecks registration, and applies the original comparison:

| Observer | Latest snapshot accepted when |
| --- | --- |
| Player | Its realm equals the observer's realm. |
| Scene listener | Its realm is announced and its parcel belongs to that realm's parcel set. |

The player comparison checks realm only. Distance and tier remain the original interest query's decision, including on this fallback. A rejected existing view is removed and receives one `PlayerLeft`; a rejected new subject receives no `PlayerJoined`.

Resolve the target before aliasing and realm lifecycle handling. History scans and resync responses remain bounded by the resolved target. A pending resync bypasses tier pacing and receives the resolved state; a skipped ordinary tier tick still stamps an existing accepted view as visible.

The fallback deliberately weakens the snapshot-copy guarantee. An overwritten teleport, emote event, or original stop reason may be lost; latest state cannot reconstruct that event. Three prior eviction expectations changed for this reason. Retained-target expectations remain intact. Keep the comparison isolated in the named hard fallback when modifying this code.

## Eviction metric and investigation

`dcl_pulse_interest_snapshot_evicted_total` exposes hard fallback attempts. Its counting scope, exclusions, Grafana query, and interpretation are defined in [Interest Snapshot Evictions](metrics.md#interest-snapshot-evictions). The local dashboard export requires operator import; deployment of the counter alone does not install its panel.

For an increase, compare the effective history capacity with admitted publisher bursts, incoming backlog, and delay between selection and resolution. Check tick duration and scheduling pressure before choosing a larger ring. Extra capacity increases retained snapshot storage for every peer; it improves tolerance without establishing a time bound. Keep the exact-sequence path as the normal contract while measuring whether the fallback needs further work.

## Performance findings

The accepted-state implementation adds eligibility validation, registration fences, and deduplication to the legacy query. The larger copied entries also increased buffer traffic. The bitmap experiment improved collector cost modestly but left the complete query substantially slower; the simpler HashSet was restored. Sequence entries reduce buffer traffic while adding an exact historical read at consumption.

The [benchmark records](superpowers/plans/2026-10-07-interest-snapshot-consistency.md#follow-up-verification) contain entry sizes, timings, confidence limits, and the reproduction command. The retained-target run excludes concurrent writers, hard fallback frequency and cost, history scans, and network encoding. Its ratios describe query and consumption cost, not complete server throughput or production eviction risk.

## Regression evidence

Preserve the behavioral assertions in these tests when changing the contract:

- `InterestCollectorTests` and `SpatialHashAreaOfInterestTests`: stale candidates, first acceptance across duplicates, matching tier and registration, and retained sequences after later publications.
- `PeerSimulationTests.InterestSnapshots` and `PeerSimulationTests.InterestSequenceFallback`: exact retained targets, overwritten events, fallback acceptance and rejection for both observer types, realm lifecycle ordering, skipped tiers, resync, disconnects, and same-sequence slot reuse.
- `InterestSnapshotMetricsTests`: counter aggregation, Prometheus export at zero, and absence of peer labels. Simulation cases distinguish real fallback attempts from retained targets and inactive or recycled entries.

The [follow-up verification record](superpowers/plans/2026-10-07-interest-snapshot-consistency.md#follow-up-verification) holds the TDD results, full-suite result, build status, dashboard lint, and independent review.
