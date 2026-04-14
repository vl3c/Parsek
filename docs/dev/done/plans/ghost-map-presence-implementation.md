# Ghost Map Presence — Implementation Plan

**Design doc:** `docs/dev/done/research/ghost-map-presence-design.md`
**Branch:** `claude/ghost-orbits-trajectories-JrKkc`

---

## Work Package A: Interface Extension + GhostMapPresence Core

### A1. Extend IPlaybackTrajectory (Source/Parsek/IPlaybackTrajectory.cs)

Add 8 terminal orbit properties in a new section after `SurfacePos`:

```csharp
// === Terminal orbit (for map presence) ===
string TerminalOrbitBody { get; }
double TerminalOrbitSemiMajorAxis { get; }
double TerminalOrbitEccentricity { get; }
double TerminalOrbitInclination { get; }
double TerminalOrbitLAN { get; }
double TerminalOrbitArgumentOfPeriapsis { get; }
double TerminalOrbitMeanAnomalyAtEpoch { get; }
double TerminalOrbitEpoch { get; }
```

### A2. Implement in Recording (Source/Parsek/Recording.cs)

These fields already exist on Recording (lines 71-80). Just add the interface implementation — trivial pass-through getters. Recording already declares `IPlaybackTrajectory` in its class declaration.

### A3. Rewrite GhostMapPresence (Source/Parsek/GhostMapPresence.cs)

Transform from pure data layer (current 130 lines) to full ProtoVessel lifecycle manager. Keep existing `HasOrbitData` and `ComputeGhostDisplayInfo` methods.

**New state:**
```csharp
// PID tracking set — the canonical ghost vessel identification
internal static readonly HashSet<uint> ghostMapVesselPids = new HashSet<uint>();

// Map from chain PID → ghost Vessel for orbit updates and cleanup
private static readonly Dictionary<uint, Vessel> vesselsByChainPid = new Dictionary<uint, Vessel>();
```

**New methods:**

```csharp
/// O(1) check used by all guard code throughout the codebase.
internal static bool IsGhostMapVessel(uint persistentId)
    => ghostMapVesselPids.Contains(persistentId);

/// Create a ghost ProtoVessel for a chain with orbital data.
/// Returns the Vessel, or null if no orbit data / creation failed.
internal static Vessel CreateGhostVessel(GhostChain chain, IPlaybackTrajectory traj)
{
    // 1. Guard: HasOrbitData check
    // 2. Resolve CelestialBody from traj.TerminalOrbitBody
    // 3. Build Orbit from terminal elements
    // 4. Build part node: ProtoVessel.CreatePartNode("sensorBarometer", 0)
    // 5. Build discovery node: DiscoveryLevels.Owned, infinity lifetime
    // 6. Read VesselType from chain snapshot (fall back to Ship)
    // 7. ProtoVessel.CreateVesselNode(name, type, orbit, 0, parts, discovery)
    // 8. Set vesselSpawning=False, prst=True, cln=False (autoClean off)
    // 9. new ProtoVessel(node, game)
    // 10. PRE-REGISTER PID: ghostMapVesselPids.Add(pv.persistentId) BEFORE Load
    //     (pv.Load fires onVesselCreate — guards must see the PID already registered)
    // 11. flightState.protoVessels.Add(pv) (required for persistence layer tracking)
    // 12. pv.Load(flightState) (creates Vessel GO, OrbitDriver, MapObject, fires events)
    // 13. vesselsByChainPid[chainPid] = pv.vesselRef
    // 14. Log creation
    // NOTE: Do NOT call GameEvents.onNewVesselCreated.Fire() — ghosts are not "new vessels"
}

/// Update orbit when ghost traverses an OrbitSegment boundary.
internal static void UpdateGhostOrbit(uint chainPid, OrbitSegment segment)
{
    // 1. Look up vessel from vesselsByChainPid
    // 2. Build new Orbit from segment elements
    // 3. Update vessel.orbitDriver.orbit
    // 4. If reference body changed, update orbitDriver.celestialBody
    // 5. Call orbitDriver.updateFromParameters() to force recalculation
    // 6. Log update
}

/// Remove a single ghost vessel (chain resolved or despawned).
internal static void RemoveGhostVessel(uint chainPid, string reason)
{
    // 1. Look up vessel from vesselsByChainPid
    // 2. Capture target BEFORE Die: wasTarget = (FlightGlobals.fetch.VesselTarget?.GetVessel() == vessel)
    // 3. vessel.Die() (fires onVesselWillDestroy, clears target, destroys components)
    // 4. Remove from ghostMapVesselPids and vesselsByChainPid
    // 5. Log removal with reason
}

/// Remove all ghost vessels (rewind or scene cleanup).
internal static void RemoveAllGhostVessels(string reason)
{
    // 1. Iterate vesselsByChainPid values
    // 2. vessel.Die() each
    // 3. Clear both collections
    // 4. Log with count and reason
}

/// HasOrbitData overload accepting IPlaybackTrajectory
internal static bool HasOrbitData(IPlaybackTrajectory traj)
{
    // Same logic as existing Recording overload:
    // !string.IsNullOrEmpty(traj.TerminalOrbitBody) && traj.TerminalOrbitSemiMajorAxis > 0
}

/// Reset for testing (avoids Debug.Log crash).
internal static void ResetForTesting()
{
    ghostMapVesselPids.Clear();
    vesselsByChainPid.Clear();
}
```

