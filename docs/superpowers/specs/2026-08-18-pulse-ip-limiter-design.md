# Runtime Feature Flags + Hard Per-IP Connection Limiter

**Date:** 2026-08-18
**Status:** Design approved, pending implementation

Two deliverables, sequenced. Part A is the runtime-configuration mechanism; Part B is its
first consumer. They can ship as two PRs — Part B depends on Part A only for *live*
reconfiguration; the limiter itself works off `dynamicconfig.json` defaults alone.

---

## Part A — Runtime configuration from `pulse.json`

### A.1 Decisions taken

| Decision | Choice | Rationale |
|---|---|---|
| Flag purpose | Server-wide ops toggles | The server fetches one document with no user context, so per-wallet targeting is not available. |
| Config model | Custom `IConfigurationSource` | Existing `IOptions<T>` plumbing keeps working; `IOptionsMonitor<T>` gets live reload for free. |
| Apply model | Curated live set | Liveness is a property of the *consumer*, not the value. Only knobs whose consumer observes changes are dynamic. |
| Flag mapping | Config fragment + named gates | Per-subsystem flags carrying a `configuration` json variant. |
| Scope | **New keys only** | No existing `appsettings.json` key becomes dynamic. No `IOptions` → `IOptionsMonitor` migration of the 24 existing consumers. |

Because no existing key becomes dynamic, the precedence conflict with `manual-deploy.yml`'s
always-set `Peers__ResyncWithDelta` disappears. That workflow input stays as-is.

### A.2 `dynamicconfig.json` — the type schema

A new file, separate from `appsettings.json`, shipped with the image, carrying the offline
defaults for the dynamic knobs. Because a default value has a JSON type, those same values are
the **type schema** the remote document's values are checked against.

> **Revised 2026-08-19 — the allowlist is removed.** This section originally made the file's key
> set an allowlist: the provider accepted only keys already present in it and dropped anything
> else with a warning, and that filter was called the security boundary. **Unleash is a trusted
> source**, so bounding what it may set buys nothing — anyone who can author the `pulse.json`
> payload is already trusted with the server's configuration, and the filter's only real effect
> was to make every new knob a two-place change and to silently swallow path typos. What survives
> is the *type check* described below, and it survives for an availability reason, not a security
> one. See A.4.

```jsonc
// dynamicconfig.json — offline defaults for the dynamic knobs, and their type schema.
{
  "Transport": { "Hardening": { "IpLimiter": {
    "Enabled": false,
    "MaxConcurrency": 10,
    "Whitelist": "",
  }}},
}
```

Consequences:

- The remote document may set **any** configuration key — `ParcelEncoder:*`, `HttpService:Port`,
  cert paths, bind addresses. Nothing in the server filters it, and nothing needs a second file
  edited to become remotely settable. What such a key does depends entirely on whether its
  consumer reads it live (`IOptionsMonitor<T>`); most existing knobs are `IOptions<T>` and are
  captured at construction, so setting them remotely is inert rather than dangerous.
- `/about` is unauthenticated and reports the applied overrides verbatim, so whoever authors the
  Unleash payload decides what that endpoint publishes. Accepted along with the trusted-source
  premise.
- Keys still in the file get their values type-checked; keys not in it are applied unchecked,
  because nothing declares what type they should be.
- .NET's JSON config parser skips `//` comments and allows trailing commas (verified
  empirically), so per-key notes live inline.
- `reloadOnChange: true` gives a local dev loop with no Unleash round-trip: edit the file on a
  running server and `IOptionsMonitor` fires.

**Requirements:**

- Registered `optional: false`, and the schema reader throws on a missing file to match. It is
  the only source of the offline defaults — fail loudly at startup instead of running knobs
  nobody configured.
- Needs `<Content Update="dynamicconfig.json" CopyToOutputDirectory="PreserveNewest" />` in
  `DCLPulse.csproj`. **`Update`, not `Include`** — an earlier draft of this spec claimed the Worker
  SDK's content glob is `appsettings*.json`; it is actually `**/*.json`, so the file is already
  globbed as `Content` and `Include` fails the build with `NETSDK1022: Duplicate 'Content' items`.
  What the SDK does *not* do is set `CopyToOutputDirectory` for anything but `appsettings*.json`,
  which is exactly what `Update` supplies. Verified reaching both `bin/` and `dotnet publish -c
  Release` output. Per CLAUDE.md's build rule, still rebuild the Docker images to confirm.

