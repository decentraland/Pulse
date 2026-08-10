---
name: modify-protocol
description: Modify the Pulse wire protocol — add/change/remove messages, enums, fields, or quantization options in the .proto files and regenerate C# bindings. Use when the user wants to change the wire format, add a new Client/Server message variant, add or retune a quantized field, or otherwise touch the schema.
user-invocable: true
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
argument-hint: <change description, e.g. "add Crouch state flag" or "add ClientMessage variant for ping">
---

# Modify the Pulse Protocol

The protocol is the single source of truth for the wire format. It lives in a **sibling repository** (`@dcl/protocol`), not in this repo. Pulse consumes it as `.proto` sources and generates C# at build time.

## Where things live

| Thing | Path | Notes |
|---|---|---|
| Proto sources | `../protocol/proto/decentraland/` | **Sibling repo** — edit these |
| Pulse-specific protos | `../protocol/proto/decentraland/pulse/` | Three files: `pulse_client.proto` (C→S messages + `ClientMessage` envelope), `pulse_server.proto` (S→C messages + `ServerMessage` envelope), `pulse_shared.proto` (types referenced by both directions: `PlayerState`, `GlideState`, `PlayerAnimationFlags`) |
| Shared primitives | `../protocol/proto/decentraland/common/` | `vectors.proto`, `options.proto` (custom `quantized` / `quantized_power` / `bit_packed`), etc. |
| Bitwise plugin | `../protocol/protoc-gen-bitwise/plugin.js` | Node/JS protoc plugin — emits C# quantized-accessor partials (`*.Bitwise.cs`) |
| Plugin runtime | `../protocol/protoc-gen-bitwise/runtime/cs/{BitReader,BitWriter,Quantize}.cs` | `Quantize.cs` is copied into `Generated/` at build time |
| Plugin wrapper (this repo) | `tools/protoc-gen-bitwise/{protoc-gen-bitwise.cmd,.sh}` | Invokes the sibling `plugin.js` with `node` |
| Generated C# | `src/Protocol/Generated/` | **Do not hand-edit** — overwritten on build |
| Build wiring | `src/Protocol/Protocol.csproj` + `src/Protocol/Directory.Build.props` | Runs protoc + bitwise plugin via MSBuild targets |
| Hand-written partial extensions | `src/Protocol/ProtoTypesConversion.cs` | Convenience conversions / nullable accessors — safe to extend |

The default `_ProtocolRepo` assumes the layout:
```
D:\Decentraland\protocol\   ← @dcl/protocol (sibling)
D:\Decentraland\Pulse\      ← this repo
```
Override with `/p:_ProtocolRepo=...` or a local `Directory.Build.props` if your checkout differs.

## Change workflow

1. **Edit the `.proto` file(s)** in `../protocol/proto/decentraland/...`
   - **Pick the right pulse file by direction:**
     - Client→server message → add it to `pulse_client.proto`, then add the variant to the `ClientMessage` `oneof` at the bottom of that file.
     - Server→client message → add it to `pulse_server.proto`, then add the variant to the `ServerMessage` `oneof` at the bottom of that file.
     - Type referenced from **both** directions (e.g. another shared state struct like `PlayerState`) → put it in `pulse_shared.proto`. Both `pulse_client.proto` and `pulse_server.proto` import it. Don't cross-import client↔server.
   - For shared primitives across protocols (vectors, etc.), edit the appropriate file in `common/`.
   - Filenames are `snake_case.proto`; message types are `PascalCase`; fields are `snake_case`.
   - **Keep comments minimal** — at most one short line above a message or field. No multi-paragraph docblocks, bullet-list "contracts", or lifecycle prose. The proto is a schema vendored into every client repo; invariants belong in the server handler that enforces them or in the PR description, not here.

