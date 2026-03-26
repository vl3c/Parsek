# Ghost Orbits & Trajectories — Design Investigation

## Status: Investigation / Step 1–2 of workflow

## Problem

Ghost vessels currently have minimal map view presence. They appear as basic GUI dot markers (`ParsekUI.DrawMapMarkers`) but lack:

1. **Orbit lines** — no Keplerian orbit path drawn in map view or tracking station
2. **Tracking station entries** — ghosts don't appear in the tracking station vessel list
3. **Navigation targeting** — ghosts can't be set as rendezvous targets for transfer planning
4. **Clickable map icons** — ghost markers are static GUI overlays, not interactive map objects

This makes orbital mission planning with ghost vessels impractical. A player planning a rendezvous with a ghost station (committed recording of a space station) can't see its orbit, can't set it as a target, and can't get distance/closest-approach data. The ghost is effectively invisible in the planning tools that KSP provides.

The roadmap (`docs/roadmap.md`) identifies this under v0.5 remaining work: *"ghost map presence KSP integration (tracking station, orbit lines, nav target)"* and bug #60 in the todo list tracks the 4 stubbed integration points in `GhostMapPresence.cs`.

## Why This Matters to the Player

Ghost vessels represent committed recordings — vessels that *exist* in the player's timeline. They should behave like real vessels in every planning context. A ghost relay constellation should show orbits. A ghost space station should be targetable for rendezvous. A ghost tanker on a supply run should appear in tracking station for monitoring.

Without this, the player has to mentally track ghost vessel positions and switch to flight view to eyeball relative positions — defeating the purpose of map view as a planning tool.

## Current State of Parsek Ghost Map System

### What exists today

1. **GhostMapPresence.cs** — Pure data layer complete, 4 KSP integration points stubbed:
   - `RegisterTrackingEntry()` — register ghost in tracking station (TODO)
   - `CreateMapOrbitLine()` — create orbit line in map view (TODO)
   - `SetAsNavigationTarget()` — enable rendezvous targeting (TODO)
   - `RemoveTrackingEntry()` — cleanup on despawn (TODO)

2. **ParsekUI.DrawMapMarkers()** (`ParsekUI.cs:3415`) — Basic GUI overlay markers:
   - Uses `PlanetariumCamera.Camera` to project ghost world positions to screen
   - Draws a 10px colored dot + vessel name label
   - Works for preview ghost and timeline ghosts
   - Not clickable, no orbit data, no KSP integration

3. **ParsekFlight.DrawGhostLabels()** (`ParsekFlight.cs:7795`) — Flight view floating labels:
   - Chain ghost labels with spawn status text
   - Uses `FlightCamera.fetch.mainCamera` (flight view only, not map view)

4. **GhostCommNetRelay** — Precedent for virtual KSP system integration:
   - Creates CommNet nodes for ghost antennas without ProtoVessels
   - Uses CommNet API directly, bypassing the vessel system
   - Tracks registered nodes per ghost for cleanup
   - **Key pattern**: direct API integration without fake vessels

### Orbital data already available

Each recording stores terminal orbital elements (`Recording.cs:73-80`):
- `TerminalOrbitInclination`, `TerminalOrbitEccentricity`, `TerminalOrbitSemiMajorAxis`
- `TerminalOrbitLAN`, `TerminalOrbitArgumentOfPeriapsis`
- `TerminalOrbitMeanAnomalyAtEpoch`, `TerminalOrbitEpoch`
- `TerminalOrbitBody`

Plus full `OrbitSegment` lists for the recorded trajectory (`IPlaybackTrajectory.OrbitSegments`).

`GhostExtender.PropagateOrbital()` already does pure Keplerian propagation from these elements — computing position at any UT without KSP's Orbit class.

`ParsekFlight.PositionGhostFromOrbit()` (`ParsekFlight.cs:7290-7356`) **already constructs KSP `Orbit` objects** from OrbitSegment Keplerian elements and caches them in a `Dictionary<int, Orbit> orbitCache`. This proves the pattern works — constructing `new Orbit()` from stored elements and calling `orbit.getPositionAtUT()`.

`GhostMapPresence.HasOrbitData()` checks if a recording has sufficient data for orbit display.

### Rendering zones

`RenderingZoneManager` classifies ghosts by distance:
- **Physics** (< 2.5 km): full mesh + physics
- **Visual** (< 120 km): full mesh, no physics
- **Beyond** (120 km+): no mesh, position tracked for map view only

The "Beyond" zone explicitly says "position tracked for map view only" — this is where proper orbit lines would replace the current dot markers.

### Soft cap system

`GhostSoftCapManager` has a `SimplifyToOrbitLine` action (enum value 2) that hides ghost meshes and is supposed to show orbit lines instead. Currently the mesh hiding works but the orbit line rendering is a placeholder — the exact gap this feature would fill.

## KSP API Investigation Results

### Critical Finding: No Map Presence Without a Real Vessel

