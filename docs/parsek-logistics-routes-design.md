# Design: Logistics Routes (Phase 11 + 12)

## Status: Step 3 — Design Document

---

## Problem

Parsek records flights and replays them as ghosts. Looped recordings already make ghost vessels fly the same route repeatedly. But the loops are purely visual — no resources actually move. A player who flies a fuel tanker from KSC to their Mun base once should be able to automate that supply run, with real fuel appearing at the destination each cycle.

"Fly it once, automate it forever." The player earns logistics routes by flying them manually. The ghost replays visually on each cycle. Resources move abstractly — deducted at origin, added at destination after transit time. No physical vessel during transit, no physics simulation.

---

## Terminology

**Route** — a separate entity that defines a repeating resource transfer between two locations. Created from a recording that contains a valid dock-transfer-undock sequence. Uses the recording's loop system for timing and ghost visuals.

**Endpoint** — origin or destination of a route. Defined by body, coordinates, and the vessel PID from the dock event. Surface endpoints fall back to 50m proximity if the PID vessel is gone. Orbital endpoints use PID only.

**Delivery manifest** — the per-resource amounts transferred per cycle. Computed as pre-dock minus pre-undock transport resources (only decreases).

**Start manifest** — the transport vessel's resources at recording start. For non-KSC origins, this is the cost deducted from the origin each cycle (cargo + transit fuel).

**Dispatch** — the moment a route cycle begins. Origin resources are checked and deducted. A timeline event is created.

**Delivery** — the moment a route cycle completes (after transit duration). Resources are added to the destination. A timeline event is created.

**Round-trip link** — a scheduling constraint pairing two one-way routes: "don't dispatch me until my partner completes."

---

## Mental Model

```
  RECORDING (one-time flight)
       │
       │  player enables "Create Route" after commit
       ▼
     ROUTE
       │
       ├── Origin endpoint (where transport departed)
       │     coordinates + body + vessel PID
       │     KSC = free, non-KSC = deduct start manifest
       │
       ├── Destination endpoint (where transport docked)
       │     coordinates + body + vessel PID
       │     surface: PID match or 50m proximity fallback
       │     orbital: PID match only
       │
       ├── Delivery manifest (what was transferred)
       │     pre-dock minus pre-undock, per resource, only decreases
       │
       ├── Transit duration (recording duration)
       │
       └── Dispatch timing
             same-SOI: player-set interval (≥ recording duration)
             inter-body: synodic period of origin + dest bodies
             player can override

  CYCLE FLOW:
  ┌─────────────────────────────────────────────────────────┐
  │                                                         │
  │  1. Check timing (transfer window / interval elapsed)   │
  │          │                                              │
  │          ▼                                              │
  │  2. Check destination capacity                          │
  │     └── full → skip cycle, no deduction                 │
  │     └── partial → compute proportional delivery         │
  │          │                                              │
  │          ▼                                              │
  │  3. Check origin resources (if non-KSC)                 │
  │     └── insufficient → delay until available            │
  │          │                                              │
  │          ▼                                              │
  │  4. DISPATCH: deduct from origin, create timeline event │
  │          │                                              │
  │          ▼                                              │
  │  5. Wait transit duration (ghost replays visually)      │
  │          │                                              │
  │          ▼                                              │
  │  6. DELIVERY: add to destination, create timeline event │
  │          │                                              │
  │          └── loop back to step 1                        │
  └─────────────────────────────────────────────────────────┘
```

---

## Data Model

### ResourceAmount

```csharp
internal struct ResourceAmount
{
    public double amount;
    public double maxAmount;
}
```

### ResourceManifest

A `Dictionary<string, ResourceAmount>` keyed by resource name (e.g., "LiquidFuel", "Oxidizer"). Used for start/end snapshots on recordings and for delivery/cost manifests on routes.

### RouteEndpoint

```csharp
internal struct RouteEndpoint
{
    public string bodyName;        // e.g., "Kerbin", "Mun"
    public double latitude;        // from dock event trajectory point
    public double longitude;
    public double altitude;
    public uint vesselPid;         // PID of vessel docked to during recording
    public bool isOrbital;         // true if endpoint is in orbit (no surface proximity fallback)
}
```

### RouteStatus enum

