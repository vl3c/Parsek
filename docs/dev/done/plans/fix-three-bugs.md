# Fix Plan: Three Playtest Bugs (2026-04-10)

Surfaced during the 2026-04-10 playtest. Log: `Player.log` (modified 15:11).

---

## Bug A: Atmo/landed ghost map icons only appear with W (watch) button

**Symptom**: In map view, ghosts that are in atmosphere or landed (e.g., reentry capsule, landed EVA kerbal) have no icon unless the user presses the W (watch recording) button.

**Root cause**: Two independent systems conspire to hide the icon:

1. **No ProtoVessel created for Landed terminal state.** `ParsekPlaybackPolicy.HandleGhostCreated` (line 570-577) skips ProtoVessel creation for all terminal states except `Orbiting`, `Docked`, and `SubOrbital`. When `terminal == Landed`, the method returns early. No `pendingMapVessels` entry is added, so `CheckPendingMapVessels` never creates a ProtoVessel. Result: no native KSP orbit icon in tracking station or map view.

2. **Ghost mesh hidden by zone distance.** `ApplyZoneRenderingImpl` (ParsekFlight.cs:8078) computes ghost distance from the active vessel. When the ghost is in the `Beyond` zone, `shouldHideMesh = true`, and `state.ghost.SetActive(false)` at line 8147.

