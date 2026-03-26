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

## KSP API Investigation Areas

### Approach 1: Lightweight ProtoVessel (Recommended for investigation)

Create a minimal `ProtoVessel` per ghost with orbital data. This gives:
- Automatic tracking station entry
- Automatic map view icon (clickable `MapObject`)
- Automatic orbit line via `OrbitRenderer`
- ITargetable support (vessels implement `ITargetable`)
- All KSP native tools work (closest approach, distance readouts)

**How it works:**
```
ConfigNode vesselNode → ProtoVessel → flightState.protoVessels.Add → pv.Load
```

Parsek already does this for chain-tip spawns (`VesselSpawner.RespawnVessel`). The difference: a "map-only" ghost ProtoVessel would be:
- Minimal part tree (single part or even partless)
- Marked with a distinct vessel type (e.g., `VesselType.Unknown` or custom)
- Orbit data from recording's terminal orbital elements
- `DiscoveryLevels.Owned` for tracking station visibility

**Concerns:**
- ProtoVessel creation is "heavy" — Section 13.2 design constraint says *"ProtoVessel creation is acceptable ONLY for unloaded chain-tip spawns and tracking station entries"*. Map-only ghosts fit this exception.
- Must be carefully lifecycle-managed: created when ghost becomes active, removed when chain resolves
- PID collision risks (same fix as `RegenerateVesselIdentity`)
- Performance with many ghost ProtoVessels (soft cap already limits ghost count)

### Approach 2: Custom OrbitRenderer + MapNode

Create orbit lines and map icons without a ProtoVessel:
- Construct `Orbit` object from Keplerian elements
- Create `OrbitRenderer` or use GL line drawing
- Create custom `MapNode` for clickable icon

**Pros:** Lighter weight, no vessel system pollution
**Cons:** Much more API surface to implement, targeting won't work natively (need custom `ITargetable`), no tracking station entry without more work

### Approach 3: GL Line Drawing (Trajectories mod approach)

The KSPTrajectories mod draws trajectory predictions directly using OpenGL:
- `MapOverlay.cs` hooks into `OnPostRender` of `PlanetariumCamera`
- Uses `GLUtils.DrawPath` with `GL.Begin(GL.LINE_STRIP)`
- Creates a `Material` with a line shader
- Converts orbital positions to `ScaledSpace` coordinates
- Draws independently of KSP's orbit rendering system

**Pros:** Full control over visual style, no vessel system interaction
**Cons:** No native KSP interaction (clicking, targeting), doesn't appear in tracking station, significant rendering code to write

### Approach 4: Hybrid

Combine approaches:
- **ProtoVessel** for tracking station + targeting (Approach 1) — gives all the interaction for free
- **Custom orbit line rendering** for visual differentiation — ghost orbits drawn in a distinct style (dashed, semi-transparent, different color) overlaid on or replacing the default orbit renderer

This is likely the best approach: get full KSP integration via ProtoVessel, then customize the visual appearance.

## ITargetable Interface (for rendezvous targeting)

KSP vessels implement `ITargetable` which provides:
- `GetName()` — display name
- `GetVessel()` — owning vessel
- `GetOrbit()` — orbital data
- `GetTransform()` — world transform for position
- `GetObtVelocity()` — orbital velocity
- `GetSrfVelocity()` — surface velocity
- `GetFwdVector()` — forward direction

If using the ProtoVessel approach, `ITargetable` comes for free via `Vessel`. If using custom approach, would need to implement this interface on a ghost wrapper object and register it with `FlightGlobals.fetch.SetVesselTarget()`.

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

### Scenario Sketches (for Step 2 discussion)

**Scenario A: Rendezvous with ghost station**
Player committed a recording of building a space station in 80km Kerbin orbit. Now recording a supply mission. Switches to map view — sees station's orbit line (in ghost color). Right-clicks ghost icon → "Set as Target". Navball shows target markers. Maneuver node planning shows closest approach. Ghost station behaves identically to targeting a real vessel.

**Scenario B: Ghost constellation in tracking station**
Player committed 4 relay recordings forming a Kerbin relay constellation. Goes to Tracking Station. Sees 4 ghost relay entries with "Ghost — spawns at UT=X" status. Can click each to see its orbit. Can filter by "Ghost" vessel type.

**Scenario C: Ghost orbit during chain window**
Player reverts after recording a Mun transfer. The transfer vessel is now a ghost chain. In map view, the ghost shows its transfer orbit (from the recording's orbital data). The player plans a new mission while seeing where their committed vessel will be.

**Scenario D: Looped transport route**
Player has a looped fuel tanker recording. In map view, the tanker's orbit is visible at its current position in the loop cycle. Only one orbit line (current cycle), not all historical cycles.

**Scenario E: Surface ghost (no orbit)**
Player committed a Mun base recording. The base is landed — no orbital data. In tracking station, it appears as "Ghost — Landed on Mun". In map view, a ground marker (like a landed vessel icon) appears at the base location. No orbit line (not applicable).