### A.3 Fetch — `FeatureFlagsPoller`

A `BackgroundService` modelled on `BansPollingHttpService`, in `src/DCLPulse/FeatureFlags/`.

- URL `https://feature-flags.decentraland.{EnvName.HttpSuffix}/pulse.json`, overridable via
  `FeatureFlags:Url`.
- Headers `X-Debug` and `referer` (Unleash hostname strategy). No `X-Address-Hash` — the server
  has no user identity.
- Parse `{flags, variants}`, strip the `pulse-` app-name prefix from keys, then for each flag
  that is `true`, deserialize its `configuration` variant's `payload.value` **string** as a
  config fragment. A flag set to `false` means its fragment is ignored and the shipped defaults
  apply — a free per-subsystem kill switch.
- Failure is always soft: retain the previous document and log a warning carrying the exception.
  Never throw.
  <br/>**Revised 2026-08-19 — logs only, no counter.** This originally paired every outcome with a
  `pulse.feature_flags.poll_total{result=…}` increment. Removed on the repo owner's call: the poller
  is a cold path on a seconds schedule whose entire state is one small key/value set that `/about`
  already publishes verbatim, so a log line carries strictly more than a counter would. See
  **Metrics** below.
- `PollIntervalSeconds = 0` disables the *refresh loop* only. The blocking first load still runs,
  so the document is frozen at whatever boot produced. The total kill switch is
  `FeatureFlags:Enabled = false`, which skips the remote fetch entirely and leaves the server on
  shipped defaults. (An earlier draft said interval-zero makes the server "behave exactly as
  today" — that was wrong, because it ignored the blocking first load. Corrected to match the
  implementation.)

**Startup ordering.** `IConfigurationSource` providers load during `builder.Configuration`
construction, before DI exists and before `builder.Build()`. The first fetch is therefore a
**blocking load with a ~5s timeout, failing open** to the shipped defaults — same semantics as
`optional: true` on a JSON file. A dead CDN delays startup and logs loudly; it does not fail the
task.

### A.4 Provider — `PulseFlagsConfigurationSource`

A `ConfigurationProvider` + `IConfigurationSource` pair, appended last to
`builder.Configuration.Sources`.

- Flatten each fragment via `new ConfigurationBuilder().AddJsonStream(ms).Build().AsEnumerable()`
  — reuses Microsoft's own flattener, so nesting and type coercion behave identically to
  `appsettings.json`.
- Apply every flattened leaf. **No key filter** — see the A.2 revision note; Unleash is trusted, so
  a key absent from `dynamicconfig.json` is applied like any other.
- Type-check each value **before** swapping, against the type its default in `dynamicconfig.json`
  declares. A value that cannot convert is logged and **that key alone is skipped**; the rest of
  the document applies and the document still announces itself as applied. The skipped knob keeps
  whatever the lower-precedence sources give it.
  <br/>**Not via `IValidateOptions<T>`** — those live in DI, which does not exist while
  configuration is being built, so that route is unreachable and shipped as a dangling no-op the
  first time. `dynamicconfig.json` is the schema instead: it carries a default for every knob whose
  type matters, so each key's expected type is known at config-build time. Convertibility, not kind
  identity — `"20"` for a number is fine, `"ten"` is not. Note this needs the *raw file*:
  `IConfiguration` stringifies every value on load, so the JSON type is unavailable from a built
  configuration.
  <br/>**Revised 2026-08-19 — skip the key, not the document.** This originally rejected the whole
  document on any type failure. Rejecting siblings that are perfectly valid makes one typo cost an
  entire operator change and, worse, leaves the operator looking at an Unleash document the server
  is wholly ignoring. The failure is per-key, so the response is per-key. The warning log is the
  whole signal; the document-level rollback below logs its own, distinct warning.
  <br/>**Extended 2026-08-19 — the same per-key skip covers array-shaped values.** A remote
  `"Whitelist": ["a","b"]` flattens to `Whitelist:0` / `Whitelist:1`, which have no declared type and
  so apply unchecked, while `Whitelist` itself never appears and keeps its default — applied-looking
  and empty. `DynamicConfigSchema` catches it by shape: a key whose **parent path is itself a
  declared scalar key** is an index into a knob that holds a value. Warned and skipped per key, same
  as an unbindable value.
  <br/>Belt and braces, and unchanged: wrap `OnReload()` so a binder throw restores the previous
  `Data` instead of leaving a poison value live. The type check only sees what a *declared* type
  makes visible; this catches everything else. Without it, a bad value faults the transport thread
  on the next connect and `BackgroundServiceExceptionBehavior.StopHost` takes the server down — and
  if the bad value arrives on the blocking first load, the server boots clean and dies on the first
  player. The rollback logs `rolled back to the previous overrides` — the one warning that means
  Unleash is showing values the server is not running.