**VesselType resolution:** Read `type` value from chain's vessel snapshot ConfigNode. Parse via `Enum.TryParse<VesselType>`. Fall back to `VesselType.Ship`.

**Ghost name format:** `"Ghost: " + vesselName` — simple prefix for player display. Programmatic identification always uses PID set.

---

## Work Package B: Guard Rails

### B1. Add IsGhostMapVessel guard to FlightGlobals.Vessels iteration sites

Each site needs individual evaluation. The pattern is:

```csharp
// BEFORE:
for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
{
    Vessel v = FlightGlobals.Vessels[i];
    // ... logic

// AFTER:
for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
{
    Vessel v = FlightGlobals.Vessels[i];
    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
    // ... logic
```

**Site-by-site audit:**

| File | Line(s) | Purpose | Guard? | Reason |
|------|---------|---------|--------|--------|
| `ParsekFlight.cs` | 2200-2204 | Scan for new vessels still alive | YES | Ghost PV is not a "new vessel" to record |
| `ParsekFlight.cs` | 4260-4264 | Discover spawned vessels | YES | Ghost PV is not a spawned vessel |
| `ParsekFlight.cs` | 5014-5016 | Strip old vessels pre-spawn | YES | Must not strip ghost PV |
| `ParsekFlight.cs` | 5055-5059 | Find vessel by SpawnedVesselPersistentId | NO | Uses specific PID match — ghost PID won't match a recording's SpawnedVesselPersistentId |
| `ParsekFlight.cs` | 6964 | Find vessel by PID | YES | Utility lookup — should not return ghost PV as a "real vessel" |
| `BackgroundRecorder.cs` | 318-322 | Background recording candidates | YES | Must not record ghost PV |
| `BackgroundRecorder.cs` | 390-394 | Background recording candidates | YES | Must not record ghost PV |
| `FlightRecorder.cs` | 3650-3653 | Vessel info buffer for split detection | YES | Ghost PV is not a split candidate |
| `FlightRecorder.cs` | 4835-4838 | FindVesselByPid (static utility) | YES | Widely-used utility — most impactful site to guard |
| `GhostPlaybackLogic.cs` | 456-459 | Cache vessel PIDs for duplicate detection | SKIP | Ghost PIDs should be in the set (they ARE vessels), but they'll be excluded from ghost-creation decisions downstream |
| `GhostPlaybackLogic.cs` | 1060-1062 | Flag collision check | YES | Ghost PV is not a flag |
| `SpawnCollisionDetector.cs` | 298-300 | Collision candidates | YES | Ghost PV at orbit position is a false-positive collision |
| `SpawnCollisionDetector.cs` | 368-370 | Proximity candidates | YES | Same reason |
| `TimeJumpManager.cs` | 349, 485, 576 | VesselsLoaded iterations | MAYBE | Ghost PV should never be loaded (Harmony prevents it). But add guard for safety. |
| `VesselSpawner.cs` | 324-326 | FindNearestVesselDistance | YES | Ghost PV at orbit position is a false-positive nearest vessel |
| `VesselSpawner.cs` | 529-531 | Crew enumeration | YES | Ghost PV has no crew, but skip for safety |
| `VesselSpawner.cs` | 752-754 | Find vessel by PID | Same pattern — add guard |
| `CrewReservationManager.cs` | (1 site) | Crew search | YES | Ghost PV has no crew |

### B2. Add guards to GameEvent handlers

**ParsekFlight.cs event handlers (12 handlers):**

