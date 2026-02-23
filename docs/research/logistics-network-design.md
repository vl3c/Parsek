# Logistics Network: Recorded Routes as Automated Supply Chains

## The Idea

Parsek records flights and replays them as ghosts. A natural extension: recorded flights that loop become **reusable supply routes**. A player manually flies a resupply mission from KSC to their Mun base once. Parsek records it. Then that recording becomes an automated logistics route — periodically consuming fuel at the origin and delivering cargo at the destination. The ghost replays visually as a reminder that the supply run is happening.

This turns Parsek from a flight recorder into an economic automation layer on top of KSP's career mode.

## Why This Fits Parsek

The mod is already 80% of the way there:

**What exists today:**
- Trajectory recordings with per-point resource tracking (funds, science, reputation)
- Vessel snapshots defining what ship flew the route
- Chain segments splitting recordings by phase (atmo/exo/space) and body
- Loop playback with configurable period and pause interval
- Per-segment enable/disable and per-chain grouping
- Ghost visual playback with engine FX, part events, the full visual narrative
- Game state recording tracking contracts, tech, crew, facilities, and currency changes

**What's missing:**
- Physical resource tracking per trajectory point (LF, Ox, Ore, MonoPropellant, etc.)
- A "route" abstraction on top of recordings
- Resource transfer logic between origin and destination vessels/bases
- Transfer window validation for interplanetary routes
- UI for route management

The game state recording system (`GameStateRecorder`, `GameStateStore`, `GameStateBaseline`) is particularly relevant. It already captures career-level economic events at specific UTs with before/after values. Extending this to track physical resource flows during a logistics route execution would be natural — a route dispatch is just another game state event.

## The Connection to Game State Recording

### What Exists Today (GameStateStore + GameStateRecorder)

The game state system captures a baseline snapshot of the entire career state (funds, science, tech tree, crew roster, facility levels, active contracts) and then records delta events as they happen. Events are typed (`GameStateEventType` enum covering contracts, tech, crew, facilities, currencies) with UT, key, detail, and before/after values.

### What's Being Built (Milestones + ResourceBudget — timeline-actions branch)

The milestone system in development takes this further:

- **Milestones** bundle semantic events (tech research, part purchases, facility upgrades) into time-bounded groups linked to specific flight recordings. Each milestone captures events from its UT range, filtering out raw resource deltas to keep only meaningful career progression events.

- **Epoch isolation** prevents abandoned timeline branches from polluting future milestones. Each revert increments a global epoch counter. Milestones created after the quicksave point get their `LastReplayedEventIndex` reset to -1 (unreplayed), ensuring clean branch separation.

- **ResourceBudget** aggregates committed costs across all recordings and milestones, computing "available = current - reserved" for funds, science, and reputation. Pre-launch snapshots (`PreLaunchFunds`, etc.) captured at recording start provide the baseline for cost calculations.

- **Replay tracking** via `LastReplayedEventIndex` persists which events have been applied during playback, surviving save/load cycles. This is exactly the pattern a logistics route would need — tracking which dispatches have been executed vs pending.

### How Logistics Extends This

The milestone/epoch/replay pattern is the exact foundation logistics needs:

**Baseline captures** could be extended to include:
- Resource inventories at known bases/stations (total LF, Ox, Ore, etc.)
- Active logistics routes and their schedules
- Pending deliveries in transit

**New event types** (extending `GameStateEventType`):
- `RouteDispatched` — fuel deducted from origin, delivery scheduled
- `RouteDelivered` — cargo added to destination
- `RouteFailed` — origin ran out of fuel, destination destroyed, etc.
- `RouteEstablished` / `RouteRetired` — lifecycle events

**Epoch isolation applies directly:** If the player reverts to before a route dispatch, the epoch increment naturally invalidates the dispatch. `RestoreMutableState(resetUnmatched: true)` resets route milestones to unreplayed, undoing fuel deductions. The same infrastructure that handles "undo this tech research" handles "undo this supply run."

