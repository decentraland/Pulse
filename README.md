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

## Docker debugging

### Start the debug container

```bash
docker compose -f docker-compose.debug.yml up --build
```

Wait for the server to log that it is listening on port 7777. The `src/` directory is volume-mounted — after editing source, restart the container to pick up changes (`docker compose -f docker-compose.debug.yml restart`).

### Attach Rider

1. Run → Edit Configurations → **+** → **.NET Attach to Remote Process**
2. Connection type: **Docker container**
3. Container: `dcl-pulse-debug`
4. vsdbg path: `/vsdbg`
5. Path mapping: local `./src` ↔ container `/app/src` (usually resolved automatically via the volume mount; set manually if Rider misses it)

Use **logpoints over breakpoints** for networking code (right-click gutter → Add Logpoint). They log to the Debug console without pausing the ENet tick loop or disconnecting clients.

### Quick reference

| Action | Command |
|---|---|
| Start | `docker compose -f docker-compose.debug.yml up --build` |
| Tail logs | `docker logs -f dcl-pulse-debug` |
| Rebuild | `docker compose -f docker-compose.debug.yml up --build --force-recreate` |
| Stop | `docker compose -f docker-compose.debug.yml down` |
| Connect client | `127.0.0.1:7777` |

### Remote debugging (dev environment)

The **Deploy Dev (Debug)** workflow (`deploy-dev-debug.yml`) deploys a debug-capable image to the dev environment. Trigger it manually from the GitHub Actions UI.

The debug image includes vsdbg and full debug symbols but runs on the slim `dotnet/runtime` base (not the SDK), so it's close to production weight.

#### Connecting Rider via ECS Exec

1. Find the running task:

```bash
aws ecs list-tasks --cluster <cluster> --service-name dcl-pulse --query 'taskArns[0]' --output text
```

2. Verify ECS Exec is enabled and attach:

```bash
aws ecs execute-command \
  --cluster <cluster> \
  --task <task-id> \
  --container dcl-pulse \
  --interactive \
  --command "/bin/bash"
```

3. In Rider: Run → Edit Configurations → **+** → **.NET Attach to Remote Process**
   - Connection type: **Custom pipe**
   - Pipe command: `aws ecs execute-command --cluster <cluster> --task <task-id> --container dcl-pulse --interactive --command`
   - Debugger path: `/vsdbg/vsdbg`
   - Path mapping: local `src/` ↔ container `/app/`

4. Select the `dotnet` process from the process list.

Prefer **logpoints over breakpoints** — a breakpoint pauses the ENet tick loop and disconnects all clients.

#### Reverting to production

After debugging, push to `main` or run the **Manual Deploy** workflow to redeploy the production image.