Research confirms: **there is no way to have map/tracking station presence without a real `Vessel` in `FlightGlobals.Vessels`**. The tracking station reads from `FlightGlobals.Vessels` directly. `MapObject.ObjectType` is a closed enum (`Vessel`, `CelestialBody`, `ManeuverNode`) — mods cannot add new types. Any ghost that needs to appear in tracking station or have a clickable map icon must be backed by an actual Vessel object.

This resolves the "ProtoVessel vs custom" question decisively in favor of ProtoVessel.

### Approach 1: Lightweight ProtoVessel (RECOMMENDED)

Create a minimal `ProtoVessel` per ghost with orbital data. This gives:
- Automatic tracking station entry
- Automatic map view icon (clickable `MapObject` via `vessel.mapObject`)
- Automatic orbit line via `OrbitRenderer` (managed by `OrbitDriver`)
- `ITargetable` support (Vessel implements ITargetable)
- All KSP native tools work (closest approach, distance readouts, navball markers)

**Contract Configurator pattern** (the canonical reference for lightweight vessel creation):
```csharp
// Create orbit from Keplerian elements
Orbit orbit = new Orbit(inc, ecc, sma, lan, argPe, mna, epoch, body);

// Build minimal part nodes (single lightweight part)
ConfigNode[] partNodes = /* single probe core or structural part */;

// Discovery info for tracking station visibility
ConfigNode discoveryNode = ProtoVessel.CreateDiscoveryNode(
    DiscoveryLevels.Owned, UntrackedObjectClass.A, lifetime, maxLifetime);

// Create vessel node
ConfigNode protoVesselNode = ProtoVessel.CreateVesselNode(
    name, vesselType, orbit, 0, partNodes,
    new ConfigNode[] { discoveryNode });

// Register with game — this creates a Vessel with mapObject, OrbitDriver, etc.
ProtoVessel protoVessel = new ProtoVessel(protoVesselNode, HighLogic.CurrentGame);
protoVessel.Load(HighLogic.CurrentGame.flightState);
```

Parsek already does this for chain-tip spawns (`VesselSpawner.RespawnVessel`). The difference: a "map-only" ghost ProtoVessel would be:
- Minimal part tree (single part — e.g., a probe core)
- Marked with a distinct vessel type (e.g., `VesselType.Probe` or `VesselType.Unknown`)
- Orbit data from recording's terminal orbital elements
- `DiscoveryLevels.Owned` for tracking station visibility
- Name prefixed/suffixed to indicate ghost status (e.g., "[Ghost] Station Alpha")

**OrbitDriver.UpdateMode** controls orbit propagation:
- `UPDATE` (1) — Keplerian propagation (on-rails). This is what ghost vessels need.
- `TRACK_Phys` (0) — physics-driven. Not needed for ghosts.
- `IDLE` (2) — not updating.

**Discovery system controls visibility:**
- `DiscoveryLevels.Owned` — fully visible in tracking station
- `UntrackedObjectClass.A` through `I` — size classification
- Created via `ProtoVessel.CreateDiscoveryNode(level, sizeClass, lifetime, maxLifetime)`

**Concerns:**
- ProtoVessel creation is "heavy" — Section 13.2 design constraint says *"ProtoVessel creation is acceptable ONLY for unloaded chain-tip spawns and tracking station entries"*. Map-only ghosts fit this exception.
- Must be carefully lifecycle-managed: created when ghost becomes active, removed when chain resolves
- PID collision risks (same fix as `RegenerateVesselIdentity`)
- Performance with many ghost ProtoVessels (soft cap already limits ghost count)
- If player flies near ghost ProtoVessel, KSP may try to load it — must prevent or handle

### Approach 2: Custom OrbitRenderer + MapNode

Create orbit lines and map icons without a ProtoVessel:
- Construct `Orbit` object from Keplerian elements
- Create `OrbitRenderer` or use GL line drawing
- Create custom `MapNode` for clickable icon

**Pros:** Lighter weight, no vessel system pollution
**Cons:** `MapObject.ObjectType` is a closed enum — can't create custom map object types. No tracking station entry without a Vessel. Targeting needs custom `ITargetable` implementation. **Effectively a dead end for most features we need.**

### Approach 3: Procedural Mesh Rendering (Trajectories mod approach)

The KSPTrajectories mod draws trajectory predictions using procedural mesh ribbons:
- `MapOverlay.cs` attaches a `MonoBehaviour` to `PlanetariumCamera.Camera.gameObject`
- Uses `MapView.fetch.orbitLinesMaterial` for stock-matching appearance
- Builds triangle-strip meshes in `OnPreRender` with screen-space ribbon width
- Respects `MapView.Draw3DLines` (layer 24 for 3D, layer 31 for 2D)
- Converts positions via `ScaledSpace.LocalToScaledSpace()`

**Pros:** Full control over visual style, works for non-Keplerian trajectories
**Cons:** No native KSP interaction (clicking, targeting), doesn't appear in tracking station, significant rendering code

### Approach 4: Hybrid

Combine approaches:
- **ProtoVessel** for tracking station + targeting (Approach 1) — gives all the interaction for free
- **Custom orbit line rendering** for visual differentiation — ghost orbits drawn in a distinct style (dashed, semi-transparent, different color) overlaid on or replacing the default orbit renderer