**ResourceBudget extends naturally:** Route fuel costs become another line item in the budget. "Funds: 35,000 available (15,000 committed)" becomes "LiquidFuel: 1,200 available at KSC (1,150 committed to Mun Resupply)". The budget pattern already handles partial replay and aggregation across multiple recordings — routes are just another source of committed costs.

**Replay tracking maps to dispatch tracking:** `LastReplayedEventIndex` on a milestone tracks which events have been applied. For logistics, a `LastDispatchedCycle` index on a route tracks which delivery cycles have executed. Both need persistence, both need reset on revert, both use the same epoch isolation.

## How a Route Works

### Recording Phase (Manual)

1. Player builds a cargo vessel at KSC (or orbital station, or wherever)
2. Loads it with fuel and cargo
3. Flies it to the destination (Mun base, Duna outpost, orbital station)
4. Parsek records the flight, including physical resource snapshots at each sample point
5. On arrival, player lands/docks and completes the delivery manually

### Route Definition (Automatic)

When the recording is committed, Parsek can extract:
- **Origin**: body + location of first point (e.g., Kerbin, KSC coordinates)
- **Destination**: body + location of last point (e.g., Mun, specific base coordinates)
- **Transit time**: EndUT - StartUT
- **Fuel cost**: resource delta between first and last point (what was consumed)
- **Cargo manifest**: what resources were aboard at arrival (from vessel snapshot)
- **Vessel template**: the vessel snapshot itself (what ship type runs this route)

The player doesn't configure any of this — it's all derived from the recording they already made.

### Route Execution (Automated)

Each cycle:
1. Check if origin has enough fuel to dispatch
2. Deduct fuel cost from origin inventory
3. Start transit timer (duration = recorded transit time)
4. Ghost replays the flight visually (existing loop playback)
5. When transit timer expires, add cargo to destination inventory
6. Log as game state event
7. Wait for next cycle

### Abstracted vs Physical

**Abstracted (recommended):** No actual vessel exists during transit. Just math — deduct at origin, wait, add at destination. The ghost is purely visual. Simple, no physics edge cases, works with time warp.

**Physical (future):** Spawn an actual vessel, put it on rails following the recorded trajectory, despawn on arrival. More immersive but requires solving vessel persistence across scene changes, SOI transitions while unloaded, and resource tracking on packed vessels. Significant additional complexity for marginal gameplay benefit.

The abstracted approach is the right starting point. It can always be upgraded to physical later.

## Looping and Transfer Windows

### Local Routes (Same Parent Body)

KSC → Mun Base, KSC → Minmus Station, Orbital Station → Surface Base — these work at any time because the destination is always reachable. The loop interval is just the transit time plus turnaround time, set by the player.

The Mun's orbital period is ~6.4 days. A Mun resupply that takes 2 days transit could run every 4-5 days comfortably. Minmus (12 day orbit) might need slightly longer intervals but is always reachable.

### Interplanetary Routes

Kerbin → Duna transfers only work during specific planetary alignments (~2 Kerbin years apart). A recorded Hohmann transfer captures the trajectory at a specific alignment. The route can only re-execute when the planets return to approximately the same configuration.

**Approach: synodic period scheduling.** The synodic period between two bodies is the time between consecutive transfer windows. For Kerbin-Duna it's about 2.135 Kerbin years. Rather than computing phase angles, the route simply declares: "This route repeats every N days" where N approximates the synodic period. The recording already implicitly encodes the correct alignment — the route interval just needs to match the orbital mechanics.

| Route | Synodic Period | Practical Interval |
|-------|---------------|-------------------|
| Kerbin → Mun | ~6.4 days | Player-set (e.g., 5-10 days) |
| Kerbin → Minmus | ~14.5 days | Player-set (e.g., 15-20 days) |
| Kerbin → Duna | ~2.14 years | ~2.14 years (auto-computed) |
| Kerbin → Eve | ~1.58 years | ~1.58 years (auto-computed) |
| Kerbin → Jool | ~3.6 years | ~3.6 years (auto-computed) |

