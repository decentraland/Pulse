---
name: add-metric
description: Add a new metric to the Pulse analytics layer — instrument, collect, snapshot, and display on the dashboard. Use when the user wants to track a new server metric.
user-invocable: true
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
argument-hint: <metric description, e.g. "tick duration histogram per worker">
---

# Add a Metric to Pulse

Follow this checklist to add a new metric end-to-end. There are two patterns depending on the data source.

## Pattern A: Counter-based metric (uses System.Diagnostics.Metrics)

For values recorded on hot paths (ENet thread, worker threads) via `Interlocked` counters.

**Examples:** bytes sent, packets received, peers connected.

### 1. Define the instrument — `src/DCLPulse/Metrics/PulseMetrics.*.cs`

Add to the appropriate nested class (e.g. `PulseMetrics.Transport`), or create a new partial file (`PulseMetrics.Simulation.cs`) for a new domain.

```csharp
// PulseMetrics.Transport.cs
public static readonly Counter<long> MY_METRIC =
    METER.CreateCounter<long>("pulse.transport.my_metric");
```

Naming: `UPPER_SNAKE_CASE` for the field, `pulse.<domain>.<name>` for the instrument name.

### 2. Record at the source

Call `.Add()` on the hot path. This fires the MeterListener callback.

```csharp
PulseMetrics.Transport.MY_METRIC.Add(value);
```

### 3. Accumulate in MetricsCollector — `src/DCLPulse/Metrics/MetricsCollector.cs`

Add a `long` field for the accumulated total and handle it in `OnLongMeasurement`:

```csharp
private long myMetric;

// In OnLongMeasurement switch:
case "pulse.transport.my_metric":
    Interlocked.Add(ref myMetric, value);
    break;
```

Add a `RateTracker` if you need rate + P95/P99:

```csharp
private readonly RateTracker myMetricTracker = new (PERCENTILE_WINDOW);
```

In `Tick()`:

```csharp
MyMetric = myMetricTracker.Update(Interlocked.Read(ref myMetric), elapsedSec),
```

### 4. Add to snapshot — `src/DCLPulse/Metrics/MetricsSnapshot.cs`

Add the field to the appropriate nested struct:

```csharp
public RateStats MyMetric { get; init; }
```

Use `RateStats` for rate + P95/P99. Use raw types (`long`, `int`) for simple totals.

### 5. Display on dashboard — `src/DCLPulse/Dashboard/ConsoleDashboard.cs`

Add a `RateStatsView` + `Sparkline`, update in `ConsumeSnapshot`, add a row in `BuildVisualTree`.

See Pattern B step 4 for the exact code — it's the same for both patterns.

---

## Pattern B: Sampled metric (direct read, no Metrics API)

For values read directly from shared state on the collector's timer tick. Avoids the MeterListener indirection.

**Examples:** queue depths, gauge values, external counters.

### 1. Expose the value

Add a property or method to the source class:

```csharp
// MessagePipe.cs
public int MyQueueDepth => Volatile.Read(ref myDepth);
```

If the source uses `Channel<T>` with `SingleWriter=true`, `Reader.Count` throws `NotSupportedException`. Track depth manually with `Interlocked.Increment`/`Decrement` on write/read.

### 2. Sample in MetricsCollector — `src/DCLPulse/Metrics/MetricsCollector.cs`

Inject the source via constructor. Add a `PercentileBuffer`:

```csharp
private readonly PercentileBuffer myDepthBuffer = new (PERCENTILE_WINDOW);
```

In `Tick()`, read the value and feed the buffer:

```csharp
int depth = source.MyQueueDepth;
myDepthBuffer.Add(depth);
// In snapshot init:
MyDepth = myDepthBuffer.ToStats(depth),
```

### 3. Add to snapshot — `src/DCLPulse/Metrics/MetricsSnapshot.cs`

```csharp
public RateStats MyDepth { get; init; }
```

`RateStats` is reused for gauge-like metrics — `PerSec` holds the current value, `P95`/`P99` hold percentiles over the rolling window.

