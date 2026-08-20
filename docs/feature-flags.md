# Feature Flags & Runtime Configuration

Pulse reads a remote Unleash document and turns it into the highest-precedence layer of
`IConfiguration`. Existing `IOptions<T>` plumbing keeps working unchanged; consumers that want
live values take `IOptionsMonitor<T>` and get reload for free.

The flags are **server-wide ops toggles**. The server fetches one document with no user context,
so per-wallet or per-session targeting is not available and never will be through this path.

The first consumer is the per-IP connection limiter — see
[docs/hardening.md](hardening.md) (Group H) for what its knobs mean.

---

## The endpoint

```
https://feature-flags.decentraland.{org|zone}/pulse.json
```

`org` for prd, `zone` for every other environment — the same `EnvName.HttpSuffix` split the bans
poller uses. `FeatureFlags:Url` overrides the **origin** (no trailing path); the document name is
`{AppName}.json`, so the Unleash application name picks the document.

The request carries `X-Debug` and `referer` (Unleash's hostname strategy keys off `referer`).
There is no `X-Address-Hash` header: the server has no user identity to hash.

`FeatureFlagsPoller` (`src/DCLPulse/FeatureFlags/`) is a `BackgroundService` modelled on
`BansPollingHttpService`.

### Config

The `FeatureFlags` section is read while the configuration is still being built, before DI
exists, so **these keys can never themselves be set from the remote document**.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. `false` runs neither the blocking first load nor the poller — the server behaves exactly as it does with no remote document at all. |
| `Url` | *(derived)* | Origin of the feature-flag service, no trailing path. Unset derives `https://feature-flags.decentraland.{HttpSuffix}`. |
| `PollIntervalSeconds` | 60 | Refetch interval. `0` disables the poller; whatever the blocking first load produced stays in effect for the process lifetime. |
| `HttpTimeoutSeconds` | 10 | Per-request HTTP timeout. `0` means no timeout. |
| `InitialFetchTimeoutSeconds` | 5 | How long startup waits for the first document before continuing on shipped defaults. `0` skips the blocking load and leaves the first fetch to the poller. |
| `AppName` | `pulse` | Unleash application name. Selects the document (`{AppName}.json`) and is the prefix stripped from every flag and variant key. |
| `Hostname` | *(unset)* | Value sent as the `referer` header, which Unleash's hostname strategy matches on. The header is omitted when unset. |

### Document shape

Unleash serves `{flags, variants}`. Two details of that shape matter and both are easy to get
wrong:

1. **Flag keys carry the application-name prefix `pulse-`**, which the server strips before
   matching. `pulse-ip-limiter` in Unleash is `ip-limiter` to the server.
2. **`payload.value` is a *string containing JSON*, not a nested object.** It needs a second
   parse. A reader who treats it as an object gets a type error, and a writer who puts a raw
   object there produces a payload the server cannot read.

Abbreviated, with the JSON string unescaped for readability:

```jsonc
{
  "flags": { "pulse-hardening": true },
  "variants": {
    "pulse-hardening": {
      "name": "configuration",
      "payload": {
        "type": "json",
        // A STRING. The real document escapes every quote inside it.
        "value": "{ \"Transport\": { \"Hardening\": { \"IpLimiter\": { \"Enabled\": true, \"MaxConcurrency\": 10, \"Whitelist\": \"\" } } } }"
      }
    }
  }
}
```

## Flag naming and the config-fragment model

One flag per subsystem. Each flag carries a variant named `configuration` whose payload is a
literal **appsettings-shaped fragment** — the same nesting you would write into
`appsettings.json`, at the same key path. Fragments are flattened with Microsoft's own flattener
(`AddJsonStream(...).Build().AsEnumerable()`), so nesting and type coercion behave identically to
a JSON config file.

A flag set to `false` means its fragment is **ignored** and the shipped defaults apply. That is a
free per-subsystem kill switch: one toggle reverts one subsystem to the values baked into the
image without disturbing any other subsystem's overrides.

## `dynamicconfig.json` — the type schema

`dynamicconfig.json` ships with the image, separate from `appsettings.json`. Its values are the
offline defaults for the dynamic knobs, and — because a default value has a JSON type — those same
values are the **type schema** the remote document's values are checked against.

**It is not an allowlist.** Unleash is a trusted source: the remote document may set any
configuration key, whether or not this file declares it. What the file bounds is what can be
*type-checked*, not what can be *set*.

Only **leaf** keys carry a type — a key that holds a value. Section nodes (`Transport`,
`Transport:Hardening`) carry a null value and are skipped.

```jsonc
// dynamicconfig.json — offline defaults for the dynamic knobs, and their type schema.
{
  "Transport": { "Hardening": { "IpLimiter": {
    "Enabled": false,
    "MaxConcurrency": 10,
    "SceneListenerMaxConcurrency": 2,
    "Whitelist": "",
  }}},
}
```

.NET's JSON config parser skips `//` comments and allows trailing commas, so per-key notes live
inline in the file.

### Why the type check needs the raw file

`IConfiguration` stringifies every value as it loads — `10` becomes `"10"` — so by the time a
built configuration can be queried, the JSON type is gone. `DynamicConfigSchema` parses the file
itself for exactly this reason: it is the only place a key's declared type survives, and therefore
the only thing that can tell a value the options binder will refuse (`"ten"` for an `int`) from one
it will accept (`"20"`, which binds fine — convertibility, not kind identity).

### Malformed value: log and skip that key

A key **with** a default here whose remote value cannot convert to that default's type is skipped
on its own:

- a warning names the flag, the key, the offending value and the expected type;
- **only that key** is withheld — every other key in the document applies, and the skipped knob
  falls back to whatever the lower-precedence sources give it (the offline default, or an operator's
  environment variable);
- the document is **not** rejected — it still applies and still announces itself.

A key **without** a default here has no known type, so nothing can be checked and it is applied as
written.

The reason for the check at all is narrow and specific: an unbindable value throws inside the
options binder on whichever thread first reads the knob. In this server that is a transport thread,
and `BackgroundServiceExceptionBehavior.StopHost` turns it into a stopped host. Worse, if it arrives
on the blocking first load the server boots clean, passes health checks and dies on the first
player. The check exists to keep a typo in Unleash from doing that — not to police what Unleash may
change.

Two more properties of the file:

- Registered `optional: false`, and `DynamicConfigSchema.LoadFromFile` throws on a missing file to
  match. It is the only source of the offline defaults; failing loudly at startup is the better
  failure.
- `Microsoft.NET.Sdk.Worker` already globs `**/*.json` as `Content` but sets
  `CopyToOutputDirectory` only for `appsettings*.json`, so this file needs an explicit
  `<Content Update="dynamicconfig.json" CopyToOutputDirectory="PreserveNewest" />` in
  `DCLPulse.csproj` (`Update`, not `Include` — `Include` fails with `NETSDK1022`). Per the CLAUDE.md
  build rule, rebuild all three Docker images to confirm it reaches `/app/publish`.

### What this means for `/about`

`/about` reports the applied overrides verbatim and takes no bearer token. With no allowlist in
front of it, that endpoint publishes whatever keys the remote document set — so whoever authors the
Unleash payload decides what an unauthenticated endpoint exposes. Nothing in the server filters it.

### Scope — new keys only

No existing `appsettings.json` key becomes dynamic, and none of the existing `IOptions<T>`
consumers are migrated. Liveness is a property of the **consumer**, not of the value: a knob is
dynamic only if the code reading it observes changes. See the next section.

## How to add a dynamic knob

1. **Add the key to `dynamicconfig.json`** with a safe offline default — the value the server
   should run on when Unleash is unreachable or the flag is off. Use the key's final, real config
   path; hardening knobs follow the house convention `<Layer>:Hardening:<Name>`.
2. **Consume it via `IOptionsMonitor<T>`, never `IOptions<T>`.** `IOptions<T>` resolves once at
   construction and captures that value for the process lifetime — the knob would be remotely
   settable and silently inert, which is the worst of both worlds. If the consumer caches a
   derived form (a parsed set, a compiled predicate), rebuild it from `IOptionsMonitor.OnChange`
   and publish the result as an immutable snapshot rather than mutating in place.
3. **Add the key to the Unleash payload** — the subsystem flag's `configuration` variant, using
   the same nesting as step 1. Nothing checks the *path*: a key the payload sets that is not in
   `dynamicconfig.json` is applied as written, so a path typo is a live override on a key nothing
   reads. `/about` is how you catch that — it lists the keys actually applied.

To verify what a running task actually has, read the `/about` endpoint: it reports the
currently-applied overrides, which is the question you want answered — not what Unleash claims to
be serving.

## Why `Whitelist` is a string, not an array

List-shaped knobs are **comma-separated strings**, not JSON arrays. This is a verified .NET
configuration behaviour rather than a style preference, so do not "improve" it back into an
array.

`IConfiguration` flattens a JSON array into indexed keys — `Whitelist:0`, `Whitelist:1`, … — and
a higher-precedence source *overwrites keys*, it does not *replace collections*. Two consequences,
both verified empirically:

- **An empty array cannot clear a list.** `[]` flattens to *zero* config keys. With `Whitelist:0`
  and `Whitelist:1` present in a lower-precedence source, a higher-precedence `[]` leaves both in
  place.
- **Shortening a list silently leaks entries.** Going from three entries to two overwrites
  `Whitelist:0` and `Whitelist:1` and leaves `Whitelist:2` untouched — the IP you meant to remove
  stays whitelisted.

A single scalar key has neither problem: `"Whitelist": ""` overwrites cleanly and clears the
list, and any shorter value fully replaces any longer one. The consumer splits on commas.

This applies to every list-shaped dynamic knob, not only this one.

**The typo is caught, not silently applied.** A remote document that writes an array anyway would
otherwise be the worst kind of wrong: `Whitelist:0` and `Whitelist:1` have no declared type so they
apply unchecked, while `Whitelist` itself never appears and keeps its default — the document reads
as applied and the whitelist stays empty. `DynamicConfigSchema` catches it by shape: an incoming key
whose **parent path is itself a declared scalar key** is an index into a knob that holds a value, so
it is skipped with a warning naming the key and restating that list-shaped knobs are comma-separated
strings. Same per-key scope as the type check — the rest of the document applies.

## Local dev loop

`dynamicconfig.json` is registered with `reloadOnChange: true`. Editing the file on a running
server fires `IOptionsMonitor` change tokens directly — no Unleash round trip, no redeploy, no
poller involved. It is the fastest way to confirm a knob is genuinely live-wired before touching
the remote document.

The shipped defaults in that file are also what a dev machine and a CI run see, since neither has
the remote document applied.

## Operational safety

- **Soft failure.** Any fetch problem — non-2xx, timeout, DNS failure, malformed JSON — retains
  the previous document and logs a warning with the exception. The poller never throws. Through a
  CDN outage the server keeps running on the last known good document.
- **Type and shape check before swap, per key.** Every candidate value whose key has a default in
  `dynamicconfig.json` is checked against that default's type before anything is published, and any
  key nested under a declared scalar is rejected as an array written where a delimited string
  belongs. One that fails either check is logged and skipped; the rest of the document applies.
- **Rollback if a consumer refuses the swap.** The type check cannot foresee every way a binder
  rejects a value, so the swap is transactional: if firing the reload token throws, the previous
  `Data` is restored, consumers are notified again, and a warning records the rollback
  (`rolled back to the previous overrides`). This is the last-resort net that stops a poison value
  from faulting a transport thread and taking the host down with it.
- **Blocking first fetch, failing open.** `IConfigurationSource` providers load while
  `builder.Configuration` is being constructed, before DI exists and before `builder.Build()`.
  The first fetch is therefore a blocking load with a ~5 s timeout that fails open to the shipped
  defaults — the same semantics as `optional: true` on a JSON file. A dead CDN delays startup and
  logs loudly; it does not fail the task.
- **Kill switches, in increasing scope.** A flag set to `false` reverts one subsystem to its
  shipped defaults. `FeatureFlags:PollIntervalSeconds = 0` freezes the document at whatever the
  first load produced. `FeatureFlags__Enabled=false` disables the provider wholesale — no first
  load, no poller — and the server behaves exactly as it did before this mechanism existed.

## What to watch — logs and `/about`

There are **no feature-flag metrics**. The repo owner's call: the poller is a cold path on a
seconds schedule, and its whole state is one small key/value set that `/about` already publishes
verbatim, so a log line carries more than a counter would.

| Signal | Level | When |
|---|---|---|
| `Feature flag overrides changed; N key(s) now applied: …` | Information | Only when the applied override set actually differs from the one already in force — the key set changed, or a key's value did. A steady-state poller is silent. |
| `Feature flags poll failed` | Warning | Fetch or parse failure on a background poll; previous document retained. |
| `Initial feature flags fetch from {Url} failed` | Error | The blocking first load failed; the task booted on shipped defaults. |
| `Feature flags document carried a malformed configuration payload` | Warning | A variant payload string is not valid JSON; previous overrides retained. |
| `…skipping that key and applying the rest of the document` | Warning | One key failed the type or shape check. The document still applied — grep for this when a knob you set is missing from `/about`. Fires when a key *starts* failing, not on every poll: a document left broken says it once, and a key that is fixed and breaks again says it again. |
| `…rolled back to the previous overrides` | Warning | A consumer threw while rebinding, so the whole swap unwound. This is the serious one: Unleash now shows values the server is not running. |

The change line is the positive confirmation that a fetch worked. Because it fires only on change,
its **absence is not a failure signal** — a healthy server that nobody has touched says nothing.
To check what a task is actually running, read `/about`.

## Setup prerequisites

- An Unleash **application named `pulse`** must exist at `features.decentraland.systems`. The
  document served at `.../pulse.json` is that application's flag set, and the `pulse-` prefix the
  server strips from flag keys *is* that application name (`FeatureFlags:AppName`).
- If a flag uses Unleash's hostname strategy, `FeatureFlags:Hostname` must be set to a value that
  strategy accepts for that environment. If it is unset the `referer` header is omitted; if it is
  wrong, hostname-scoped flags resolve `false` and every subsystem silently runs on shipped
  defaults. The concrete per-environment value is not pinned down in the design — confirm it
  before relying on hostname-scoped flags.