This is likely the best approach: get full KSP integration via ProtoVessel, then customize the visual appearance.

## ITargetable Interface (for rendezvous targeting)

KSP's `ITargetable` interface (reconstructed from decompiled code and mod usage):
```csharp
public interface ITargetable
{
    Vector3 GetFwdVector();
    string GetName();
    string GetDisplayName();
    Vessel GetVessel();
    VesselTargetModes GetTargetingMode();
    bool GetActiveTargetable();
    Orbit GetOrbit();
    OrbitDriver GetOrbitDriver();
    Transform GetTransform();
    Vector3d GetObtVelocity();
    Vector3d GetSrfVelocity();
}
```

**Stock implementors:** `Vessel`, `CelestialBody`, `ModuleDockingNode`.

**`FlightGlobals.fetch.SetVesselTarget(ITargetable tgt)`** accepts any `ITargetable`. This means:
- With ProtoVessel approach: targeting comes **free** — `Vessel` implements `ITargetable`
- Without ProtoVessel: would need a custom `ITargetable` implementation wrapping ghost orbit data

**Critical methods for rendezvous:**
- `GetOrbit()` — needed for intercept/closest-approach calculations
- `GetTransform()` — needed for navball direction vectors
- `GetTargetingMode()` — return `DirectionAndVelocity` for rendezvous
- `GetVessel()` — can return null for non-vessel targets (CelestialBody does this)

Since the ProtoVessel approach creates a real `Vessel`, all targeting works natively without custom code.

## KSPTrajectories Mod — Detailed Analysis

*(Cloned to /tmp/Trajectories from github.com/neuoy/KSPTrajectories)*

### Repository structure
- `src/Plugin/Display/MapOverlay.cs` — map view trajectory rendering
- `src/Plugin/Display/FlightOverlay.cs` — flight view overlay
- `src/Plugin/Display/GfxUtil.cs` — graphics utilities (LineRenderer, crosshair)
- `src/Plugin/3rdParty/GLUtils.cs` — OpenGL drawing utilities (from MechJeb2)
- `src/Plugin/Predictor/Trajectory.cs` — core trajectory computation
- `src/Plugin/Predictor/TargetProfile.cs` — landing target management
- `src/Plugin/Display/NavBallOverlay.cs` — navball trajectory indicators

### How Trajectories Renders in Map View

Trajectories uses **procedural mesh ribbons**, NOT GL lines or LineRenderer:

1. **Material**: Uses `MapView.fetch.orbitLinesMaterial` (MapOverlay.cs:100) — the stock orbit line material. This makes trajectory lines look identical to stock orbit lines.

2. **Camera attachment**: A `MapTrajectoryRenderer` MonoBehaviour is attached to `PlanetariumCamera.Camera.gameObject` (MapOverlay.cs:101). Its `OnPreRender` callback rebuilds meshes every frame.

3. **Layer handling**: Respects `MapView.Draw3DLines` — uses layer 24 for 3D mode, layer 31 for 2D mode (MapOverlay.cs:82-83, 172). KSP toggles between these based on zoom.

4. **Ribbon construction** (MapOverlay.cs:237-276): The `MakeRibbonEdge` method:
   - Converts world positions to screen space via `PlanetariumCamera.Camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(pos))`
   - Computes perpendicular offset in screen space for constant 3px pixel width
   - Converts back to world space (3D mode) or keeps screen coords (2D mode)
   - Handles front/back culling with degenerate triangles

5. **Orbit sampling** (MapOverlay.cs:278-374): `InitMeshFromOrbit` samples using `orbit.getRelativePositionAtUT()` with adaptive stepping based on true anomaly changes (max 2*PI/128 per step).

6. **Crosshair markers** (MapOverlay.cs:420-489): Procedural meshes — two perpendicular quads that scale with camera distance. Impact=red, target=green. Altitude hack (+1200m) prevents ground clipping.

### Flight View Rendering (Different Approach)

For flight scene, uses `LineRenderer` component instead:
- Attached to `FlightCamera.fetch.mainCamera.gameObject` (FlightOverlay.cs:46)
- Uses `Shader.Find("KSP/Orbit Line")` with fallback to `"KSP/Particles/Additive"` (GfxUtil.cs:66-67)
- Width dynamically adjusted based on camera distance

### Target System

`TargetProfile` is entirely custom — does NOT interact with KSP's native targeting (`ITargetable`, `FlightGlobals.fetch.VesselTarget`):
- Stores `CelestialBody` + `LocalPosition` (body-relative Vector3d)
- Persisted per-vessel via `TrajectoriesVesselSettings` PartModule
- Set methods: `SetFromWorldPos`, `SetFromLocalPos`, `SetFromLatLonAlt`

### NavBall Integration

`NavBallOverlay` clones `navball.progradeVector` transform (NavBallOverlay.cs:99) to create custom markers:
- Green circle (corrected direction guide) and red square (reference direction)
- Custom PNG textures from `GameData/Trajectories/Textures/`
- Positions via `navball.attitudeGymbal * (direction * navball.VectorUnitScale)`

