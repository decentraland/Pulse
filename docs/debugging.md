# Debugging

Two debugging paths are supported: **local** (docker-compose) and **remote** (ECS Fargate dev via bastion).

> The generic Decentraland ECS debugging workflow (bastion access, tunnel setup, internal hostnames) is documented in the `decentraland/playbooks` repository — **internal access only**. This doc focuses on the Pulse-specific Dockerfile setup and Rider configuration.

## Dockerfiles

| File | Target | Base image | Build config | Extras |
|---|---|---|---|---|
| `src/DCLPulse/Dockerfile` | Production (Fargate prod + regular dev) | `runtime:10.0` | Release | None |
| `src/DCLPulse/Dockerfile.dev-debug` | Fargate dev debug deploy | `runtime:10.0` | Debug | vsdbg + sshd + RiderRemoteDebugger |
| `Dockerfile.debug` | Local docker-compose | `sdk:10.0` | Debug | vsdbg |

## Ports

| Port | Protocol | Purpose |
|---|---|---|
| 7777 | UDP | ENet game traffic |
| 5000 | TCP | HTTP `/health` + `/metrics` (Prometheus) |
| 2222 | TCP | SSH for Rider remote debugger (debug images only) |

## Remote debugging (Fargate dev)

### What's baked into `Dockerfile.dev-debug`

- **vsdbg** at `/vsdbg` — Microsoft's .NET debugger backend
- **openssh-server** on port 2222 — passwordless root login, reachable only from within the VPC via the bastion
- **JetBrains RiderRemoteDebugger** at `/root/.local/share/JetBrains/RiderRemoteDebugger/<version>/` — pre-installed so Rider skips the "Install tools to remote computer" prompt on every deploy. Version is resolved from the JetBrains redirect filename at build time:
  ```
  https://data.services.jetbrains.com/products/download?code=RRD&platform=linux64
    → .../JetBrains.Rider.RemoteDebuggerUploads.linux-x64.<version>.zip
  ```

### Workflow

1. **Deploy debug image** — trigger the **Deploy Dev (Debug)** GitHub Action on `main`. This replaces the regular dev image with `Dockerfile.dev-debug`. The next push to `main` restores the Release image automatically.

2. **Open an SSH tunnel through the dev bastion** to reach port 2222 on the Pulse container. The bastion address, internal service hostname, and 2FA procedure are documented in the internal playbook.

3. **Attach Rider** — **Run → Attach to Remote Process**
   - Connection type: SSH
   - Host: `localhost`, Port: `2222`, User: `root`
   - Authentication: Password, leave empty
   - Select the `dotnet DCLPulse.dll` process
   - Path mapping: local `src/` ↔ remote `/app`

### Logpoints over breakpoints

Breakpoints in the ENet tick loop freeze the server and disconnect all clients. Use **right-click gutter → Add Logpoint** — evaluates an expression and logs to the Debug console without pausing execution.

## Local debugging (docker-compose)

Uses `Dockerfile.debug` + `docker-compose.debug.yml`. Rider attaches via Docker socket rather than SSH.

1. Start the container:
   ```bash
   docker compose -f docker-compose.debug.yml up --build
   ```

2. In Rider: **Run → Edit Configurations → + → .NET Attach to Remote Process**
   - Connection type: Docker container
   - Container: `dcl-pulse-debug`
   - vsdbg path: `/vsdbg`

3. Path mapping: local `./src` ↔ container `/app/src` (auto-resolved via volume mount).

### Required capabilities

`docker-compose.debug.yml` sets these for vsdbg to work:

```yaml
cap_add:
  - SYS_PTRACE         # vsdbg needs ptrace to attach
security_opt:
  - seccomp:unconfined # vsdbg uses syscalls blocked by default profile
```

### Quick reference

| Action | Command |
|---|---|
| Start debug container | `docker compose -f docker-compose.debug.yml up --build` |
| Tail logs | `docker logs -f dcl-pulse-debug` |
| Rebuild after Dockerfile change | `docker compose -f docker-compose.debug.yml up --build --force-recreate` |
| Stop | `docker compose -f docker-compose.debug.yml down` |

## Local addresses

| Scenario | Address |
|---|---|
| Unity client → game server (same machine) | `127.0.0.1:7777` |
| Container internal IP | `docker inspect dcl-pulse-debug \| grep IPAddress` |
| Container → container (same compose) | Use service name, e.g. `dcl-pulse:7777` |
| Container → host machine service | `host.docker.internal` (Linux: add `extra_hosts: - "host.docker.internal:host-gateway"`) |

## Debug symbols

Debug builds emit full PDBs via `src/DCLPulse/DCLPulse.csproj`:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DebugType>full</DebugType>
  <DebugSymbols>true</DebugSymbols>
  <Optimize>false</Optimize>
</PropertyGroup>
```