- `SetData(next)` + `OnReload()` to fire `IOptionsMonitor` change tokens.
- Kill switch: `FeatureFlags__Enabled=false` disables the provider wholesale.

### A.5 Required corrections to the current `pulse.json` — DONE

> **Resolved 2026-08-18.** All three fixes are live at the endpoint; the server now fetches and
> applies three keys. Retained below as the rationale record.

The live document at `https://feature-flags.decentraland.org/pulse.json` needs three fixes. The
outer Unleash document is valid; the flag name, variant name and `payload.type` are all correct.

**1. The payload string is truncated — one missing `}`** (3 opening braces, 2 closing;
`Expecting ',' delimiter: line 8 column 4`).

**2. Move under `Transport:Hardening:`** to match house convention. Every other hardening knob
lives at `<Layer>:Hardening:<Name>` (`Transport:Hardening:PreAuth`,
`Transport:Hardening:CorruptedPacket`).

**3. `Whitelist` must be a delimited string, not an array.** An empty JSON array flattens to
*zero* config keys, so it cannot clear or shorten a list from a lower-precedence source —
verified: with `Whitelist:0` and `Whitelist:1` underneath, a higher-precedence `[]` leaves both in
place. Shortening a 3-entry list to 2 would silently leave the third behind. A single scalar key
always overwrites cleanly.

Corrected `configuration` variant payload:

```json
{
  "Transport": {
    "Hardening": {
      "IpLimiter": {
        "Enabled": true,
        "MaxConcurrency": 10,
        "Whitelist": ""
      }
    }
  }
}
```

---

## Part B — Hard per-IP connection limiter

### B.1 Threat model

A single source IP opens connections without bound. Each admitted connection consumes a
`PeerIndex` from a fixed pool (`Transport:MaxPeers`, 4095), an ENet peer slot, and — once it
reaches `OnPeerConnected` — a worker's `peerStates` entry and a 30-second `PENDING_AUTH` window.
Because released slots sit in the allocator's pending-recycle grace window, connection churn from
one host degrades slot availability for everyone even when no connection is long-lived.

### B.2 Cost asymmetry

An attacker spends one ENet connect handshake (two round trips, no crypto). The server spends a
pool slot, a dictionary insert in three structures, a worker lifecycle event, and a 30-second
reservation. Strongly asymmetric — the defense belongs at the cheapest possible layer.

### B.3 Layer and placement

**Transport, at the top of `EventType.Connect` — before `peerIndexAllocator.TryAllocate`.**

This is strictly earlier than the existing `PreAuthAdmission`, which runs *after* allocation and
then rolls back with `MarkPending` + `Release`. Refusing before allocation means a flooding IP
never touches the allocator's pending-recycle state at all, never creates a `ConnectedPeer`, never
emits `OnPeerConnected`, never reaches a worker, and never enters `PENDING_AUTH`. That satisfies
"dropped as early as possible without moving on to handshake".

Both transports must enforce it — they share the `PeerIndex` pool, so the cap counts ENet and
WebTransport connections together.

> Earlier still would be ENet's `intercept` callback, which fires on raw datagram receive before
> protocol handling. The C# binding does not surface it. Out of scope; noted as a future option if
> connect-flood volume ever justifies it.