For interplanetary routes, the mod could auto-compute the interval from the origin and destination body orbital periods, or let the player override it.

### Atmospheric Segments Loop Freely

An important insight from the loop playback analysis: atmospheric segments of a route (launch, landing) can replay at any time because they use geographic coordinates. `GetWorldSurfacePosition(lat, lon, alt)` always returns the correct position regardless of when it's called. So the launch and landing ghosts look correct on every cycle.

Exo/orbital segments drift slightly per cycle (~1 km per 110 seconds for a Kerbin orbit) due to body orbital motion, but this is cosmetic and invisible in practice. Nobody watches the transit ghost in detail.

## Resource Tracking

### What Needs to Be Captured

Currently, `TrajectoryPoint` tracks career currencies (funds, science, reputation). For logistics, we need physical resources:

```
// Per trajectory point, snapshot of total vessel resources
LiquidFuel: 1200.0
Oxidizer: 1466.7
MonoPropellant: 30.0
Ore: 0.0
ElectricCharge: 200.0
```

This can be stored as a flat key-value dictionary on the trajectory point. The recording format already supports extensibility through the external `.prec` sidecar files — adding resource keys is a format version bump, not a schema redesign.

### Computing Route Cost and Delivery

```
First point resources:  LF=1200, Ox=1467, Ore=0
Last point resources:   LF=50,   Ox=67,   Ore=500

Route cost:    LF=1150, Ox=1400  (consumed during flight)
Route delivery: Ore=500           (gained at destination)
```

The delta between first and last point tells you everything. Fuel consumed is the cost. Resources gained (from mining, science experiments, etc.) is the delivery.

### Integration with Game State Events

A route dispatch becomes a game state event:
```
UT=50000  RouteDispatched  key="route-mun-resupply"
          detail="origin=KSC;dest=MunBase;cost=LF:1150,Ox:1400;deliver=Ore:500"
          valueBefore=1200  valueAfter=50  (origin LF before/after)
```

A delivery becomes another event:
```
UT=50173  RouteDelivered  key="route-mun-resupply"
          detail="dest=MunBase;delivered=Ore:500"
```

These integrate with the existing revert system — reverting past a dispatch event undoes the deduction and cancels the delivery.

## Base/Station Identification

A key question: how does the system know what counts as a "base" or "station" at the origin and destination?

**Simple approach:** Any landed or orbiting vessel within 500m of the route's first/last point coordinates. The player doesn't explicitly designate bases — the system finds them by proximity to the recorded start/end positions.