### Key Findings for Parsek

| Need | Trajectories Provides | Usefulness |
|---|---|---|
| Ghost orbit lines in map view | Procedural mesh ribbon technique | Medium — works but complex; ProtoVessel + native `OrbitRenderer` may be simpler for Keplerian ghosts |
| Ghost markers in map view | Crosshair mesh technique | Medium — works for position markers |
| Tracking station entries | Nothing | Not applicable |
| Rendezvous targeting | Nothing — uses own target system | Not applicable |
| NavBall indicators | progradeVector cloning pattern | Useful if ghost-relative guidance desired |
| Camera integration | `OnPreRender` on camera GameObject | Useful architectural pattern |
| Stock material reuse | `MapView.fetch.orbitLinesMaterial` | Directly reusable for visual consistency |
| Coordinate transforms | `ScaledSpace.LocalToScaledSpace`, layer 24 vs 31 | Essential for any map rendering |

### Critical Insight

Trajectories bypasses KSP's orbit renderer entirely because it renders non-Keplerian (atmospheric) trajectories. Parsek ghosts, however, **are** Keplerian orbits — stored as standard orbital elements. This means Parsek can potentially use KSP's native `OrbitRenderer` instead of reimplementing Trajectories' ribbon mesh system, getting orbit lines + map integration for free via a lightweight ProtoVessel approach.

## Recommended Investigation Path

### Phase 1: ProtoVessel-based Map Presence (Core)

**Goal:** Ghost vessels appear in tracking station, have clickable map icons, show orbit lines, and are targetable.

1. Create a minimal ProtoVessel from recording terminal orbital elements
2. Add to `flightState.protoVessels` when ghost chain becomes active
3. Mark with distinct vessel type and custom name suffix (e.g., "[Ghost] Station Alpha")
4. Remove ProtoVessel when chain resolves (vessel spawns or chain terminates)
5. Test: ghost appears in tracking station, orbit line visible, clickable in map

**Key questions to resolve in-game:**
- What's the minimal ConfigNode that produces a working ProtoVessel with orbit?
- Does a single-part ProtoVessel show an orbit line in map view?
- Can we suppress the part mesh for a ProtoVessel (we already have ghost meshes)?
- How does `OrbitRenderer` interact with unloaded ProtoVessels?
- Can we set a ProtoVessel's orbit color/style?

### Phase 2: Custom Orbit Line Style (Visual Polish)

**Goal:** Ghost orbits are visually distinct from real vessel orbits.

1. Hook into orbit rendering to customize ghost vessel orbit appearance
2. Options: different color (semi-transparent cyan), different line width, or custom GL overlay
3. May use Harmony patch on `OrbitRenderer.DrawOrbit` or a parallel GL renderer

### Phase 3: Rendezvous Targeting (Interaction)

**Goal:** Player can set a ghost vessel as navigation target.

If ProtoVessel approach works:
- Ghost ProtoVessels are already `ITargetable` via the `Vessel` class
- `FlightGlobals.fetch.SetVesselTarget(ghostVessel)` should work natively
- Closest approach, relative velocity, distance readouts come for free

If custom approach needed:
- Implement `ITargetable` wrapper around ghost orbital data
- Register with targeting system

### Phase 4: Lifecycle Integration

**Goal:** Ghost map presence correctly tracks ghost chain lifecycle.

1. Register map presence when `GhostChainWalker` creates a chain
2. Update orbit when chain tip changes (new recording = new terminal orbit)
3. Remove on chain resolution (spawn, terminate, abandon)
4. Handle scene transitions (tracking station ↔ flight ↔ space center)
5. Handle multiple ghosts (performance with many orbital ghost ProtoVessels)

## Relationship to Existing Systems

### GhostMapPresence.cs
The existing pure data layer (`HasOrbitData`, `ComputeGhostDisplayInfo`) remains valid. The stubbed methods get implemented using whichever approach is chosen.

### GhostCommNetRelay
Pattern precedent for virtual system integration. CommNet relay uses direct API, not ProtoVessel. Map presence may use ProtoVessel (heavier but gets more for free). Both approaches coexist — CommNet uses the lightweight path, map presence uses the ProtoVessel path.

### GhostExtender
Already computes orbital propagation from Keplerian elements. If using custom orbit rendering (not ProtoVessel), can share this math. If using ProtoVessel, KSP handles propagation natively via `OrbitDriver`.

### GhostSoftCapManager
The `SimplifyToOrbitLine` cap action becomes meaningful — when a ghost is simplified, its mesh hides but its ProtoVessel orbit line remains visible. Natural integration.

### VesselSpawner
`RespawnVessel` already creates ProtoVessels for chain-tip spawns. A lighter variant (`CreateMapOnlyVessel`?) would create the minimal ProtoVessel for map presence without the full part tree.

### ParsekUI.DrawMapMarkers
Can be deprecated once proper map icons exist via ProtoVessel or MapNode. Or kept as a fallback for non-orbital ghosts (surface-landed ghosts without orbit data).

## Edge Cases to Investigate