| Handler | Line | Guard? | Reason |
|---------|------|--------|--------|
| `OnVesselWillDestroy` | 918 | YES | Ghost PV Die() fires this — must not process as a "real vessel destroyed" |
| `OnVesselSwitchComplete` | 1205 | YES | Should never switch to ghost PV |
| `OnVesselSituationChange` | 2963 | YES | Ghost PV situation changes are irrelevant |
| `OnVesselGoOnRails` | 3100 | SKIP | Ghost PV is already on rails; handler logic is safe |
| `OnVesselGoOffRails` | 3106 | YES | Ghost PV should never go off rails |
| `OnVesselLoaded` | 308 | YES | Ghost PV should never load (Harmony prevents, but defend) |
| `OnVesselUnloaded` | (paired) | SKIP | Ghost PV is always unloaded |
| `OnVesselSOIChanged` | 310 | YES | Ghost PV SOI changes handled by UpdateGhostOrbit, not regular handler |
| `OnCrewOnEva` | 304 | SKIP | Ghost PV has no crew, event can't fire for it |
| `OnCrewBoardVessel` | 305 | SKIP | Same |
| `OnPartCouple` | 311 | SKIP | Ghost PV has no docking ports |
| `OnPartUndock` | 312 | SKIP | Same |

**ParsekScenario.cs event handlers (2 handlers):**

| Handler | Line | Guard? | Reason |
|---------|------|--------|--------|
| `OnVesselRecovered` | 1592 | YES | Must not process ghost recovery as a real vessel recovery |
| `OnVesselTerminated` | 1614 | YES | Must not process ghost termination as a real vessel termination |

### B3. ParsekScenario.OnSave — strip ghost ProtoVessels

In `ParsekScenario.OnSave`, after normal save processing, strip ghost ProtoVessels from `flightState.protoVessels`:

```csharp
// Strip ghost map ProtoVessels — they are transient and reconstructed on load
if (GhostMapPresence.ghostMapVesselPids.Count > 0)
{
    var flightState = HighLogic.CurrentGame?.flightState;
    if (flightState != null)
    {
        int stripped = flightState.protoVessels.RemoveAll(
            pv => GhostMapPresence.IsGhostMapVessel(pv.persistentId));
        if (stripped > 0)
            ParsekLog.Info("Scenario",
                $"Stripped {stripped} ghost map ProtoVessel(s) from save");
    }
}
```

### B4. StripOrphanedSpawnedVessels exclusion

In `ParsekScenario.StripOrphanedSpawnedVessels`, add a ghost PID check before stripping:

```csharp
// Don't strip ghost map ProtoVessels
if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;
```

---

## Work Package C: Harmony Patches

### C1. Ghost vessel loading prevention (Patches/GhostVesselLoadPatch.cs)

New Harmony patch file. Prefix on `Vessel.Load` (the unloaded→loaded transition, not ProtoVessel.Load):

```csharp
[HarmonyPatch(typeof(Vessel), nameof(Vessel.Load))]
internal static class GhostVesselLoadPatch
{
    static bool Prefix(Vessel __instance)
    {
        if (GhostMapPresence.IsGhostMapVessel(__instance.persistentId))
        {
            ParsekLog.Verbose("GhostMap",
                $"Blocked Vessel.Load for ghost vessel '{__instance.vesselName}' pid={__instance.persistentId}");
            return false; // skip original method
        }
        return true;
    }
}
```

**CRITICAL NOTE (from review):** `Vessel.Load` may not exist or may not be the physics-range loading entry point. KSP's vessel loading when entering physics range likely goes through range checking in `Vessel.CheckVisibility` or the `VesselRanges` system, not a method called `Vessel.Load`. Candidate patch targets (try in order until one works):
1. `Vessel.Load` — if it exists and is the loading entry
2. `Vessel.GoOffRails` — transition from on-rails to physics
3. `Vessel.MakeActive` — prevent becoming active vessel
4. The `VesselPrecalculate` loading path

**In-game verification required.** Compile with logging on all candidates, fly near a stock unloaded vessel, observe which method fires. Then patch that one. For now, implement as `Vessel.GoOffRails` prefix (most likely correct — prevents physics activation without breaking on-rails state).

### C2. Tracking station actions (deferred to Phase 3)

Per design doc, Fly/Recover/Terminate interception is Phase 3. The exact method names need in-game verification of `SpaceTracking` decompilation.

---

## Work Package D: Lifecycle Wiring

### D1. Chain initialization (ParsekFlight.cs, ~line 3798)

In the `EvaluateAndApplyGhostChains` method, after building `activeChains`, create ghost ProtoVessels:

```csharp
// After: activeChains[pid] = chain;
// Create ghost map vessel for chains with orbital data
var tipRecording = RecordingStore.FindRecordingById(chain.TipRecordingId);
if (tipRecording != null && GhostMapPresence.HasOrbitData(tipRecording))
{
    GhostMapPresence.CreateGhostVessel(chain, tipRecording);
}
```

