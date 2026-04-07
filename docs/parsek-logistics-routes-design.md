# Design: Logistics Routes (Phase 11 + 12)

## Status: Step 2 — Gameplay Scenarios (in progress)

We are walking through concrete gameplay simulations to validate that the system works at an abstract level before formalizing the design. The vision and research are done; we need more scenarios to understand edge cases and confirm the right abstractions.

---

## Problem

Parsek records flights and replays them as ghosts. Looped recordings already make ghost vessels fly the same route repeatedly. But the loops are purely visual — no resources actually move. A player who flies a fuel tanker from KSC to their Mun base once should be able to automate that supply run, with real fuel appearing at the destination each cycle.

## Vision

"Fly it once, automate it forever." The player earns logistics routes by flying them manually. The ghost replays visually on each cycle. Resources move abstractly — deducted at origin, added at destination after transit time. No physical vessel during transit, no physics simulation. The system works in background, during time warp, across scene changes.

---

## What We Have Today

### Vessel snapshots capture resource state

Every separation boundary (dock, undock, decouple, breakup, EVA) already calls `VesselSpawner.TryBackupSnapshot()`, which serializes a full ProtoVessel to ConfigNode. Each PART node contains RESOURCE nodes with current `amount` and `maxAmount`. The resource data is already captured and persisted — just not extracted or used.

### Loop system with event hooks

- `LoopRestartedEvent` fires each loop cycle with cycle index
- `PlaybackCompletedEvent` fires when ghost reaches trajectory end
- `HandleLoopRestarted()` in ParsekPlaybackPolicy is a logging stub — ready for delivery logic
- Per-recording loop period, time unit, start/end UT range already configurable

### Location context (Phase 10)

Recordings know their start/end body, biome, situation, coordinates, and launch site. This defines route origin and destination.

### Career resource tracking

TrajectoryPoint carries `funds`, `science`, `reputation` per point. ResourceBudget computes deltas. Game actions ledger tracks economic events with revert-safe epoch isolation. All proven patterns.

### Modifying unloaded vessel resources is feasible

`ProtoPartResourceSnapshot.amount` is a public writable field. Confirmed via decompilation:
- `Save()` writes the current field value, overwriting cached ConfigNode
- `Load()` reads the field into the Part when the vessel comes off rails
- Multiple mods use this pattern (BackgroundProcessing, StageRecovery, others)

Delivery code pattern:
```csharp
foreach (var entry in deliveryManifest)
{
    double remaining = entry.Value;
    foreach (var part in dest.protoPartSnapshots)
        foreach (var res in part.resources)
            if (res.resourceName == entry.Key && res.flowState && remaining > 0)
            {
                double space = res.maxAmount - res.amount;
                double add = Math.Min(remaining, space);
                res.amount += add;
                remaining -= add;
            }
}
```

No existing KSP mod does cross-vessel physical resource transfer on unloaded vessels. This would be novel.

### What's missing

1. **Resource manifest extraction** — lightweight summary of vessel resources from snapshot ConfigNode
2. **Resource manifest on Recording** — start/end resource amounts as recording metadata
3. **Route abstraction** — recording + origin + destination + cost + cargo
4. **Dispatch/delivery scheduling** — on loop cycle, deduct at origin, add at destination
5. **UI** — route status, cost/cargo display, management

---

## Gameplay Scenarios (Step 2)

### Scenario 1: Fuel Delivery Rover (simplest case)

**Setup:**
- "Base" = probe core + empty fuel tank (200 LF capacity) + docking port + landing legs, sitting on flat ground near KSC
- Rover = probe core + full fuel tank (200 LF) + wheels + docking port, starts at runway

**Player flies:**
1. Drives rover from runway to base (~5 min, uses EC not fuel)
2. Docks rover to base via docking port
3. Transfers 150 LF from rover to base using KSP's resource transfer UI (keeps 50 LF for return trip)
4. Undocks rover from base
5. Drives rover back to KSC (or leaves it nearby)
6. Commits recording

**What Parsek captured (chain segments):**
- Segment 1 (runway → base): start snapshot = rover with 200 LF
- Dock boundary: pre-dock snapshot = rover with 195 LF (5 LF used by fuel cell driving)
- Segment 2 (docked): player transfers 150 LF to base
- Undock boundary: pre-undock snapshot = rover with 45 LF
- Segment 3 (base → KSC): rover drives back with 45 LF