1. **Ghost without orbital data** — surface-landed ghost has no orbit. Show as ground marker only?
2. **SOI transitions** — ghost crosses SOI boundary during chain window. Terminal orbit is for one body, but ghost may have moved to another.
3. **Multiple ghosts, same recording** — looped recordings create multiple instances. Each needs separate map presence? Or single orbit line for the loop?
4. **ProtoVessel cleanup on scene change** — tracking station → flight transition may reload ProtoVessels. Ghost ProtoVessels must survive or be recreated.
5. **Ghost ProtoVessel loading** — if player flies near a ghost ProtoVessel, KSP may try to load it as a real vessel. Must prevent or handle this.
6. **Performance** — many ghost ProtoVessels with orbit renderers. Soft cap interaction.
7. **Visual duplication** — ghost mesh visible + ghost ProtoVessel visible = double rendering. Need to suppress one.
8. **Target persistence** — if ghost is set as target, chain resolves, ghost becomes real vessel. Target should transfer seamlessly.
9. **Save/load** — ghost ProtoVessels in the save file vs. reconstructed on load. Prefer reconstruction from recording data (source of truth).
10. **Rewind** — rewind clears ghost chains. Ghost ProtoVessels must be cleaned up.

## What Doesn't Change

- Ghost visual mesh building (`GhostVisualBuilder`) — unchanged, continues to build physical ghost meshes for loaded ghosts
- Ghost playback engine (`GhostPlaybackEngine`) — unchanged, continues kinematic positioning
- Recording data model — no changes to `Recording`, `OrbitSegment`, etc.
- Chain walking logic — `GhostChainWalker` unchanged
- CommNet relay — `GhostCommNetRelay` unchanged, complementary system
- Flight recorder — no changes to recording capture

## Next Steps

Per the development workflow:

1. **Step 1 (Vision)** — This investigation covers the vision: ghost vessels should feel like real vessels in map/tracking/planning contexts
2. **Step 2 (Scenarios)** — Walk through concrete gameplay sessions with this feature (below)
3. **Step 3 (Design Doc)** — Formalize into a design document after scenarios are explored
4. **Step 4 (Plan/Build)** — Implementation with clean-context agents

## Step 2: Gameplay Scenarios

### Scenario A: Rendezvous with ghost station (happy path)

**Setup:** Player committed a recording of building a space station in 80 km Kerbin orbit. They rewound to an earlier point to record a supply mission.

**Play session:**
1. Player launches supply vessel from KSC, reaches orbit
2. Opens map view — sees station ghost's orbit line in a distinct ghost color (semi-transparent cyan)
3. Clicks the ghost icon in map view — it's selectable, shows "Ghost — Station Alpha"
4. Right-clicks ghost icon → "Set as Target" (or uses the stock target button)
5. Navball shows target prograde/retrograde markers, distance readout, closing velocity
6. Player creates a maneuver node for Hohmann transfer — closest approach indicator works normally
7. Executes maneuver, approaches ghost station
8. At chain-tip spawn UT, the ghost becomes a real vessel. Target transfers seamlessly — navball markers don't jump, distance continues counting down
9. Player docks with the now-real station

**What the player expects:** The ghost station behaves identically to targeting any real vessel. No jarring transitions.

**Edge cases surfaced:**
- Target transfer when ghost → real vessel (PID preservation needed)
- Ghost orbit accuracy vs. actual spawn orbit (should be identical — both from same Keplerian elements)
- What if the player is in physics range when chain tip spawns? Ghost mesh + ProtoVessel both exist briefly

---

### Scenario B: Ghost constellation in tracking station

**Setup:** Player committed 4 relay recordings forming a Kerbin relay constellation (different orbital phases). They're now at the Space Center.

**Play session:**
1. Player opens Tracking Station
2. Sees 4 ghost entries: "Ghost — Relay Alpha", "Ghost — Relay Beta", etc.
3. Each shows status: "Ghost — spawns at UT=12000" (or "Ghost — active" if currently visible as ghost)
4. Clicks one relay — tracking station camera centers on it, orbit is visible
5. Can switch between ghost relays like switching between normal vessels
6. Ghost relays have a visual indicator distinguishing them from real vessels (icon color? tag?)
7. Player goes back to KSC, launches a new vessel, opens map — sees all 4 ghost relay orbits

**What the player expects:** Ghost relays are first-class entries in tracking station, not hidden or second-class.

**Edge cases surfaced:**
- Filtering: can the player filter to show/hide ghosts? New filter category needed?
- Ghost count: 50 committed recordings = 50 ghost entries? Tracking station could be cluttered
- Vessel type: what VesselType should ghosts use? Affects stock filtering (Probe, Relay, Station, etc.)
- Ghost CommNet relays already provide signal coverage — tracking station entries complement this

---

### Scenario C: Ghost orbit during chain window (Mun transfer)

**Setup:** Player recorded a Mun transfer, committed it, then rewound to before the launch. The transfer vessel is now a ghost chain.

