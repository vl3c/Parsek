# Loop Playback, Orbital Drift, and Logistics Network Research

## 1. Atmospheric Loops Are Position-Independent

Atmospheric ("atmo") recordings use geographic coordinates (lat/lon/alt) stored per trajectory point. Ghost position is computed via `body.GetWorldSurfacePosition(lat, lon, alt)`, which is a KSP API that converts geographic coords to world-space position **accounting for the body's current rotation and orbital position at the current UT**.

This means atmo recordings can loop at any interval without visual drift:
- The ghost always appears at the correct surface location relative to terrain
- Body rotation is handled internally by KSP - same lat/lon tracks the same ground point
- Body orbital motion doesn't matter - the coordinate system is body-relative

**Loop mechanics** (`ParsekFlight.TryComputeLoopPlaybackUT`):
- Compute cycle: `cycleDuration = recordingDuration + pauseSeconds`
- Remap current UT into the recording's UT range: `loopUT = startUT + (elapsed % cycleDuration)`
- Ghost is rebuilt fresh each cycle (part events, engine FX reset cleanly)
- Resource deltas are NOT replayed during loops (visual only)

**No issues here. Atmo loops work perfectly.**

---

## 2. Exoatmospheric Loops and Orbital Drift

### The Problem

Exo/space recordings use orbit segments with Keplerian parameters. Ghost position is computed via:
```
orbit = new Orbit(inc, ecc, sma, lan, argPe, mna, epoch, body)
worldPos = orbit.getPositionAtUT(loopUT)
```

The orbit's `epoch` is fixed from recording time. When looping, `loopUT` remaps back to the original UT range. But `orbit.getPositionAtUT(loopUT)` returns position relative to the **body's center in inertial space** - and the body itself has moved since the original recording.

### Drift Quantification

For a 100km Kerbin orbit, 100-second recording looping every 110 seconds:
- Kerbin orbital velocity: ~9.285 km/s
- Kerbin moves ~1,021 km per loop cycle
- Ghost drifts ~1 km per cycle relative to the planet's actual position
- After 10 loops: ~10 km accumulated drift

For heliocentric "Sun space" segments, the ghost position relative to the Sun is correct (orbit is around the Sun), but relative to any planet it drifts by the planet's orbital motion.

### Why This Doesn't Matter in Practice

Nobody watches orbital recordings loop. Exo segments are transit phases between interesting atmospheric events. The drift is slow (~1 km per 2 minutes) and the ghost is in space where there's no visual reference to notice it.

The only scenario where it would matter: looping a specific orbital rendezvous or station flyby recording. This is an extreme edge case.

### Transfer Window Constraint

An exo recording captures a trajectory that was valid at a specific planetary alignment. If you wanted to loop an interplanetary transfer, you'd need the planets in the same relative positions - which only happens at transfer window intervals:
- Kerbin-Mun: ~6.4 day synodic period (frequent)
- Kerbin-Duna: ~2 year synodic period (rare)
- Kerbin-Jool: ~3.6 year synodic period (very rare)

This constraint is irrelevant for current playback (recordings play at their original UTs after revert, so planets are in the right positions). It only becomes relevant for the logistics concept below.

---

## 3. Future Concept: Logistics Network via Recorded Routes

### Core Idea

A recorded flight becomes a reusable "route" - a supply run between two locations. When enabled, the route periodically transfers resources from origin to destination, consuming fuel and delivering cargo. The ghost replays visually while resources flow automatically.

### How It Builds on Existing Infrastructure

| Existing System | Logistics Extension |
|----------------|-------------------|
| Resource tracking (funds/science/rep per point) | Add physical resources (LF, Ox, Ore, etc.) per point |
| Vessel snapshots | Define what vessel runs the route (fuel capacity, cargo capacity) |
| Chain segments (atmo/exo/space per body) | Multi-phase routes: launch → transit → landing |
| Loop playback with configurable period | Route cadence: "every N days" |
| PlaybackEnabled toggle | Route enabled/disabled toggle |
| VesselSpawner | Spawn delivery vessel at destination |

### Resource Tracking Extension

Currently `TrajectoryPoint` tracks only career currencies. Logistics would need physical resources:
```
// Snapshot total vessel resources at each sample point
Dictionary<string, double> partResources  // "LiquidFuel" → 450, "Ore" → 500
```

The delta between first and last point defines the route's cost and delivery:
- Cost = resources consumed during flight (fuel burn)
- Delivery = resources gained at destination (mining, cargo)

### Two Implementation Approaches

**Option A: Abstracted Routes (simpler, recommended for MVP)**
- No physical vessel during transit
- Deduct fuel cost from origin, add cargo to destination after elapsed time
- Just math + scheduling, no physics
- Example: "Route costs 1,200 LF, delivers 500 Ore in 6 days"
- Implementation: ~500 lines, reuses existing loop timing

**Option B: Physical Vessel Routing (realistic, complex)**
- Spawn actual vessel at origin, put on rails with recorded trajectory
- Vessel exists in tracking station during transit
- Despawn at destination, transfer resources to base
- More immersive but harder to debug (vessel loading, time warp, SOI transitions)
- Implementation: significant, needs deep KSP vessel lifecycle integration

### Route Scheduling

**Local routes (same body, e.g., KSC → Mun Base):**
- Always available (Mun's orbital period is short, ~6.4 days)
- Cadence set by player: "every 10 days"
- No transfer window validation needed

**Interplanetary routes (e.g., Kerbin → Duna):**
- Only available during transfer windows
- Two approaches:
  1. **Astronomical**: Check current phase angle between bodies, compare to recorded alignment
  2. **Pragmatic**: "This route is available every N days" where N ≈ synodic period
- Pragmatic is clearer to players and avoids exposing orbital mechanics

### KSP API for Resource Manipulation

The APIs exist and are straightforward:
- `vessel.parts[i].Resources["LiquidFuel"].amount` - read/write resource amounts
- `vessel.parts[i].Resources["Ore"].maxAmount` - check capacity
- Works on both loaded (unpacked) and unloaded (packed/on-rails) vessels
- For on-rails vessels, modify the ConfigNode protoVessel instead

### Game Balance

Key constraint: routes should not be free. Each execution must:
1. Consume fuel proportional to the recorded flight
2. Have a minimum interval (prevent instant resource duplication)
3. Fail if origin doesn't have enough fuel
4. Fail if destination is destroyed or inaccessible

The recording itself acts as a "proof of capability" - you can only automate what you've already flown manually. This keeps the logistics system grounded in gameplay.

### Example Workflow

1. Record a KSC → Mun Base resupply mission (manual flight)
2. After landing, mark recording as "logistics route" in Parsek UI
3. System extracts: origin (KSC), destination (Mun Base), cost (1,500 LF), delivery (500 Ore), transit time (6 days)
4. Enable route, set cadence to 10 days
5. Every 10 days: check fuel at KSC, deduct, schedule delivery
6. After 6 days: add 500 Ore to Mun Base inventory
7. Ghost replays the flight visually during transit (optional)

### Open Questions

- Should the logistics vessel show as a ghost during transit, or be invisible?
- How to handle base destruction mid-transit? (Refund? Lost cargo?)
- Should routes degrade over time? (Simulating wear, requiring re-recording?)
- Integration with KSP's contract system? (Automatic contracts for route establishment?)
- Can logistics routes chain? (KSC → Station A → Mun Base, with Station A as relay)
- How to handle multiple routes competing for the same fuel supply?