### B.4 Why this is not merged into `PreAuthAdmission`

The `add-hardening` skill's rule 5 says to merge overlapping gates rather than keep siblings in
sync, and `PreAuthAdmission` already has a `MaxConcurrentPreAuthPerIP`. The rule's premise is "two
counters that move together should live inside one class" — and these do **not** move together:

| | `PreAuthAdmission.MaxConcurrentPreAuthPerIP` | `IpLimiter.MaxConcurrency` |
|---|---|---|
| Counts | Connections in `PENDING_AUTH` only | **All** connections, authenticated included |
| Released on | Promotion **or** disconnect | Disconnect only |
| Enforced | After `TryAllocate` | Before `TryAllocate` |
| Configured | Boot-time `IOptions` | Runtime `IOptionsMonitor` |
| Whitelist | No | Yes |

Merging would also force `PreAuthAdmissionOptions` onto the dynamic path, which is out of scope by
instruction ("don't rewire the existing values"). Keep them as siblings; document the interaction
in `docs/hardening.md`.

### B.5 Admission sequence

In `ENetHostedService.HandleEvent`, `case EventType.Connect`, with all rollback owned by an
extracted method in the existing `ENetHostedService.Hardening.cs` partial:

```
1. ipLimiter.TryAcquire(ip)       → refuse: DisconnectNow(IP_CONNECTION_LIMIT_EXCEEDED), return
                                     (nothing allocated, nothing to undo)
2. peerIndexAllocator.TryAllocate → fail:   ipLimiter.Abandon(ip); DisconnectNow(SERVER_FULL)
3. preAuthAdmission.TryAdmit      → fail:   allocator rollback; ipLimiter.Abandon(ip); disconnect
4. ipLimiter.Bind(peerIndex, ip)  ← commit the reservation to the peer
5. …existing wiring → messagePipe.OnPeerConnected(peerIndex)
```

`WebTransportHostedService.HandleConnect` mirrors this exactly, using `ParseIp(ev.RemoteAddress)`.

**Release paths.** Steps 2 and 3 refuse *before* `OnPeerConnected`, so no worker lifecycle event
will ever fire for those peers — the transport thread must release them inline via `Abandon(ip)`.
Admitted peers release from the worker on the Disconnected lifecycle event (`PeersManager`,
alongside the existing `preAuthAdmission.ReleaseOnDisconnect`), keyed by `PeerIndex`. This honours
the skill's "release on the worker thread" rule for every peer a worker ever sees, and is forced
for the ones it never does. `Release` is idempotent via lookup-and-clear, so a duplicate call is a
no-op.

### B.6 `IpLimiter`

`src/DCLPulse/Transport/Hardening/IpLimiter.cs` and `IpLimiterOptions.cs`.

```csharp
public sealed class IpLimiter(IOptionsMonitor<IpLimiterOptions> options)
{
    public bool TryAcquire(string ip);                // transport thread, before TryAllocate
    public void Bind(PeerIndex peerIndex, string ip); // transport thread, after all checks pass
    public void Abandon(string ip);                   // transport thread, rollback before OnPeerConnected
    public void Release(PeerIndex peerIndex);         // worker thread, Disconnected lifecycle
}
```

- One `Lock syncRoot` guarding `Dictionary<string,int> perIpCounts` and
  `Dictionary<PeerIndex,string> ipByPeer` — same shape as `PreAuthAdmission`. Contention is bounded
  by connect rate, not packet rate. The lock is required because ENet and WebTransport run on
  separate threads.
- `perIpCounts` entries are removed at zero, so the dictionary is bounded by concurrent
  connections, not by distinct IPs seen.

```csharp
public sealed class IpLimiterOptions
{
    public const string SECTION_NAME = "Transport:Hardening:IpLimiter";

    /// <summary>Master switch. When false, connections are still counted but never refused.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum concurrent connections from one source IP, across both transports.
    /// Zero disables the cap.</summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>Comma-separated exact IPs exempt from the cap. Whitelisted IPs are still counted.</summary>
    public string Whitelist { get; set; } = "";
}
```

### B.7 Runtime reconfiguration semantics

