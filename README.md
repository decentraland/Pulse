# Pulse
.NET-based authoritative server for fulminant social interactions

## Protocol generation

Protocol C# files are auto-generated from the sibling [protocol](https://github.com/decentraland/protocol/tree/quantization) repo using `protoc` + the `protoc-gen-bitwise` plugin.

### Prerequisites

The `protocol` repo must be checked out as a sibling of this repo:

```
D:\<root>\protocol\   ← @dcl/protocol (quantization branch)
D:\<root>\Pulse\      ← this repo
```

You can override the path via `Directory.Build.props` or `-p:_ProtocolRepo=<path>`.

### GenerateProto switch

The `Protocol.csproj` has a `GenerateProto` property that controls whether `.proto` files are regenerated at build time or the committed `Generated/` files are used as-is.

| Mode | When to use | What happens |
|------|------------|--------------|
| `GenerateProto=true` (default) | Local development with the `protocol` repo available | Runs `protoc` + bitwise plugin, regenerates `src/Protocol/Generated/` |
| `GenerateProto=false` | Docker builds, CI, or when the `protocol` repo is not available | Skips generation, compiles committed `Generated/*.cs` files directly |

To build without generation:

```bash
dotnet build -p:GenerateProto=false
```

After modifying `.proto` files, build normally (or explicitly with `GenerateProto=true`) and commit the updated `Generated/` files so Docker and CI builds stay in sync.

#### Rider

To set `GenerateProto` from Rider:

- **Solution-wide:** Settings → Build, Execution, Deployment → Toolset and Build → MSBuild CLI arguments → add `-p:GenerateProto=false`
- **Per configuration:** Run → Edit Configurations → select configuration → Before launch → click the build step → add `-p:GenerateProto=false` to MSBuild arguments

In practice the default (`true`) is correct for local development. The `false` value is used by Docker/CI builds that don't have the protocol repo available.