```csharp
internal enum RouteStatus
{
    Active,             // dispatching on schedule
    WaitingForResources,// origin doesn't have enough — delayed
    DestinationFull,    // destination can't accept delivery — skipping cycles
    EndpointLost,       // destination vessel gone (orbital) or no vessel at location (surface)
    Paused,             // player manually paused
    InTransit           // dispatched, waiting for transit duration to elapse
}
```

### Route

```csharp
internal class Route
{
    // Identity
    public string Id;                    // unique route ID (GUID)
    public string RecordingId;           // source recording ID
    public string Name;                  // player-visible name (editable)

    // Endpoints
    public RouteEndpoint Origin;
    public RouteEndpoint Destination;
    public bool IsKscOrigin;             // true = no origin deduction

    // Resource transfer
    public Dictionary<string, double> DeliveryManifest;  // per-resource delivery amounts
    public Dictionary<string, double> CostManifest;      // per-resource origin cost (start manifest)

    // Timing
    public double TransitDuration;       // seconds (= recording duration)
    public double DispatchInterval;      // seconds between cycle starts
    public double NextDispatchUT;        // UT of next scheduled dispatch
    public double? PendingDeliveryUT;    // UT when in-transit delivery arrives (null if not in transit)
    public Dictionary<string, double> PendingDeliveryAmounts; // actual amounts to deliver (computed at dispatch, survives save/load)

    // Linking
    public string LinkedRouteId;         // paired route for round-trip (null if standalone)

    // State
    public RouteStatus Status;
    public int CompletedCycles;          // total successful deliveries
    public int SkippedCycles;            // cycles skipped (destination full, origin empty)
}
```

### Serialization (ConfigNode in .sfs)

```
ROUTE
{
    id = <guid>
    recordingId = <recording-guid>
    name = Mun Fuel Run

    ORIGIN
    {
        bodyName = Kerbin
        latitude = -0.0972
        longitude = -74.5577
        altitude = 75.2
        vesselPid = 12345
        isOrbital = False
    }
    DESTINATION
    {
        bodyName = Mun
        latitude = 3.2001
        longitude = -45.1234
        altitude = 612.5
        vesselPid = 67890
        isOrbital = False
    }

    isKscOrigin = True
    transitDuration = 12345.6
    dispatchInterval = 43200.0
    nextDispatchUT = 55000.0
    pendingDeliveryUT = -1
    linkedRouteId =
    status = Active
    completedCycles = 5
    skippedCycles = 1

    DELIVERY_MANIFEST
    {
        LiquidFuel = 150.0
        Oxidizer = 183.3
    }
    COST_MANIFEST
    {
        LiquidFuel = 200.0
        Oxidizer = 244.0
    }
    PENDING_DELIVERY
    {
        // present only when Status == InTransit; actual amounts computed at dispatch
        LiquidFuel = 100.0
        Oxidizer = 0.0
    }
}
```

Routes are stored in ParsekScenario's save data alongside recordings and game actions. Additive — saves without routes load fine. Routes reference recordings by ID but are independent entities.

### Recording extensions (Phase 11)

Two new optional fields on Recording:

```csharp
public Dictionary<string, ResourceAmount> StartResources;  // manifest at recording start
public Dictionary<string, ResourceAmount> EndResources;     // manifest at recording end
```

Serialized as:

```
RESOURCE_MANIFEST
{
    RESOURCE { name = LiquidFuel  startAmount = 3600  startMax = 3600  endAmount = 200  endMax = 3600 }
    RESOURCE { name = Oxidizer    startAmount = 4400  startMax = 4400  endAmount = 244  endMax = 4400 }
}
```

Additive — missing node = no data. No format version bump.

---

## Behavior

### Route creation

**Trigger:** Player clicks "Create Route" on a committed recording in the Recordings Manager.

**Validation:** The recording qualifies only if:
1. At least one dock event exists (PartEventType.Docked boundary in chain)
2. At least one undock event exists AFTER the dock (PartEventType.Undocked boundary)
3. At least one resource decreased on the transport between the dock and undock snapshots

If validation fails: button is absent or greyed out. Tooltip explains what's missing (e.g., "Transport must undock from destination to enable route").

