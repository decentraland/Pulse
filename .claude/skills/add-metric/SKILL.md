---
name: add-metric
description: Add a new metric to the Pulse analytics layer — instrument, accumulate, snapshot, export to Prometheus, and display on the console dashboard. Use when the user wants to track a new server metric.
user-invocable: true
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
argument-hint: <metric description, e.g. "tick duration histogram per worker">
---

# Add a Metric to Pulse

End-to-end checklist. Three patterns depending on the data source.

## Pipeline overview

```
Hot path              MeterListenerMetricsCollector       Two consumers
─────────────────     ───────────────────────────────     ────────────────────────────
ENet / worker thread  OnLongMeasurement / OnIntMeasurement  PrometheusFormatter → /metrics
 PulseMetrics.Add() ► Interlocked accumulation into field   (HTTP endpoint)
                      TakeSnapshot() builds MetricsSnapshot
                                                            ConsoleDashboard → dev TUI
                                                            (RateTracker / GaugeTracker
                                                             compute P50/P95/P99 locally)
```

The collector only holds **raw totals** (`long`, `int`). Rates and percentiles are computed
downstream in the dashboard. Prometheus emits raw totals.

---

## Pattern A: Counter-based metric

For values recorded on hot paths via `System.Diagnostics.Metrics`. Most common pattern.

**Examples:** bytes sent, packets received, peers connected, handshake attempts exceeded.

### 1. Declare the instrument — `src/DCLPulse/Metrics/PulseMetrics.*.cs`

Add to the appropriate nested class (e.g. `PulseMetrics.Transport`, `PulseMetrics.Hardening`), or create a new partial (`PulseMetrics.Simulation.cs`) for a new domain.

```csharp
// PulseMetrics.Hardening.cs
public static readonly Counter<long> MY_METRIC =
    METER.CreateCounter<long>("pulse.hardening.my_metric");

// For gauges (current value that goes up and down), use UpDownCounter:
public static readonly UpDownCounter<int> MY_GAUGE =
    METER.CreateUpDownCounter<int>("pulse.hardening.my_gauge");
```

Naming: `UPPER_SNAKE_CASE` field, `pulse.<domain>.<snake_name>` instrument name.

### 2. Record at the source

```csharp
PulseMetrics.Hardening.MY_METRIC.Add(1);
// Or for gauges:
PulseMetrics.Hardening.MY_GAUGE.Add(+1);  // on increase
PulseMetrics.Hardening.MY_GAUGE.Add(-1);  // on decrease
```

### 3. Accumulate in the collector — `src/DCLPulse/Metrics/MeterListenerMetricsCollector.cs`

- Add a `long` (or `int` for UpDownCounter) field next to the existing totals:
  ```csharp
  private long myMetric;
  ```
- Add a `case` in `OnLongMeasurement` (or `OnIntMeasurement` for `int`):
  ```csharp
  case "pulse.hardening.my_metric":
      Interlocked.Add(ref myMetric, value);
      break;
  ```
  Instrument name must match exactly. Missing a case = silent drop.

### 4. Expose in the snapshot — `src/DCLPulse/Metrics/MetricsSnapshot.cs`

Add a field to the nested struct (or create a new one for a new domain):
```csharp
public readonly record struct HardeningSnapshot
{
    public long TotalMyMetric { get; init; }
    public int MyGauge { get; init; }
}
```
And expose it:
```csharp
public HardeningSnapshot Hardening { get; init; }
```

### 5. Populate the snapshot — `MeterListenerMetricsCollector.TakeSnapshot`

```csharp
return new MetricsSnapshot
{
    Transport = new MetricsSnapshot.TransportSnapshot { … },
    Hardening = new MetricsSnapshot.HardeningSnapshot
    {
        TotalMyMetric = Interlocked.Read(ref myMetric),
        MyGauge = Volatile.Read(ref myGauge),
    },
    …
};
```

### 6. Emit Prometheus — `src/DCLPulse/Metrics/PrometheusFormatter.cs`

