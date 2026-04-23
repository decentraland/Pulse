---
name: add-hardening
description: Add a defense against a specific network/protocol attack vector to Pulse — isolate protection in Hardening/ classes, wire it through config + metrics + docs, and verify with Rider inspections. Use when the user wants to add rate limiting, admission control, input validation, resource caps, replay protection, or any other server-side defense.
user-invocable: true
allowed-tools: Read, Edit, Write, Glob, Grep, Bash, Skill, mcp__rider__get_file_problems, mcp__rider__reformat_file
argument-hint: <attack description, e.g. "bound ResyncRequests dict per peer" or "clamp emote string length">
---

# Add a Hardening Defense to Pulse

End-to-end playbook distilled from Group A (pre-auth admission, handshake throttling). Use
this when introducing a new protection against an identified attack vector. The goal is an
isolated, tested, configurable, metric-instrumented defense that survives future refactors.

## Foundational rules

1. **Isolate the protection** — all new hardening logic lives in a `Hardening/` subfolder of
   its owning layer (`Transport/Hardening/`, `Messaging/Hardening/`, `Peers/Hardening/`). Core
   code (transport loop, message handler, simulation) gets a one-line hook at most; the policy
   itself is a dedicated class or partial file. If you find yourself inlining a condition into
   an existing method, stop and extract.

2. **Per-peer state goes on `PeerState`** (typically `PeerState.TransportState`) — never
   introduce a parallel board keyed by `PeerIndex`. The only exception is genuinely global
   counters that can't fit on per-peer state; put those as atomic fields inside the hardening
   component itself.

3. **Zero means disabled** — every config knob must treat `0` as "unlimited / off". This keeps
   `appsettings.Development.json` overrides trivial (set everything to `0` for load tests) and
   gives operators a clear way to hot-disable a broken check without redeploying.

4. **Release on the worker thread**, admit on the ENet thread — cross-thread decrement paths
   drift in subtle ways. Put all release calls in the owning worker's handler (HandshakeHandler
   on promotion, PeersManager on Disconnected lifecycle) so a single thread drives every
   decrement. Make release idempotent via lookup-and-clear so a duplicate call is a no-op.

5. **Merge overlapping gates** — if two checks guard the same thing at different granularities
   (e.g. per-IP vs global), prefer one component with two caps + one lock + one release path,
   over two sibling components that must be kept in sync. Two counters that move together
   should live inside one class.

---

## Phase 1 — Design

Before writing any code, write down four things. If you can't answer any of them, pause and
ask the user.

### 1.1 Threat model
What attacker capability / input triggers the problem? Pre-auth squat? Spoofed packet?
Oversized field? Flood? Amplification?

### 1.2 Cost asymmetry
What does the attacker spend vs. what does the server spend? Defenses matter most when server
cost is much higher than attacker cost (e.g. ECDSA per packet, fan-out per emote start).

### 1.3 Layer
Which layer enforces the cap? Pick the cheapest layer that still has the relevant context:
- **Transport** — before any parsing, when the attack is packet-rate or connect-rate (e.g.
  `PreAuthAdmission`).
- **Message handler** — when the attack is per-protocol-message (e.g.
  `HandshakeAttemptPolicy`, hypothetical emote-rate limiter).
- **Simulation** — when the attack amplifies through observer fan-out.
- **Serialization** — bound fields at deserialization boundary.

### 1.4 Client recovery contract
What should a legitimate client do when the defense fires against it? Decide now so the
`DisconnectReason` / error code carries the right semantics. See "Client recovery" below.

---

## Phase 2 — Implement

### 2.1 Options class — `<Layer>/Hardening/<Name>Options.cs`

```csharp
namespace Pulse.<Layer>.Hardening;

public sealed class <Name>Options
{
    public const string SECTION_NAME = "<Layer>:Hardening:<Name>";

    /// <summary>
    ///     <what this caps>. Zero disables the limit.
    /// </summary>
    public int MaxSomething { get; set; } = <prod-default>;
}
```

Conventions:
- `SECTION_NAME` = colon-separated path matching the intended `appsettings.json` location.
- Every numeric cap defaults to a sane prod value; `0` must mean disabled.
- Document the knob's semantics in the XML doc including the unit ("peers/sec", "bytes",
  "concurrent").

### 2.2 Protection class — `<Layer>/Hardening/<Name>.cs`

