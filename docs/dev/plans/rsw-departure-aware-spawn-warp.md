# RSW Departure-Aware Spawn Warp

## Problem

The Real Spawn Warp (RSW) system allows a player to warp to a nearby ghost's `EndUT` so the ghost becomes a real vessel for docking/EVA interaction. It uses epoch-shifting to freeze all vessel positions during the time jump, preserving the rendezvous geometry.

**The bug:** If the ghost is mid-recording (e.g., in a parking orbit) and will later depart to a different orbit or SOI before `EndUT`, the warp creates an impossible situation:

1. Player intercepts ghost in 100km Kerbin parking orbit
2. Ghost recording shows: parking orbit until UT=1000, then Mun transfer burn, then Mun orbit at EndUT=5000
3. Player clicks "Warp" in RSW — epoch-shift freezes positions, clock jumps to UT=5000
4. Player is still in Kerbin parking orbit (correct — epoch-shifted)
5. Ghost spawn resolves from `VesselSnapshot` lat/lon/alt — which is in **Mun orbit**
6. The vessel materializes at the Mun. The player warped expecting a rendezvous but the target vanished.

The epoch-shift preserves the *current* relative geometry, but the spawn position reflects the *recorded final state*, which can be in a completely different orbit or SOI.

## Solution: Two-Part Approach

### Part A: Departure Detection Guard

Detect when a ghost's final spawn position diverges from its current position and **replace the "Warp to Spawn" button with "Warp to Dep."** (epoch-shifted warp to departure UT).

**Detection method:** Compare the orbit segment active at `currentUT` with the orbit at `EndUT` (final segment, terminal orbit, or last segment as fallback). If any orbital parameter differs — or if the body changes — the ghost will depart before spawn time.

**Departure UT:** The `endUT` of the current orbit segment. This is the last moment the ghost is guaranteed to be in the same orbit the player intercepted.

### Part B: UI Enhancement

Transform the RSW window from a single-action ("Warp") to a context-aware display:

| Scenario | State Column | Button | Action |
|----------|-------------|--------|--------|
| Ghost stays in same orbit until spawn | — | **Warp** | Epoch-shifted time jump to `EndUT` (existing behavior) |
| Ghost will depart before spawn | "Departs in T-Xm Xs" | **Warp to Dep.** | Epoch-shifted warp to departure UT (preserves rendezvous geometry) |
| Ghost has no orbit segments | — | **Warp** | Existing behavior (trajectory-point-only recordings) |

## Detailed Design

### 1. `SelectiveSpawnUI.ComputeDepartureInfo` (new pure static method)

```csharp
internal struct DepartureInfo
{
    public bool willDepart;       // true if ghost leaves current orbit before EndUT
    public double departureUT;    // endUT of the current orbit segment (0 if !willDepart)
    public string destination;    // body name of next segment, or "maneuver" if same body
}

internal static DepartureInfo ComputeDepartureInfo(
    List<OrbitSegment> orbitSegments, double endUT,
    string terminalOrbitBody, double terminalOrbitSMA,
    double terminalOrbitEcc, double terminalOrbitInc,
    double terminalOrbitArgPe,
    TerminalState? terminalState,
    double currentUT)
```

Takes minimal data rather than a full `Recording` — enables testing without constructing complete Recording objects, and works with any `IPlaybackTrajectory` implementation.