Add to `Write(StreamWriter, MetricsSnapshot)`:
```csharp
WriteCounter(writer, "dcl_pulse_my_metric_total", "Human description", snap.Hardening.TotalMyMetric);
WriteGauge  (writer, "dcl_pulse_my_gauge",       "Human description", snap.Hardening.MyGauge);
```
Convention: `dcl_pulse_<snake_name>` + `_total` suffix for counters, no suffix for gauges.

### 7. Display on the console dashboard — `src/DCLPulse/Metrics/Console/ConsoleDashboard.cs`

- Add a tracker field. `RateTracker` for rate-over-time counters, `GaugeTracker` for gauges:
  ```csharp
  private readonly RateTracker  myMetricTracker = new (SPARKLINE_MAX_SAMPLES);
  private readonly GaugeTracker myGaugeTracker  = new (SPARKLINE_MAX_SAMPLES);
  ```
- Add a `RateStatsView` + `Sparkline`:
  ```csharp
  private readonly RateStatsView myMetric = new ();
  private readonly Sparkline     myMetricSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
  ```
- Update in `TryConsumeSnapshot`:
  ```csharp
  RateStats myMetricRate = myMetricTracker.Update(snap.Hardening.TotalMyMetric, elapsed);
  myMetric.Apply(myMetricRate, v => v.ToString("N0"));
  ShiftSample(myMetricSparkline.Values, myMetricRate.PerSec);
  ```
  For gauge:
  ```csharp
  RateStats myGaugeRate = myGaugeTracker.Record(snap.Hardening.MyGauge);
  myGauge.Apply(myGaugeRate, v => v.ToString("N0"));
  ShiftSample(myGaugeSparkline.Values, snap.Hardening.MyGauge);
  ```
- Add a row in `BuildVisualTree` (inside existing `metricsTable` or a new `Group`):
  ```csharp
  RateStatsRow("My Metric", myMetric, myMetricSparkline.Style(STYLE_BACKPRESSURE)),
  ```
- Choose sparkline style by semantic group:
  - `STYLE_PEERS` (purple) — peer counts
  - `STYLE_INBOUND` (blue) — incoming data
  - `STYLE_OUTBOUND` (coral) — outgoing data
  - `STYLE_BACKPRESSURE` (yellow) — queue depths, throttles, refusals
  - `STYLE_ERROR` (red) — failures

### 8. Document in `docs/metrics.md`

Add a section under the appropriate domain heading (or create a new heading for a new domain). Describe what the metric means, the expected value range, and `Signal | Meaning` rows for operators reading the dashboard.

---

## Pattern B: Sampled metric (direct read, no Metrics API)

For values read directly from shared state on the collector's `TakeSnapshot` call. Avoids the `MeterListener` indirection.

**Examples:** queue depths, in-flight counters already exposed as properties.

### 1. Expose the value on the source class

```csharp
// MessagePipe.cs
public int IncomingQueueDepth => Volatile.Read(ref incomingDepth);
```
If the source uses `Channel<T>` with `SingleWriter=true`, `Reader.Count` throws — track depth manually with `Interlocked.Increment/Decrement` on write/read.

### 2. Inject the source into `MeterListenerMetricsCollector`

Add to constructor parameters. No field accumulation needed — the collector reads the property live on every `TakeSnapshot` call.

### 3. Add to snapshot — `MetricsSnapshot.cs`

```csharp
public int IncomingQueueDepth { get; init; }
```
Raw type (`int`/`long`), not `RateStats`. Percentiles are computed downstream.

### 4. Read in `TakeSnapshot`

```csharp
IncomingQueueDepth = messagePipe.IncomingQueueDepth,
```

### 5. Emit Prometheus — `PrometheusFormatter.cs`

```csharp
WriteGauge(writer, "dcl_pulse_incoming_queue_depth", "Pending messages in incoming channel", snap.Transport.IncomingQueueDepth);
```

### 6. Display on dashboard