**Play session:**
1. Player is at KSC, planning a different mission
2. Opens map view — sees ghost chain vessel's current orbit
3. Ghost's orbit changes over time: initially in low Kerbin orbit, then transfer orbit, then Mun orbit (following the recording's orbital segments)
4. Player uses this to plan timing — "I need to launch before the ghost reaches Mun to avoid cluttering the Mun SOI"
5. Ghost orbit line updates as the ghost traverses its recording's orbital segments

**What the player expects:** The ghost's map presence reflects its actual position in the recording timeline, not just the terminal orbit.

**Edge cases surfaced:**
- **Which orbit to show?** The recording has multiple `OrbitSegments` (LKO, transfer, Mun capture). The ProtoVessel can only have one `Orbit` at a time. Need to update the ProtoVessel's orbit as the ghost traverses segments.
- **SOI transitions in the recording:** Ghost moves from Kerbin SOI to Mun SOI. The ProtoVessel's reference body must change.
- **Orbit update frequency:** How often to update the ProtoVessel orbit? Every frame? Every segment boundary? Every N seconds?
- **Accuracy vs. playback:** The ghost mesh is positioned by `GhostPlaybackEngine` from trajectory points; the ProtoVessel orbit is Keplerian. These may diverge slightly during atmospheric or near-body segments.

---

### Scenario D: Looped transport route

**Setup:** Player has a looped fuel tanker recording — flies from Minmus back to Kerbin station every 6 hours.

**Play session:**
1. Player opens map view — sees tanker ghost's current orbit (wherever it is in this loop cycle)
2. Ghost orbit line shows the tanker's current trajectory segment
3. Multiple overlap ghosts may exist (from negative loop interval) — each has its own position
4. Player sets the tanker ghost as target to time their own departure from Minmus

**What the player expects:** One orbit line for the "current" tanker, not orbit lines for every historical loop cycle cluttering the map.

**Edge cases surfaced:**
- **Multiple overlap ghosts:** Looped recordings with negative interval create multiple simultaneous ghosts. Each is a separate position. Do we create a ProtoVessel per overlap ghost? That could be many vessels.
- **Decision:** Probably only the primary (most recent) loop cycle gets a ProtoVessel. Older overlap ghosts are visual-only.
- **Loop phase:** Ghost position jumps back to recording start on each loop cycle. ProtoVessel orbit must be updated accordingly.
- **Anchor vessel dependency:** Some looped ghosts are positioned relative to an anchor vessel. Their "orbit" isn't absolute — it's relative.

---

### Scenario E: Surface ghost (no orbit)

**Setup:** Player committed a Mun base recording. The base is landed on the Mun surface.

**Play session:**
1. Player opens Tracking Station — sees "Ghost — Mun Base" with status "Landed on Mun"
2. Clicks it — tracking station camera shows the Mun surface location
3. In map view, a ground marker appears at the base location (like landed vessel icon)
4. No orbit line (not applicable — vessel is on the surface)
5. Player can set it as target for a landing mission — navball shows direction to surface target

**What the player expects:** Surface ghosts appear in tracking station like landed vessels.

**Edge cases surfaced:**
- **ProtoVessel for landed ghost:** Needs `LANDED` situation with lat/lon/alt. Different from orbital ProtoVessel.
- **Can a landed ProtoVessel be targeted?** Yes — real landed vessels can be targeted.
- **Terrain height changes:** Between recording and playback, terrain mods or different PQS resolution could change surface height. Ghost may appear floating or buried.

---

### Scenario F: Ghost spawn and despawn transitions

**Setup:** Player is in flight, time warping. A ghost chain's spawn UT arrives.

**Play session:**
1. Player is in map view, time warping
2. Ghost ProtoVessel is visible with orbit line and map icon
3. Time warp passes the chain's spawn UT — real vessel spawns
4. Ghost ProtoVessel must be removed, real vessel replaces it
5. If ghost was the current target, target transfers to the real vessel
6. Orbit line color changes from ghost color to real vessel color
7. Tracking station entry changes from "Ghost — Station Alpha" to "Station Alpha"

**What the player expects:** Seamless transition. No flicker, no lost target, no duplicate entries.

**Edge cases surfaced:**
- **Spawn blocked by collision:** Ghost continues past recording end (GhostExtender). ProtoVessel orbit remains from terminal elements. Ghost mesh keeps moving. ProtoVessel orbit and ghost mesh position may diverge if blocked for a long time.
- **PID transfer:** Chain tip spawns preserve PID (`preserveIdentity=true`). Ghost ProtoVessel had a different PID (regenerated). Target reference must be updated from ghost PID to real PID.
- **Brief double-existence:** Between "ghost ProtoVessel still exists" and "real vessel created" there's a frame where both exist. Must ensure no duplicate map icons.
- **Map view vs. flight view:** If player is in flight view during spawn, they see the ghost mesh → real vessel transition. Map view should reflect the same transition.

---

### Scenario G: Rewind clears ghosts

**Setup:** Player has several ghost chains active, some targeted. Player rewinds to an earlier save.

**Play session:**
1. Player rewinds — all ghost chains are cleared
2. All ghost ProtoVessels must be removed from `FlightGlobals.Vessels`
3. If any ghost was the current target, target is cleared
4. Tracking station no longer shows those ghosts
5. New ghost chains computed from the rewound timeline state