3. **Custom marker requires active mesh.** `DrawMapMarkers` (ParsekUI.cs:672) checks `kvp.Value.activeSelf`. When the ghost mesh is inactive (zone-hidden), the custom marker is skipped. The `activeSelf` check exists to prevent stale FloatingOrigin positions from projecting to wrong map locations (#245/#247).

4. **Watch mode exempts from zone hiding.** `ApplyZoneRenderingImpl` (line 8119-8128) has `if (isWatchedGhost && shouldHideMesh) → shouldHideMesh = false`. The watched ghost's mesh stays active, so the custom marker draws.

**Fix approach**: Compute trajectory-derived positions for map markers, independent of ghost mesh state.

### Changes

**ParsekUI.cs — `DrawMapMarkers()`**:
- When `MapView.MapIsEnabled` and the ghost mesh is NOT active, compute the ghost's world position from the recording's trajectory data instead of relying on the mesh position.
- Add a helper method `ComputeGhostWorldPosition(int recordingIndex, double currentUT)` that interpolates the trajectory point at the current UT and converts (lat, lon, alt) to world position via `body.GetWorldSurfacePosition`.
- The existing `activeSelf` check remains for flight-view markers (preserving #245/#247 fix).
- For map view: if `!kvp.Value.activeSelf`, call the helper, and if it returns a valid position, draw the marker there.

**Code sketch**:
```csharp
// In DrawMapMarkers, around line 672:
if (kvp.Value == null) continue;
bool meshActive = kvp.Value.activeSelf;

// In flight view, skip hidden ghosts (stale positions cause wrong map markers #245/#247)
if (!meshActive && !MapView.MapIsEnabled) continue;

Vector3 markerWorldPos;
if (meshActive)
{
    markerWorldPos = kvp.Value.transform.position;
}
else
{
    // Map view: compute position from trajectory data
    if (!TryComputeGhostWorldPosition(kvp.Key, Planetarium.GetUniversalTime(), out markerWorldPos))
        continue;
}
```

**New helper** (in ParsekUI or extracted into a utility):
```csharp
private Dictionary<int, int> mapMarkerCachedIndices = new Dictionary<int, int>();

private bool TryComputeGhostWorldPosition(int recordingIndex, double ut, out Vector3 worldPos)
{
    worldPos = Vector3.zero;
    var committed = RecordingStore.CommittedRecordings;
    if (recordingIndex < 0 || recordingIndex >= committed.Count) return false;
    var rec = committed[recordingIndex];
    if (rec.Points == null || rec.Points.Count == 0) return false;
    if (ut < rec.StartUT || ut > rec.EndUT) return false;

    // Use InterpolatePoints (InterpolateAtUT does not exist in TrajectoryMath).
    // Cache the waypoint index per-recording-index for sequential playback performance.
    int cachedIdx;
    if (!mapMarkerCachedIndices.TryGetValue(recordingIndex, out cachedIdx))
        cachedIdx = -1;
    TrajectoryPoint before, after;
    float t;
    bool found = TrajectoryMath.InterpolatePoints(rec.Points, ref cachedIdx, ut,
        out before, out after, out t);
    mapMarkerCachedIndices[recordingIndex] = cachedIdx;

    // InterpolatePoints returns false for UT before start or single-point recording.
    // In either case, 'before' is set to points[0] — use it as fallback.
    // Interpolate lat/lon/alt for smooth marker movement:
    double lat, lon, alt;
    if (found)
    {
        lat = before.latitude + (after.latitude - before.latitude) * t;
        lon = before.longitude + (after.longitude - before.longitude) * t;
        alt = TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t);
    }
    else
    {
        lat = before.latitude;
        lon = before.longitude;
        alt = before.altitude;
    }

    CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
    if (body == null) return false;

    worldPos = (Vector3)body.GetWorldSurfacePosition(lat, lon, alt);
    return true;
}
```

> **REVIEWER NOTE**: The original plan called `TrajectoryMath.InterpolateAtUT` which does **not exist**.
> The actual API is `TrajectoryMath.InterpolatePoints(points, ref cachedIndex, targetUT, out before, out after, out t)`.
> There is also `TrajectoryMath.BracketPointAtUT` (returns the nearest bracket point, no interpolation).
> For smooth map marker movement, use `InterpolatePoints` and lerp lat/lon/alt between the bracket points.
> The cached index dictionary should be cleared when recordings are reindexed (deletion, optimization pass).

### Edge cases

- **UT past recording end**: `InterpolatePoints` clamps `t` to 1.0 when `targetUT >= lastPoint.ut` (returns the last segment with t=1). The marker correctly stays at the final position.
- **Single-point recording**: `InterpolatePoints` returns false, `before` is set to `points[0]`. The marker draws at the single point.
- **Body change mid-recording**: The helper uses `before.bodyName` to resolve the CelestialBody. If the recording crosses an SOI boundary, the bracket point's body might not match the interpolated position. For surface recordings (the main use case for Bug A), this is not an issue because landed/atmospheric ghosts don't change SOI. For correctness, could check `before.bodyName == after.bodyName` and skip if mismatched.
- **Recording reindexing**: `mapMarkerCachedIndices` must be cleared whenever recordings are reindexed (deletion, optimization pass) to avoid stale index references.

### Tests

- Unit test: `TryComputeGhostWorldPosition` returns false for out-of-range UT, empty points, invalid body.
- Unit test: verify interpolated position is between bracket points for mid-range UT.
- In-game test: record a vessel doing reentry, exit to map view without pressing W, verify custom marker appears at the ghost's trajectory position.

### Logging

- Log when computing a trajectory-derived map marker position (rate-limited, tag `[MapMarker]`).

---

## Bug B: Flag-planting kerbal did not spawn

**Symptom**: At the end of recording playback, the capsule with the correct kerbal spawned, another kerbal spawned, the flag spawned, but the kerbal who planted the flag did not spawn.

**Log evidence**: The 2026-04-10 log shows:
- Recording #17 "Bill Kerman" (UT 121787-121817, 47 pts, terminal=Landed, snapshot=True, leaf=True) — Bill planted the flag at UT ~122987.
- KSCSpawn line 36912: `Attempting spawn for #17 "Bill Kerman"` → line 36939: `Vessel spawned for #17 "Bill Kerman" pid=484546861`
- Second KSCSpawn at line 49613: `Spawn not needed for #17 "Bill Kerman": already spawned (pid=1403590088)` — different PID, meaning re-spawned between sessions.

**Observation**: The log shows Bill WAS spawned successfully by KSCSpawn. But the user reports not seeing the kerbal. Possible causes:

### Hypothesis 1: Spawn position wrong (kerbal spawns underground/inaccessible)

Bill's flag was planted at `(0.0224, 72.7600, 1051.7)` on Kerbin (line 31906). Bill's recording has terminal=Landed. The spawn path uses the trajectory endpoint position (`useTrajectoryEndpoint = true` for EVA recordings, VesselSpawner.cs:677). If the endpoint position is correct, the kerbal should appear at the flag location.

But: the recording was finalized at UT 122989 (line 32371), while the last flush shows points up to UT 122987.4 (line 32233). The recording might not have the very final standing position — the kerbal might be between two trajectory points, and the interpolated endpoint could be slightly off.

**Investigation needed**: Check the spawned vessel's position against the trajectory endpoint. The EVA spawn uses `ClampAltitudeForLanded` (VesselSpawner.cs:631-634) which clamps to terrain altitude. If terrain altitude at the endpoint differs from recording altitude, the kerbal could spawn underground.

### Hypothesis 2: Spawned vessel destroyed by KSP physics

EVA kerbals are fragile. If spawned at even slightly wrong altitude or on a slope, KSP might kill them. The spawn collision detection (`CheckSpawnCollisions`) might also walkback to a position that's problematic.

**Investigation needed**: Search for vessel destruction events for pid=484546861 or pid=1403590088 after spawn.

### Fix approach (tentative — needs more investigation):

1. **Add post-spawn survival logging**: After `KSCSpawn` spawns a vessel, log whether the ProtoVessel still exists in `FlightGlobals.Vessels` after a few frames. Add a deferred check (10-frame delay) that verifies the spawned vessel is still alive.

2. **EVA spawn altitude safety margin**: In `VesselSpawner.ResolveSpawnPosition`, when the recording is an EVA with terminal=Landed, add a small safety margin (e.g., +0.5m) above terrain to prevent underground spawns. The existing `ClampAltitudeForLanded` uses `LandedClearanceMeters = 2.0`, but this is applied to vessel bounds, not EVA kerbal height.

3. **Log the exact spawn coordinates**: Enhance `KSCSpawn` logging to include lat/lon/alt of the spawned vessel and the source (trajectory endpoint vs snapshot).

### Tests

- Synthetic test: EVA kerbal recording with flag event, verify spawn position matches trajectory endpoint.
- In-game test: plant flag with EVA kerbal, return to KSC, verify kerbal spawns near flag.

---

## Bug C: Merge dialog for vessel that just stayed on the pad

**Symptom**: When going to KSC view, a merge dialog appears for a vessel that stayed on the launch pad doing nothing.

**Log evidence**: Line 49688-50009 shows a deferred merge dialog for `#autoLOC_501224` (Jumping Flea), which sat on the pad while the user did EVA work. The recording was for a vessel that never left the pad.

**Root cause**: `IsPadFailure` (ParsekFlight.cs:7966-7968) requires `duration < 10.0 && maxDistanceFromLaunch < 30.0`. A vessel that sits on the pad for minutes or hours while the user plays with EVA kerbals has `duration >> 10s`, so it doesn't qualify as a "pad failure" even though `maxDistanceFromLaunch ≈ 0`.

The existing pad failure check was designed for "vessel exploded on the pad in the first few seconds" — not "vessel sat idle on the pad for a long time."

**Flow**: The Jumping Flea sat on the pad (terminal=Landed, maxDist near 0). When the user exits Flight to KSC, `ShouldShowCommitApproval` returns true (terminal=Landed, destination=SpaceCenter). This triggers `ShowDeferredMergeDialog` via line 1148. The dialog correctly appears — but it shouldn't, because the recording is trivial.

**Fix approach**: Add an "idle on pad" classification alongside the existing pad failure check. If a vessel's `maxDistanceFromLaunch` is below a threshold, the recording is trivial and should be auto-discarded, regardless of duration.

### Changes

**ParsekFlight.cs**:

1. Add a new static method:
```csharp
/// <summary>
/// Returns true if the recording is idle-on-pad: the vessel never moved
/// more than a small distance from its launch position, regardless of duration.
/// </summary>
internal static bool IsIdleOnPad(double maxDistanceFromLaunch)
{
    return maxDistanceFromLaunch < 50.0; // 50m covers pad vibration + small shifts
}
```

2. Where `IsPadFailure` is used to decide auto-discard, also check `IsIdleOnPad`:
   - Standalone recording destruction handler (~line 1389-1410): also check `IsIdleOnPad`
   - Tree auto-discard in `ShowPostDestructionTreeMergeDialog` (~line 1471-1490): also check tree equivalent

3. **Scene-exit auto-discard for standalone recordings**: In `ParsekScenario`'s deferred merge dialog path (line 1144-1150), before showing the dialog, check if the pending recording is idle-on-pad. If so, auto-discard with screen message "Recording discarded — vessel idle on pad" instead of showing the dialog.

4. Add `IsTreeIdleOnPad` parallel to `IsTreePadFailure`:
```csharp
internal static bool IsTreeIdleOnPad(RecordingTree tree)
{
    if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
        return false;
    foreach (var rec in tree.Recordings.Values)
    {
        if (!IsIdleOnPad(rec.MaxDistanceFromLaunch))
            return false;
    }
    return true;
}
```

**ParsekScenario.cs**:

> **REVIEWER NOTE**: The original plan used `RecordingStore.PendingRecording` which does **not exist**.
> The correct API is `RecordingStore.Pending` (property, returns `Recording`).
> Also, there is no `PendingType` enum — the code uses `RecordingStore.HasPending` and `RecordingStore.HasPendingTree` boolean checks.

The idle-on-pad check must be inserted in `ShowDeferredMergeDialog` (line 2104), after the 60-frame wait and the EVA child recording check, but **before** the dialog is shown. This is the single convergence point for all three merge dialog paths (lines 1148, 1180, 1350):

```csharp
// In ShowDeferredMergeDialog, after the EVA child auto-commit block (~line 2144):

// Idle-on-pad auto-discard: vessel never left the pad area, recording is trivial
if (RecordingStore.HasPending && ParsekFlight.IsIdleOnPad(RecordingStore.Pending.MaxDistanceFromLaunch))
{
    ParsekLog.Info("Scenario",
        $"Idle on pad detected — auto-discarding recording " +
        $"(maxDist={RecordingStore.Pending.MaxDistanceFromLaunch.ToString("F1", CultureInfo.InvariantCulture)}m)");
    RecordingStore.DiscardPending();
    ScreenMessages.PostScreenMessage("Recording discarded — vessel idle on pad", 4f);
    mergeDialogPending = false;
    yield break;
}

if (RecordingStore.HasPendingTree && ParsekFlight.IsTreeIdleOnPad(RecordingStore.PendingTree))
{
    ParsekLog.Info("Scenario",
        $"Idle on pad detected — auto-discarding tree recording");
    RecordingStore.DiscardPendingTree();
    ScreenMessages.PostScreenMessage("Recording discarded — vessel idle on pad", 4f);
    mergeDialogPending = false;
    yield break;
}
```

Additionally, the idle-on-pad check should also be added in `ParsekFlight.CommitOrShowDialog` (line 8934) and in the destruction handler at line 1391. Both paths can receive idle-on-pad recordings when `autoMerge` is ON (the vessel wasn't destroyed, just switched away from). See "Missed path" in the reviewer notes.

### Edge cases

- **EVA kerbal walking near pad**: EVA recordings are child recordings (auto-committed, not in the merge dialog path). Won't be affected.
- **Short engine test on pad**: A vessel firing engines but not lifting off stays within 50m. This IS an idle recording — correct to auto-discard.
- **Vessel partially destroyed on pad**: `VesselDestroyed` would be true, and destruction handlers already have pad-failure auto-discard. `IsIdleOnPad` adds a second safety net.
- **Pad vehicle doing science experiments**: The vessel doesn't move but science data is generated. This is still an "idle on pad" recording — the science data is tracked by KSP's science system, not by Parsek's recording. Auto-discarding the recording is correct.
- **Rover driving away from pad**: A rover that drives 40m from the launchpad and stops would be caught by the 50m threshold. This is probably wrong — 50m is enough to be a meaningful recording. Consider 30m (matching `IsPadFailure`'s existing threshold) or at most 50m with a duration guard (e.g., `maxDist < 50 && duration < 120`). See reviewer notes.

### Tests

- Unit test: `IsIdleOnPad(0)` → true, `IsIdleOnPad(49)` → true, `IsIdleOnPad(51)` → false.
- Unit test: `IsTreeIdleOnPad` with all-idle tree → true, mixed tree → false.
- Unit test: existing `IsPadFailure` tests still pass (no regression).

### Logging

- Log when auto-discarding idle-on-pad: `[Parsek][INFO][Flight] Idle on pad detected — auto-discarding recording (maxDist=X.Xm)`.

---

## Implementation order

1. **Bug C** (idle on pad) — simplest, cleanest fix, immediate user impact
2. **Bug A** (map icons) — moderate complexity, requires trajectory interpolation in map marker path
3. **Bug B** (EVA kerbal spawn) — needs more investigation; start with enhanced logging to diagnose exact failure mode

## Open questions

- Bug B: Is the spawned kerbal being destroyed by KSP physics post-spawn? Need to check for vessel death events matching the spawned PID.
- Bug B: Should we add a "spawn survival verification" system that checks spawned vessels still exist after N frames?
- Bug A: Should we also create ProtoVessels for Landed/Recovered recordings to get native tracking station entries? (Currently only orbital recordings get tracking station presence.)

---

## Reviewer Notes

### Bug A — Critical: `InterpolateAtUT` does not exist

The plan's code sketch calls `TrajectoryMath.InterpolateAtUT(rec.Points, ut, ref cachedIdx)`. **This method does not exist in TrajectoryMath.cs.** The available interpolation methods are:

1. `InterpolatePoints(points, ref cachedIndex, targetUT, out before, out after, out t)` — returns two bracket points and interpolation factor `t`. Returns `bool` (false = UT before start or single point).
2. `BracketPointAtUT(points, ut, ref cachedIndex)` — returns `TrajectoryPoint?`, the nearest bracket point without interpolation. Simpler but produces jumpy marker movement.
3. `InterpolateAltitude(altBefore, altAfter, t)` — helper for altitude lerp.

For smooth map markers, use `InterpolatePoints` and manually lerp latitude, longitude, altitude. The corrected code sketch above shows the pattern.

**Additional concern**: The `mapMarkerCachedIndices` dictionary needs to be cleared in response to recording deletions/reindexing. Otherwise stale cached indices will point to wrong segments. Wire this to `RecordingStore.RunOptimizationPass` events or recording deletion callbacks.

### Bug A — Architecture concern: position source

The plan proposes computing trajectory-derived positions only when the ghost mesh is inactive (`!kvp.Value.activeSelf`). This is correct for the bug fix, but consider whether the trajectory-derived position should be used **always** in map view. The ghost mesh position is updated by the flight-scene positioner (which converts lat/lon/alt to world coords), but the FloatingOrigin offset that motivated the `activeSelf` check (#245/#247) affects even active ghosts — they just happen to be close enough that the position is approximately correct. For map view, the ScaledSpace projection is more forgiving, so the mesh position works in practice, but the trajectory-derived position is more principled.

### Bug A — Line number verification

- `DrawMapMarkers` starts at line 627 (confirmed). The `activeSelf` check is at line 672 (confirmed).
- `HandleGhostCreated` starts at line 556 (confirmed). Terminal state filter at lines 570-577 (confirmed).
- `CheckPendingMapVessels` at line 675 (confirmed).
- `ApplyZoneRenderingImpl` at line 8078 (confirmed). Watch mode exemption at lines 8119-8128 (confirmed).

### Bug B — Assessment agrees but needs sharper investigation plan

The plan correctly identifies two hypotheses. The log evidence shows the spawn succeeded (`Vessel spawned for #17 "Bill Kerman" pid=484546861`). The fact that a second spawn attempt (`Spawn not needed for #17`) used a different PID (1403590088 vs 484546861) is suspicious — it implies the vessel was re-registered (possible scene reload or session cross-contamination).

**Stronger investigation step**: Before implementing safety margins, search the Player.log for `pid=484546861` after line 36939 to see if there is a vessel destruction event, or check if the vessel's `lat/lon/alt` at spawn time is valid (not NaN, not underground). The fix approach of adding a post-spawn survival check (deferred 10-frame verification) is sound and should be implemented regardless since it will catch future issues.

**EVA spawn altitude**: The `ClampAltitudeForLanded` call happens in `VesselSpawner.TryWalkbackForEndOfRecordingSpawn` (line 633-634) for Landed terminals, and uses `body.TerrainAltitude(walkLat, walkLon)`. This is correct. But note that `OverrideSnapshotPosition` at line 284 updates the snapshot with the walkback result — so the spawn position should be correct. The issue is more likely post-spawn physics (terrain collision, slope bounce, or KSP destroying the EVA kerbal for being in an invalid state).

### Bug C — Threshold analysis: 50m is borderline

The plan proposes 50m for `IsIdleOnPad`. Consider:
- KSP launch pad is roughly 20m across. Pad vibration during physics settling is typically < 5m.
- `IsPadFailure` uses 30m. Using 50m for idle-on-pad means a vessel that moved 40m would be discarded as "idle" even though it traveled further than the pad failure threshold. This inconsistency is confusing.
- **Recommendation**: Use 30m (same as `IsPadFailure`). The key difference from `IsPadFailure` is removing the duration constraint, not raising the distance threshold. A vessel that sits still for 30 minutes with `maxDist < 30m` is clearly idle, regardless of the 50m vs 30m choice.

### Bug C — Missed merge dialog entry paths

The plan focuses on `ShowDeferredMergeDialog` (the coroutine at line 2104) and the destruction handler at line 1389. However, there are additional paths:

1. **`ParsekFlight.CommitOrShowDialog` (line 8934)**: Called from multiple places when auto-merge is ON. If a vessel sits on the pad and the player switches to another vessel (not destruction, not scene exit), the recording is committed immediately by `CommitOrShowDialog` without any idle check. With autoMerge ON, idle-on-pad recordings would be committed instead of discarded.

2. **Split-recorder destruction path (line 2335-2342)**: The `FallbackCommitSplitRecorder` checks `IsPadFailure` but not idle-on-pad. Same gap.

3. **`ParsekScenario` line 1314 (autoMerge ON, outside Flight)**: Auto-commits without checking idle-on-pad. If autoMerge is ON and the player goes to KSC, the recording bypasses `ShouldShowCommitApproval` entirely and is auto-committed.

**Recommendation**: Add `IsIdleOnPad` checks in all three locations, parallel to existing `IsPadFailure` checks. The `ShowDeferredMergeDialog` check catches the autoMerge-OFF + commit-approval-dialog path. The `CommitOrShowDialog` and auto-commit paths need separate guards.

### Bug C — `IsIdleOnPad` should remain pure and testable

The plan correctly proposes `internal static bool IsIdleOnPad(double maxDistanceFromLaunch)`. This is good. Keep it pure — no Recording parameter, just the distance. This matches the pattern of `IsPadFailure(duration, maxDist)`.

### Bug C — Tree idle-on-pad check

The proposed `IsTreeIdleOnPad` iterates all recordings in the tree and checks each one. This is correct. However, it uses `rec.MaxDistanceFromLaunch` directly — this is relative to each recording's own launch point, not the original launch pad. For tree recordings where a stage separates and drifts 40m, the stage's `MaxDistanceFromLaunch` is 40m from its separation point, not from the pad. Consider whether this matters — in practice, tree recordings that are all idle-on-pad will have very small `MaxDistanceFromLaunch` values across all segments.

### General — Thread safety is not a concern

All the code paths run on Unity's main thread (OnGUI, coroutines, event handlers). No thread safety issues with the proposed changes.