This is the point of Part A, so the semantics need to be exact.

- **Read per call.** `TryAcquire` reads `options.CurrentValue` at entry. Connect rate is low —
  this is not a hot path in the CLAUDE.md sense (per-tick fan-out, per-packet parse), so the
  monitor lookup is free.
- **Always count, gate only enforcement.** When `Enabled == false` or `MaxConcurrency == 0`,
  `TryAcquire` still increments and `Release`/`Abandon` still decrement — only the refusal branch
  is skipped. This deliberately departs from the skill's "bail early when disabled" guidance: if
  counting stopped, re-enabling would resume from a zero baseline and over-admit until the
  population churned. Connect-rate cost makes the tradeoff free.
- **Whitelisted IPs are counted too**, for the same reason — removing an IP from the whitelist must
  take effect against an accurate count.
- **Lowering `MaxConcurrency` does not evict.** Connections already above the new cap stay; the
  counter simply refuses new ones until it drains. Retroactive eviction would kick legitimate
  players because an operator typed a smaller number. (Contrast `BanEnforcer`, which *does* evict —
  bans are about identity, not capacity.)
- **Whitelist parsing is cached.** Subscribe to `IOptionsMonitor.OnChange`, rebuild a
  `HashSet<string>`, publish with `Volatile.Write`; read with `Volatile.Read`. Same
  swap-an-immutable-snapshot pattern as `BanList`, per CLAUDE.md's "reuse existing primitives".

### B.8 Known limitations — document, do not fix now

- **Exact IP match only.** No CIDR. Office/VPN egress ranges must be listed individually. The
  string format extends to CIDR later without a schema change.
- **IPv6 is weak.** A single customer typically controls a whole /64, so per-address limiting is
  easily evaded. Keying IPv6 by /64 prefix is the correct fix; out of scope here.
- **NAT/CGNAT collateral.** A shared public IP consumes one budget. `MaxConcurrency: 10` is the
  starting point; watch the refusal metric before tightening. This is precisely why the knob is
  runtime-adjustable.

### B.9 `DisconnectReason`

`src/DCLPulse.Transport/Package/Runtime/DisconnectReason.cs` — hand-written enum, not
proto-generated, so this is a plain edit with no regeneration.

```csharp
/// <summary>
///     Hard per-source-IP concurrent-connection cap exceeded. Unlike
///     PRE_AUTH_IP_LIMIT_EXHAUSTED, authenticated connections count against this cap, and
///     the connection is refused before a PeerIndex is allocated. Retryable with backoff —
///     capacity frees as other connections from the same IP close.
/// </summary>
IP_CONNECTION_LIMIT_EXCEEDED = 17,
```

**Client recovery contract:** retryable. Exponential backoff **with jitter** — without jitter,
clients behind one NAT resynchronise and re-trigger the cap indefinitely. Surface as "too many
connections from your network", never as an authentication failure. Reuse the existing auth chain
if still inside the anti-replay window, so no wallet signature prompt is needed.

---

## Metrics

Run each through the `add-metric` skill to completion — a `Counter` declared in
`PulseMetrics.Hardening.cs` without the `MeterListener` case is silently dropped.

| Metric | Type | Fires |
|---|---|---|
| `pulse.hardening.ip_limit_refused` | `Counter<long>` | Connection refused by the cap |
| `pulse.hardening.ip_limit_whitelist_bypass` | `Counter<long>` | Would have been refused, allowed by whitelist |
| `pulse.hardening.ip_limit_tracked_ips` | `UpDownCounter<int>` | Distinct IPs currently holding ≥1 connection |

The whitelist-bypass counter is the one that tells you a whitelist entry is load-bearing rather
than vestigial.

**Feature flags carry no metrics.** `pulse.feature_flags.poll_total` and
`pulse.feature_flags.applied_keys` shipped with this design and were **removed on 2026-08-19** —
"I don't need granular stats for feature flags health itself — log is enough." Do not reintroduce
them. The replacement, in full:

- every failure path already logs with its exception (fetch failure, malformed payload, key skipped
  by the type or shape check, document rolled back by a refusing consumer);