**Logic:**
1. If `orbitSegments == null || orbitSegments.Count == 0` → `willDepart = false` (no orbit data, can't detect)
2. Find current segment: `TrajectoryMath.FindOrbitSegment(orbitSegments, currentUT)`
3. If no current segment found → `willDepart = false` (ghost is off-rails / in atmosphere — not an orbital intercept scenario)
4. Find the "final orbit" to compare against, using a resolution cascade:
   a. `TrajectoryMath.FindOrbitSegment(orbitSegments, endUT)` — segment covering EndUT
   b. If null: build from terminal orbit fields (if `terminalOrbitBody` is non-empty and `terminalOrbitSMA != 0`)
   c. If also no terminal orbit: use the **last segment** in the list (`orbitSegments[Count-1]`) — covers the common case of a recording that ends during an off-rails phase after multiple orbit changes
5. Compare current vs final using `OrbitsMatch(currentSeg, finalOrbit)`:
   - If match → `willDepart = false`
   - If no match → `willDepart = true`, `departureUT = currentSeg.endUT`
6. Special case: if `terminalState` is Landed, Splashed, or Destroyed and there's no final orbit segment covering EndUT → `willDepart = true` (ghost is currently orbiting but will land/crash)
7. Determine `destination`:
   - If final orbit has different `bodyName` → destination = final body name (e.g., "Mun")
   - If same body but different orbital elements → destination = `"maneuver"`
   - If landed/splashed terminal → destination = body name of current segment + " surface"

**Why compare current vs final (not current vs next)?** The player cares about one thing: "will this ghost still be here when it spawns?" Comparing against the final/spawn orbit answers exactly that.

**Note on "return trip" scenario:** If a ghost departs to the Mun and returns to the same Kerbin orbit, `willDepart = false`. This is correct: the spawn position at EndUT IS in the same orbit, so epoch-shifted warp will place the vessel correctly. The ghost will be invisible during the trip, but the spawned vessel will be where the player expects. Documented and tested.

### 2. `SelectiveSpawnUI.OrbitsMatch` (new pure static method)

```csharp
internal static bool OrbitsMatch(OrbitSegment a, OrbitSegment b)
```

Compares two orbit segments for functional equivalence:
- `bodyName` must match (string equality)
- `semiMajorAxis` within 0.1% relative tolerance (`|a - b| / max(|a|, |b|) < 0.001`)
- `eccentricity` within absolute tolerance 0.0001
- `inclination` within 0.01 degrees
- `argumentOfPeriapsis` within 1.0 degree — **but only when `max(a.ecc, b.ecc) > 0.01`**. For near-circular orbits (ecc < 0.01), argPe is numerically unstable and physically meaningless, so it is skipped.

**Why include argPe?** Unlike LAN and mean anomaly (which are time-dependent or phase-dependent), argPe defines where periapsis is relative to the ascending node. Two orbits with identical SMA/ecc/inc but different argPe are materially different — different altitudes at any given true anomaly. An in-plane rotation maneuver would change argPe while leaving other elements identical, creating a false negative if we skip it.

**Why skip LAN and mean anomaly?** LAN precesses over time due to J2 perturbation (even for the same orbit). Mean anomaly is purely time-dependent. Neither indicates an intentional orbit change.

**Tolerance rationale:** The tolerances are tight enough that any intentional maneuver (even a tiny correction burn) will be detected, but loose enough to handle float noise from the same orbit being re-captured after a brief off-rails physics phase.

For the special case of comparing against terminal orbit fields (not an `OrbitSegment`), build a temporary struct from the terminal orbit parameters.

### 3. `NearbySpawnCandidate` Enhancement

Add fields to `NearbySpawnCandidate`:

```csharp
public bool willDepart;        // ghost will leave current orbit before EndUT
public double departureUT;     // UT when ghost departs (0 if !willDepart)
public string destination;     // "Mun", "maneuver", etc.
```

**Note on struct size:** `NearbySpawnCandidate` is a value type. Adding `willDepart` (1 byte), `departureUT` (8 bytes), and `destination` (8 byte ref) increases size moderately. The list is tiny (typically 0-3 candidates), copied rarely (sort + cache), so the overhead is negligible. The `destination` string is a managed reference in a value type; this is acceptable for a small, short-lived collection that is never serialized or used with unmanaged buffers.

Populated during `UpdateProximityCheck` by calling `ComputeDepartureInfo`.

### 4. UI Changes in `ParsekUI.cs`

#### New column: "State"

Add a **"State"** column (width ~110px) between "In T-" and the button column. Content:

| Condition | State text | Color |
|-----------|-----------|-------|
| `!willDepart` | (empty) | — |
| `willDepart` | `"Departs T-Xm Xs"` | Yellow |
| `willDepart && departureUT <= currentUT` | `"Departing → {destination}"` | Orange |

Use `SelectiveSpawnUI.FormatCountdown(departureUT - currentUT)` for the countdown. Note: `FormatCountdown` uses `SharedSB` which is not reentrant. Since IMGUI is single-threaded and the "In T-" column call completes before the State column call (sequential within the same loop iteration), this is safe. Do not cache the returned string reference across calls.

#### Button changes

Replace the single "Warp" button logic. Widen `SpawnColW_Warp` from 50 to 85 to accommodate the longer label:

```
if (!cand.willDepart)
    → Button label: "Warp"
    → Action: flight.WarpToRecordingEnd(cand.recordingIndex)  [existing]

if (cand.willDepart)
    → Button label: "Warp to Dep."
    → Action: flight.WarpToDeparture(cand.recordingIndex, cand.departureUT)  [new]
```

Both buttons use **epoch-shifted** warp (`ExecuteJump`). "Warp to Dep." targets `departureUT` instead of `EndUT`. This preserves the rendezvous geometry — the player and ghost both stay at their current positions. The ghost is still in its current orbit at `departureUT`, and the playback engine will then show the ghost departing in real time.

### 5. `ParsekFlight.WarpToDeparture` (new method)

```csharp
internal void WarpToDeparture(int recordingIndex, double departureUT)
```

Similar to `WarpToRecordingEnd` but:
- Target UT = `departureUT` (not `rec.EndUT`)
- Uses `TimeJumpManager.ExecuteJump(departureUT, null, vesselGhoster)` — **epoch-shifted**, same as `WarpToRecordingEnd`
- Screen message: `"Warped to departure of '{vesselName}' ({delta:F0}s)"`
- Logging: `[Flight] WarpToDeparture: jumping to UT={departureUT:F1} for '{vesselName}' (delta={delta:F1}s)`

This preserves the rendezvous geometry. The ghost hasn't departed yet at `departureUT`, so the player sees the ghost still nearby. The ghost will then depart in real time from that point.

### 6. Cache Invalidation Fix

The existing `cachedSortedCandidates` cache in ParsekUI.cs only invalidates on count/sort changes. Departure fields (`willDepart`, `departureUT`) can change between proximity checks (every 1.5s) without the count changing.

**Fix:** Add a `proximityCheckGeneration` counter to `ParsekFlight` that increments on every `UpdateProximityCheck` call. Include it in `NearbySpawnCandidate` (or expose via a property). In ParsekUI, compare the generation against the cached value to detect field-level changes.

### 7. Tooltip / Screen Notification Updates

**Proximity notification:** When a departing ghost first enters RSW proximity range:
- Current: `"Nearby craft: {name}. Open the Real Spawn Control window to fast forward and interact."`
- Departing: `"Nearby craft: {name} (departs to {destination} in T-Xm). Open Real Spawn Control."`

**`FormatNextSpawnTooltip`:** Update to reflect "Warp to Departure" instead of "Warp to spawn" when the next candidate has `willDepart = true`.

## Edge Cases

### Ghost with no orbit segments (trajectory-only recording)
No departure detection possible. Show normal "Warp" button. These are typically short surface/atmospheric recordings where departure isn't a concern.

### Ghost currently off-rails (between orbit segments)
`FindOrbitSegment(currentUT)` returns null. This means the ghost is in a physics-recorded phase (burn, atmospheric flight). Can't reliably predict departure. Show normal "Warp" button. In practice, off-rails phases are short — the ghost will enter an orbit segment soon, and the next proximity check (1.5s) will detect the departure.

### Ghost in final orbit segment (current segment covers EndUT)
`OrbitsMatch` compares the segment against itself (or terminal orbit which should match). `willDepart = false`. Correct — the ghost stays in this orbit until spawn.

### Departure UT is very close (< 30 seconds)
Still show "Warp to Dep." — the player gets a few seconds of real-time observation before the ghost departs. No special handling needed.

### EndUT falls in off-rails gap (no segment covers EndUT, no terminal orbit)
Fallback: compare current segment against the **last** orbit segment in the list. If they differ, it's a departure. If they match, `willDepart = false`. This covers the common case of multi-phase recordings that end during a final burn phase (e.g., Mun capture burn).

### Landed/splashed terminal state with orbital current segment
Ghost is currently orbiting but will land before `EndUT`. No final orbit segment exists. `terminalState` is Landed/Splashed → `willDepart = true`. `destination` = current segment body name (the landing body).

### "Return trip" — ghost departs and returns to same orbit
Ghost: Kerbin orbit → Mun → Kerbin orbit (same parameters). `willDepart = false`. Correct: the spawn position IS in the same orbit, so epoch-shifted warp places the vessel correctly. The ghost will be invisible during the trip, but the spawned vessel will be where the player expects.

### Hyperbolic orbits (negative SMA)
SMA comparison uses `Math.Abs` for both values in the relative tolerance: `|a - b| / max(|a|, |b|)`. The `Math.Abs` ensures negative SMA (escape trajectories) compares correctly. Tested explicitly.

## Files Changed

| File | Change |
|------|--------|
| `SelectiveSpawnUI.cs` | Add `DepartureInfo` struct, `ComputeDepartureInfo`, `OrbitsMatch` |
| `SelectiveSpawnUI.cs` | Add fields to `NearbySpawnCandidate` |
| `ParsekFlight.cs` | Populate departure info in `UpdateProximityCheck`, add `WarpToDeparture`, add `proximityCheckGeneration` |
| `ParsekUI.cs` | Add "State" column, change button label/action based on `willDepart`, widen button column, fix cache invalidation |
| Tests | `DepartureInfoTests.cs` — `ComputeDepartureInfo` and `OrbitsMatch` unit tests |

## Test Plan

### Unit Tests (`DepartureInfoTests.cs`)

**OrbitsMatch tests:**
1. **Identical segments** → true
2. **Different body** → false
3. **Different SMA (>0.1%)** → false
4. **Different eccentricity (>0.0001)** → false
5. **Different inclination (>0.01 deg)** → false
6. **Within tolerances** → true (slightly different elements from float noise)
7. **Different LAN, same shape** → true (LAN is not compared)
8. **Different argPe, eccentric orbit** → false (argPe matters for ecc > 0.01)
9. **Different argPe, near-circular orbit** → true (argPe skipped for ecc < 0.01)
10. **Hyperbolic orbits (negative SMA), matching** → true
11. **Hyperbolic orbits, different** → false

**ComputeDepartureInfo tests:**
12. **No orbit segments** → `willDepart = false`
13. **Single segment covers full range** → `willDepart = false`
14. **Two segments, different body** → `willDepart = true`, destination = body name
15. **Two segments, same body, different SMA** → `willDepart = true`, destination = "maneuver"
16. **currentUT outside any segment** → `willDepart = false`
17. **Terminal orbit differs from current segment** → `willDepart = true`
18. **Terminal orbit matches current segment** → `willDepart = false`
19. **Landed terminal state, orbital current** → `willDepart = true`
20. **EndUT in off-rails gap, last segment differs from current** → `willDepart = true` (fallback to last segment)
21. **EndUT in off-rails gap, last segment matches current** → `willDepart = false`
22. **Return trip (current matches final, intermediate differs)** → `willDepart = false`

### In-Game Tests

- [ ] Record multi-phase mission (parking orbit → Mun transfer → Mun orbit)
- [ ] Intercept ghost during parking orbit phase — verify "Departs in T-" state and "Warp to Dep." button
- [ ] Click "Warp to Dep." — verify time advances to departure UT with epoch-shift (rendezvous preserved)
- [ ] After warp, observe ghost depart in real time
- [ ] Intercept ghost during final orbit phase — verify normal "Warp" button
- [ ] Record single-orbit mission — verify no departure info shown, normal "Warp" button
- [ ] Verify proximity notification text includes departure info
- [ ] Verify FormatNextSpawnTooltip reflects departure action

## Implementation Order

1. `OrbitsMatch` + tests (pure static, no dependencies)
2. `ComputeDepartureInfo` + tests (pure static, depends on `TrajectoryMath.FindOrbitSegment`)
3. `NearbySpawnCandidate` field additions + `UpdateProximityCheck` population
4. `ParsekFlight.WarpToDeparture`
5. UI column + button changes + cache invalidation fix
6. Notification text + tooltip updates
7. Build + run all tests
