# EVA Spawn Position Fix (#264)

## Problem

During the 2026-04-09 Butterfly Rover playtest, Valentina EVA'd from a rover and walked ~170 m away before the player committed the recording. When her spawn-at-end fired, she was placed **on top of the rover** instead of at her exact recorded lat/lon/alt. Two terminal positions were ~170 m apart (lat/lon differ by ~0.0009°/0.0012°), so the spawn should have been clearly distinct.

Tracked as `#264` in `docs/dev/todo-and-known-bugs.md`. Priority: Medium. EVA spawn accuracy is a stated design goal.

**Additional constraint from the user (not in the original todo):** the fix must also guarantee EVA kerbals do not spawn inside *any* loaded real vessel, by stepping the spawn position backward along the recorded trajectory — **subdivided with linear interpolation at fixed 1-2 m steps** — until the bounding box clears. A walkback of 10-50 m at once (trajectory-point granularity) is not acceptable.

## Root cause (verified via decompilation)

Decompiled `ProtoVessel`, `Vessel`, `KerbalEVA`, and `OrbitDriver` from `Assembly-CSharp.dll`.

`ProtoVessel.Load` sets `vesselRef.latitude/longitude/altitude` from the snapshot values and computes `vesselRef.transform.position = referenceBody.GetWorldSurfacePosition(latitude, longitude, altitude)`. **This part works** — writing the override lat/lon/alt into the snapshot correctly places the kerbal at frame 0. So the existing `OverrideSnapshotPosition` call at `VesselSpawner.cs:268` is not wrong per se; it just isn't enough on its own.

> **PR description must include** the exact decompiled source of `Vessel.LandedOrSplashed` getter, `ProtoVessel.Load` orbit-driver initialization sequence, and `OrbitDriver.updateFromParameters` — so a future reviewer can audit the claim without re-running `ilspycmd`. The plan's current verbal description is load-bearing and must be traceable to source.

The bug is one frame later. `ProtoVessel.Load` also initializes `OrbitDriver.updateMode` from `vesselRef.LandedOrSplashed`:

- `LandedOrSplashed` → `updateMode = IDLE` (2) → transform sticks, no further orbit-driven updates.
- Otherwise → `updateMode = UPDATE` (1) → on the first physics tick, `OrbitDriver.updateFromParameters` runs `vessel.SetPosition(referenceBody.position + orbit.pos - localCoM)`, reading `orbit.pos` from the snapshot's **stale ORBIT Keplerian subnode** which was captured back when the kerbal was on the parent vessel's ladder. The corrected transform gets overwritten with the parent position before the player ever sees it.

So the kerbal sticks on the rover whenever `updateSituation()` classifies the loaded EVA as FLYING / SUB_ORBITAL / ORBITING — which happens for any EVA kerbal caught with residual `srfSpeed` at commit time (mid-stride, jetpack drift, hopping between low-gravity steps). Kerbals committed while perfectly still with `LandedOrSplashed == true` would escape the bug entirely, which explains why not every EVA triggered it.

### Rejected hypothesis

`KerbalEVA.StartEVA` has an `autoGrabLadderOnStart` path that fires `st_ladder_acquire` when `currentLadderTriggers.Count > 0`. The trigger list is populated exclusively by Unity `OnTriggerEnter` callbacks on colliders tagged `"Ladder"`. At 170 m from the parent vessel no such collider overlap exists, so this path could not have snapped Valentina to the rover. Eliminated.

### Incidental finding (out of scope)

`StripEvaLadderState` (`VesselSpawner.cs:1034-1065`) writes the literal FSM state string `"idle"` when clearing ladder state. Real KerbalEVA FSM state names are `st_idle_gr` / `st_idle_fl` / `st_swim_idle` — `"idle"` is not a valid state. `StartEVA` line ~3017 does `fsm.StartFSM(loadedStateName)` in a try/catch; the unknown name throws, the catch falls through to a `SurfaceContact`-driven fallback that picks a real state. Cosmetically broken, functionally fine. Flagged as a latent cleanup in the follow-up section; **not in scope for this PR**.

## Fix direction

