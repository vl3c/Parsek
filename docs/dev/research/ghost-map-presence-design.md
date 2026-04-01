# Ghost Map Presence — Design Document

*ProtoVessel-based tracking station entries, orbit lines, and navigation targeting for ghost vessels.*

**Status:** Implemented (Phases 1–3), pending in-game verification
**Branch:** `claude/ghost-orbits-trajectories-JrKkc`
**Prerequisites:** Investigation document + KSP API decompilation (same branch)

---

## 1. Context

Ghost vessels are committed recordings playing back as visual Unity GameObjects. They currently lack KSP system integration: no tracking station entries, no orbit lines in map view, no clickable map icons, no navigation targeting. This makes orbital mission planning with ghosts impractical.

The investigation confirmed that KSP's `MapObject.ObjectType` is a closed enum — there is no map/tracking station presence without a real `Vessel` in `FlightGlobals.Vessels`. The ProtoVessel approach is the only viable path for full integration.

**Reference documents:**
- `docs/dev/research/ghost-orbits-trajectories-investigation.md` — problem, scenarios, edge cases
- `docs/dev/research/ksp-map-presence-api-decompilation.md` — decompiled API reference
- `docs/mods-references/KSPTrajectories-architecture-analysis.md` — custom rendering reference

---

## 2. Approach

Create a lightweight ProtoVessel per ghost chain. This single action gives us:
- Tracking station entry (automatic — `FlightGlobals.Vessels` is the source)
- Map view icon with clickable `MapObject` (automatic — `Vessel.AddOrbitRenderer` creates it)
- Orbit line via `OrbitRenderer` (automatic — Keplerian propagation via `OrbitDriver.UpdateMode.UPDATE`)
- Navigation targeting (automatic — `Vessel` implements `ITargetable`)
- Closest approach, distance readouts, navball markers (automatic — all KSP targeting infrastructure works)

Cleanup is `vessel.Die()` — handles all deregistration.

The existing `GhostMapPresence.cs` pure data layer (`HasOrbitData`, `ComputeGhostDisplayInfo`) stays. The stubbed TODO methods get implemented.

---

## 3. Design Decisions

### D1. VesselType — mirror the original vessel's type

Ghost ProtoVessels use the same `VesselType` as the original recorded vessel. A ghost of a Station appears under the Stations filter. A ghost of a Relay appears under Relay.

**How:** The vessel snapshot (`_vessel.craft`) stores the original vessel type. Read it during ghost ProtoVessel creation. If not available, fall back to `VesselType.Ship`.

**Why not a single type?** Players expect ghosts to behave like the vessels they represent. A relay constellation should filter with relays. A station ghost should filter with stations. Using `Unknown` would lump ghosts with asteroids.

### D2. Dual representation — keep ProtoVessel alive, prevent loading via Harmony

The ghost ProtoVessel stays alive in all rendering zones. A Harmony prefix on the vessel loading path prevents KSP from loading the ghost ProtoVessel when the player enters physics range.

**Why not destroy/recreate at zone boundaries?** Destroying the ProtoVessel at the 120km Visual zone boundary would cause orbit line flicker in map view — the line disappears whenever the player is within 120km of any ghost. This is unacceptable for rendezvous planning (the exact scenario where orbit lines matter most).

**Implementation:** Harmony prefix on `Vessel.MakeActive` and the unloaded→loaded transition path. When `ghostMapVesselPids.Contains(vessel.persistentId)`, skip loading and log. The ghost mesh (from `GhostVisualBuilder`) provides the visual; the ProtoVessel provides the map/orbit/targeting data. They coexist at all distances.

**Position sync:** The ProtoVessel's orbit is Keplerian-propagated by `OrbitDriver`. The ghost mesh is positioned by `GhostPlaybackEngine` from trajectory points. During recorded trajectory playback (within recording time span), these may diverge slightly for atmospheric/sub-orbital segments. This is acceptable — the orbit line shows the Keplerian approximation, the mesh shows the true recorded path. Once past recording end, `GhostExtender.PropagateOrbital` uses the same Keplerian elements, so they converge.

### D3. Tracking station Fly/Recover — Harmony prefixes

Harmony prefix on `SpaceTracking.FlyVessel` (or equivalent method called when clicking Fly): if the vessel's PID is in `ghostMapVesselPids`, show a `PopupDialog` ("This is a ghost vessel — it will materialize at UT=X") and return false.

Similarly for Recover/Terminate: block and show popup.

