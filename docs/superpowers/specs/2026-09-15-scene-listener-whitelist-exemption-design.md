# Scene-Listener Budget Exemption for Whitelisted IPs — Design

Date: 2026-09-15
Status: Approved

## Overview

`SceneListener:MaxParcels` caps how much area one scene-listener announcement may claim —
a single cumulative budget over realms and parcels alike, enforced identically on
`SCENE_LISTENER_HANDSHAKE` and `SCENE_LISTENER_UPDATE`. It exists because any peer holding a
valid Decentraland auth chain can announce a listener AoI; there is no per-wallet listener
allowlist.

A trusted cohosting fleet is a different case. Its egress IPs are already listed in
`Transport:Hardening:IpLimiter:Whitelist`, which today exempts them from both per-IP connection
caps. This change extends that same exemption to the parcel budget: **a whitelisted source IP
announces whatever its scenes cover, with no `MaxParcels` ceiling at all.**

## Requirements

1. An announcement from a whitelisted source IP is never rejected for exceeding
   `SceneListener:MaxParcels`, on either the handshake or a `SceneListenerUpdate`.
2. Every other announcement rule still applies to whitelisted IPs unchanged.
3. Non-whitelisted peers see no behavioral change whatsoever.
4. The exemption tracks the live whitelist, including remote reconfiguration mid-session.

## Decisions (resolved during brainstorming)

| Question | Decision |
|---|---|
| Ceiling for whitelisted IPs | **None.** The budget is not applied at all — not raised, not replaced by a second knob. |
| Which whitelist | Reuse `Transport:Hardening:IpLimiter:Whitelist`. No new config key. |
| Scope | Handshake *and* `SceneListenerUpdate` — one shared validator; a listener whose update could not match its own handshake AoI would be broken. |
| Where the decision lives | Inside `FieldValidator`, not passed in by callers. |
| New metric / log | None. The existing accept logs already carry parcel and realm counts. |

## Architecture

### `IpLimiter.IsWhitelisted(PeerIndex)`

`FieldValidator` receives a `PeerIndex` and a `PeerState`; neither carries a source address.
`IpLimiter` already maintains `reservationByPeer: PeerIndex → Reservation(Ip, Class)`, committed
by `Bind` on the connect path of both transports for every admitted peer, with the address already
run through `Normalize`. That index is the lookup.

```csharp
public bool IsWhitelisted(PeerIndex peerIndex)
```

- Reservation lookup under `syncRoot`; whitelist probed on the lock-free immutable snapshot
  *outside* the lock, matching the pattern the class already documents.
- Canonicalisation comes for free: a dotted whitelist entry matches a peer reported as v4-mapped
  IPv6, and IPv6 hex casing does not matter.
- Returns `false` for a peer with no reservation, and for the unidentified peer ENet admits with
  an empty `Peer.IP` when the limiter is disabled — `ParseWhitelist` drops empty entries, so the
  empty key can never match.
- Reads the live snapshot on every call rather than a connect-time capture, so a whitelist change
  pushed through the remote feature-flag document takes effect on the next `SceneListenerUpdate`
  of an already-connected listener.

### `FieldValidator` takes `IpLimiter`

The validator owns the budget knob and therefore owns the whole budget rule, which becomes
*"within `MaxParcels`, unless the peer's source IP is exempt."* Threading a `bool unrestricted`
down from the two handlers would split one policy across three files and let a future third
caller of `ValidateSceneListenerAoi` silently skip it.

Layering is unchanged in kind: `FieldValidator` already depends on `Pulse.Transport`
(`ITransport`, for the rejection path), both types are hardening singletons registered in
`Program.cs`, and `IpLimiter` depends only on `IOptionsMonitor` + `ILogger`, so there is no cycle.

### The gate in `ValidateSceneListenerAoi`

```csharp
bool enforceBudget = !ipLimiter.IsWhitelisted(from);
```

Resolved **once per announcement**, before the realm loop, so the exemption cannot flip between
two rects of the same payload. The two existing `budget > maxSceneListenerBudget` checks become
`enforceBudget && budget > maxSceneListenerBudget`. Budget accumulation itself stays unconditional
— it is a handful of adds, and `realmArea` is still needed for presizing.

Untouched for every peer, whitelisted or not: realm non-empty, realm within `MaxRealmLength`, no
repeated realm, at least one rect per realm, no inverted rect, every corner inside the encodable
parcel bounds.

