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
| Pulse-specific protos | `../protocol/proto/decentraland/pulse/pulse_comms.proto` | `ClientMessage` / `ServerMessage` envelopes + all game messages |
| Shared primitives | `../protocol/proto/decentraland/common/` | `vectors.proto`, `options.proto` (custom `quantized` / `bit_packed`), etc. |
| Bitwise plugin | `../protocol/protoc-gen-bitwise/plugin.py` | Python protoc plugin — emits C# `Encode`/`Decode` partials |
| Plugin runtime | `../protocol/protoc-gen-bitwise/runtime/cs/{BitReader,BitWriter,Quantize}.cs` | `Quantize.cs` is copied into `Generated/` at build time |
| Plugin wrapper (this repo) | `tools/protoc-gen-bitwise/{protoc-gen-bitwise.cmd,.sh,requirements.txt}` | Invokes the sibling `plugin.py` with `py` / `python3` |
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
   - For new top-level game messages, add to the `ClientMessage` / `ServerMessage` `oneof` in `pulse_comms.proto`.
   - For shared primitives, edit the appropriate file in `common/`.
   - Filenames are `snake_case.proto`; message types are `PascalCase`; fields are `snake_case`.
   - **Keep comments minimal** — at most one short line above a message or field. No multi-paragraph docblocks, bullet-list "contracts", or lifecycle prose. The proto is a schema vendored into every client repo; invariants belong in the server handler that enforces them or in the PR description, not here.

2. **Apply quantization / bit-packing where appropriate** (bandwidth-critical hot-path fields):
   ```protobuf
   import "decentraland/common/options.proto";

   // Float-as-uint32, clamped and quantized to N bits:
   optional uint32 position_x = 6 [(decentraland.common.quantized) = { min: 0, max: 16, bits: 8 }];

   // Integer packed into fewer than 32 bits:
   uint32 entity_id = 4 [(decentraland.common.bit_packed) = { bits: 20 }];
   ```
   Rules of thumb: match existing tiering in `pulse_comms.proto`; `optional` on a quantized field means it participates in the plugin-generated field_mask (absent fields don't hit the wire).

3. **Rebuild** — proto regen runs automatically:
   ```bash
   DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln
   ```
   Do **not** pass `-p:GenerateProto=false` during schema changes — that flag is for Docker/CI where the protocol repo isn't available and uses committed `Generated/` files.

   What happens on build:
   - `_EnsurePythonDeps` installs `protobuf==4.22.3` (once, stamp-based).
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
- **Don't add new top-level proto files outside `decentraland/common/**` or `decentraland/pulse/**`** — the `_ProtoGlobs` in `Protocol.csproj` only sees those two subtrees.
- **Don't skip regen by passing `-p:GenerateProto=false`** when you've changed `.proto`. That flag is for environments without the protocol repo.

## Validation

After regeneration:

1. Build: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln`
2. Run tests: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
3. Verify the new/changed C# types show up in `src/Protocol/Generated/PulseComms.cs` (data class) and `PulseComms.Bitwise.cs` (if the message has `quantized` / `bit_packed` fields).
4. If you changed `ClientMessage` / `ServerMessage` oneofs, confirm the new `MessageOneofCase` enum value appears and your handler is registered.

## Troubleshooting

- **`_ProtocolRepo` path error at build:** the sibling `../protocol` checkout is missing or in a different location. Clone `@dcl/protocol` as a sibling of Pulse, or override `_ProtocolRepo` via `/p:_ProtocolRepo=...`.
- **`py: command not found` (Windows) or `python3: command not found` (unix):** the plugin wrapper in `tools/protoc-gen-bitwise/` shells out to `py` / `python3`. Install Python 3 and ensure it's on PATH.
- **`pip install` fails inside `_EnsurePythonDeps`:** delete `tools/protoc-gen-bitwise/.pip.stamp` and rebuild, or `py -m pip install -r tools/protoc-gen-bitwise/requirements.txt` manually.
- **Generated files not refreshing:** delete `src/Protocol/Generated/.proto.stamp` to force regen on next build.
- **Bitwise output missing for a message:** the plugin only emits `Encode`/`Decode` for messages that contain at least one `[(quantized)]` or `[(bit_packed)]` field (directly or transitively). Pure protobuf messages get `csharp_out` only — that's expected.