### 4. Display on dashboard — `src/DCLPulse/Dashboard/ConsoleDashboard.cs`

Add state + sparkline:

```csharp
private readonly RateStatsView myDepth = new ();
private readonly Sparkline myDepthSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
```

Update in `ConsumeSnapshot`:

```csharp
myDepth.Apply(snap.Transport.MyDepth, v => v.ToString("N0"));
ShiftSample(myDepthSparkline.Values, snap.Transport.MyDepth.PerSec);
```

Add a row in the table inside `BuildVisualTree`:

```csharp
RateStatsRow("My Depth", myDepth, myDepthSparkline.Style(STYLE_BACKPRESSURE)),
```

Choose the sparkline style by semantic group:
- `STYLE_PEERS` (purple) — peer counts
- `STYLE_INBOUND` (blue) — incoming data
- `STYLE_OUTBOUND` (coral) — outgoing data
- `STYLE_BACKPRESSURE` (yellow) — queue depths, health indicators

---

## Pattern C: Per-enum-value collection metric

For counting occurrences per enum variant (e.g. per message type, per disconnect reason).

**Examples:** messages by type, disconnects by reason.

### 1. Create a shared counter class — `src/DCLPulse/Metrics/`

```csharp
public sealed class MyCounters
{
    private readonly long[] counts = new long[BUCKET_COUNT];
    public void Increment(MyEnum value) => Interlocked.Increment(ref counts[(int)value]);
    public long Read(MyEnum value) => Interlocked.Read(ref counts[(int)value]);
}
```

Register as singleton in `Program.cs`. Inject into the recording site and `MetricsCollector`.

### 2. Record at the source

```csharp
myCounters.Increment(item.SomeEnumValue);
```

### 3. Track in MetricsCollector

Define tracked values in one place:

```csharp
private static readonly MyEnum[] TRACKED_VALUES = [ MyEnum.A, MyEnum.B, ... ];
private readonly Dictionary<MyEnum, RateTracker> myTrackers =
    TRACKED_VALUES.ToDictionary(v => v, _ => new RateTracker(PERCENTILE_WINDOW));
```

In `Tick()`, build a dictionary:

```csharp
var myStats = new Dictionary<MyEnum, RateStats>(myTrackers.Count);
foreach (var (val, tracker) in myTrackers)
    myStats[val] = tracker.Update(myCounters.Read(val), elapsedSec);
snapshot = snapshot with { MyStats = myStats };
```

### 4. Add to snapshot

```csharp
public Dictionary<MyEnum, RateStats> MyStats { get; init; }
```

### 5. Display on dashboard

Define display config in one place:

```csharp
private static readonly (MyEnum Value, string Label, SparklineStyle Style)[] MY_CONFIG = [ ... ];
```

Create a `Dictionary<MyEnum, MessageTypeView>` from the config. In `ConsumeSnapshot`, iterate and apply. In `BuildVisualTree`, use LINQ `.Select()` to build table rows.

---

## Supporting types reference

| Type | Location | Purpose |
|---|---|---|
| `RateStats` | `Metrics/RateStats.cs` | Bundles `PerSec`, `P95`, `P99` |
| `RateTracker` | `Metrics/RateTracker.cs` | Counter → rate + percentiles (uses `PercentileBuffer`) |
| `PercentileBuffer` | `Metrics/PercentileBuffer.cs` | Ring buffer with percentile computation |
| `RateStatsView` | `Dashboard/ConsoleDashboard.cs` | Three `State<string>` + `Apply(RateStats, format)` |
| `MessageTypeView` | `Dashboard/ConsoleDashboard.cs` | `RateStatsView` + `Sparkline` + `Apply(RateStats)` |
| `ByteFormat` | `Formatting/ByteFormat.cs` | `Format(bytes)` / `FormatRate(bytes/s)` |

## After adding the metric

1. Build: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
2. Run tests: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
3. If `PeersManager` constructor changed, update test files that construct it (`WorkerAsyncTests.cs`, `DrainPeerLifeCycleEventsTests.cs`, `WaitForMessagesOrTickTests.cs`).