**What the player expects:** Clean slate after rewind. No orphaned ghost entries.

**Edge cases surfaced:**
- **Rewind timing:** `ResetAllPlaybackState` zeroes all ghost tracking. Ghost ProtoVessel cleanup must happen before or during this.
- **Scene transition during rewind:** Rewind loads a quicksave, which triggers scene transitions. Ghost ProtoVessels in the save file? They shouldn't be there (reconstructed from recordings).
- **Rewind then fast-forward:** After rewind, new ghost chains are computed. New ProtoVessels created. Must not collide with stale data.

---

### Scenario H: Many ghosts (performance stress)

**Setup:** Player has 30+ committed recordings. Most have terminal orbits. Soft caps are enabled.

**Play session:**
1. Player opens map view — 30+ ghost orbit lines visible
2. Performance impact: 30 `OrbitRenderer` instances running
3. Soft cap kicks in: distant ghosts get `SimplifyToOrbitLine` — mesh hidden, but orbit line stays
4. Above despawn threshold: lowest-priority ghosts despawned entirely — including their ProtoVessel?
5. Player scrolls through tracking station — long list of ghost entries

**What the player expects:** Performance stays acceptable. Ghost orbits are informative, not overwhelming.

**Edge cases surfaced:**
- **Soft cap interaction with ProtoVessel:** When `GhostSoftCapManager` despawns a ghost, should its ProtoVessel also be removed? Or should the orbit line persist even when the mesh is gone?
- **Decision:** `ReduceFidelity` and `SimplifyToOrbitLine` → keep ProtoVessel (orbit visible). `Despawn` → remove ProtoVessel too.
- **Tracking station clutter:** Need UI filtering to hide/show ghost entries
- **Map view clutter:** Ghost orbit lines should be less prominent (thinner, more transparent) to avoid overwhelming the view
- **ProtoVessel overhead:** Each ProtoVessel has an OrbitDriver running Keplerian propagation. 30 additional OrbitDrivers should be lightweight (stock KSP handles hundreds of debris).

---

### Scenario I: Ghost in editor/VAB context

**Setup:** Player is in the VAB building a vessel.

**Play session:**
1. Player opens map view from the editor (some editors allow this)
2. Should ghost orbits be visible? Yes — useful for planning
3. Player checks "does my station ghost have the right orbit for this mission?"

**What the player expects:** Ghost map presence works from any scene with map access.

**Edge cases surfaced:**
- **Scene support:** ProtoVessels persist in `flightState` across scenes. If created in flight, they'll exist when returning to KSC, editor, or tracking station.
- **Editor map view:** Limited map functionality in editor. Ghost orbits should still be visible if map is accessible.
- **Creation timing:** Ghost ProtoVessels should be created when ghost chains are computed (flight scene entry) and persist until cleared.

---

### Scenario J: Ghost with no recording data (edge case)

**Setup:** A committed recording has corrupted or missing trajectory/orbit data.

**Play session:**
1. Ghost chain exists but `HasOrbitData` returns false and no surface position
2. No ProtoVessel can be created (no orbit, no position)
3. Ghost still appears as a visual mesh in flight view (using trajectory points if available)
4. No map presence — graceful degradation

**What the player expects:** No crash, no error spam. Ghost just doesn't appear in map.

**Edge cases surfaced:**
- **Partial data:** Has trajectory points but no terminal orbit (recording ended abruptly). Can we derive an approximate orbit from the last few trajectory points?
- **Log coverage:** Must log why a ghost has no map presence (missing data diagnosis)

---

### Scenario K: Ghost vessel interaction with real vessel physics

**Setup:** Player flies within physics range of a ghost ProtoVessel's orbital position.

**Play session:**
1. Player approaches the ghost's orbital position
2. KSP may try to "load" the ghost ProtoVessel (bring it into physics range)
3. Ghost ProtoVessel has a minimal part tree (single probe core) — if loaded, it would be a tiny invisible vessel
4. Meanwhile, the ghost mesh (from GhostVisualBuilder) is already visible at this location

**What the player expects:** No weird duplicate vessel appearing. Ghost mesh is the visual representation; ProtoVessel is invisible.

**Edge cases surfaced:**
- **Vessel loading prevention:** Can we prevent KSP from loading the ghost ProtoVessel within physics range? Options:
  - Remove ProtoVessel when ghost mesh is loaded (within Visual zone)
  - Set ProtoVessel orbit to something far away (defeats the purpose)
  - Mark ProtoVessel as non-loadable somehow
  - Let it load but make its parts invisible (hacky)
- **This is the hardest edge case.** ProtoVessel gives us free map integration but creates a dual-representation problem: ghost mesh (visual) vs. ProtoVessel (map). They must never both be visible as physical objects.
- **Possible solution:** Only create ProtoVessel for ghosts in the Beyond rendering zone (>120 km). When ghost enters Visual zone, destroy ProtoVessel and rely on mesh-only. When ghost exits to Beyond, recreate ProtoVessel. This naturally separates the two representations by distance.

---

### Scenario L: Fast-forward past ghost spawn times