**Better approach:** Let the player tag vessels as "logistics endpoints" in the Parsek UI. This avoids false matches (a rover parked near the base shouldn't receive cargo) and survives minor base relocations.

**Best approach:** Both. Auto-detect by proximity, but allow player override. Show the detected endpoint in the route UI and let them change it.

## UI Concepts

### Route Panel (Extension of Recordings Window)

The existing recordings window shows chains with segments. A route-enabled chain would show additional info:

```
> Mun Resupply (4 segments, 2d 3h transit)
  [Route: Active | Every 5 days | Next dispatch in 1d 14h]
  [Cost: 1,150 LF + 1,400 Ox | Delivers: 500 Ore]
  [Origin: KSC (1,200 LF available) | Dest: Mun Base Alpha]
    Kerbin atmo  | 0:02:30 | active
    Kerbin exo   | 0:45:00 | active
    Mun space    | 1:10:00 | active
    Mun atmo     | 0:05:30 | active
```

### Route Establishment Flow

1. Player records a cargo flight (normal Parsek recording)
2. After committing, a "Create Route" button appears in the chain header
3. Clicking it opens a route configuration panel:
   - Auto-detected origin and destination
   - Computed cost and delivery
   - Cadence slider (every N days)
   - Enable/disable toggle
4. Player confirms, route is established

### Map View Integration

Parsek already renders map markers for recordings. Routes could show:
- Origin and destination markers connected by a line
- Current ghost position along the route (during transit)
- Delivery countdown timer
- Color-coded by status (green=active, yellow=waiting for fuel, red=broken)

## Crew Implications

Routes that use crewed vessels need crew management. Parsek already handles crew reservation and replacement for recordings — the same system applies:

- When a route dispatches, the crew is "reserved" (unavailable for other missions)
- A replacement kerbal with the same trait is hired
- When the route completes, the crew returns (or stays at destination, player's choice)
- If the route loops, crew is reserved for the duration

This is exactly what `ParsekScenario.ReserveSnapshotCrew` and `SwapReservedCrewInFlight` already do. No new crew logic needed.

## Risks and Open Questions

**Reliability:** What happens if the origin base runs out of fuel mid-game? Route should gracefully degrade — show a warning, pause dispatches, resume when fuel is available.

**Multiple routes sharing fuel:** If two routes draw from the same KSC fuel supply, they could compete. Need priority ordering or fair-share allocation. Or just first-come-first-served and let the player manage it.

**Base destruction:** If the Mun base is destroyed (asteroid impact, Kraken), pending deliveries should be lost and the route should auto-pause with a notification.

**Save/load:** Route state (next dispatch time, pending deliveries) must serialize into the save file. The existing `GameStateStore` persistence handles this pattern.

**Performance:** 10 active routes checking once per in-game day is negligible. 100 routes with per-frame checks would need optimization. The existing loop playback system already handles this efficiently — route scheduling would piggyback on the same timing.

**Mod compatibility:** ISRU (mining drills), planetary bases, and life support mods all add resources. The route system should work with any resource name — just capture whatever the vessel has, delta it, transfer it. No hard-coded resource names.

## Implementation Phases

**Phase 0 (prerequisite, in progress — timeline-actions branch):** Milestone system with epoch isolation, replay tracking, and resource budget. This is actively being built. The `MilestoneStore`, `ResourceBudget`, and `PreLaunch*` snapshot infrastructure establishes the patterns that logistics would reuse: time-bounded event bundles, branch-aware replay state, and committed cost aggregation.

**Phase 1:** Physical resource snapshots per trajectory point. Extend `TrajectoryPoint` or add a parallel resource log. This is the foundation — everything else builds on knowing what resources were where during the flight. The `.prec` sidecar format already supports extensibility; adding resource keys is a format version bump.

**Phase 2:** Route definition. Extract origin, destination, cost, delivery from a committed recording. Store as metadata on the chain. UI: "Create Route" button on eligible chains. Routes become a new entity alongside milestones — both linked to recordings, both time-bounded, both epoch-aware.

**Phase 3:** Abstracted route execution. Dispatch/delivery scheduling using existing loop timing. Deduct from origin vessel resources, add to destination after transit time. Route dispatches become game state events (new `GameStateEventType` values), bundled into milestones at commit time. `LastDispatchedCycle` parallels `LastReplayedEventIndex` for replay tracking.

**Phase 4:** Route management UI. Status display, cadence configuration, fuel warnings, map view integration. Extends `ResourceBudget.ComputeTotal()` to include route fuel commitments alongside recording and milestone costs.

**Phase 5 (optional):** Physical vessel routing. Spawn actual vessels on recorded trajectories. Significantly more complex, may not be worth the effort vs abstracted approach.

## Summary

Logistics is a natural evolution of Parsek's recording system. The "fly it once, automate it forever" model is compelling because it ties automation to player skill — you earn routes by flying them. The infrastructure is largely in place: recordings, chains, loops, vessel snapshots, game state tracking, crew management. The main new work is physical resource tracking and the route scheduling layer, both of which build directly on existing code.

The milestone system being developed on `timeline-actions` is the key enabler. It establishes the patterns logistics would reuse: epoch-isolated event bundles tied to recordings, replay tracking with persistent indices, resource budget aggregation, and branch-aware state management. Once milestones land, adding route dispatches as another event type in the same framework is a natural next step — the hard architectural problems (revert handling, branch isolation, cost tracking, persistence) are already solved.
