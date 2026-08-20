# QA — validating the per-IP connection limiter

Manual validation of `Transport:Hardening:IpLimiter` against a deployed instance (`zone`, `dev`).
Every step gives the command for **macOS/Linux** (bash/zsh) and **Windows** (PowerShell).

Four properties to confirm:

1. A source IP over the player cap is refused, and the client receives `IP_CONNECTION_LIMIT_EXCEEDED`.
2. Whitelisting the IP **at startup** admits every connection.
3. Whitelisting the IP **at runtime**, with no restart, admits every connection.
4. A source IP over the scene-listener cap is refused with `SCENE_LISTENER_IP_LIMIT_EXCEEDED`.

---

## Prerequisites

**Both platforms**

- .NET SDK 10 — `dotnet --version` must report `10.x`.
- A checkout of this repo.
- `metaforge` on `PATH` — `metaforge --version`. At least 12 accounts for step 2:
  `metaforge account list`, and create any missing with `metaforge account create loadtest-<n>`.
  Auth chains are validated locally by the server, so accounts created against `org` work against
  `zone`.
- The target host and UDP port.
- Edit access to the `pulse-hardening` flag at features.decentraland.systems.

**Platform-specific**

| | macOS | Windows |
|---|---|---|
| ENet native library | `brew install enet` | `enet.dll` ships in the build output — no action |
| HTTP client | `curl` | **`curl.exe`**, not `curl` — see the note below |
| Text search | `grep` | `Select-String` |

> **Windows: always write `curl.exe`.** In Windows PowerShell 5.1, `curl` is an *alias for
> `Invoke-WebRequest`*, which takes different flags and returns an object rather than text. Every
> `-s`/`-H` example below will fail if the alias is hit. PowerShell 7+ removed the alias, but
> writing `curl.exe` is correct on both.

---

## Step 0 — find your public egress IP

This is the address the server sees and the value the whitelist needs. **Not** your LAN or VPN-internal
address.

**macOS**
```bash
curl -s https://ifconfig.me
```

**Windows**
```powershell
(Invoke-RestMethod https://ifconfig.me/ip).Trim()
```

Note it down as `<EGRESS_IP>`. If several testers share an office NAT or VPN egress they share one
budget — coordinate, or expect interference. Re-check this if you connect/disconnect a VPN mid-session.

---

## Step 1 — read the live configuration

**macOS**
```bash
curl -s https://feature-flags.decentraland.zone/pulse.json | python3 -m json.tool
```

**Windows**
```powershell
Invoke-RestMethod https://feature-flags.decentraland.zone/pulse.json | ConvertTo-Json -Depth 6
```

Find the `pulse-hardening` variant's `configuration` payload and note `Enabled`, `MaxConcurrency`
and `SceneListenerMaxConcurrency`. **If `Enabled` is `false` the limiter is inert** and steps 2 and 4
cannot fail — turn it on first.

---

## Step 2 — case 1: not whitelisted, refusal reaches the client

With `Whitelist` empty, run **more bots than `MaxConcurrency`**. At the shipped 10, use 12.

Prefer this over lowering `MaxConcurrency`: that knob is global, so dropping it on a shared
environment starts refusing real clients.

**macOS**
```bash
dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --account=loadtest --bot-count=12 --ip=<HOST> --port=<PORT> 2>&1 | tee /tmp/qa-client.log
```

**Windows**
```powershell
dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --account=loadtest --bot-count=12 --ip=<HOST> --port=<PORT> 2>&1 | Tee-Object -FilePath $env:TEMP\qa-client.log
```

Let it run ~20 seconds, then stop with `Ctrl+C`. Count the refusals:

**macOS**
```bash
grep -c IP_CONNECTION_LIMIT_EXCEEDED /tmp/qa-client.log
```

**Windows**
```powershell
(Select-String -Path $env:TEMP\qa-client.log -Pattern IP_CONNECTION_LIMIT_EXCEEDED).Count
```

**Pass:** at least 2 lines of the form

```
[peer 5] disconnected by server: IP_CONNECTION_LIMIT_EXCEEDED (17).
```

Expect roughly `bot-count − MaxConcurrency` of them. Exact counts vary: peer slots are recycled and
bots retry, so treat "≥ 2 and clearly fewer than `bot-count`" as the pass condition rather than an
exact number.

---

## Step 3 — case 2: whitelisted at startup

A running instance cannot have environment variables changed, so the true startup path needs a
**redeploy** with `Transport__Hardening__IpLimiter__Whitelist=<EGRESS_IP>` set — Manual Deploy →
`dev`. If a redeploy is out of scope, record that you covered the runtime path (step 4) only; the
startup path is already covered by the local Docker e2e in PR #38.

After the deploy, the server log must show this **before any traffic**:

```
IP limiter whitelist loaded: 1 entries [<EGRESS_IP>].
```

Then repeat step 2. **Pass:** zero `IP_CONNECTION_LIMIT_EXCEEDED` lines.

---

## Step 4 — case 3: whitelisted at runtime, no restart

1. Re-run step 2 and confirm refusals — this is your baseline.
2. In Unleash, edit the `pulse-hardening` flag's `configuration` variant payload:

```json
{
  "Transport": {
    "Hardening": {
      "IpLimiter": {
        "Enabled": true,
        "MaxConcurrency": 10,
        "SceneListenerMaxConcurrency": 2,
        "Whitelist": "<EGRESS_IP>"
      }
    }
  }
}
```

**Include all four keys even if you are only changing one.** The payload is the complete override
set, not a patch — anything omitted falls back to the shipped default, and `Enabled` defaults to
`false`, which would switch the limiter off entirely.

**`Whitelist` is a comma-separated string, never a JSON array.** `["a","b"]` flattens into indexed
keys, leaves the knob on its default, and is rejected with a warning — the whitelist would look
configured and do nothing. Multiple IPs: `"1.2.3.4,5.6.7.8"`.

3. Wait one poll interval (`FeatureFlags:PollIntervalSeconds`, default 60 s).

4. Confirm it applied via `/about`:

**macOS**
```bash
curl -s http://<HOST>:5000/about | python3 -m json.tool
```

**Windows**
```powershell
Invoke-RestMethod http://<HOST>:5000/about | ConvertTo-Json -Depth 4
```

Expect `Transport:Hardening:IpLimiter:Whitelist` to hold `<EGRESS_IP>` under
`featureFlagOverrides`. If the key is **missing**, the value was type-rejected — check the server log
for `skipping that key and applying the rest of the document`.

> `/about` is on the HTTP side-car port (5000), which may not be publicly reachable on a deployed
> environment. If it refuses to connect you need a tunnel or bastion — see the `playbooks` repo. The
> server log is the fallback signal.

5. Repeat step 2.

**Pass:** zero `IP_CONNECTION_LIMIT_EXCEEDED` lines, and the server log shows:

```
IP limiter whitelist changed — added [<EGRESS_IP>], removed []; now 1 entries [<EGRESS_IP>].
```

---

## Step 5 — case 4: the scene-listener budget

Listeners have their own budget, `SceneListenerMaxConcurrency` (default **2**), reached by *promotion*
after the listener handshake rather than at connect.

**One process = one listener connection** (a listener run uses `--account` as-is and ignores
`--bot-count`), so exceeding a budget of 2 needs **three** processes with distinct accounts. Run each
in its own terminal, leaving the previous ones running.

The parcel spec is comma-separated `x:z` or `x1:z1..x2:z2`.

**macOS** — three terminals
```bash
dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --account=loadtest-0 --scene-listener-parcels=-7:0 --ip=<HOST> --port=<PORT>
```
```bash
dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --account=loadtest-1 --scene-listener-parcels=-6:0..-5:1 --ip=<HOST> --port=<PORT>
```
```bash
dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --account=loadtest-2 --scene-listener-parcels=-4:0 --ip=<HOST> --port=<PORT>
```

**Windows** — identical commands in three PowerShell windows.

**Pass:** the first two listeners stay connected; the **third** reports

```
disconnected by server: SCENE_LISTENER_IP_LIMIT_EXCEEDED (18).
```

Reason **18**, distinct from 17, is the point — it tells you the *listener* budget was hit, not the
player cap, so you change `SceneListenerMaxConcurrency` rather than `MaxConcurrency`.

Whitelisting exempts both budgets, so with `<EGRESS_IP>` whitelisted all three listeners connect.

---

## Step 6 — clean up

Remove `<EGRESS_IP>` from the flag payload. Leaving it in whitelists that address indefinitely, and
because whitelisted IPs bypass **both** budgets it silently removes all per-IP protection for it.

---

## Optional — server-side confirmation

`/metrics` requires the metrics bearer token. Set it in your environment first; never paste the token
into a command line.

**macOS**
```bash
curl -s -H "Authorization: Bearer $WKC_METRICS_BEARER_TOKEN" http://<HOST>:5000/metrics | grep ip_limit
```

**Windows**
```powershell
curl.exe -s -H "Authorization: Bearer $env:WKC_METRICS_BEARER_TOKEN" http://<HOST>:5000/metrics | Select-String ip_limit
```

What to read:

| Series | Meaning |
|---|---|
| `ip_limit_refused_total{class="player"}` | Rises during step 2 |
| `ip_limit_refused_total{class="scene_listener"}` | Rises during step 5 |
| `ip_limit_whitelist_bypass_total` | Rises during step 4. **Flat zero means your whitelist entry never matched** — almost always the wrong IP |
| `ip_limit_tracked_ips` | Distinct source IPs currently holding a connection |

---

## Pitfalls

| Symptom | Cause |
|---|---|
| No refusals at all in step 2 | `Enabled: false`, or `bot-count` ≤ `MaxConcurrency` |
| Whitelist looks set but nothing changes | `Whitelist` written as a JSON array instead of a comma-separated string |
| Key missing from `/about` | Value type-rejected — grep the log for `skipping that key` |
| Refusals persist after whitelisting | Wrong IP (LAN/VPN-internal instead of egress), or the poll interval has not elapsed |
| Limiter silently switched off after editing the payload | Omitted `Enabled` — the payload replaces the whole override set |
| Results differ between testers | Shared office/VPN egress — one budget across everyone |
| Windows: `-s`/`-H` rejected, or output is an object | `curl` hit the PowerShell alias — use `curl.exe` |
| `ENet library failed to initialize` | macOS: `brew install enet` |
| Handshake fails for every bot | Ephemeral key expired (25 h) — `metaforge account remove <name>` and re-run |