**Route derived automatically:**
- Delivery = pre-dock (195 LF) minus pre-undock (45 LF) = **150 LF per cycle**
- Origin: KSC (free — no deduction needed)
- Destination: base vessel PID (from dock event)
- Minimum cycle interval: recording duration (the drive takes as long as it took)

**Each cycle:**
1. Ghost rover replays the full recording (drive out, dock visually, undock, drive back)
2. At loop completion, 150 LF added to the base vessel's tanks via ProtoPartResourceSnapshot
3. If base tanks are full, excess silently discarded — ghost continues looping

**Validated:**
- Dock event exists (segment boundary)
- Transfer occurred (195 - 45 = 150 LF decreased on transport)
- Undock event exists (segment boundary after dock)
- Docking port is free for next cycle

### Scenario 2: Orbital Monoprop Resupply

**Setup:**
- "Kerbin Station" in 100km Kerbin orbit, near-empty monoprop tanks (10/750 MP)
- Resupply capsule built at KSC: probe core + MP tank (750 MP) + LF/Ox engine + docking port + heatshield

**Player flies:**
1. Launch from KSC, circularize, transfer to station orbit
2. Rendezvous and dock at station (within 2.3km, recording switches to RELATIVE frame)
3. Transfer 650 MP to station (keeps 80 MP for separation RCS)
4. Undock from station
5. Deorbit and recover capsule at KSC
6. Commits recording

**Route derived:**
- Delivery = pre-dock (730 MP) minus pre-undock (80 MP) = **650 MP per cycle**
- Origin: KSC (free)
- Destination: Kerbin Station PID (from dock event)
- Transit time: recording duration (~40 min)