### D2. Chain resolution — spawn (ParsekFlight.cs)

Find the chain-tip spawn code path. After the real vessel spawns successfully, remove the ghost ProtoVessel:

```csharp
GhostMapPresence.RemoveGhostVessel(chainPid, "chain-tip-spawn");
```

Also handle target transfer: if the ghost was the navigation target, set the newly spawned real vessel as the new target.

### D3. Chain resolution — termination

When a chain is terminated (vessel destroyed/recovered in recording), remove ghost:

```csharp
GhostMapPresence.RemoveGhostVessel(chainPid, "chain-terminated");
```

### D4. Rewind cleanup (ParsekFlight.cs, ~line 6093)

Before or alongside `engine.DestroyAllGhosts()`:

```csharp
GhostMapPresence.RemoveAllGhostVessels("rewind");
```

### D5. Scene cleanup (ParsekFlight.cs OnDestroy, ~line 708)

In the OnDestroy cleanup path:

```csharp
GhostMapPresence.RemoveAllGhostVessels("scene-cleanup");
```

### D6. Soft cap despawn (GhostPlaybackEngine.cs or ParsekPlaybackPolicy.cs)

When `GhostSoftCapManager` despawns a ghost:

```csharp
// In the Despawn action handler:
GhostMapPresence.RemoveGhostVessel(chainPid, "soft-cap-despawn");
```

For `ReduceFidelity` and `SimplifyToOrbitLine` — do NOT remove. The orbit line stays.

### D7. Orbit segment changes (GhostPlaybackEngine.cs)

Find where the engine detects orbit segment transitions during per-frame playback. Add a call:

```csharp
GhostMapPresence.UpdateGhostOrbit(chainPid, newSegment);
```

This requires mapping from recording index (engine's key) to chain PID (GhostMapPresence's key). The mapping exists via `ParsekPlaybackPolicy` which knows both.

---

## Work Package E: Tests

### E1. GhostMapPresence unit tests (Tests/GhostMapPresenceTests.cs)

**PID tracking:**
```csharp
[Fact] IsGhostMapVessel_EmptySet_ReturnsFalse
[Fact] IsGhostMapVessel_AfterAdd_ReturnsTrue
[Fact] IsGhostMapVessel_AfterRemove_ReturnsFalse
[Fact] RemoveAllGhostVessels_ClearsBothCollections
```

**HasOrbitData (IPlaybackTrajectory overload):**
```csharp
[Fact] HasOrbitData_WithValidOrbit_ReturnsTrue
[Fact] HasOrbitData_NullBody_ReturnsFalse
[Fact] HasOrbitData_ZeroSMA_ReturnsFalse
```

**ConfigNode construction (cannot test full ProtoVessel.Load outside Unity, but can test node building):**
```csharp
[Fact] BuildGhostVesselNode_ContainsOrbitNode
[Fact] BuildGhostVesselNode_ContainsDiscoveryNode
[Fact] BuildGhostVesselNode_VesselNamePrefixed
[Fact] BuildGhostVesselNode_PersistentTrue
[Fact] BuildGhostVesselNode_VesselSpawningFalse
```

### E2. IPlaybackTrajectory extension tests

```csharp
[Fact] Recording_ImplementsTerminalOrbitProperties
```

Verify that Recording's terminal orbit fields are accessible through the interface.

### E3. Guard behavior tests

```csharp
[Fact] StripOrphanedSpawnedVessels_SkipsGhostPids
```

Test that `StripOrphanedSpawnedVessels` does not remove a ProtoVessel whose PID is in `ghostMapVesselPids`. This requires setting up the static state and calling the method.

### E4. Log assertion tests

```csharp
[Fact] CreateGhostVessel_LogsCreation
[Fact] RemoveGhostVessel_LogsRemoval
[Fact] RemoveAllGhostVessels_LogsCount
[Fact] IsGhostMapVessel_Guard_LogsBlockedLoad
```

Use `ParsekLog.TestSinkForTesting` pattern from existing tests.

---

## Implementation Order

1. **A1 + A2** — IPlaybackTrajectory extension + Recording implementation (5 min, no dependencies)
2. **A3** — GhostMapPresence rewrite (core methods, ConfigNode builder, PID tracking)
3. **C1** — Harmony patch for vessel loading prevention
4. **B1 + B2** — Guard rails audit (mechanical but thorough, biggest time investment)
5. **B3 + B4** — Save stripping + orphan exclusion
6. **D1–D7** — Lifecycle wiring (depends on A3 existing)
7. **E1–E4** — Tests (can start after A3, parallelizable)

Steps 3-5 can run in parallel once A3 is done.