```csharp
using Microsoft.Extensions.Options;
using Pulse.Metrics;

namespace Pulse.<Layer>.Hardening;

/// <summary>
///     <what this protects against>. <threading model in one sentence>.
/// </summary>
public sealed class <Name>(IOptions<<Name>Options> options)
{
    private readonly Lock syncRoot = new ();
    private readonly int cap = options.Value.MaxSomething;

    public bool IsEnabled => cap > 0;

    public bool TryAdmit(...)
    {
        if (!IsEnabled) return true;
        lock (syncRoot)
        {
            // check + commit atomically
            if (exceeded)
            {
                PulseMetrics.Hardening.<METRIC>.Add(1);
                return false;
            }
            // commit
            return true;
        }
    }

    public void Release(...) { /* idempotent lookup-and-decrement */ }
}
```

Conventions:
- **Primary constructor** for trivial ctor bodies (just `options.Value.X` assignments).
- **`Lock` type**, not `object`, for sync roots.
- **Enum values** (e.g. `TryAdmit` result enum) in `UPPER_SNAKE_CASE`, matching the rest of
  the codebase (`DisconnectReason`, `PeerConnectionState`).
- If the defense exposes per-peer state, put a field on `PeerState.TransportState` and mutate
  via `state.TransportState = state.TransportState with { X = ... }`. No new board.
- Short `IsEnabled` property for the disable flag.
- Bail early when disabled — zero cost on the hot path when a defense is off.

### 2.3 Thin hook in the core code

The core method (e.g. `ENetHostedService.HandleEvent`, `HandshakeHandler.Handle`) gets **one
line** to invoke the defense. If you need more than a line, extract into a partial file:

```csharp
// ENetHostedService.Hardening.cs (partial)
public sealed partial class ENetHostedService
{
    private bool TryAdmitOrRefuse(ref Event netEvent, PeerIndex peerIndex) { ... }
}
```

The core class gets `partial` added; the hardening method sits in a sibling file named
`<Core>.Hardening.cs`. Rollback logic (releasing the pool allocation when admission fails)
lives inside the extracted method, not in the caller.

### 2.4 DisconnectReason / error code

When the defense disconnects a peer, add a **new, specifically-named** `DisconnectReason`:

```csharp
// DisconnectReason.cs
/// <summary>
///     <Specific reason, not generic "RATE_LIMITED">. <When a client sees this>.
/// </summary>
SPECIFIC_REASON = <next integer>,
```

Avoid generic names like `RATE_LIMITED` — prefer names that carry semantics (e.g.
`PRE_AUTH_IP_LIMIT_EXHAUSTED`, `PRE_AUTH_BUDGET_EXHAUSTED`). Parallel names across related
codes help clients route retry logic.

### 2.5 DI registration — `Program.cs`

```csharp
builder.Services.Configure<<Name>Options>(
    builder.Configuration.GetSection(<Name>Options.SECTION_NAME));

builder.Services.AddSingleton<<Name>>();
```

### 2.6 Config — `appsettings.json` + `appsettings.Development.json`

Nest under the appropriate layer:

```json
// appsettings.json — prod values
{
  "Transport": {
    "Hardening": {
      "<Name>": {
        "MaxSomething": 32
      }
    }
  }
}

// appsettings.Development.json — disabled for load tests
{
  "Transport": {
    "Hardening": {
      "<Name>": {
        "MaxSomething": 0
      }
    }
  }
}
```

### 2.7 Metrics — invoke the `add-metric` skill

Every defense should emit at least:
- A **counter** incremented when the defense fires (refusals, exceeds, violations).
- Optionally a **gauge** for in-flight state (e.g. `pre_auth_in_flight`).

Call the skill:
```
/add-metric pulse.hardening.<my_counter> (Counter<long>), fires on refusal
```

The skill handles: declaration in `PulseMetrics.Hardening.cs`, accumulation in
`MeterListenerMetricsCollector`, `MetricsSnapshot.HardeningSnapshot`, Prometheus emit,
Console dashboard with `STYLE_BACKPRESSURE`, `docs/metrics.md` entry.

**Do not** declare a `Counter` in `PulseMetrics.Hardening.cs` without running `/add-metric`
through to completion — dangling instruments are invisible to `/metrics` and the dashboard.

### 2.8 Tests — `DCLPulseTests/Hardening/<Name>Tests.cs`

Structure mirrors source. Test shape:

```csharp
[TestFixture]
public class <Name>Tests
{
    private static <Name> Create(int cap) =>
        new (Options.Create(new <Name>Options { MaxSomething = cap }));

    [Test] public void AdmitsUpToCap_ThenRefuses() { ... }
    [Test] public void ZeroCap_DisablesDefense() { ... }
    [Test] public void Release_FreesSlot() { ... }
    [Test] public void DoubleRelease_IsNoOp() { ... }
    [Test] public void Concurrent_RespectsBudget()         // when multi-threaded
    {
        var barrier = new Barrier(THREADS);
        Parallel.For(0, THREADS, _ => { barrier.SignalAndWait(); /* hammer */ });
        Assert.That(admits, Is.EqualTo(CAP));
    }
}
```