**Why Harmony?** No KSP API to mark vessels as non-flyable/non-recoverable. Vessel state flags (`IsRecoverable`, `IsControllable`) are checked in some paths but not reliably. Harmony prefix is the only way to intercept the action before it executes.

**Fallback:** If the exact method names differ from decompilation, use `GameEvents.onVesselRecoveryRequested` for Recover and identify the Fly path via testing. The Harmony patcher (`ParsekHarmony.cs`) already supports this pattern.

### D4. Save/load — strip on save, reconstruct on load

Ghost ProtoVessels are NOT persisted to the save file. They are transient derived artifacts from recording data.

**On save (`ParsekScenario.OnSave`):** After the normal save, iterate `HighLogic.CurrentGame.flightState.protoVessels` and remove entries whose `persistentId` is in `ghostMapVesselPids`. This prevents ghost ProtoVessels from accumulating in the .sfs file.

**On load (flight scene entry):** Ghost chains are computed from recording data. Ghost ProtoVessels are created during chain initialization. This is the same time `GhostCommNetRelay` registers CommNet nodes.

**Why strip rather than persist?** Recording data is the single source of truth. Persisted ghost ProtoVessels would drift from recording data after rewinds, commits, or recording edits. Reconstruction is cheap (single ConfigNode parse + `ProtoVessel.Load`).

### D5. Overlap ghosts — primary cycle only

Only the primary (most recent) loop cycle gets a ghost ProtoVessel. Older overlap ghosts are visual-only (mesh + CommNet, no ProtoVessel).

**Why?** Multiple ProtoVessels for the same recording would clutter the tracking station with near-duplicate entries. The primary cycle is the one the player cares about for targeting. Overlap ghosts are visual echoes, not planning targets.

**Implementation:** `GhostPlaybackEngine` already tracks `loopCycleIndex`. ProtoVessel is created only for cycle 0 (primary).

### D6. Ghost identification — HashSet on GhostMapPresence

```csharp
internal static HashSet<uint> ghostMapVesselPids = new HashSet<uint>();
```

On `GhostMapPresence`. Every guard in the codebase checks this set for O(1) exclusion. The set is:
- **Added to** when a ghost ProtoVessel is created
- **Removed from** when `vessel.Die()` is called for cleanup
- **Cleared** on rewind (`ResetAllPlaybackState`) and scene transitions

Additionally, ghost vessel names are prefixed with `"Ghost: "` (e.g., `"Ghost: Station Alpha"`). This is for player-facing display only — programmatic identification always uses the PID set.

### D7. Multi-segment orbit updates

When the ghost traverses `OrbitSegment` boundaries in the recording, the ProtoVessel's orbit is updated:

```csharp
ghost.orbitDriver.orbit.UpdateFromOrbitAtUT(newOrbit, currentUT, body);
// or: replace orbit entirely and call orbitDriver.updateFromParameters()
```

**Trigger:** `GhostPlaybackEngine` already detects segment transitions during per-frame positioning. Add a callback/event (`OnOrbitSegmentChanged`) that `GhostMapPresence` subscribes to.

**SOI transitions:** When the OrbitSegment's reference body changes, update `orbitDriver.celestialBody` and `orbit.referenceBody`. This naturally changes the orbit line to the new body's reference frame.

**Past recording end:** Use terminal orbit elements (frozen). The ProtoVessel's `OrbitDriver.UpdateMode.UPDATE` handles Keplerian propagation from those elements indefinitely.

### D8. Contract exclusion — accept risk, monitor

Ghost ProtoVessels may technically satisfy vessel-counting contracts ("have N vessels in orbit of Kerbin"). This is a low-probability issue:

- Most contracts check specific conditions (crew aboard, science experiments, docking) that ghost ProtoVessels can't satisfy (single probe core, no crew, no science)
- VesselType mirroring (D1) means ghost types match real types — contracts filtering by type would count both. This is actually correct from the player's perspective: they DID put that station in orbit
- If reported as a bug, fix with a Harmony prefix on contract vessel evaluation

**Not worth pre-emptive complexity.** The guard-rail work (D7) is already substantial.

### D9. CommNet conflict — use antenna-free part

The ghost ProtoVessel's single part must NOT have `ModuleDataTransmitter`. Otherwise, KSP's CommNet system would create a CommNet node for the ProtoVessel, conflicting with the existing `GhostCommNetRelay` nodes.

**Part choice:** Use `sensorBarometer` (Barometer, stock part, no antenna, no special modules, lightweight). Alternative: any stock structural part without transmitter capability.