2. **Apply quantization / bit-packing where appropriate** (bandwidth-critical hot-path fields):
   ```protobuf
   import "decentraland/common/options.proto";

   // Float-as-uint32, clamped and uniformly quantized to N bits:
   uint32 position_x = 2 [(decentraland.common.quantized) = { min: 0, max: 16, bits: 8 }];

   // Signed float on a power-law curve — fine resolution near zero, coarse at the extremes;
   // sign + (bits-1)-bit magnitude, symmetric [-max, max]; a stopped value encodes to exactly 0:
   uint32 velocity_x = 5 [(decentraland.common.quantized_power) = { max: 50, pow: 2, bits: 8 }];

   // Integer packed into fewer than 32 bits:
   uint32 entity_id = 4 [(decentraland.common.bit_packed) = { bits: 20 }];
   ```
   A field carries `quantized` **or** `quantized_power`, never both. `optional` on a quantized field uses native protobuf per-field presence (absent fields don't hit the wire; no plugin-generated mask).

   For each quantized `uint32` field the plugin generates (in `*.Bitwise.cs`) a `float {Name}Quantized` accessor (encode on set / decode on get) **and** a `public const float {Name}QuantizedStep` — the coarsest quantization step for that field (uniform step for `quantized`, worst-case top-of-range step for `quantized_power`), safe to use as an equality tolerance. In tests, assert decoded values with `Is.EqualTo(x).Within(PlayerState.{Name}QuantizedStep)` rather than hardcoding a magic tolerance.

   **Quantization lives in two proto messages that must stay in lockstep.** A movement/animation field is quantized in *both*:
   - `PlayerState` in `pulse_shared.proto` — the full state (STATE_FULL, PlayerJoined, EmoteStarted/Stopped, Teleported, plus the client's `PlayerStateInput` and handshake seed).
   - `PlayerStateDeltaTier0` in `pulse_server.proto` — the per-tick delta.

   Both must annotate a given field (`position_x`, `velocity_x`, `rotation_y`, `head_yaw`, `point_at_x`, …) with the **identical** `min`/`max`/`bits` (and `pow`) so a full snapshot and a stream of deltas decode onto the *same grid* — otherwise a resync / join / teleport lands the client on a different value than the deltas do. The field *numbers* differ between the two messages (that's fine — they're separate messages); only the quantization params must match. When you add or retune such a field, change it in **both** messages together. `DCLPulseTests/PlayerStateQuantizationTests` guards this: it round-trips both through the wire and asserts the decoded values are equal, failing if they drift. (The one intended difference: `PlayerStateDeltaTier0` marks every field `optional` because a diff only carries what changed; `PlayerState` keeps always-present fields non-optional and leaves only the genuinely optional ones — `head_yaw`, `head_pitch`, `point_at_*` — `optional`.)

   **Server-side, a new quantized `PlayerState` field must also be threaded through the snapshot ledger**, which stores the raw quantized codes (not decoded floats) and diffs on them exactly: add a `uint`/`uint?` field to `PeerSnapshot`, populate it in `PeerSnapshotPublisher`, compare + emit it in `PeerViewDiff.CreateMessage` and `PlayerStateInputHandler.IsSameState`, and copy it in `PeerSimulation.CreatePlayerState`. Only `GlobalPosition` is kept decoded there — it's the one value the server itself consumes (interest management).

   **Input-range validation is automatic** — the plugin emits a `bool AreQuantizedFieldsInRange()` on every message with quantized fields (a pure integer check that each raw code is within its `2^bits-1` bound), and `FieldValidator` already calls it on every inbound message (`PlayerStateInput`, `EmoteStart`, handshake `PlayerInitialState`, `TeleportRequest`). A new quantized field is therefore range-checked with no extra code — the server never relays a code that would decode outside the field's `[min, max]`.

3. **Rebuild** — proto regen runs automatically:
   ```bash
   DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln
   ```
   Do **not** pass `-p:GenerateProto=false` during schema changes — that flag is for Docker/CI where the protocol repo isn't available and uses committed `Generated/` files.

   What happens on build:
   - `_CheckNode` verifies `node` is on PATH (the bitwise plugin is a dependency-free Node script — nothing to install).
   - `_GenerateProto` runs `protoc` with both `--csharp_out` and `--bitwise_out` against every `*.proto` under `decentraland/common/**` and `decentraland/pulse/**`. Stamp-based, so only re-runs when a `.proto` changes.
   - `Quantize.cs` is copied from the plugin's runtime into `Generated/`.
   - `_AddGeneratedProtoToCompile` globs `Generated/**/*.cs` into the compile.

4. **Wire up the new message on the server side** (if you added one):
   - Add a handler class in `src/DCLPulse/Messaging/` implementing `IMessageHandler` (see `EmoteStartHandler`, `TeleportHandler`, etc.).
   - Register it in `src/DCLPulse/Program.cs`:
     - `AddSingleton<MyNewHandler>()`.
     - Add to the `Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>` map.
   - **Bump `ClientMessageCounters` / `ServerMessageCounters` capacity** in `Program.cs` (the `new ClientMessageCounters(N)` constructor — N must be ≥ oneof case count). The same constant is passed in tests (`DrainPeerLifeCycleEventsTests.cs`, `WaitForMessagesOrTickTests.cs`, etc.) — update those too or they'll fail at construction.
   - For server→client sends, call the appropriate pipeline (`MessagePipe.Enqueue` / worker outbox). Pick the channel per the CLAUDE.md conventions: ch0 reliable for control/discrete events, ch1 unreliable sequenced for continuous state.

5. **Extend hand-written partials** if useful: add convenience methods to `src/Protocol/ProtoTypesConversion.cs` (e.g. nullable accessors for `optional` fields, implicit conversions). These live in the same namespace as the generated partial classes and survive regen.

6. **Commit the regenerated `Generated/**/*.cs`** alongside the proto change. The Docker pipeline builds with `-p:GenerateProto=false` and relies on committed files.

7. **Commit the sibling protocol repo change too.** The schema change is two PRs: one in `../protocol` (source of truth, shared across clients), one in Pulse (generated code + server wiring). Per the protocol repo's README, the merge order is:
   1. Open PR in `../protocol`.
   2. Open PR in Pulse (and Unity client, if relevant).
   3. Merge the protocol PR first, then the consumer PRs.
   Do **not** merge the protocol PR until the corresponding Unity implementation is ready — CI across consumer repos will break otherwise.

## What not to do

- **Don't hand-edit anything in `src/Protocol/Generated/`.** It's regenerated on every proto change. Put extensions in `ProtoTypesConversion.cs` instead.
- **Don't change field numbers of existing fields** or reuse numbers of deleted fields — protobuf wire compatibility breaks silently. Mark old fields `reserved` if needed.
- **Don't change quantization `min`/`max`/`bits` on a deployed field without coordinating client + server rollout** — encoded values won't round-trip across the version boundary.
- **Don't retune (or add) a quantized field in only one of `PlayerState` / `PlayerStateDeltaTier0`** — they must carry identical `quantized` / `quantized_power` params, or a full state and its deltas decode onto different grids. `PlayerStateQuantizationTests` fails when they drift.
- **Don't add new top-level proto files outside `decentraland/common/**` or `decentraland/pulse/**`** — the `_ProtoGlobs` in `Protocol.csproj` only sees those two subtrees.
- **Don't skip regen by passing `-p:GenerateProto=false`** when you've changed `.proto`. That flag is for environments without the protocol repo.

## Validation

After regeneration:

1. Build: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln`
2. Run tests: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
3. Verify the new/changed C# types show up in the generated file matching the source proto: `src/Protocol/Generated/PulseClient.cs`, `PulseServer.cs`, or `PulseShared.cs` (data classes), plus `PulseServer.Bitwise.cs` etc. (if the message has `quantized` / `bit_packed` fields). All three live in the same `Decentraland.Pulse` namespace, so callers don't need to know which file a type came from.
4. If you changed `ClientMessage` / `ServerMessage` oneofs, confirm the new `MessageOneofCase` enum value appears and your handler is registered.

## Troubleshooting

- **`_ProtocolRepo` path error at build:** the sibling `../protocol` checkout is missing or in a different location. Clone `@dcl/protocol` as a sibling of Pulse, or override `_ProtocolRepo` via `/p:_ProtocolRepo=...`.
- **`node: command not found` / `'node' is not recognized`:** the plugin wrapper in `tools/protoc-gen-bitwise/` runs `node`. Install Node 16+ and ensure it's on PATH, or build with `-p:GenerateProto=false` to use the committed `Generated/` files.
- **`protoc-gen-bitwise: Plugin failed`:** the sibling `../protocol/protoc-gen-bitwise/plugin.js` is missing or `_ProtocolRepo` doesn't point at your `../protocol` checkout. Confirm the protocol repo is a sibling of Pulse (or override `/p:_ProtocolRepo=...`).
- **Generated files not refreshing:** delete `src/Protocol/Generated/.proto.stamp` to force regen on next build.
- **Bitwise output missing for a message:** the plugin only emits a `*.Bitwise.cs` partial for messages that contain at least one `[(quantized)]` or `[(bit_packed)]` field (directly or transitively). Pure protobuf messages get `csharp_out` only — that's expected.