- the success path logs one `Information` line **only when the applied override set actually
  changes**, naming the resulting key count and keys — so a 60-second poller is silent in steady
  state and speaks the moment a flag takes effect;
- `/about` is the runtime view of applied state.

Expose the currently-applied overrides on the `/about` endpoint — now the only runtime view, and the
highest-value debugging affordance regardless, since it shows what a running task *actually* has
rather than what Unleash claims.

---

## Tests

`src/DCLPulseTests/Hardening/IpLimiterTests.cs`, NSubstitute throughout.

- `AdmitsUpToCap_ThenRefuses`
- `ZeroMaxConcurrency_DisablesEnforcement`
- `Disabled_StillCounts_SoReEnableIsAccurate` — the subtle one from B.7
- `WhitelistedIp_ExceedsCap_IsAdmitted`
- `WhitelistedIp_IsStillCounted` — de-whitelist, then assert the cap applies immediately
- `WhitelistChangedAtRuntime_TakesEffect` — drive `IOptionsMonitor.OnChange`
- `MaxConcurrencyLowered_DoesNotEvictExisting`
- `Release_FreesSlot` / `DoubleRelease_IsNoOp` / `Abandon_FreesUnboundReservation`
- `Concurrent_RespectsCap` — `Barrier` + `Parallel.For`, assert admits == cap
- `PerIpEntry_RemovedAtZero` — no unbounded dictionary growth

`src/DCLPulseTests/Hardening/IpLimiterLifecycleTests.cs` — drive `PeersManager.DrainEvents` through
connected → disconnected and assert counter state, mirroring `PreAuthAdmissionLifecycleTests.cs`.

Part A: fragment flattening, prefix stripping, `flag == false` ignores its fragment, malformed
payload retains previous document, consumer-refused swap rolls back and retains previous document,
blocking-first-fetch timeout fails open. Per the A.2/A.4 revision: a key absent from
`dynamicconfig.json` is applied unchecked, and a value that cannot convert to its declared type — or
an array written where a scalar is declared — is skipped on its own while its siblings apply and the
document still announces itself as applied.

**Test fallout:** adding a DI dependency to `PeersManager` breaks four call sites —
`DrainPeerLifeCycleEventsTests.cs`, `WaitForMessagesOrTickTests.cs`, `WorkerAsyncTests.cs`, and
`Hardening/PreAuthAdmissionLifecycleTests.cs`. Update each to pass a disabled limiter.

---

## Docs

- `docs/hardening.md` — new group section following the existing structure: threat model, defenses,
  config, **how the limits interact** (the `PreAuthAdmission` overlap from B.4), client recovery,
  metrics to watch.
- `docs/feature-flags.md` — new: endpoint, naming, `dynamicconfig.json` as the type schema, how to add
  a dynamic knob, the array-vs-string whitelist rationale, local dev loop.
- `CLAUDE.md` — short section pointing at both.

---

## Verification

```bash
dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

```bash
dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

Rider `get_file_problems` on every touched file; zero warnings is the bar. Watch for the convention
slips the skill calls out: enum casing (`OK` not `Ok`), `Lock` not `object`, primary constructors
for trivial bodies.

Because `DCLPulse.csproj` gains a `<Content>` item, rebuild all three images per CLAUDE.md:

```bash
docker build -f src/DCLPulse/Dockerfile -t pulse-prod-test .
```

Confirm `dynamicconfig.json` is present in `/app/publish`. Note the dev override does **not** go in
`appsettings.Development.json` — these keys live only in `dynamicconfig.json`, which ships
`Enabled: false`, so local dev and load tests are unclamped by default and the limiter is turned on
from Unleash per environment.

---

## Prerequisites and open items

1. **Fix the `pulse.json` payload** per A.5 — three changes: the missing brace, the
   `Transport:Hardening:` path, and the whitelist string format.
2. **Confirm the `referer` hostname value** the server should send per environment for the Unleash
   hostname strategy.
3. **Decide the production `MaxConcurrency` starting value.** 10 is in the current payload; validate
   against real NAT/CGNAT population before enabling in prd. Recommend shipping `Enabled: false` and
   turning it on from Unleash after watching `ip_limit_refused` in dev.