**Setup:** Player is at KSC, uses fast-forward to jump past several ghost chain spawn UTs.

**Play session:**
1. Fast-forward advances UT past chain spawn times
2. Ghost ProtoVessels should be removed as their chains resolve
3. Real vessels spawn (or are queued via deferred spawn)
4. Tracking station shows real vessels where ghosts used to be

**What the player expects:** Clean transitions during fast-forward, same as time warp.

**Edge cases surfaced:**
- **Instant UT jump:** Fast-forward is now an instant UT jump (v0.5). Multiple ghost chains may resolve simultaneously. Batch cleanup needed.
- **Deferred spawn queue:** Some spawns may be deferred. Ghost ProtoVessel should stay until real vessel actually spawns (not just until spawn UT).

---

## Comprehensive Edge Case Registry

Consolidating all edge cases from scenarios above:

### Lifecycle
1. **Ghost → real vessel transition:** PID changes (ghost has regenerated PID, real vessel uses preserved PID). Target reference must transfer.
2. **Brief double-existence:** Frame(s) between ghost ProtoVessel removal and real vessel creation. No duplicate map icons.
3. **Rewind cleanup:** All ghost ProtoVessels removed on rewind. No orphans.
4. **Fast-forward batch resolution:** Multiple chains resolve simultaneously. Batch ProtoVessel cleanup.
5. **Scene transitions:** Ghost ProtoVessels survive KSC ↔ Flight ↔ Tracking Station transitions (they're in flightState).
6. **Deferred spawn:** Ghost ProtoVessel persists until real vessel actually spawns, not just until spawn UT.

### Dual Representation (mesh + ProtoVessel)
7. **Visual zone entry:** Ghost mesh becomes visible (< 120 km). ProtoVessel must not also load as a physical vessel. Solution: destroy ProtoVessel in Visual/Physics zones, recreate in Beyond zone.
8. **Visual duplication:** Ghost mesh + ProtoVessel icon both visible in map view. Need to suppress one. When ghost mesh exists, suppress ProtoVessel map icon? Or vice versa?
9. **Position divergence:** Ghost mesh positioned by GhostPlaybackEngine (trajectory points), ProtoVessel positioned by OrbitDriver (Keplerian). They may diverge during atmospheric/suborbital segments.

### Orbital Data
10. **No orbital data:** Surface-landed ghost, destroyed ghost, or corrupted recording. Graceful degradation — no ProtoVessel, no orbit line.
11. **Multi-segment orbits:** Recording traverses multiple orbital segments (LKO → transfer → Mun orbit). ProtoVessel orbit must be updated at segment boundaries.
12. **SOI transitions:** Ghost crosses SOI boundary. ProtoVessel reference body must change.
13. **Anchor-relative positioning:** Looped ghosts positioned relative to an anchor vessel. Their "orbit" isn't absolute.
14. **Orbit accuracy during atmospheric/sub-orbital segments:** Terminal orbit stored is the last on-rails orbit. During atmospheric segments, the Keplerian orbit is meaningless.

### Looped Recordings
15. **Overlap ghosts:** Multiple simultaneous instances from negative loop interval. Only primary cycle gets ProtoVessel?
16. **Loop phase jump:** Ghost position resets to recording start on each cycle. ProtoVessel orbit must be updated.
17. **Anchor vessel not loaded:** Loop anchor vessel may be unloaded or in a different SOI.

### Performance & Clutter
18. **Many ghost ProtoVessels:** 30+ additional OrbitDrivers. Should be manageable (KSP handles hundreds of debris).
19. **Tracking station clutter:** Need UI filtering for ghost entries.
20. **Map view clutter:** Ghost orbit lines need visual differentiation (color, opacity, width).
21. **Soft cap interaction:** `Despawn` removes ProtoVessel. `SimplifyToOrbitLine` and `ReduceFidelity` keep it.

### Targeting
22. **Target persistence across ghost→real transition:** Target transfers from ghost PID to real vessel PID.
23. **Target on terminated chain:** Ghost was targeted, chain terminates (vessel destroyed). Target should be cleared with notification.
24. **Target on surface ghost:** Targeting a landed ghost for a landing mission. Should work like targeting any landed vessel.

### Save/Load
25. **Ghost ProtoVessels in save file:** Should NOT be persisted to save. Reconstructed from recording data on load. If accidentally saved, must be identified and cleaned up on load.
26. **Recording data is source of truth:** ProtoVessel is a derived artifact. Recording changes (rewind, new commit) → ProtoVessel rebuilt.

### Interaction with Other Systems
27. **CommNet:** Ghost ProtoVessel might register its own CommNet node (from the single probe core part). Conflicts with `GhostCommNetRelay` which already manages ghost CommNet nodes. Solution: use a part without ModuleDataTransmitter, or disable CommNet on ghost ProtoVessel.
28. **Contracts:** Some contracts target vessel types or count vessels. Ghost ProtoVessels shouldn't be countable for contracts. VesselType or naming convention to exclude them.
29. **Science/resources:** Ghost ProtoVessel shouldn't have science data or resources that could be "recovered" for value.