**Visual behavior:**
- Ghost visible during launch (surface-relative, always correct)
- Ghost invisible during orbital transit (no one watching)
- Ghost visible during station approach (RELATIVE frame, tracks station's actual orbital position)
- No visual continuity needed between segments — ghost appears next to station when player is nearby

**Validated:**
- Anchor/relative frame system handles orbital visual fidelity automatically
- Approach segment tracks station's current position, not recording-time position
- Route duration = recording duration (real transit time)

### Scenario 3: Minmus Ore Delivery (chained routes, non-KSC origin)

**Setup:**
- Mining base on Minmus surface: drill + ISRU + ore tanks (2000 Ore) + fuel tanks + docking port
- Kerbin orbital depot: large ore tanks + docking port, in 100km Kerbin orbit

**The player sets up TWO one-way routes:**

**Route A: KSC → Minmus Base (fuel supply)**
Player flies a fuel tanker from KSC to Minmus base, docks, transfers LF/Ox, undocks, deorbits.
- Origin: KSC (free)
- Delivery: 800 LF + 978 Ox per cycle
- Keeps the mining base fueled for drill operation and tanker departures

**Route B: Minmus Base → Kerbin Depot (ore delivery)**
Player flies an ore tanker from Minmus base to Kerbin depot, docks, transfers ore, undocks, deorbits.
- Origin: Minmus base (**non-KSC — must have resources**)
- Origin cost per cycle: 1200 Ore (cargo) + 600 LF + 733 Ox (transit fuel) = transport's start manifest
- Delivery: 1200 Ore per cycle to Kerbin depot
- **Dispatch gated:** cycle delayed until Minmus base has all required resources

**How the chain works:**
1. Route A delivers fuel to Minmus base (KSC origin, always dispatches)
2. Mining base uses fuel to run drill + ISRU, producing ore over time (player's responsibility — not simulated by Parsek)
3. When Minmus base has 1200 Ore + 600 LF + 733 Ox, Route B dispatches
4. After transit time, 1200 Ore appears at Kerbin depot

**If Route A stops:** Minmus base eventually runs out of fuel → drill stops → ore depletes → Route B dispatch delayed indefinitely. Player must fix the supply chain.

**Key design point: no round trips.** Each route is one-way. The tanker is conceptual — it defines what gets moved and how long transit takes. If the player wants to "reuse" a tanker (fly it back), that's a second route. The system doesn't care how resources arrive at an origin — mining, another route, or manual player delivery. It just checks "are they there?" at dispatch time.

**Validated:**
- Non-KSC origin deduction works (start manifest = cost)
- Dispatch gating by resource availability works
- Routes chain naturally: output of one feeds input of another
- No simulation of mining/ISRU — Parsek only moves resources, doesn't produce them

### Scenario 4: Eeloo Supply Run (interplanetary dispatch timing)

**Setup:**
- Eeloo base with docking port, needs periodic resupply
- Player flies a supply ship from KSC via direct Hohmann transfer (~3 year transit)

**Route derived:**
- Origin: KSC (free)
- Destination: Eeloo base location (coordinates from dock point)
- Delivery: whatever was transferred at Eeloo (supplies, fuel)
- Transit time: ~3 years (from recording duration)

**Dispatch timing:**
System computes the synodic period of the origin body (Kerbin) and destination body (Eeloo) from their orbital periods. For Kerbin-Eeloo this is ~1.9 Kerbin years. Route dispatches at that interval, matching when the two bodies return to approximately the same relative alignment as during the original recorded departure.

**Gravity assist case:** Player flew KSC → Jool flyby → Tylo assist → Eeloo. The system still schedules based on Kerbin-Eeloo synodic period only — intermediate flybys are not tracked. The trajectory was proven possible; the two-body window is a good approximation. If the gravity assist timing doesn't quite align every synodic period, the player can adjust the dispatch offset manually.

**Player override:** All interplanetary routes allow manual dispatch timing override. The synodic computation is a default, not a constraint.

**Same-body routes (for comparison):** Mun, Minmus, and same-body orbital routes have no transfer window — destination is always reachable. Dispatch interval = recording duration + player-set pause.

**Validated:**
- Two-body synodic period covers direct transfers correctly
- Gravity assists work but scheduling is approximate — player can fine-tune
- Can always expand to multi-body alignment tracking later

### Scenario 5: Failure Cases

**5a. Destination destroyed or rebuilt.**
Mun base is destroyed. Player rebuilds a new base at the same spot with a new docking port. The route continues — it delivers to the **location** (coordinates + 50m radius), not to a specific vessel PID. Any vessel with available tank capacity near the destination point receives resources. If nothing is there, delivery fails silently — ghost still loops visually, no resources deducted from origin.

**5b. Origin destroyed or empty (non-KSC route).**
Minmus mining base is recovered or runs out of ore. Route persists but cannot dispatch — no resources available at origin location. When new tanks with resources appear at the origin (rebuilt base, another route delivers there, player manually resupplies), the route resumes automatically.

**5c. Destination tanks full.**
Ghost loops visually but no resources are moved. Origin is NOT deducted — no point burning fuel for a delivery that would be discarded. For partial capacity: deduct from origin only the amount the destination can accept. If destination has room for 50 LF out of a 150 LF delivery, deduct proportionally from origin (the fuel cost to transport 50 LF).

**5d. Player reverts past a dispatch.**
Route dispatches and deliveries are **timeline events** at specific UTs. The existing epoch isolation system handles revert: epoch increments, events from the abandoned timeline are invalidated. Origin resources restored via quicksave. Pending deliveries cancelled. Routes recalculate from the new timeline state.

**5e. Time warp past multiple cycles.**
All pending deliveries on the timeline are processed sequentially during warp. Three cycles due at UT=50000, 50500, 51000 — each is evaluated in order. For non-KSC origins, resource availability is checked per-cycle (first dispatch may deplete origin, blocking second dispatch).

**5f. Spawned vessel at destination.**
After first playback, the real tanker is spawned docked at the station. Player may undock it, leave it, or fly it away. Doesn't affect the route — subsequent deliveries go to any vessel with tank capacity within 50m of the destination coordinates. The orphaned tanker is just another vessel.

**Validated:**
- Endpoints are locations, not vessel PIDs — survives destruction, rebuilding, and vessel changes
- Docking port only matters during initial recording (route validation). Later cycles are abstracted.
- No deduction when destination can't accept delivery — prevents waste
- Timeline integration gives revert safety for free
- Sequential time warp processing handles multi-cycle catch-up

---

## Phase 11 Tasks (Resource Snapshots — prerequisite)

These can be implemented before the full route system. They add value independently (showing resource info in the Recordings Manager).

### T11.1. ExtractResourceManifest — pure extraction function

```csharp
internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(ConfigNode vesselSnapshot)
```

Walk all PART > RESOURCE nodes, sum `amount` and `maxAmount` by resource name. Pure, static, testable. No Unity dependency.

### T11.2. Capture manifests at recording boundaries

At recording start and end, call `ExtractResourceManifest` and store the result.

```csharp
public Dictionary<string, ResourceAmount> StartResources;
public Dictionary<string, ResourceAmount> EndResources;
```

Capture at: `StartRecording()`, `StopRecording()`, chain boundaries.

### T11.3. Serialize/deserialize resource manifests

```
RESOURCE_MANIFEST
{
    RESOURCE { name = LiquidFuel  startAmount = 3600  startMax = 3600  endAmount = 200  endMax = 3600 }
    RESOURCE { name = Oxidizer    startAmount = 4400  startMax = 4400  endAmount = 244  endMax = 4400 }
}
```

Additive format — no version bump. Missing node = no data.

### T11.4. Display resource summary in UI

Recordings Manager: resource signature in tooltip or collapsible detail row.

### T11.5. Resource delta computation

```csharp
internal static Dictionary<string, double> ComputeResourceDelta(
    Dictionary<string, ResourceAmount> start,
    Dictionary<string, ResourceAmount> end)
```

---

## Design Decisions (confirmed)

### Route identity

A Route is a **separate entity** from a Recording, with its own UI and state. It uses the recording's loop system as its execution basis (loop timing, ghost replay, cycle events), but has its own fields: origin/destination vessel PIDs, delivery manifest, dispatch history, route status. Requires dedicated UI for route management.

### Route validation — dock + transfer + undock required

A recording qualifies as a route if and only if:

1. **Dock event exists** — the transport docked to a destination vessel
2. **Resource transfer occurred** — at least one resource decreased on the transport between dock and undock
3. **Undock event exists after the dock** — the transport freed the docking port

If the transport is still docked at recording end: no "Create Route" option. Tooltip: *"Transport must undock from destination to enable route — docking port needs to be free for the next cycle."*

What happens after undock is the player's business (drive back, deorbit, recover for funds).

### Delivery = what was actually transferred

Delivery manifest = pre-dock transport resources minus pre-undock transport resources, per resource, **only counting decreases**. Resources that increased on the transport while docked (EC from solar panels, etc.) are ignored.

```
Pre-dock:   195 LF, 240 Ox, 50 EC
Pre-undock:  45 LF, 240 Ox, 180 EC
Delivery:   150 LF,   0 Ox,   0 EC   ← only decreases count
```

This is fully automated — derived from snapshots the system already captures. No player input needed. The route replicates exactly what the player did during the original run.

**Snapshot requirements:** The recording chain captures snapshots at dock and undock boundaries via `CommitDockUndockSegment`. Both already exist in the current system. `ExtractResourceManifest` (T11.1) reads the RESOURCE nodes from each snapshot.

### Origin fuel model — hybrid

- **KSC-origin routes are free.** The player can always build and fuel a new vessel — that's what KSC does. No resource deduction.
- **Non-KSC origins deduct from a real vessel.** The origin must supply the transport's **start manifest** — cargo plus transit fuel. Dispatch is **gated**: if the origin vessel doesn't have enough resources, the cycle is delayed until it does. The system doesn't care how resources arrive at the origin — mining, another route, or manual player delivery. It just checks "are they there?" at dispatch time.

### No round trips — every route is one-way, with optional round-trip linking

A round trip is two separate routes, each created from its own recording:

1. **First recording:** Player flies tanker KSC → Station, docks, transfers fuel, undocks. Parsek spawns the real tanker at the station at end of first playback.
2. **Second recording:** Player flies the spawned tanker back: Station → KSC (or to another destination), docks, transfers, undocks.
3. **Two routes created:** Route A (outbound) and Route B (return). Each is one-way with its own dock-transfer-undock.

**Round-trip link:** The player can link two routes as a pair. The scheduling constraint is simple: **don't dispatch me until my partner completes.** The cycle alternates:

```
Route A completes → Route B dispatches → Route B completes → Route A dispatches → ...
```

This is not a new route type — just paired timing on two independent one-way routes. Each route keeps its own delivery manifest, origin, destination, and validation. The link only affects dispatch scheduling.

The transport vessel is conceptual — it defines what gets moved and how long transit takes. There is no physical vessel to "recycle." Routes chain naturally: the output of one feeds the input of another.

### Endpoints are locations, not vessel PIDs

Both origin and destination are **locations** — coordinates derived from the recording's start/end points, with a 50m proximity radius. Any vessel with available tank capacity within 50m of the endpoint receives resources (destination) or supplies them (origin).

This means:
- **Destination destroyed and rebuilt** → route auto-reconnects to the new vessel at the same location
- **Origin vessel recovered** → route pauses until new tanks appear at the origin location
- **No re-targeting needed** — the route is tied to a place, not a specific vessel
- **Compatible with transfer tubes, claws, any docking method** — docking port only matters during the initial recording to validate the route. Later cycles are abstracted.

The dock event during recording defines WHERE the endpoint is (coordinates). The route doesn't track which specific vessel was docked to.

### Loop timing — realism and fidelity

- **Same-body routes** (surface rovers, orbital resupply within a SOI): destination is always reachable. Dispatch interval = recording duration + player-set pause. Cannot be faster than the original recording.
- **Inter-body routes** (Kerbin → Mun, Kerbin → Eeloo): system computes the **synodic period** of the origin and destination bodies from their orbital periods. Route dispatches at that interval by default. Player can override the timing.
- **Gravity assist routes**: scheduling uses only the two endpoint bodies' synodic period. Intermediate flyby alignment is not tracked. Approximate but sufficient — player can fine-tune dispatch offset if needed. Can expand to multi-body tracking later.
- **Transit duration is always the recording duration** — the trip takes as long as it actually took.
- **Delivery happens at loop completion** — ghost reaches end of trajectory, resources appear at destination.
- Tanks stop filling at full capacity; excess is silently discarded. The visual ghost continues looping regardless.

### Abstracted execution

No physical vessel during transit. Deduct at origin (if non-KSC), wait recording duration, add to destination at completion. Ghost replays visually via existing loop system. Works in background, during time warp, across scene changes.

### Resource modification on unloaded vessels

Direct `ProtoPartResourceSnapshot.amount` modification. Proven pattern across multiple mods. Distribute delivery across all vessels within 50m of endpoint with available tank capacity, respect `flowState`, clamp to `maxAmount`.

### Smart deduction — don't waste origin resources

Origin is only deducted for the amount the destination can actually accept:
- **Destination full** → no deduction, ghost loops visually but no resources move
- **Destination partially full** → deduct proportionally (only the cost to transport what fits)
- **No vessel at destination** → no deduction, delivery fails silently

### Preventing infinite resource glitches

- The route delivers exactly what the player transferred — no more, no less
- For KSC origins: "free" is correct because KSP charges funds to build the vessel (career mode economy handles it)
- For non-KSC origins: resources deducted from origin vessel(s) at origin location, preventing duplication
- Recovery funds from the real transport vessel are a one-time thing — the route doesn't generate funds
- If the player doesn't undock the transport: route cannot be created (port occupied)

### Timeline integration

Route dispatches and deliveries are **timeline events** at specific UTs. This gives:
- **Revert safety** — epoch isolation invalidates dispatches from abandoned timelines
- **Time warp** — all pending events processed sequentially during warp
- **Recalculation** — routes participate in the same ledger/recalculation system as other game actions

### Format

Resource manifests are additive metadata on recordings. No format version bump. Missing manifest = no data. Old recordings work unchanged.

---

## Open Questions (remaining)

- **Multiple dock/undock cycles in one recording:** What if the recording has multiple dock events (e.g., dock at station A, transfer, undock, dock at station B, transfer, undock)? Multi-stop routes? Or only last dock-transfer-undock counts?
- **Crewed routes:** Reuse existing crew reservation, or defer?
- **Route UI specifics:** Where does route management live? Separate window? Tab in Recordings Manager?
- **Map view integration:** Route lines connecting origin and destination?
- **Dispatch ordering for competing routes:** If two routes share an origin, which dispatches first when resources are limited? FIFO? Priority?
- **Proportional cost for partial delivery:** If destination can only accept 1/3 of the delivery, does origin pay 1/3 of the transit fuel cost? Or full cost? Proportional is more realistic but adds complexity.

---

## Reference Documents

- `docs/dev/research/logistics-network-design.md` — full logistics network design (research phase)
- `docs/dev/research/loop-playback-and-logistics.md` — loop mechanics and orbital drift analysis
- `docs/dev/research/resource-snapshots-preparation.md` — infrastructure analysis and unloaded vessel modification patterns
- `docs/mods-references/Kerbalism-resource-system-analysis.md` — background resource processing patterns
- `docs/roadmap.md` — Phase 11 (Resource Snapshots), Phase 11.5 (Optimization), Phase 12 (Logistics)