**Route derivation (automatic):**
- Origin = recording start location (body + coordinates from first trajectory point)
- Destination = dock event location (body + coordinates from dock boundary trajectory point)
- Origin vessel PID = vessel PID at recording start (for non-KSC origin resource checking)
- Destination vessel PID = PID of vessel docked to (from the dock event's PartEvent data or the chain boundary metadata)
- IsKscOrigin = true if origin body is Kerbin and coordinates are near a launch site
- DeliveryManifest = ExtractResourceManifest(pre-dock snapshot) minus ExtractResourceManifest(pre-undock snapshot), per resource, only positive deltas
- CostManifest = ExtractResourceManifest(recording start snapshot) — the full start manifest
- TransitDuration = recording EndUT minus recording StartUT
- DispatchInterval = TransitDuration (default; player can increase)
- For inter-body routes: DispatchInterval defaults to synodic period of origin and destination bodies

**Player confirmation:** Route configuration panel shows derived values. Player can edit name, dispatch interval, and enable/disable. On confirm, route is created and scheduling begins.

### Dispatch evaluation

**Trigger:** Each physics frame (or once per second during warp), the route scheduler checks all active routes.

**For each route with Status in {Active, WaitingForResources, DestinationFull} and `NextDispatchUT <= currentUT`:**

This explicitly excludes InTransit (delivery pending), Paused (player-disabled), and EndpointLost (no vessels) routes from dispatch evaluation.

1. **Check round-trip link.** If `LinkedRouteId` is set and the linked route has `Status == InTransit`, skip — wait for partner to complete.

2. **Check destination capacity.** Find vessels at destination endpoint (PID match, or 50m surface proximity). If NO vessels found at all → set `Status = EndpointLost`, skip cycle (distinct from DestinationFull — EndpointLost means the target is gone, not just full). If vessels found but zero capacity for all resources → set `Status = DestinationFull`, increment `SkippedCycles`, advance `NextDispatchUT`. If partial capacity → compute proportional delivery: per-resource independent, each resource delivers min(deliveryAmount, destinationCapacity). Store actual delivery amounts in `PendingDeliveryAmounts`.

3. **Check origin resources (if non-KSC).** Find vessels at origin endpoint. If no vessels found at all → set `Status = EndpointLost`, skip cycle. If vessels exist but insufficient resources for full CostManifest (required if any delivery resource is non-zero) → set `Status = WaitingForResources`, do NOT advance `NextDispatchUT` (re-check next frame). Note: only one dispatch per route per evaluation. If the route was waiting for many cycles, it dispatches once and advances NextDispatchUT by one interval. No catch-up storm.

4. **Dispatch.** Origin cost (smart deduction): deduct full CostManifest if ANY delivery resource has non-zero amount (transit fuel was burned regardless of how many resources were actually delivered). Zero deduction only if total delivery is zero across all resources (all at capacity). Store actual delivery amounts in `PendingDeliveryAmounts`. Set `PendingDeliveryUT = currentUT + TransitDuration`. Set `Status = InTransit`. Create ROUTE_DISPATCHED timeline event. Advance `NextDispatchUT`.

### Delivery execution

**Trigger:** Route has `PendingDeliveryUT <= currentUT`.

1. **Find destination vessels** at endpoint (PID or surface proximity). If NO vessels found → clear `PendingDeliveryUT` and `PendingDeliveryAmounts`, set `Status = EndpointLost`, log warning. Resources already deducted from origin are lost (transit cost of a failed delivery). This prevents InTransit deadlock. Return.
2. **For each resource in `PendingDeliveryAmounts`:** distribute across destination vessel tanks, clamped to current `maxAmount`. For unloaded vessels: modify `ProtoPartResourceSnapshot.amount` directly, respect `flowState`, clamp to `maxAmount`. For loaded vessels: use `Part.RequestResource()`.
3. **Create ROUTE_DELIVERED timeline event.** Record amounts actually delivered.
4. **Clear `PendingDeliveryUT` and `PendingDeliveryAmounts`.** Set `Status = Active`. Increment `CompletedCycles`.
5. **If linked route exists:** the partner route's linked-wait condition is now cleared, allowing its next dispatch.

### Endpoint vessel resolution

```
ResolveEndpointVessels(endpoint):
    1. Find vessel by endpoint.vesselPid in FlightGlobals.Vessels
       → if found and within reasonable range of endpoint coordinates: return [vessel]
    2. If not found AND endpoint.isOrbital == true:
       → return [] (orbital endpoints have no fallback)
    3. If not found AND endpoint is surface:
       → scan FlightGlobals.Vessels for any vessel within 50m of (body, lat, lon, alt)
       → return all matches with available tank capacity
```

### Synodic period computation

```
SynodicPeriod(originBody, destBody):
    if originBody == destBody:
        return 0  // same body, no transfer window
    // Walk up to common parent
    a = originBody, b = destBody
    while a.referenceBody != b.referenceBody:
        if a hierarchy depth > b: a = a.referenceBody
        else: b = b.referenceBody
        if a is Sun or b is Sun: return 0  // no computable synodic period
    // a and b now orbit the same parent
    T1 = a.orbit.period, T2 = b.orbit.period
    if T1 == T2: return 0
    return abs(1 / (1/T1 - 1/T2))
```

Handles cross-system routes: Mun→Laythe walks up to Kerbin/Jool orbiting Sun. Guards against Sun-orbiting routes and equal-period edge cases. Returns 0 if not applicable (same-body routes use player-set interval instead).

### Round-trip linking

Player selects two routes in the UI and clicks "Link as Round Trip." Sets `LinkedRouteId` on both routes. The scheduling constraint: a route with a linked partner skips dispatch if the partner is `InTransit`. Unlinking clears `LinkedRouteId` on both.

---

## Edge Cases

### E1. Destination destroyed, surface base
**Scenario:** Mun base destroyed. Player rebuilds at same spot.
**Behavior:** PID match fails. Surface proximity fallback finds new vessel within 50m. Route auto-reconnects. No player action needed.
**v1 limitation:** If rebuilt base is >50m from original, route won't find it. Player must create a new route.

### E2. Destination destroyed, orbital station
**Scenario:** Kerbin station destroyed. Player rebuilds a new station.
**Behavior:** PID match fails. No proximity fallback for orbital endpoints. `Status = EndpointLost`. Player must re-target route to new station PID via UI.

### E3. Origin destroyed or recovered (non-KSC)
**Scenario:** Minmus mining base recovered for funds.
**Behavior:** If no vessels found at origin at all → `Status = EndpointLost` (same treatment as orbital destination — the endpoint is gone). If vessels exist at origin but lack sufficient resources → `Status = WaitingForResources`. Route persists in both cases. When new base is placed at same location, or resources are resupplied, route resumes.
**Note:** For surface origins, proximity fallback applies (same 50m rule as destination). For KSC origins, this check is skipped (KSC is always available).

### E4. Destination tanks full
**Scenario:** Route delivers 200 LF per cycle. Base has 200/200 LF.
**Behavior:** `Status = DestinationFull`. Ghost loops visually. Origin NOT deducted. Cycle skipped (`SkippedCycles` incremented). When player uses some fuel, next cycle delivers.

### E5. Destination partially full
**Scenario:** Base has room for 100 LF but full on Ox. Delivery manifest: 150 LF, 183 Ox.
**Behavior:** Per-resource independent: deliver 100 LF, 0 Ox. Origin pays full cost (transit fuel was burned). `PendingDeliveryAmounts` stores {LF: 100}. Log partial delivery.

### E6. Player reverts past a dispatch
**Scenario:** Route dispatched at UT=50000. Player reverts to UT=49000.
**Behavior:** Timeline events (ROUTE_DISPATCHED, ROUTE_DELIVERED) are invalidated by epoch isolation. Origin resources restored via quicksave. Route recalculates NextDispatchUT from the restored state.

### E7. Time warp past multiple cycles
**Scenario:** Three cycles due at UT=50000, 50500, 51000.
**Behavior:** All processed sequentially. Each dispatch checks origin availability independently. First dispatch may deplete origin, blocking subsequent dispatches.

### E8. Transport still docked at recording end
**Scenario:** Player forgets to undock after transferring resources.
**Behavior:** Route validation fails (no undock event after dock). "Create Route" button absent. Tooltip: "Transport must undock from destination."

### E9. No resources transferred during docking
**Scenario:** Player docks and undocks without transferring anything.
**Behavior:** Route validation fails (no resource decrease between dock and undock). "Create Route" button absent. Tooltip: "No resource transfer detected during docking."

### E10. Multiple dock/undock in one recording
**Scenario:** Recording contains dock→transfer→undock→dock→transfer→undock.
**Behavior:** v1 — only the LAST dock-transfer-undock sequence counts. The destination is the last docking target. Delivery is from the last dock/undock pair.
**Future:** Multi-stop routes (multiple deliveries per cycle).

### E11. Route dispatch while player is at destination
**Scenario:** Player is sitting at the Mun base when delivery is due.
**Behavior:** Destination vessel is loaded. Use `Part.RequestResource()` instead of `ProtoPartResourceSnapshot`. Resources appear in real-time in the player's view.

### E12. Competing routes at same origin
**Scenario:** Two routes share a Minmus base origin. Base has enough for one dispatch but not both.
**Behavior:** v1 — FIFO by NextDispatchUT. Whichever route's dispatch is due first gets priority. Second route enters `WaitingForResources` until origin is resupplied.
**Future:** Player-configurable priority ordering.

### E13. Linked route partner is paused
**Scenario:** Route A and B are linked. Player pauses Route B.
**Behavior:** Route A still dispatches on its own schedule (linked-wait only applies to `InTransit` status, not `Paused`). When B is resumed, the alternation resumes from B's next scheduled dispatch.

### E14. Recording deleted
**Scenario:** Player deletes the source recording for a route.
**Behavior:** Route becomes orphaned — no ghost visual replay (recording gone), but resource transfer logic still works. Route continues to dispatch/deliver on schedule without visuals. Status shows "No ghost" indicator.

### E15. Save/load round-trip
**Scenario:** Player saves, loads, routes should persist.
**Behavior:** All Route fields serialized in ParsekScenario OnSave/OnLoad. Route state (NextDispatchUT, PendingDeliveryUT, Status, CompletedCycles) restored exactly.

---

## v1 Limitations

- **Capacity changes during transit:** Delivery clamps to `maxAmount` at delivery time. Excess silently lost. Origin was already deducted at dispatch.
- **Zero transit duration:** Dispatch and delivery may process in same frame. Acceptable.
- **EC-only delivery:** Electric charge fluctuates rapidly. Route status may flicker between Active and DestinationFull. Acceptable for v1.
- **Resource not on destination:** If delivery manifest includes a resource the destination has no tanks for, that resource is silently skipped (delivered amount = 0).
- **Origin loaded vs unloaded:** Dispatch deduction uses same loaded/unloaded distinction as delivery (`Part.RequestResource` for loaded, `ProtoPartResourceSnapshot` for unloaded).
- **Scene handling:** Route scheduler runs in all scenes via ParsekScenario (always active). `FlightGlobals.Vessels` available in all scenes for endpoint resolution.
- **Revert mechanism:** Route state (`NextDispatchUT`, `PendingDeliveryUT`, `PendingDeliveryAmounts`, `Status`, etc.) is serialized in .sfs. Quicksave load restores the Route ConfigNode. `ParsekScenario.OnLoad` re-reads route state. Timeline events use epoch isolation for revert safety.

---

## What Doesn't Change

- **Recording system** — recordings are unchanged. Resource manifests are additive optional metadata.
- **Loop system** — loop timing, ghost replay, cycle events all work as today. Routes add a delivery hook but don't modify loop behavior.
- **Ghost playback engine** — no changes to GhostPlaybackEngine, IPlaybackTrajectory, or IGhostPositioner.
- **Chain system** — chain segments, dock/undock boundaries, snapshots all unchanged.
- **Merge dialog** — route creation happens after commit, not during merge.
- **Crew reservation** — deferred. Routes don't reserve crew in v1.
- **Game actions system** — route events (DISPATCHED, DELIVERED) are new event types in the existing ledger. No changes to recalculation engine or resource modules.
- **Map markers** — deferred. No map view integration in v1.

---

## Backward Compatibility

- **Saves without routes:** Load fine. No routes created. ROUTE ConfigNode simply absent.
- **Saves without resource manifests:** Load fine. Recordings without RESOURCE_MANIFEST show no resource info. Route creation is unavailable for recordings without manifests (manifests only captured on new recordings after Phase 11).
- **Old recordings:** Cannot be converted to routes (no manifest data). Player must re-fly the route to create a new recording with manifests.
- **Format:** All new data is additive. No version bump. Missing nodes = no data.

---

## Diagnostic Logging

### Route creation
- `[Parsek][INFO][Route] Route created: id={id} name={name} recording={recId} origin={bodyName}({lat},{lon}) dest={bodyName}({lat},{lon}) delivery={manifest} cost={manifest}`
- `[Parsek][INFO][Route] Route validation failed for recording {recId}: {reason}` (no dock, no undock, no transfer)

### Dispatch evaluation
- `[Parsek][VERBOSE][Route] Dispatch check: route={name} nextUT={ut} currentUT={ut} status={status}`
- `[Parsek][INFO][Route] Dispatch: route={name} cycle={n} deducted={amounts} from origin vessel(s) at {body}({lat},{lon})`
- `[Parsek][INFO][Route] Dispatch delayed: route={name} — origin missing {resource}={needed} (available={have})`
- `[Parsek][INFO][Route] Dispatch skipped: route={name} — destination full (capacity={amounts})`
- `[Parsek][INFO][Route] Dispatch skipped: route={name} — linked route {linkedId} in transit`

### Delivery
- `[Parsek][INFO][Route] Delivery: route={name} cycle={n} delivered={amounts} to {vesselCount} vessel(s) at {body}({lat},{lon})`
- `[Parsek][INFO][Route] Partial delivery: route={name} — {resource} delivered {actual}/{requested} (destination capacity limited)`
- `[Parsek][WARN][Route] Delivery failed: route={name} — no vessels at destination {body}({lat},{lon})`

### Endpoint resolution
- `[Parsek][VERBOSE][Route] Endpoint resolve: {type} pid={pid} found={true/false} fallback={proximity/none} vessels={count}`

### State transitions
- `[Parsek][INFO][Route] Status change: route={name} {oldStatus} → {newStatus}`

### Timeline events
- `[Parsek][INFO][Route] Timeline event: ROUTE_DISPATCHED route={name} ut={ut}`
- `[Parsek][INFO][Route] Timeline event: ROUTE_DELIVERED route={name} ut={ut} amounts={amounts}`

---

## Test Plan

### Unit tests (pure logic, no Unity)

**ExtractResourceManifest** (T11.1)
- Empty ConfigNode → empty manifest. *Catches: null deref on missing PART nodes.*
- Single part, one resource → manifest with one entry. *Catches: parsing errors.*
- Multi-part, same resource → amounts summed. *Catches: overwrite instead of accumulate.*
- Multiple resource types → all present in manifest. *Catches: single-resource assumption.*
- Missing RESOURCE node on a part → skipped gracefully. *Catches: null deref.*
- Zero-amount resource → included (maxAmount matters for capacity). *Catches: filtering out zero amounts.*

**ComputeResourceDelta**
- Start > end → positive delta (consumed). *Catches: sign inversion.*
- Start < end → negative delta (gained). *Catches: absolute value bug.*
- Start == end → zero delta, excluded. *Catches: noise from unchanged resources.*

**ComputeDeliveryManifest** (pre-dock minus pre-undock, only decreases)
- Normal transfer: pre-dock 200 LF, pre-undock 50 LF → delivery 150 LF. *Catches: wrong snapshot pair.*
- Resource increased (EC): pre-dock 50, pre-undock 180 → delivery 0 (ignored). *Catches: including increases.*
- Multiple resources, mixed: some decrease, some increase → only decreases in manifest. *Catches: all-or-nothing bug.*
- No decreases → empty manifest (route validation should reject). *Catches: false positive validation.*

**Route validation**
- Recording with dock + transfer + undock → valid. *Catches: false rejection.*
- Recording with dock + undock, no transfer → invalid. *Catches: false acceptance.*
- Recording with dock + transfer, no undock → invalid. *Catches: missing undock check.*
- Recording with no dock at all → invalid. *Catches: missing dock check.*

**Dispatch evaluation**
- KSC origin, destination has capacity → dispatch succeeds, no origin deduction. *Catches: deducting from KSC.*
- Non-KSC origin with sufficient resources → dispatch succeeds, origin deducted. *Catches: skipping deduction.*
- Non-KSC origin with insufficient resources → dispatch delayed. *Catches: dispatching without resources.*
- Destination full → cycle skipped, origin NOT deducted. *Catches: deducting for wasted delivery.*
- Destination partially full → per-resource independent delivery, full origin cost if any resource delivered. *Catches: proportional cost instead of full cost, coupled resources.*
- Linked route partner in transit → dispatch skipped. *Catches: ignoring link constraint.*

**Synodic period computation**
- Kerbin-Mun → known value (~6.4 days). *Catches: formula error.*
- Mun-Laythe → walks up to Kerbin/Jool, computes from their orbital periods. *Catches: cross-system hierarchy walk.*
- Same body → returns 0. *Catches: division by zero.*
- Sun-orbiting body → returns 0 when hierarchy walk hits Sun. *Catches: missing guard at top of tree.*
- Equal orbital periods → returns 0. *Catches: division by zero from T1==T2.*

**Endpoint resolution**
- Vessel PID exists → return that vessel. *Catches: skipping PID check.*
- PID gone, surface endpoint, vessel within 50m → return fallback vessel. *Catches: missing fallback.*
- PID gone, surface endpoint, no vessel within 50m → return empty. *Catches: false match.*
- PID gone, orbital endpoint → return empty (no fallback). *Catches: orbital proximity fallback.*

### Log assertion tests

- Route creation logs manifest and endpoint details. *Catches: silent creation.*
- Dispatch delayed logs which resource is missing and amounts. *Catches: silent delay.*
- Delivery logs amounts actually delivered per vessel. *Catches: silent delivery.*
- Status change logs old→new transition. *Catches: silent state change.*
- Partial delivery logs fraction and reason. *Catches: silent partial.*

### Serialization round-trip tests

- Route serialize → deserialize → all fields match. *Catches: missing field in save/load.*
- ResourceManifest serialize → deserialize → amounts match with full precision. *Catches: locale-dependent formatting.*
- Route with null LinkedRouteId → serialize → deserialize → still null. *Catches: empty string vs null.*
- Route with PendingDeliveryUT → survives save/load. *Catches: in-transit state lost on reload.*

### Integration tests (synthetic recordings)

- Create recording with dock+transfer+undock → route validation passes. *Catches: real chain structure mismatch.*
- Create recording without undock → route validation rejects. *Catches: validation not checking chain events.*
- Inject route into save file → loads correctly. *Catches: ParsekScenario integration.*

---

## Appendix: Gameplay Scenarios (from Step 2)

### Scenario 1: Fuel Delivery Rover (simplest case)

**Setup:** Base with empty fuel tank near KSC. Rover with full tank at runway.

**Player:** Drives to base → docks → transfers 150 LF → undocks → drives back.

**Route:** Delivery 150 LF per cycle. Origin KSC (free). Interval = recording duration.

### Scenario 2: Orbital Monoprop Resupply

**Setup:** Kerbin station at 100km, low on monoprop. Resupply capsule from KSC.

**Player:** Launch → rendezvous → dock → transfer 650 MP → undock → deorbit.

**Route:** Delivery 650 MP per cycle. Origin KSC (free). Ghost visible during launch and station approach (RELATIVE frame tracks station position). Transit is invisible.

### Scenario 3: Minmus Ore Delivery (chained routes)

**Setup:** Mining base on Minmus. Kerbin orbital depot. Two one-way routes.

**Route A:** KSC → Minmus base. Delivers fuel. Free origin.
**Route B:** Minmus base → Kerbin depot. Delivers 1200 Ore. Non-KSC origin — dispatch gated by resource availability.

Route A feeds Route B. If Route A stops, Minmus base runs dry, Route B pauses.

### Scenario 4: Eeloo Supply Run (interplanetary)

**Setup:** Eeloo base. Supply ship from KSC via Hohmann or gravity assist.

**Route:** Dispatch interval = Kerbin-Eeloo synodic period (~1.9 years). Gravity assists use same two-body synodic approximation. Player can override timing.

### Scenario 5: Failure Cases

- **Destination destroyed (surface):** Proximity fallback auto-reconnects to rebuilt base.
- **Destination destroyed (orbital):** Route pauses, player re-targets.
- **Origin empty:** Dispatch delayed until resources available.
- **Destination full:** Cycle skipped, origin not deducted.
- **Revert:** Epoch isolation invalidates dispatches from abandoned timeline.
- **Time warp:** All pending cycles processed sequentially.
- **Transport still docked:** Route validation rejects (no undock event).

---

## Open Questions (deferred to v2)

- **Multi-stop routes:** Recording with multiple dock-transfer-undock sequences. v1 uses only the last pair.
- **Crewed routes:** Crew reservation for route dispatches. v1 ignores crew.
- **Map view integration:** Route lines on the map. Deferred.
- **Dispatch priority for competing routes:** v1 uses FIFO by NextDispatchUT.

---

## Reference Documents

- `docs/dev/research/logistics-network-design.md` — full logistics network design (research phase)
- `docs/dev/research/loop-playback-and-logistics.md` — loop mechanics and orbital drift analysis
- `docs/dev/research/resource-snapshots-preparation.md` — infrastructure analysis and unloaded vessel modification patterns
- `docs/mods-references/Kerbalism-resource-system-analysis.md` — background resource processing patterns
- `docs/roadmap.md` — Phase 11 (Resource Snapshots), Phase 11.5 (Optimization), Phase 12 (Logistics)