**Why not probe core?** Most probe cores include `ModuleDataTransmitter` (internal antenna). Using a probe core risks CommNet double-registration.

### D10. IPlaybackTrajectory extension

Terminal orbit fields are on `Recording` but not on `IPlaybackTrajectory` (the engine's interface). The ghost map presence system needs orbit data without depending on `Recording` directly.

Add to `IPlaybackTrajectory`:
```csharp
string TerminalOrbitBody { get; }
double TerminalOrbitSemiMajorAxis { get; }
double TerminalOrbitEccentricity { get; }
double TerminalOrbitInclination { get; }
double TerminalOrbitLAN { get; }
double TerminalOrbitArgumentOfPeriapsis { get; }
double TerminalOrbitMeanAnomalyAtEpoch { get; }
double TerminalOrbitEpoch { get; }
```

This maintains the mod boundary: `GhostPlaybackEngine` can create ghost ProtoVessels through the interface without knowing about `Recording`.

---

## 4. Architecture

### New/Modified Files

| File | Change |
|------|--------|
| `GhostMapPresence.cs` | Major: implement ProtoVessel creation/destruction, PID tracking set, orbit updates |
| `IPlaybackTrajectory.cs` | Add 8 terminal orbit properties |
| `Recording.cs` | Implement new IPlaybackTrajectory properties (trivial) |
| `ParsekHarmony.cs` | Register new patches |
| `Patches/GhostVesselGuardPatch.cs` | New: Harmony prefixes for vessel loading, Fly, Recover |
| `ParsekScenario.cs` | Strip ghost ProtoVessels on save |
| `ParsekFlight.cs` | Wire up GhostMapPresence lifecycle (create on chain init, destroy on chain resolve) |
| `GhostPlaybackEngine.cs` | Fire orbit segment change events |
| `GhostSoftCapManager.cs` | `Despawn` action also calls `GhostMapPresence.RemoveGhostVessel` |

### Lifecycle

```
Chain initialized (flight scene entry or new commit)
  → GhostMapPresence.CreateGhostVessel(chain, trajectory)
      → Build ConfigNode from terminal orbit + vessel type
      → new ProtoVessel(node, game)
      → pv.Load(flightState)
      → Add PID to ghostMapVesselPids
      → Log: "[GhostMap] Created ghost vessel 'Ghost: X' pid=Y body=Z"

Ghost playback advances through orbit segments
  → GhostMapPresence.UpdateGhostOrbit(pid, newOrbitSegment)
      → Update OrbitDriver orbit
      → Log: "[GhostMap] Updated orbit for pid=X segment=Y body=Z"

Chain resolves (spawn UT reached, vessel spawns)
  → GhostMapPresence.RemoveGhostVessel(pid)
      → vessel.Die()
      → Remove PID from ghostMapVesselPids
      → Log: "[GhostMap] Removed ghost vessel pid=X reason=spawn"

Target transfer (ghost was navigation target, real vessel spawns)
  → If FlightGlobals.fetch.VesselTarget?.GetVessel()?.persistentId matches ghost PID:
      → After real vessel spawns, set new vessel as target
      → Log: "[GhostMap] Transferred target from ghost pid=X to real vessel pid=Y"

Rewind
  → GhostMapPresence.RemoveAllGhostVessels()
      → Die() each tracked ghost vessel
      → Clear ghostMapVesselPids
      → Log: "[GhostMap] Cleared all ghost vessels (rewind)"

Scene transition (flight → KSC → tracking station)
  → Ghost ProtoVessels survive in flightState (they're real vessels)
  → On re-entering flight, chains re-init → recreate any missing ghost ProtoVessels
  → ParsekScenario.OnSave strips them before persistence
```

### ConfigNode Construction

```csharp
internal static Vessel CreateGhostVessel(GhostChain chain, IPlaybackTrajectory traj)
{
    if (!HasOrbitData(traj)) return null;

    CelestialBody body = FlightGlobals.Bodies.FirstOrDefault(
        b => b.name == traj.TerminalOrbitBody);
    if (body == null) return null;

    // Build orbit from terminal elements
    Orbit orbit = new Orbit(
        traj.TerminalOrbitInclination,
        traj.TerminalOrbitEccentricity,
        traj.TerminalOrbitSemiMajorAxis,
        traj.TerminalOrbitLAN,
        traj.TerminalOrbitArgumentOfPeriapsis,
        traj.TerminalOrbitMeanAnomalyAtEpoch,
        traj.TerminalOrbitEpoch,
        body);

    // Single antenna-free part
    ConfigNode partNode = ProtoVessel.CreatePartNode("sensorBarometer", 0);

    // Discovery: fully visible, infinite lifetime
    ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
        DiscoveryLevels.Owned, UntrackedObjectClass.C,
        double.PositiveInfinity, double.PositiveInfinity);

    // Create vessel node
    string name = "Ghost: " + (chain.VesselName ?? "Unknown");
    VesselType vtype = /* read from vessel snapshot, fallback Ship */;
    ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
        name, vtype, orbit, 0, new[] { partNode }, discovery);

    // Critical: prevent ground positioning and KSC cleanup
    vesselNode.SetValue("vesselSpawning", "False", true);
    vesselNode.SetValue("prst", "True", true);  // persistent

    // Create and load
    ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
    pv.Load(HighLogic.CurrentGame.flightState);

    ghostMapVesselPids.Add(pv.vesselRef.persistentId);
    vesselsByChainPid[chain.OriginalVesselPid] = pv.vesselRef;

    ParsekLog.Info(Tag, $"Created ghost vessel '{name}' pid={pv.vesselRef.persistentId} " +
        $"body={body.name} sma={traj.TerminalOrbitSemiMajorAxis:F0}");

    return pv.vesselRef;
}
```

---

## 5. Guard Rails

Ghost ProtoVessels pollute `FlightGlobals.Vessels`. Every Parsek system that iterates vessels or handles vessel events needs a guard. The canonical check is:

```csharp
if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
// or: return; for event handlers
```

### 5.1 FlightGlobals.Vessels iteration sites (59 occurrences across 8 files)

| File | Occurrences | Guard needed? |
|------|-------------|---------------|
| `ParsekFlight.cs` | 15 | Yes — vessel discovery, chain walking, spawn detection |
| `TimeJumpManager.cs` | 10 | Yes — time jump vessel enumeration |
| `VesselSpawner.cs` | 9 | Yes — spawn collision, vessel search |
| `GhostPlaybackLogic.cs` | 7 | Yes — flag detection, vessel lookup |
| `FlightRecorder.cs` | 6 | Yes — vessel caching, background candidates |
| `BackgroundRecorder.cs` | 6 | Yes — background recording candidates |
| `SpawnCollisionDetector.cs` | 5 | Yes — collision detection (ghost ProtoVessel at orbit position could false-positive) |
| `CrewReservationManager.cs` | 1 | Yes — crew search |

**Implementation:** Add `GhostMapPresence.IsGhostMapVessel(uint pid)` as a one-liner wrapping the HashSet check. Audit each site and add the guard where the loop body would malfunction on a ghost ProtoVessel.

Not every iteration needs a guard — some are safe because they check conditions ghost ProtoVessels can't satisfy (e.g., checking for specific part modules). The audit must evaluate each site individually.

### 5.2 GameEvent handlers (24 occurrences across 3 files)

| File | Events handled | Guard needed? |
|------|----------------|---------------|
| `ParsekFlight.cs` | onVesselCreate, onVesselLoaded, onVesselGoOnRails, onVesselWillDestroy, onVesselSituationChange + more | Yes — must not treat ghost ProtoVessel creation as a "new vessel appeared" |
| `ParsekScenario.cs` | onVesselCreate, onVesselWillDestroy, onVesselRecovered, onVesselTerminated + more | Yes — must not track ghost ProtoVessels in scenario state |
| `VesselSpawner.cs` | onVesselCreate, onVesselWillDestroy | Probably safe — only tracks vessels it spawned, but verify |

**Implementation:** Each handler gets an early-return guard:
```csharp
void OnVesselCreate(Vessel v)
{
    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
    // ... existing logic
}
```

### 5.3 Other systems needing guards

| System | Issue | Guard |
|--------|-------|-------|
| `ParsekScenario.StripOrphanedSpawnedVessels` | Would strip ghost ProtoVessels as "orphaned" | Check `ghostMapVesselPids` before stripping |
| `BackgroundRecorder` | Could try to record ghost ProtoVessel if loaded | Already filters by VesselType — but `sensorBarometer` is Debris? Test. Add explicit PID check. |
| `FlightRecorder` vessel cache | Could cache ghost ProtoVessel modules | PID check in cache loop |
| `SpawnCollisionDetector` | Ghost ProtoVessel at orbit position could false-positive as collision | PID check in candidate enumeration |

### 5.4 Harmony patches needed

| Patch | Target | Purpose |
|-------|--------|---------|
| `GhostVesselLoadPatch` | `Vessel.Load` or `Vessel.MakeActive` | Prevent KSP from loading ghost ProtoVessel when player enters physics range |
| `GhostVesselFlyPatch` | Tracking station Fly action | Block flying ghost vessel, show info popup |
| `GhostVesselRecoverPatch` | Tracking station Recover/Terminate action | Block recovery, show info popup |

The exact patch targets for tracking station actions need in-game verification — decompilation of `SpaceTracking` was incomplete. Identify the methods via testing.

---

## 6. Implementation Phases

### Phase 1: Core ProtoVessel creation + guards (MVP)

1. Extend `IPlaybackTrajectory` with terminal orbit properties
2. Implement `Recording` properties (trivial pass-through)
3. Implement `GhostMapPresence.CreateGhostVessel` / `RemoveGhostVessel` / `RemoveAllGhostVessels`
4. Add `ghostMapVesselPids` HashSet and `IsGhostMapVessel` check
5. Wire lifecycle: create on chain init, remove on chain resolve, clear on rewind
6. Add Harmony prefix to prevent ghost vessel loading in physics range
7. Strip ghost ProtoVessels in `ParsekScenario.OnSave`
8. Audit and add guards to all 59 vessel iteration sites and 24 event handlers
9. Tests: ProtoVessel creation/cleanup, PID tracking, guard behavior, save stripping

**Result:** Ghost orbits visible in map view, ghost entries in tracking station, targeting works. Player can plan rendezvous with ghost station.

### Phase 2: Orbit segment updates + soft cap integration

1. Add `OnOrbitSegmentChanged` event to `GhostPlaybackEngine`
2. Implement `GhostMapPresence.UpdateGhostOrbit` for segment transitions
3. Handle SOI transitions (reference body change)
4. Wire `GhostSoftCapManager.Despawn` to remove ghost ProtoVessel
5. Keep ProtoVessel alive for `ReduceFidelity` and `SimplifyToOrbitLine` cap actions
6. Tests: segment transitions, SOI changes, soft cap interaction

**Result:** Ghost orbit line tracks actual trajectory through multi-body transfers.

### Phase 3: Tracking station interception + polish

1. Add Harmony patches for Fly/Recover/Terminate in tracking station
2. Info popup showing ghost status and spawn UT
3. Target transfer: when ghost → real vessel, transfer navigation target
4. Ghost name display in tracking station info panel with chain status
5. Tests: Fly/Recover blocking, target transfer

**Result:** Complete tracking station integration. No accidental actions on ghosts.

### Phase 4 (future): Visual differentiation

Custom ghost orbit line style (different color, dashed, semi-transparent). Uses Trajectories-style ribbon mesh overlay or `OrbitRenderer` color patching. Deferred — stock orbit line is sufficient for MVP.

---

## 7. Testing Strategy

### Unit tests (no Unity)

- `GhostMapPresence.HasOrbitData` — existing, verify still passes
- `GhostMapPresence.ComputeGhostDisplayInfo` — existing, verify still passes
- `GhostMapPresence.IsGhostMapVessel` — add/remove/clear PID tracking
- `IPlaybackTrajectory` terminal orbit properties — verify Recording implements them
- Guard behavior: mock vessel with ghost PID, verify early returns

### Integration tests (synthetic recordings)

- Create a synthetic recording with terminal orbit data → verify ConfigNode is well-formed
- Round-trip: build ConfigNode → parse → verify all fields present
- Save stripping: verify ghost PIDs removed from flightState on save

### In-game verification (manual)

- Ghost orbit visible in map view for orbital recording
- Ghost appears in tracking station with correct type filter
- Ghost targetable for rendezvous (distance, closest approach displayed)
- Fly/Recover blocked with popup
- Ghost ProtoVessel not loaded when player flies within 2.5km
- Orbit updates when ghost crosses SOI boundary
- Target transfers from ghost to real vessel at spawn
- Rewind clears all ghost ProtoVessels
- 30+ ghosts: performance acceptable, tracking station scrollable
- Surface ghost: appears as landed entry (no orbit line)

---

## 8. What This Does NOT Change

- Ghost visual mesh building (`GhostVisualBuilder`) — unchanged
- Ghost playback engine (`GhostPlaybackEngine`) — minor: fires orbit segment events
- Recording data model — unchanged (terminal orbit fields already exist)
- Chain walking logic — unchanged
- CommNet relay (`GhostCommNetRelay`) — unchanged, complementary system
- Flight recorder — unchanged (guards are additive)
- Recording file format — no version bump needed