**Route EVA spawns through the existing `SpawnAtPosition` path.** That method (used since #171 for Orbiting/Docked terminals) rebuilds the ORBIT subnode from the endpoint's lat/lon/alt + velocity via `orbit.UpdateFromStateVectors(worldPos, velocity, body, ut)` before `ProtoVessel.Load` runs. With ORBIT coherent, `OrbitDriver.updateFromParameters` on the first physics tick reads the *correct* position from the *correct* orbit, and nothing overwrites the transform. Also works for breakup-continuous spawns for the same reason, so extend the routing there too.

**Walkback safety:** before `SpawnAtPosition` is called, check bounding-box overlap with loaded vessels (currently skipped for EVA). On overlap, walk backward along the recorded trajectory using **1.5 m linear sub-steps** until a non-overlapping position is found, or the full trajectory is exhausted. Exhaustion → `SpawnAbandoned = true` AND a new transient `Recording.WalkbackExhausted = true` (distinct from the collision-abandon case so diagnostics can distinguish the two).

### Why this covers both failure modes

1. Stale ORBIT: `SpawnAtPosition` rebuilds it. Fix applies regardless of whether the kerbal was walking, hopping, or still at commit time.
2. Proximity to another loaded vessel: walkback guarantees the chosen spawn point has clearance. Works identically for EVA kerbals and any other vessel that routes through `SpawnOrRecoverIfTooClose`.

## Code changes

### 1. New pure helper: `OverrideSituationFromTerminalState`

`Source/Parsek/VesselSpawner.cs`, added near `DetermineSituation` (around line 1672):

```csharp
internal static string OverrideSituationFromTerminalState(string sit, TerminalState? terminalState)
```

Takes the `DetermineSituation` output and the recording's terminal state. Returns the overridden situation string (or the input unchanged). Logs a single `[Spawner] SpawnAtPosition: overriding sit FLYING → X (terminal=Y)` line when overriding.

Rules (the input is always `sit == "FLYING"` because `DetermineSituation` at `VesselSpawner.cs:1672-1681` is a 4-way classifier returning only `SPLASHED | LANDED | ORBITING | FLYING` — it never emits `SUB_ORBITAL`; the snapshot-path `ComputeCorrectedSituation` handles SUB_ORBITAL separately before `SpawnAtPosition` runs):

| input sit | terminal | result |
|-----------|----------|--------|
| FLYING    | Orbiting | ORBITING |
| FLYING    | Docked   | ORBITING |
| FLYING    | Landed   | LANDED |
| FLYING    | Splashed | SPLASHED |
| anything else | —    | unchanged |

Inside `SpawnAtPosition`, replace the inline `FLYING → ORBITING` block at lines 139-148 with a single call to the new helper. Behavioral parity with #176 is preserved (regression-guarded by a new unit test).

### 2. Add optional `Quaternion? rotation = null` parameter to `SpawnAtPosition`

`Source/Parsek/VesselSpawner.cs:115`. Add an optional `Quaternion? rotation = null` parameter at the end of the parameter list (additive, revert-safe, existing callers unaffected).

Inside the method body, immediately after `ApplySituationToNode(spawnNode, sit)` at line 150:

```csharp
if (rotation.HasValue)
{
    spawnNode.SetValue("rot", KSPUtil.WriteQuaternion(rotation.Value), true);
    ParsekLog.Verbose("Spawner",
        $"SpawnAtPosition: rotation override applied (rot={rotation.Value})");
}
```

Why this is needed: the non-EVA breakup-continuous path currently writes the rotation override via `OverrideSnapshotPosition(rotation: lastPt.rotation)` at `VesselSpawner.cs:291-292`. When breakup-continuous routes through `SpawnAtPosition` (per section 3 below), that rotation override would otherwise be lost — breakup vessels would spawn in their mid-flight tumbling pose instead of their near-impact orientation. Adding the optional parameter preserves rotation parity without touching the orbital path (orbital callers pass `null`).

Callers:
- Orbital branch (current behaviour, line 329): pass `null` — no rotation override.
- EVA branch (new): pass `null` — the `StripEvaLadderState` path handles kerbal orientation separately; kerbals don't care about the `rot` field the same way multi-part vessels do.
- Breakup-continuous branch (new): pass `lastPt.rotation` — preserves the near-breakup orientation.

### 3. Route EVA + breakup-continuous through `SpawnAtPosition`

`Source/Parsek/VesselSpawner.cs` `SpawnOrRecoverIfTooClose` (line 215):

- Keep the prelude unchanged: `ResolveSpawnPosition → CheckSpawnCollisions → dead-crew guard → FindNearestVesselDistance → LogSpawnContext`.
- Keep `StripEvaLadderState` on the EVA branch (unrelated FSM safety net).
- **KEEP the `OverrideSnapshotPosition` call on the EVA path at lines 266-271** as defense-in-depth for the fallback. Reviewer caught that retiring it would silently regress #264: if `SpawnAtPosition` returns `0` (`pv.vesselRef == null`, `ProtoVessel` load failure, etc.) the code falls through to `RespawnVessel(rec.VesselSnapshot, excludeCrew)` at line 344, and without the prior `OverrideSnapshotPosition` call the snapshot still holds the stale parent-ladder lat/lon/alt — reproducing the bug. The primary fix happens inside `SpawnAtPosition` (which rebuilds ORBIT); `OverrideSnapshotPosition` becomes a belt-and-suspenders defense on the fallback. Same reasoning applies to the breakup-continuous path — also keep the existing `OverrideSnapshotPosition` call at lines 277-281 for its fallback safety.
- Widen the existing "orbital" dispatch condition at lines 324-342:

```csharp
bool routeThroughSpawnAtPosition =
    rec.TerminalStateValue == TerminalState.Orbiting
    || rec.TerminalStateValue == TerminalState.Docked
    || isEva
    || isBreakupContinuous;
```

- In that branch, call:
  ```csharp
  Quaternion? rotArg = isBreakupContinuous ? (Quaternion?)lastPt.rotation : null;
  rec.SpawnedVesselPersistentId = VesselSpawner.SpawnAtPosition(
      rec.VesselSnapshot, body, spawnLat, spawnLon, spawnAlt, velocity,
      Planetarium.GetUniversalTime(), excludeCrew,
      terminalState: rec.TerminalStateValue,
      rotation: rotArg);
  ```
- `velocity = new Vector3d(lastPt.velocity.x, lastPt.velocity.y, lastPt.velocity.z)` (same as the orbital path).
- Fallback: if `SpawnAtPosition` returns 0, fall through to `RespawnVessel` as today (preserves the existing error path). The `OverrideSnapshotPosition` call earlier in the function (kept per section above) ensures the fallback still spawns at the recorded endpoint.
- Log distinct subsystem messages (`EVA via SpawnAtPosition`, `Breakup via SpawnAtPosition`, `Orbital via SpawnAtPosition`) for grep triage.

### 4. Remove `!isEva` guard in `CheckSpawnCollisions`

`Source/Parsek/VesselSpawner.cs:427`. Delete the conditional wrapping the bounding-box overlap check. Call `SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot)` and `CheckOverlapAgainstLoadedVessels` unconditionally.

Kerbal `ComputeVesselBounds` returns a ~2.5 m cube (`DefaultPartHalfExtent = 1.25f` × 2 per `SpawnCollisionDetector.cs:31`, confirmed by reviewer). With the existing 5 m padding applied to both bounds in `BoundsOverlap`, center-to-center clear distance for a kerbal-vs-kerbal collision is ~12.25 m and kerbal-vs-generic-vessel clear distance depends on the other vessel's computed bounds. Adequate for "don't spawn inside a rover / pod". No kerbal-specific minimum bounds needed.

`CheckOverlapAgainstLoadedVessels` already filters `VesselType.EVA` via `ShouldSkipVesselType` (`SpawnCollisionDetector.cs:22-28`), so two EVA kerbals don't block each other.

Add a verbose log: `[Spawner] CheckSpawnCollisions: EVA bounds={size}` (rate-limited per index).

### 5. Add transient `Recording.WalkbackExhausted` field

`Source/Parsek/Recording.cs`. Add next to the existing `SpawnAbandoned` / `CollisionBlockCount` / `DuplicateBlockerRecovered` fields:

```csharp
/// <summary>
/// Transient runtime-only flag: set to true when `TryWalkbackForEndOfRecordingSpawn`
/// exhausts the entire recorded trajectory without finding a collision-free sub-step.
/// Distinct from `SpawnAbandoned` (which also covers KSC-exclusion abandon and
/// `MaxCollisionBlocks` abandon) so diagnostics can distinguish the two failure modes.
/// NOT serialized — reset on scene load alongside `SpawnAbandoned`.
/// </summary>
[NonSerialized]
public bool WalkbackExhausted;
```

Because the field is transient runtime-only (no `ParsekScenario.OnSave/OnLoad` touch), no format version change is needed. Reset sites: wherever `SpawnAbandoned` is reset (`ResetRecordingPlaybackFields` and similar). Find all such sites via grep and add `WalkbackExhausted = false` alongside.

### 6. Walkback helper with linear subdivision

New method in `Source/Parsek/SpawnCollisionDetector.cs`, added alongside the existing `WalkbackAlongTrajectory` (around line 291):

```csharp
/// <summary>Default walkback sub-step size in meters.</summary>
internal const float DefaultWalkbackStepMeters = 1.5f;

/// <summary>
/// Walk backward along a trajectory, subdividing each segment with linear lat/lon/alt
/// interpolation at the given step size in meters. Returns the candidate position (lat/lon/alt
/// and the corresponding world-space vector) of the first non-overlapping sub-step encountered
/// while walking outward from the last point, or (false, 0,0,0, zero) on exhaustion.
///
/// Distance between consecutive points is measured with the existing `SurfaceDistance`
/// helper (flat-Earth dx/dy approximation). At EVA scales (points ≤ tens of metres apart,
/// trajectories ≤ 1 km end-to-end) the flat approximation is accurate to well under 1% —
/// more than precise enough for step-count sizing on a 1.5 m granularity.
/// </summary>
internal static (bool found, double lat, double lon, double alt, Vector3d worldPos)
    WalkbackAlongTrajectorySubdivided(
        List<TrajectoryPoint> points,
        double bodyRadius,
        float stepMeters,
        Func<double, double, double, Vector3d> latLonAltToWorldPos,
        Func<Vector3d, bool> isOverlapping)
```

Algorithm:

1. Return `(false, 0, 0, 0, zero)` if `points` is null / empty.
2. Start from the last point (index `N-1`). First candidate = last point exactly. If `!isOverlapping(worldPosOf(lastPt))` → return immediately (shouldn't happen in practice because the caller already confirmed overlap, but it's a safe base case for unit tests).
3. For each segment `[i-1, i]` starting at `i = N-1` down to `i = 1`:
   - `dMeters = SurfaceDistance(points[i-1].lat, points[i-1].lon, points[i].lat, points[i].lon, bodyRadius)` — reuses the existing helper at `SpawnCollisionDetector.cs:226`.
   - `n = max(1, ceil(dMeters / stepMeters))`.
   - For `k = 1, 2, …, n`:
     - `t = k / n` (1.0 → points[i-1] end; we want to walk FROM points[i] TOWARD points[i-1], so this is the correct direction: t increases means moving earlier in time).
     - `lat = lerp(points[i].lat, points[i-1].lat, t)`.
     - `lon = lerp(points[i].lon, points[i-1].lon, t)`.
     - `alt = lerp(points[i].alt, points[i-1].alt, t)`.
     - `worldPos = latLonAltToWorldPos(lat, lon, alt)`.
     - If `!isOverlapping(worldPos)` → return `(true, lat, lon, alt, worldPos)`.
4. If the outer loop completes without clearing: return `(false, 0, 0, 0, zero)` (entire trajectory overlaps).
5. Verbose logging: one line per segment entered (`WalkbackSubdivided: segment [{i-1}↔{i}] d={dMeters}m n={n} stepping back`), one line per candidate that overlaps (rate-limited to avoid log spam — every 10th candidate), one info-level line when a clear position is found (`WalkbackSubdivided: cleared at segment [{i-1}↔{i}] step {k}/{n} lat={lat} lon={lon}`), and one warning on exhaustion.

Edge cases:

- Segment length `< stepMeters` → `n = 1`, `t = 1`, candidate = points[i-1]. Fine.
- Altitude changes mid-segment (EVA jumping) → linear lerp of `alt` matches linear lerp of geographic position. Acceptable.
- A segment with `d = 0` (duplicate points from the recorder) → `n = 1`, candidate = points[i-1]. Walk continues to the next segment. Fine.

**Reuse constraint:** do NOT modify the existing point-granularity `WalkbackAlongTrajectory` (used by `VesselGhoster.TryWalkbackSpawn`). Adding a new method keeps the existing chain-tip behavior and its unit tests unchanged. Follow-up cleanup (migrating `VesselGhoster` to the subdivided variant) is tracked below but not in this PR.

### 7. Walkback wrapper for end-of-recording spawns

New method in `Source/Parsek/VesselSpawner.cs`, grouped with `CheckSpawnCollisions`:

```csharp
internal static bool TryWalkbackForEndOfRecordingSpawn(
    Recording rec, int index, Bounds spawnBounds, CelestialBody body,
    out double walkLat, out double walkLon, out double walkAlt)
```

Behavior:

1. Call `SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided` with:
   - `points = rec.Points`
   - `bodyRadius = body.Radius`
   - `stepMeters = DefaultWalkbackStepMeters` (1.5 m)
   - `latLonAltToWorldPos = (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt)`
   - `isOverlapping = worldPos => { var (ov, _, _, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(worldPos, spawnBounds, 5f); return ov; }`
2. If the helper returns `found = true`:
   - For LANDED / SPLASHED terminals, pass the walkback altitude through `ClampAltitudeForLanded(alt, body.TerrainAltitude(lat, lon), index, rec.VesselName)` before returning.
   - Set the `out` parameters and return `true`.
   - Log `[Spawner] Walkback found clear position for #{index} ({vesselName}) at lat={} lon={} alt={} after {steps} sub-steps`.
3. If exhausted:
   - Set `rec.SpawnAbandoned = true`, `rec.VesselSpawned = true`, **and `rec.WalkbackExhausted = true`** (distinct from KSC-exclusion abandon so diagnostics can distinguish the two failure modes).
   - Log `[Spawner] Spawn ABANDONED for #{index} ({vesselName}): entire trajectory overlaps with loaded vessels — walkback exhausted`.
   - Set `walkLat/walkLon/walkAlt = 0` for safety.
   - Return `false`.

### 8. Integrate walkback into `CheckSpawnCollisions`

`Source/Parsek/VesselSpawner.cs:392`. Change the signature to return walkback-rewritten coordinates:

```csharp
private static (bool blocked, double spawnLat, double spawnLon, double spawnAlt, Vector3d spawnPos)
    CheckSpawnCollisions(Recording rec, int index, bool isEva,
        CelestialBody body, double spawnLat, double spawnLon, Vector3d spawnPos)
```

Flow (precedence is load-bearing — the order below must be preserved):

1. **KSC exclusion check** runs unchanged (returns `(true, …)` → blocked).
2. **Bounding-box overlap check** runs unconditionally now (no more `!isEva`). On overlap:
   - **First**: duplicate-blocker-recovery branch (#112) — unchanged. A quicksave-loaded duplicate with the same name is recovered via `ShipConstruction.RecoverVesselFromFlight`, then the overlap is re-checked. If the recovery clears the blocker, fall through to return `(false, …)` with the *original* coordinates (no walkback needed — the position was always valid, the blocker was stale).
   - **Second**: if duplicate recovery didn't apply (or didn't clear the blocker) and `rec.Points != null && rec.Points.Count > 1`:
     - Call `TryWalkbackForEndOfRecordingSpawn`.
     - On success: recompute `spawnPos = body.GetWorldSurfacePosition(walkLat, walkLon, walkAlt)`, reset `rec.CollisionBlockCount = 0`, return `(false, walkLat, walkLon, walkAlt, spawnPos)`.
     - On exhaustion: `TryWalkback…` already set `SpawnAbandoned`, `WalkbackExhausted`, `VesselSpawned`. Return `(true, spawnLat, spawnLon, spawnAlt, spawnPos)` (blocked flag = true, caller returns without spawning).
   - **Third (fallback)**: `Points.Count <= 1` — increment `CollisionBlockCount` as today, use the existing `ShouldAbandonCollisionBlockedSpawn` path. (Applies only to synthetic 1-point recordings; shouldn't fire in practice but the InjectAllRecordings test suite does produce 1-point recordings so it's not purely theoretical.)
3. Clear path → return `(false, spawnLat, spawnLon, spawnAlt, spawnPos)`.

**No pure `EvaluateCollisionAndWalkback` extraction.** The earlier plan draft proposed extracting an 8-parameter pure helper with 3 delegates for unit testability; reviewer flagged this as over-engineering. The walkback itself is already unit-testable via `WalkbackAlongTrajectorySubdivided`'s injected closures, and the duplicate-recovery + collision-block-counter layers are runtime-only concerns that don't benefit from a second pure layer. `CheckSpawnCollisions` stays as a runtime-only method that calls the pure helpers directly.

Caller update in `SpawnOrRecoverIfTooClose` at line 258:

```csharp
Vector3d spawnPos = body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt);
var collision = CheckSpawnCollisions(rec, index, isEva, body, spawnLat, spawnLon, spawnPos);
if (collision.blocked) return;
spawnLat = collision.spawnLat;
spawnLon = collision.spawnLon;
spawnAlt = collision.spawnAlt;
spawnPos = collision.spawnPos;
```

The rest of `SpawnOrRecoverIfTooClose` continues using the (possibly rewritten) coordinates.

**Decision: walkback triggers immediately on first collision.** No 5 s timeout like `VesselGhoster`. End-of-recording spawns have no running ghost to extend during a wait — the recording is already committed and the fallback is "don't spawn at all", so retrying frame-by-frame doesn't help. The existing `MaxCollisionBlocks` path remains as a secondary safety net for the `Points.Count <= 1` fallback only.

## Test plan

### 5.1 Existing unit tests that stay green unchanged

`Source/Parsek.Tests/SpawnSafetyNetTests.cs`:
- `OverrideSnapshotPosition_*` (4 tests, lines 800-846). Method still called by non-EVA Landed/Splashed path.
- `ResolveSpawnPosition_EvaVessel_UsesTrajectoryEndpoint` (line 849). Still used — `SpawnAtPosition` receives the resolved coordinates.
- `ResolveSpawnPosition_EvaLanded_FallsThroughToSplashedClamp` (line 959). Unchanged.
- `StripEvaLadderState_*` (6 tests, lines 1024-1095). Still called on EVA branch.
- `ClampAltitudeForLanded_*` (4 tests, lines 982-1017). Helper unchanged, now called from the walkback wrapper too.
- `ComputeCorrectedSituation_*`. `CorrectUnsafeSnapshotSituation` still runs unchanged in `PrepareSnapshotForSpawn`.

### 5.2 New unit tests in `SpawnSafetyNetTests.cs` (situation override helper)

```csharp
[Fact] public void OverrideSituationFromTerminalState_FlyingLanded_ReturnsLanded()
[Fact] public void OverrideSituationFromTerminalState_FlyingSplashed_ReturnsSplashed()
[Fact] public void OverrideSituationFromTerminalState_FlyingOrbiting_ReturnsOrbiting()     // regression guard for #176
[Fact] public void OverrideSituationFromTerminalState_FlyingDocked_ReturnsOrbiting()       // regression guard for #176
[Fact] public void OverrideSituationFromTerminalState_SubOrbitalLanded_ReturnsLanded()
[Fact] public void OverrideSituationFromTerminalState_LandedLanded_ReturnsLandedUnchanged()
[Fact] public void OverrideSituationFromTerminalState_FlyingNullTerminal_ReturnsUnchanged()
[Fact] public void OverrideSituationFromTerminalState_FlyingDestroyed_ReturnsUnchanged()
[Fact] public void OverrideSituationFromTerminalState_FlyingLanded_LogsOverride()          // asserts [Spawner] + "FLYING → LANDED" + "terminal=Landed"
```

### 5.3 New test file `Source/Parsek.Tests/EndOfRecordingWalkbackTests.cs`

`[Collection("Sequential")]`, `ParsekLog.TestSinkForTesting` pattern. All tests call the pure `SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided` directly with injected closures — no KSP runtime dependency.

```csharp
[Fact] public void WalkbackSubdivided_LastPointClear_ReturnsLastPoint()
    // Trajectory of 3 points, isOverlapping always false. Returns the exact last point.

[Fact] public void WalkbackSubdivided_LastPointOverlaps_FindsFirstClearSubStep()
    // 2 points 10 m apart. isOverlapping returns true within 3 m of last point.
    // Expect ~2 sub-steps (3 m / 1.5 m = 2) before clearing. Assert returned lat/lon
    // is ~3 m back from last point within 0.5 m tolerance.

[Fact] public void WalkbackSubdivided_100MeterSegment_Uses67SubSteps()
    // 2 points 100 m apart. bodyRadius = 600000 (Kerbin). stepMeters = 1.5.
    // Expect n = ceil(100 / 1.5) = 67. Force isOverlapping to count invocations and
    // return true for all. Assert call count == 67 for the first segment.

[Fact] public void WalkbackSubdivided_SegmentShorterThanStep_OneSubStep()
    // 2 points 0.8 m apart. Expect exactly 1 candidate (the earlier point).

[Fact] public void WalkbackSubdivided_SegmentExactlyStepMeters_OneSubStep()
    // Off-by-one guard (reviewer gap). 2 points exactly 1.5 m apart, stepMeters = 1.5.
    // ceil(1.5 / 1.5) = 1. Expect exactly 1 candidate at t=1.0 (points[i-1]).

[Fact] public void WalkbackSubdivided_SegmentMarginallyOverStepMeters_TwoSubSteps()
    // Adjacent off-by-one case. 2 points 1.51 m apart, stepMeters = 1.5.
    // ceil(1.51 / 1.5) = 2. Expect 2 candidates at t=0.5 and t=1.0.

[Fact] public void WalkbackSubdivided_ZeroLengthSegment_ContinuesToNextSegment()
    // 3 points: [A, A, B]. Middle segment is zero-length. Walk should advance to the
    // [A, B] segment cleanly.

[Fact] public void WalkbackSubdivided_AllOverlap_ReturnsFound_False()
    // isOverlapping always returns true. Assert found == false and exhaustion log line present.

[Fact] public void WalkbackSubdivided_EmptyTrajectory_ReturnsFound_False()
    // points = new List<TrajectoryPoint>(). Assert found == false, no crash.

[Fact] public void WalkbackSubdivided_NullTrajectory_ReturnsFound_False()
    // points = null. Assert found == false, no crash.

[Fact] public void WalkbackSubdivided_SinglePoint_UsesItIfClear()
    // 1 point, isOverlapping false. Returns that point.

[Fact] public void WalkbackSubdivided_SinglePoint_ExhaustedIfOverlapping()
    // 1 point, isOverlapping true. Returns found=false.

[Fact] public void WalkbackSubdivided_InterpolatesLatLonLinearly()
    // 2 points: (lat=0, lon=0) → (lat=10, lon=20). bodyRadius=100000. First clear position
    // should match lerp(end, start, t) for some t = k/n. Inject isOverlapping that returns
    // true for first 5 candidates then false on the 6th. Assert returned lat/lon matches
    // lerp(end, start, 6/n) within 1e-6.

[Fact] public void WalkbackSubdivided_LogsSummaryOnSuccess()
    // Assert log contains "[SpawnCollision]" + "WalkbackSubdivided: cleared at segment"
    // exactly once.
```

### 5.4 Critical regression tests in `SpawnSafetyNetTests.cs` (Butterfly Rover repro)

These cover the specific failure mode the fix targets. Reviewer flagged their absence as a gap.

```csharp
[Fact] public void DetermineSituation_AltAboveZero_LowSpeed_ReturnsFLYING()
    // Pure classifier baseline: alt=5m, speed=2, orbitalSpeed=2300 → returns "FLYING".
    // Documents why OverrideSituationFromTerminalState is needed for the walking-kerbal case.

[Fact] public void OverrideSituationFromTerminalState_WalkingKerbalRepro_ProducesLanded()
    // The Butterfly Rover repro in pure-function form.
    // sit = DetermineSituation(alt=5m, overWater=false, speed=2, orbitalSpeed=2300) → "FLYING"
    // result = OverrideSituationFromTerminalState("FLYING", TerminalState.Landed) → "LANDED"
    // Without this override chain, the EVA spawn would classify as FLYING and hit the stale-orbit bug.
    // Assert result == "LANDED" and the log line contains "overriding sit FLYING → LANDED".

[Fact] public void OverrideSituationFromTerminalState_HighVelocityLandedTerminal_LandedWins()
    // sit = DetermineSituation(alt=100m, overWater=false, speed=2500, orbitalSpeed=2300) → "ORBITING"
    // result = OverrideSituationFromTerminalState("ORBITING", TerminalState.Landed) → "ORBITING" (unchanged).
    // Documents that the override ONLY fires on FLYING input — a high-speed classifier result
    // is trusted, terminal state cannot force it back to LANDED. If the terminal says Landed
    // but the last point has orbital speed, that is a data inconsistency and we prefer the
    // faster-moving spawn situation (safer — avoids landing a moving vessel).
```

Additional unit test in `SpawnSafetyNetTests.cs` for walkback signature-change in `CheckSpawnCollisions` is deliberately **not added** — `CheckSpawnCollisions` is runtime-only (touches `FlightGlobals.Vessels` via `CheckOverlapAgainstLoadedVessels`) and the walkback behaviour is already fully covered by the pure `WalkbackAlongTrajectorySubdivided` tests in §5.3. Integration coverage is provided by the in-game tests in §5.5.

### 5.5 In-game test

New category in `Source/Parsek/InGameTests/RuntimeTests.cs`:

```csharp
[InGameTest(Category = "EvaSpawnPosition", Scene = GameScenes.FLIGHT,
    Description = "Spawned EVA kerbal lands within 5m of recorded endpoint and >=50m from parent vessel")]
public IEnumerator EvaSpawnAtRecordedEndpoint_NotOnParent()

[InGameTest(Category = "EvaSpawnPosition", Scene = GameScenes.FLIGHT,
    Description = "EVA spawn walks back when trajectory endpoint overlaps a loaded vessel")]
public IEnumerator EvaSpawnWalkbackOnOverlap()
```

Driving mechanics: **inject a synthetic recording** rather than scripting a live EVA. Real EVA scripting is fragile (requires crew boarding events, ladder triggers, physics settling). The synthetic approach builds a full-fidelity kerbal snapshot via a new helper and calls `VesselSpawner.SpawnOrRecoverIfTooClose` directly.

**Snapshot helper: parallel to `VesselSnapshotBuilder`, lives in mod assembly.**

`VesselSnapshotBuilder.EvaKerbal(...)` in `Source/Parsek.Tests/Generators/VesselSnapshotBuilder.cs` populates ~23 PART fields (`position`, `rotation`, `persistentId`, `uid`, `mid`, `flightID`, `launchID`, etc.) that a minimal hand-rolled snapshot would omit — and a minimal snapshot risks `ProtoVessel.Load` failing with missing-field exceptions. That generator lives in the test assembly (`Source/Parsek.Tests/`) which the in-game test runner in `Source/Parsek/InGameTests/` cannot reference.

Add a new helper in the mod assembly under `Source/Parsek/InGameTests/Helpers/InGameKerbalEvaSnapshot.cs`:

```csharp
internal static class InGameKerbalEvaSnapshot
{
    /// <summary>
    /// Builds a minimum-viable VESSEL ConfigNode for a `kerbalEVA` ProtoVessel that
    /// survives `ProtoVessel.Load()` without throwing on missing fields. Mirrors the
    /// field set in Source/Parsek.Tests/Generators/VesselSnapshotBuilder.EvaKerbal.
    /// </summary>
    internal static ConfigNode Build(string crewName, double lat, double lon, double alt,
        string bodyName, uint fakePersistentId)
}
```

Populates: VESSEL-level `name`, `type=EVA`, `sit=FLYING`, `landed=False`, `splashed=False`, `lat/lon/alt`, `hgt=0`, `nrm/rot`, `persistentId`, `CoM`, minimal `ORBIT` subnode (IDT=Kerbin, arbitrary elements — `SpawnAtPosition` rebuilds it), 1 PART with `name=kerbalEVA`, `pos`, `rot`, `persistentId`, `uid`, `mid`, `flightID`, `launchID`, `crew=<crewName>`, minimal `KerbalEVA` MODULE subnode (`state=st_idle_gr`, `OnALadder=False`).

**Test 1: `EvaSpawnAtRecordedEndpoint_NotOnParent`**

1. Require Flight scene with a manned active vessel. `if (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.GetCrewCount() == 0) { InGameAssert.Skip("needs manned active vessel"); yield break; }`. (There is no `InGameAssert.SkipIf` — only `InGameAssert.Skip(string)`. Reviewer caught this.)
2. Create test kerbal via `HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew)`, rename to `ParsekTestEva`.
3. Build snapshot via `InGameKerbalEvaSnapshot.Build("ParsekTestEva", lat0, lon0, alt0, body.name, fakePid)` where `lat0/lon0/alt0` are the active vessel position + tiny offset.
4. Build a `Recording` directly (no `RecordingBuilder` — that's in the test assembly too):
   - `EvaCrewName = "ParsekTestEva"`, `VesselName = "ParsekTestEva"`, `VesselPersistentId = fakePid`.
   - `VesselSnapshot = <the ConfigNode from step 3>`.
   - **20 trajectory points stepping 5 m per point due north** (`latitude += 5 / (body.Radius * π / 180)` per step in degrees). Total spread ~100 m. Each point has `bodyName = body.name`, `altitude = body.TerrainAltitude(lat, lon) + 0.5` (kerbal standing 0.5 m above terrain).
   - `TerminalStateValue = TerminalState.Landed`.
   - `TerminalBodyName = body.name`, `TerminalPosition = (final lat, final lon, final alt)`.
   - **5 m point spacing is deliberate**: with `stepMeters = 1.5`, `ceil(5 / 1.5) = 4` sub-steps per segment — so the subdivision path is actually exercised, not degenerated to point-granularity (reviewer flagged the original 1 m spacing as degenerate).
5. **Sanity pre-assertion**: compute `Bounds kerbalBounds = SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot)`. Assert `kerbalBounds.size.magnitude > 2.0f && kerbalBounds.size.magnitude < 5.0f` — confirms the snapshot's PART has a valid `pos` field and didn't fall into the 2m-cube fallback path (if it did, all subsequent distance math is wrong).
6. Call `VesselSpawner.SpawnOrRecoverIfTooClose(rec, 0)` synchronously.
7. `yield return new WaitForFixedUpdate()` twice to let `OrbitDriver.updateFromParameters` run on the first physics frame.
8. Find spawned vessel via `FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId)`.
9. `InGameAssert.IsNotNull(spawnedVessel, "SpawnedVesselPersistentId resolved to null")`.
10. Compute `expectedWorldPos = activeVessel.mainBody.GetWorldSurfacePosition(rec.Points[19].latitude, rec.Points[19].longitude, rec.Points[19].altitude)`.
11. `InGameAssert.IsLessThan((float)Vector3d.Distance(spawnedVessel.CoMD, expectedWorldPos), 5.0f, "spawned kerbal within 5m of endpoint")`.
12. `InGameAssert.IsGreaterThan((float)Vector3d.Distance(spawnedVessel.CoMD, activeVessel.CoMD), 50.0f, "spawned kerbal ≥50m from parent (endpoint was 100m out)")`.
13. `finally { if (spawnedVessel != null) ShipConstruction.RecoverVesselFromFlight(spawnedVessel.protoVessel, HighLogic.CurrentGame.flightState, true); HighLogic.CurrentGame.CrewRoster.Remove("ParsekTestEva"); }`.

**Test 2: `EvaSpawnWalkbackOnOverlap`**

Same setup and skip logic, but the trajectory ends at the active vessel rather than 100 m away:

- 20 trajectory points stepping 5 m per point due north **from 100 m away INTO the active vessel position** (trajectory converges on parent). Last point has the same lat/lon as `activeVessel`.
- `stepMeters = 1.5` walkback subdivides each 5 m segment into ~4 candidates, so the search is at the correct granularity.
- Expected walkback result: finds a clear position ~12-15 m back from the last point (depending on the active vessel's bounds + the kerbal bounds + the 5 m padding × 2).

**Assertions, computed from actual bounds:**
- Compute `Bounds parentBounds = SpawnCollisionDetector.ComputeVesselBounds(activeVessel.protoVessel.ToConfigNode())` via a live snapshot.
- Compute `Bounds kerbalBounds = SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot)`.
- `float minClearDistance = parentBounds.extents.magnitude + kerbalBounds.extents.magnitude + 10f` (5 m padding × 2).
- `InGameAssert.IsGreaterThan((float)Vector3d.Distance(spawnedVessel.CoMD, activeVessel.CoMD), minClearDistance, $"spawned kerbal ≥{minClearDistance}m from parent (bounds-derived)")`.
- `InGameAssert.IsLessThan((float)Vector3d.Distance(spawnedVessel.CoMD, activeVessel.CoMD), 40.0f, "spawned kerbal didn't walk all the way back (≤40m)")`.
- Hook `ParsekLog.TestSinkForTesting` (or inspect the in-memory log buffer) and assert a line containing `WalkbackSubdivided: cleared` was emitted during the spawn.

Both tests are tagged `Category = "EvaSpawnPosition"` for filtered runs via the test runner UI.

## Documentation updates

### `CHANGELOG.md`

Under `## 0.7.2 (unreleased)` or the current unreleased section, add to the bug-fix list (matching existing tone):

```
- **Fix EVA kerbals spawned on top of parent vessel, not at recorded endpoint (#264, in-flight spawn path only).** During the Butterfly Rover playtest, Valentina EVA'd from a rover and walked ~170 m away; on commit her ghost spawned on top of the rover instead of at the recorded final position. Root cause (verified via decompile of `ProtoVessel.Load`, `Vessel.LandedOrSplashed`, `OrbitDriver.updateFromParameters`): `OrbitDriver.updateMode` is set to `UPDATE` whenever the loaded vessel isn't strictly `LandedOrSplashed`, and on the first physics frame the driver runs `vessel.SetPosition(body.position + orbit.pos − localCoM)` using the snapshot's stale ORBIT Keplerian elements — captured when the kerbal was still on the parent ladder — overwriting the correct transform. EVA kerbals caught mid-stride with any residual `srfSpeed` triggered FLYING classification at commit time and hit this path. Fix: route EVA and breakup-continuous spawns through the existing `SpawnAtPosition` (the path added in #171 for orbital spawns), which rebuilds the ORBIT subnode from the endpoint's lat/lon/alt + last-point velocity before `ProtoVessel.Load`. Adds an `OverrideSituationFromTerminalState` pure helper extending the existing FLYING → ORBITING override (#176) to also cover FLYING → LANDED/SPLASHED. Also removes the `!isEva` guard on spawn-collision bounding-box checks and adds a trajectory walkback (`TryWalkbackForEndOfRecordingSpawn` + `SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided`) that scans backward with 1.5 m linear sub-steps along the recorded trajectory when the endpoint overlaps a loaded vessel. Walkback triggers immediately on first collision (no 5 s timeout like the chain-tip path — end-of-recording spawns have no running ghost). If the entire trajectory overlaps, `SpawnAbandoned` and a new transient `Recording.WalkbackExhausted` flag are set. **Scope note:** this fix covers the `SpawnOrRecoverIfTooClose` in-flight spawn path; the tree-leaf scene-load path (`ParsekFlight.SpawnTreeLeaves`), the KSC scene spawn path (`ParsekKSC`), and `VesselGhoster.TryWalkbackSpawn` still use the old lat/lon/alt override pattern and may partially reproduce the bug on those paths — tracked as follow-ups.
```

### `docs/dev/todo-and-known-bugs.md`

Rewrite the `#264` entry (currently lines 683-691) in place:

```
## ~~264. EVA kerbal not spawned at exact recorded final position~~

During the 2026-04-09 Butterfly Rover playtest, Valentina's EVA ended near the rover. When her ghost vessel was spawned from the recording, she was placed on top of the rover instead of at her exact recorded final position. The two terminal positions were ~170 m apart (lat/lon differ by ~0.0009°/0.0012°), so spawn should have been clearly distinct.

**Root cause:** `OverrideSnapshotPosition` correctly rewrote the snapshot's `lat`/`lon`/`alt` but not the `ORBIT` subnode — captured when the kerbal was on the parent ladder. Decompile of `ProtoVessel.Load` / `Vessel.GoOnRails` / `OrbitDriver.updateFromParameters` confirmed: `OrbitDriver.updateMode = UPDATE` whenever the loaded vessel isn't strictly `LandedOrSplashed`, and on the first physics tick the driver runs `vessel.SetPosition(body.position + orbit.pos − localCoM)` reading `orbit.pos` from the stale Keplerian elements, overwriting the corrected transform with the parent position. Any EVA kerbal committed with residual `srfSpeed` (mid-stride, jetpack drift) got FLYING classification and triggered this path. `KerbalEVA.autoGrabLadderOnStart` is trigger-collider driven and cannot fire at 170 m — hypothesis eliminated.

**Fix:** Route EVA (and breakup-continuous) spawns through `SpawnAtPosition` — the path added in #171 — which constructs a fresh `ORBIT` subnode from the endpoint's lat/lon/alt + last-point velocity before `ProtoVessel.Load` runs. Extract `OverrideSituationFromTerminalState` helper and extend to cover `FLYING → LANDED/SPLASHED` (#176 previously handled only `FLYING → ORBITING`). Remove `!isEva` skip in `CheckSpawnCollisions` so the bounding-box overlap check applies to kerbals. Add `TryWalkbackForEndOfRecordingSpawn` + `SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided` that walks backward along recorded points with 1.5 m linear sub-steps when the endpoint overlaps a loaded vessel. Walkback triggers immediately (no 5 s timeout); on exhaustion sets `SpawnAbandoned`.

**Status:** Fixed (in-flight spawn path only — see Follow-ups)

**Follow-ups (later completed):**
- `VesselGhoster` chain-tip spawns now route the normal, blocked-clear, and walkback paths through explicit resolved spawn state, and chain-tip walkback now uses the subdivided walkback helper with interpolated trajectory points.
- `ParsekFlight.SpawnTreeLeaves` now delegates to `VesselSpawner.SpawnOrRecoverIfTooClose`, so scene-load tree leaves share the same endpoint/orbit reconstruction path as the main in-flight spawner.
- `ParsekKSC.TrySpawnAtRecordingEnd` now prepares a private snapshot copy and routes EVA / breakup / orbit-sensitive KSC spawns through the same `SpawnAtPosition` / validated-respawn split as flight.
- `VesselSpawner.StripEvaLadderState` no longer writes `"idle"`; it removes the stored state value so `KerbalEVA.StartEVA` reinitializes the correct real FSM state.
```

### Audit update (2026-04-24)

- `ParsekFlight.SpawnTreeLeaves` now delegates to `VesselSpawner.SpawnOrRecoverIfTooClose`, so the scene-load tree-leaf path shares the same endpoint/orbit reconstruction logic as the main in-flight spawn path.

### Audit update (2026-04-24, later)

- `ParsekKSC.TrySpawnAtRecordingEnd` now prepares a private snapshot copy, applies the same endpoint/rotation/EVA-breakup spawn prep as flight, and routes route-sensitive KSC spawns through `SpawnAtPosition` with a validated-respawn fallback.
- `VesselGhoster` now routes chain-tip normal spawns, blocked-clear spawns, and walkback spawns through explicit resolved spawn state instead of the older "mutate lat/lon/alt then raw respawn" pattern. The walkback path now uses `SpawnCollisionDetector.WalkbackAlongTrajectorySubdividedDetailed`.
- The earlier follow-ups in this plan are now closed; the remaining note here is only the future triple-correction-layer cleanup.

### `.claude/CLAUDE.md`

No changes. No layout / build / workflow changes. The `KSP Decompilation` section already documents `ilspycmd`.

## Risk / rollback

### Blast radius

- **EVA path** — main target. Single new branch in `SpawnOrRecoverIfTooClose`; falls back to `RespawnVessel` on failure.
- **Breakup-continuous path** — now also routed through `SpawnAtPosition`. Low risk because `ResolveSpawnPosition` + `OverrideSnapshotPosition` already produced the correct lat/lon/alt; `SpawnAtPosition` additionally rebuilds a coherent orbit. Same treatment as orbital.
- **Orbital path** — inline `FLYING → ORBITING` override factored into `OverrideSituationFromTerminalState`. Behavior-identical (regression guard tests added).
- **Non-EVA Landed/Splashed path** — unchanged. Still uses `RespawnVessel` with rotation-aware `OverrideSnapshotPosition`.
- **Non-EVA fallback path (`body == null`)** — unchanged.
- **Scene-load tree leaf spawn (`SpawnTreeLeaves`)** — later aligned with the main in-flight spawn path during the 2026-04-24 audit.
- **Chain-tip spawn (`VesselGhoster`)** — later aligned during the same audit: normal/blocked/walkback paths now share explicit resolved spawn state, and walkback uses the subdivided helper with a point-granularity fallback only when body resolution is unavailable.
- **`CheckSpawnCollisions` signature change** — internal method, callers in `VesselSpawner` only, no public API impact.

### Regressions to manual-playtest

1. **EVA into orbit** — Jeb EVAs from Kerbin orbit ship, floats, commits. Now routes through `SpawnAtPosition` with velocity = last-point velocity. New orbit should ≈ terminal orbit. Also test Mun orbit.
2. **EVA walking back to parent** — kerbal walks out, returns, commits mid-return. Trajectory curves. Walkback must stop at the first clear sub-step, not overshoot.
3. **EVA near KSC launch pad** — KSC exclusion zone check still fires independently of walkback. Confirm no spurious abandon.
4. **Two simultaneous EVAs from the same ship** — each spawn lands at its own endpoint. `CheckOverlapAgainstLoadedVessels` already filters `VesselType.EVA` so kerbals don't block each other.
5. **EVA on airless body** — Mun / Minmus. `ShouldZeroVelocityAfterSpawn` still applies for LANDED/SPLASHED.
6. **Quickload during EVA walking** — #258 handles quickload mid-recording by trimming; unchanged here.
7. **Breakup-continuous landing** — splashdown with parts breaking off. Previously used `OverrideSnapshotPosition`, now routes through `SpawnAtPosition` with the new `Quaternion? rotation` parameter passing `lastPt.rotation`. Confirm no rotation regression.
8. **Jetpack-EVA velocity semantics** — `SpawnAtPosition` → `ApplyPostSpawnStabilization` zeroes velocity for LANDED/SPLASHED/PRELAUNCH (per `ShouldZeroVelocityAfterSpawn`). A kerbal committed mid-jetpack burn with non-zero residual velocity gets zeroed on spawn. This matches "at the recorded position, standing still" — usually correct — but if a user expected the ghost to inherit momentum, this is a silent behavior change. The todo repro case (Valentina walking) expects zero velocity, so this is acceptable. Document in the PR body.

### Rollback

Single commit. Revert via `git revert`. No save file format or `ParsekScenario.OnSave/OnLoad` changes. The two schema-level additions (`Recording.WalkbackExhausted` as a transient runtime-only field, `SpawnAtPosition(..., Quaternion? rotation = null)` as an optional parameter with a default) are both additive and revert-safe. The `CheckSpawnCollisions` signature change is private with exactly one caller (`SpawnOrRecoverIfTooClose` at line 258) — fully internal, no external API impact. Unit tests and in-game tests isolate the change to `VesselSpawner` + `SpawnCollisionDetector` + `Recording` + the new test files + the new `InGameKerbalEvaSnapshot` helper.

## Open questions — resolved per reviewer pass

| # | Question | Resolution |
|---|----------|------------|
| Q1 | In-game test driving method | **Synthetic injection confirmed.** Use a new `InGameKerbalEvaSnapshot.Build(...)` helper in `Source/Parsek/InGameTests/Helpers/` (parallel to the test-assembly `VesselSnapshotBuilder`). Full field coverage — no "minimal" snapshots. Reviewer flagged live-EVA scripting as too fragile and minimal snapshots as a flakiness risk. |
| Q2 | Distinct `Recording.WalkbackExhausted` field | **Yes, add it.** Transient runtime-only bool (no `ParsekScenario.OnSave/OnLoad` touch). Mirrors the `GhostChain.WalkbackExhausted` precedent at `VesselGhoster.cs:442`. Set alongside `SpawnAbandoned` in `TryWalkbackForEndOfRecordingSpawn`; reset in whatever path resets `SpawnAbandoned`. Avoids conflating the KSC-exclusion abandon and walkback-exhausted abandon in diagnostics. |
| Q3 | `Quaternion? rotation = null` parameter on `SpawnAtPosition` | **Yes, add it.** Required for breakup-continuous rotation parity (that path currently calls `OverrideSnapshotPosition(rotation: lastPt.rotation)` at `VesselSpawner.cs:291-292`, and the `SpawnAtPosition` routing would lose it otherwise). Write `spawnNode.SetValue("rot", KSPUtil.WriteQuaternion(rotation.Value), true)` after `ApplySituationToNode`. Orbital/EVA callers pass `null`; breakup-continuous callers pass `lastPt.rotation`. |
| Q4 | Walkback step size (1.5 m) | **Confirmed 1.5 m.** Satisfies the user's "1-2 m" constraint; halving doubles compute with no visible benefit for a one-shot walkback. Constant `DefaultWalkbackStepMeters` is internal so tuning later is trivial. |
| Q5 | Migrate `VesselGhoster.TryWalkbackSpawn` atomically with this PR | **Later resolved (2026-04-24 audit).** Chain-tip walkback now uses the subdivided helper with interpolated trajectory points and routes the actual spawn through the resolved-state chain-tip helper. |
| Q6 | Fix `StripEvaLadderState` `"idle"` cosmetic in this PR | **Later resolved.** `StripEvaLadderState` now removes the stored FSM state value instead of writing `"idle"`, so KSP reinitializes a valid `st_*` state. |

## Additional open questions raised during review

- **Triple-correction-layer cleanup (deferred).** `CorrectUnsafeSnapshotSituation` runs in `PrepareSnapshotForSpawn` (corrects `snapshot.sit` from the stored situation), then `SpawnAtPosition` ignores `snapshot.sit` and recomputes via `DetermineSituation`, then `OverrideSituationFromTerminalState` is a third layer. Reviewer suggests: in a future cleanup PR, replace `DetermineSituation` with "read the already-corrected `snapshot.sit` first, fall through to altitude/velocity classifier only if still `FLYING/SUB_ORBITAL`". Cleaner invariant, no new helper. **Out of scope for this PR** — flagged as a tracked cleanup idea.

- **`Vessel.LandedOrSplashed` getter semantics.** The fix chain depends on `LandedOrSplashed == true` at `ProtoVessel.Load` time when `sit=LANDED` is written in the snapshot. The decompile-based claim in §Root cause asserts this; the PR description must paste the exact decompiled getter so a reviewer can audit without re-decompiling. If `LandedOrSplashed` is evaluated from live transform altitude rather than the `sit` field, the fix still works (the correct orbit places the kerbal at the right position via `OrbitDriver.updateFromParameters` even in `UPDATE` mode), but the explanation changes. Document either way.

## Critical files for implementation

- `Source/Parsek/VesselSpawner.cs` — primary changes (helper extraction + `SpawnAtPosition` rotation param + routing + walkback wrapper + `CheckSpawnCollisions` signature change)
- `Source/Parsek/SpawnCollisionDetector.cs` — new `WalkbackAlongTrajectorySubdivided` method + `DefaultWalkbackStepMeters` constant
- `Source/Parsek/Recording.cs` — new transient `WalkbackExhausted` field
- Reset sites for `SpawnAbandoned` — grep for assignments, add `WalkbackExhausted = false` alongside
- `Source/Parsek.Tests/SpawnSafetyNetTests.cs` — new `OverrideSituationFromTerminalState` tests + Butterfly Rover regression test
- `Source/Parsek.Tests/EndOfRecordingWalkbackTests.cs` — NEW FILE — walkback subdivision tests (including off-by-one segment cases)
- `Source/Parsek/InGameTests/Helpers/InGameKerbalEvaSnapshot.cs` — NEW FILE — in-game-assembly snapshot builder (parallel to `VesselSnapshotBuilder`)
- `Source/Parsek/InGameTests/RuntimeTests.cs` — new `EvaSpawnPosition` category with 2 tests (endpoint fidelity + walkback-on-overlap)
- `CHANGELOG.md` — 0.7.2 entry (marked partial — follow-ups remain)
- `docs/dev/todo-and-known-bugs.md` — #264 rewrite; the original four follow-ups listed here were later closed during the 2026-04-24 spawn audit.