### Presize clamp (required by the above)

`new HashSet<int>((int)realmArea)` presizes from the **nominal** sum of rect areas, which
overlapping rects inflate without bound. Today that is harmless because
`realmArea ≤ budget ≤ MaxParcels` (4096). Waive the budget and it stops being harmless.

`ParcelRect` uses `sint32` with single-byte tags and proto3 omits zero-valued fields, so a rect
covering the whole encodable area from the origin costs **8 bytes** on the wire and names 26,726
parcels. `CheckOversized` accepts up to `Transport:BufferSize` = 4096 bytes (twice that is only
where it escalates from the corruption budget to a hard disconnect), so one accepted announcement
carries ~500 such rects: `realmArea` ≈ 13.6 M for a union of 26,726. The presize alone would then
ask for roughly **218 MB** — about 50,000× amplification from a 4 KB packet, on the owning worker
thread, repeatable at the discrete-event bucket rate.

The presize becomes `(int)Math.Min(realmArea, parcelEncoder.MaxIndexExclusive)`. Every rect corner
is bounds-checked before this point, so every encoded index is in range and the deduped union
provably cannot exceed the world's parcel count — the clamp can never under-size, and it caps that
218 MB at ~1.6 MB.

This line is therefore **load-bearing, not defensive**: it is what keeps an unbounded announcement
merely slow rather than fatal, and it carries a regression test (see Testing) that bounds
allocation over a small configured world.

An earlier draft of this spec justified the clamp as int-overflow protection and called it
untestable. Both were wrong: reaching the overflow needs >2.1 × 10⁹ nominal area, which no 4 KB
packet can express, and the property actually at risk — allocation volume — is testable directly.

## Risk accepted

Removing the budget removes the guard on the expansion work as well as the policy cap. Within one
accepted 4 KB packet a whitelisted host can announce either ~290 realms each holding one full-area
rect — ~7.8 M retained set entries, on the order of 100 MB held for the life of the connection — or
one realm holding ~500 overlapping full-area rects, whose Σ nominal area of ~13.6 M is expanded
one index at a time: seconds of CPU **on the owning worker thread**, stalling that shard. The
presize clamp above is what keeps the second case from also costing ~218 MB.

This is accepted deliberately: the whitelist is an explicit operator statement of trust, and the
requirement is an unrestricted budget for trusted fleets. The residual exposure is a misconfigured
trusted fleet, not a hostile one. Operators retain the existing accept-path logs
(`Scene listener accepted … N parcels across M realms`, and the reassignment line on update) as
the signal that an announcement has grown beyond expectation.

## Testing

`FieldValidator`'s constructor gains a parameter, touching eight fixtures. The existing
`SceneListenerTestFactory` — whose stated purpose is "shared construction for the scene-listener
collaborators `FieldValidator` takes" — gains an `IpLimiter()` helper so each site adds one token.

New cases in `FieldValidatorTests`, using a real `IpLimiter` with a bound peer rather than a
substitute (`IpLimiter` is sealed, and the reservation bookkeeping is the thing under test):

1. Whitelisted peer, announcement far over budget → accepted, parcels fully expanded.
2. Non-whitelisted peer, byte-identical announcement → rejected.
3. Same, via `ValidateSceneListenerUpdate` → accepted for the whitelisted peer.
4. Dotted whitelist entry matches a peer bound as v4-mapped IPv6.
5. Peer with no reservation (never bound) → not exempt.
6. Whitelisted peer with a malformed rect (inverted / out of bounds) → still rejected; the
   exemption covers the budget only.
7. Whitelisted peer announcing many realms → the `REALM_BUDGET_COST` charge is also waived.

## Documentation

- `docs/hardening.md` — the `Whitelist` config row (it exempts more than "both caps" now), the
  `SceneListener:MaxParcels` budget section, the `INVALID_SCENE_LISTENER_FIELD` reason row, and
  the "whitelist the fleet instead" sizing guidance, which this makes more true.
- `CLAUDE.md` — the `SCENE_LISTENER_HANDSHAKE` and `SCENE_LISTENER_UPDATE` bullets.
- `docs/feature-flags.md` — the `Whitelist` description.

## Out of scope

- A separate `SceneListener:Whitelist` with independent trust semantics.
- A per-wallet listener allowlist.
- Any new metric or counter for exempted announcements.
- The pre-existing ambiguity of `MaxParcels = 0` (rejects everything rather than disabling).