Same as Pattern A step 7, but use `GaugeTracker` instead of `RateTracker`:
```csharp
private readonly GaugeTracker incomingQueueDepthTracker = new (SPARKLINE_MAX_SAMPLES);
```
And in `TryConsumeSnapshot`:
```csharp
RateStats depthStats = incomingQueueDepthTracker.Record(snap.Transport.IncomingQueueDepth);
incomingQueue.Apply(depthStats, v => v.ToString("N0"));
ShiftSample(incomingQueueSparkline.Values, snap.Transport.IncomingQueueDepth);
```

### 7. Document in `docs/metrics.md`

---

## Pattern C: Per-enum-value collection metric

For counting occurrences per enum variant.

**Examples:** messages by type (existing `ClientMessageCounters` / `ServerMessageCounters`), disconnects by reason.

### 1. Create a shared counter class — `src/DCLPulse/Metrics/`

```csharp
public sealed class MyCounters : EnumCounters<MyEnum>
{
    public MyCounters() : base((int)MyEnum.COUNT) { }
}
```
Or subclass/extend `EnumCounters<T>` directly. Register as singleton in `Program.cs`.

### 2. Record at the source

```csharp
myCounters.Increment(item.SomeEnumValue);
```

### 3. Expose on snapshot — `MetricsSnapshot.cs`

```csharp
public MyCounters MyStats { get; init; }
```
The counters class itself is the snapshot — consumers call `Read(value)` on demand.

### 4. Inject into collector and add to `TakeSnapshot`

```csharp
MyStats = myCounters,
```

### 5. Emit Prometheus — `PrometheusFormatter.cs`

Define the list of tracked values + call `WriteEnumCounters`:
```csharp
private static readonly MyEnum[] MY_ENUM_VALUES = [ MyEnum.A, MyEnum.B, … ];
// In Write:
WriteEnumCounters(writer, "dcl_pulse_my_metric_total", "description", snap.MyStats, MY_ENUM_VALUES);
```

### 6. Display on dashboard

Use `MessageTableConfig<MyEnum>` + `MessageTableState<MyEnum>` — see the existing
`INCOMING_MESSAGES_CONFIG` / `OUTGOING_MESSAGES_CONFIG` wiring in `ConsoleDashboard.cs`. Add a
per-type `RateTracker` dictionary and iterate in `TryConsumeSnapshot`.

### 7. Document in `docs/metrics.md`

---

## Supporting types reference

| Type | Location | Purpose |
|---|---|---|
| `RateStats` | `Metrics/Console/RateStats.cs` | Bundles `PerSec`, `Window` (P50/P95/P99), `Lifetime` (P50/P95/P99) |
| `RateTracker` | `Metrics/Console/RateTracker.cs` | Counter → rate + percentiles. `Update(total, elapsedSec) : RateStats` |
| `GaugeTracker` | `Metrics/Console/GaugeTracker.cs` | Gauge value → percentiles. `Record(value) : RateStats` |
| `PercentileBuffer` | `Metrics/Console/PercentileBuffer.cs` | Ring buffer with percentile computation |
| `LifetimePercentileBuffer` | `Metrics/Console/LifetimePercentileBuffer.cs` | Lifetime P-squared percentile estimator |
| `RateStatsView` | `Metrics/Console/ConsoleDashboard.cs` (nested) | Reactive `State<string>` bundle + `Apply(RateStats, format)` |
| `MessageTypeView` | `Metrics/Console/ConsoleDashboard.cs` (nested) | `RateStatsView` + `Sparkline` for per-enum tables |
| `EnumCounters<T>` | `Metrics/EnumCounters.cs` | Generic per-enum atomic counters |
| `ByteFormat` | `Metrics/Console/ByteFormat.cs` | `Format(bytes)` / `FormatRate(bytes/s)` |

## After adding the metric

1. Build: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
2. Run tests: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
3. If `MeterListenerMetricsCollector` or `ConsoleDashboard` ctor signatures changed, update DI in `Program.cs` and any tests that construct them.
4. If Rider MCP is available, run `mcp__rider__get_file_problems` on each touched file to catch convention warnings before reporting done.