Conventions:
- **NSubstitute**, not Fake/Null impls, for all dependencies.
- **No line numbers** in test comments — they rot.
- If the defense has worker-lifecycle semantics, add a
  `<Name>LifecycleTests.cs` that drives `PeersManager.DrainEvents` and asserts the counter
  state after connected → disconnected / promoted flows.
- If constructing `PeersManager` is in scope, every existing test that constructs it needs
  the new DI parameter too — grep `new PeersManager(` and update all call sites.

### 2.9 Docs — `docs/hardening.md`

Append a section to the existing `docs/hardening.md`:
```markdown
## Group <X> — <name>

### Threat model
### Defenses
### Config
### How the limits interact
### Client recovery
### Metrics to watch
```

Mirror the existing Group A section's structure. The **Client recovery** subsection is the
most-missed one — see next sub-section.

### 2.10 Client recovery guidance

For every new `DisconnectReason`, decide:
- **Retryable vs terminal?** Clients must not auto-retry terminal codes (`AUTH_FAILED`,
  `DUPLICATE_SESSION`, `KICKED`).
- **Retry backoff?** Exponential with jitter is the default — without jitter, NAT-shared clients
  resynchronise and re-trigger the cap forever.
- **UI copy?** Retryable codes should surface as "reconnecting / server busy", never
  "authentication failed".
- **Reuse vs rebuild auth chain?** If still within the anti-replay window, reuse to avoid
  wallet signature prompts.

Write these in the Group's section of `docs/hardening.md` so client teams have one place to
map codes to behavior.

---

## Phase 3 — Verify

Run these in order. Any failure → fix and re-run from the top.

### 3.1 Build
```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" \
  dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

### 3.2 Tests
```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" \
  dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```
If `PeersManager` or `ENetHostedService` ctor changed, expect compile errors in the three
existing test files (`WorkerAsyncTests.cs`, `DrainPeerLifeCycleEventsTests.cs`,
`WaitForMessagesOrTickTests.cs`) — update them to pass disabled options
(`new <Name>(Options.Create(new <Name>Options { MaxX = 0 }))`).

### 3.3 Rider inspections (if available)

Run `mcp__rider__get_file_problems` on every file you touched. Zero warnings is the bar.
Common inspection findings that mean you missed a convention:
- Enum values not UPPER_SNAKE_CASE
- `object` instead of `Lock` for sync roots
- Regular ctor with trivial body instead of primary ctor
- Unused usings / fields
- Missing nullability annotations

If Rider MCP tools are not in the current tool list, say so explicitly — don't silently skip.

### 3.4 Sanity checks
- `grep` for the old DisconnectReason name if you renamed one — callers must be updated.
- Confirm `appsettings.Development.json` has the defense set to `0` so local dev isn't
  clamped.
- Confirm the Dashboard group + Prometheus emit actually show the new metrics (per
  `/add-metric` step 7).

---

## Pitfalls from Group A

These tripped me during Group A — watch for them:

1. **Declaring a `Counter<long>` without wiring it through the pipeline.** `.Add(1)` compiles
   and runs, but `MeterListener`'s switch has no case → the value is silently dropped. The
   `add-metric` skill exists precisely to avoid this.

2. **Forgetting the PeersManager-constructor test fallout.** Any new DI dep in `PeersManager`
   breaks three test files. Grep `new PeersManager(` up front.

3. **Convention slips that `grep` misses.** Enum casing (`Ok` vs `OK`), `object` vs `Lock`,
   non-primary ctor for trivial bodies. All three caught me in Group A. Rider's
   `get_file_problems` catches them reliably; manual `grep` on nearby files does not.

4. **Partial-file extraction needs the class marked `partial`.** Easy to forget when the sibling
   file compiles on its own.

5. **Release sites split across threads** is a source of drift. If you find yourself calling
   release from both the ENet thread (on Disconnect) and a worker (on promotion), consider
   moving release entirely to the worker via the lifecycle event — idempotency + single-thread
   decrement beats dual-thread bookkeeping.

6. **Client-facing semantic naming** matters. `RATE_LIMITED` is ambiguous; `PRE_AUTH_IP_LIMIT_EXHAUSTED`
   tells the client exactly what retry strategy to use. Rename aggressively when you notice
   ambiguity — the enum wire format is forward-compatible with additions.

7. **Doc drift in future-group placeholders.** Don't pre-write sections for groups you haven't
   implemented; they become stale and misleading. Extend `docs/hardening.md` one group at a
   time as you ship them.
