# Room recovery automation

`RoomRecoveryE2ETests` drives Pulse's test client through the matching Pulse, Connector,
Gatekeeper and LiveKit services. It replaces the manual backend checks with NUnit assertions;
Explorer modals and debug-panel interaction are outside this suite.

## Coverage

| Scenario | Assertion |
| --- | --- |
| Normal join | Two wallets connect to one room and see each other's participant SID. |
| Same-room takeover | A loses Pulse ownership and leaves LiveKit; B replaces A in the room kept alive by observer C. |
| Repeated takeover | A → B → a new login of A produces distinct participant SIDs; displaced clients stay disconnected. |
| Different-room takeover | B connects elsewhere; C keeps the old room and no longer sees that wallet. |
| Comms restart | Restarting Connector/LiveKit connections preserves the Pulse connection and rejoins the same island. |
| Island-only disconnect | A stationary client receives another assignment and reconnects without restarting Pulse or Connector. |
| Delayed Connector connect | B connects to Connector after cleanup, then recovers credentials through the existing recovery path. |

Each case watches the winner for three minutes after convergence. A disconnect, SDK reconnect,
participant replacement or unexpected Pulse disconnect fails the test. Credentials arriving
alone cannot pass: assertions inspect the live room and its membership.

## Run

Developers supply the matching backend stack and put MetaForge on `PATH` with `account sign`
and `--device` support. The fixture creates and removes disposable local accounts; A and B
share a wallet but use different ephemeral identities. No LiveKit administrative credentials
are required by the fixture.

Set the three required environment variables explicitly; there is no deployed-environment default:

- `PULSE_E2E_IP`: Pulse's ENet address.
- `PULSE_E2E_COMMS_URL`: Connector's WebSocket URL, including `/ws` for a direct local connection.
- `PULSE_E2E_REALM`: the realm used by the bots.

Optionally set `PULSE_E2E_PORT` for an ENet port other than `7777`.

From the Pulse repository:

```sh
dotnet test src/DCLPulseTests/DCLPulseTests.csproj -p:GenerateProto=false --filter "FullyQualifiedName~RoomRecoveryE2ETests" --logger trx
```

Success means **seven executed tests passed**. The fixture is explicit and excluded from an
ordinary unit-test run. Missing configuration, connection failures and convergence timeouts
fail an explicitly selected run.

`PULSE_E2E_TIMEOUT_SECONDS` defaults to 120 per startup or convergence check;
`PULSE_E2E_STABILITY_SECONDS` defaults to 180. Shorter stability windows are useful for smoke
tests, but omit the repeated recovery cycles covered by the default window.

## Complementary checks

Message loss and stale work belong in the existing broker/component tests: Pulse retains
pending takeovers, Gatekeeper retries completion without repeating confirmed removal, and
Connector forwards changed credentials to the current session. Delaying a bot's WebSocket
does **not** simulate a dropped takeover or completion message.

The test client skips a same-room assignment while connected or reconnecting, matching
Explorer's `ArchipelagoIslandRoom.ShouldAttemptConnection`. Explorer's
`ArchipelagoIslandRoomReconnectShould` covers cached-token backoff and the fresh-handshake
threshold in code. The bot fixture deliberately waits for fresh credentials in its
island-disconnect case; it does not execute Unity's lifecycle or cached-token retry loop.

LiveKit Cloud revocation, including persistence across room deletion, remains theoretical.
A self-hosted LiveKit run verifies room cleanup, credential delivery and reconnection; it
does not establish Cloud's token-revocation guarantees. See the
[recovery contract](cluster-assignment-recovery.md) for the implementation's bounds.
