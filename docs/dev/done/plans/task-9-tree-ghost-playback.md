# Task 9: Tree Ghost Playback

## Workflow

This task follows a multi-stage review pipeline using Opus 4.6 agents, orchestrated by the main session:

1. **Plan** -- Opus 4.6 subagent explores the codebase and writes a detailed implementation plan
2. **Plan review** -- Fresh Opus 4.6 subagent reviews the plan for correctness, completeness, and risk
3. **Orchestrator review** -- Main session reviews the plan with full project context and fixes issues
4. **Implement** -- Fresh Opus 4.6 subagent implements the plan
5. **Implementation review** -- Fresh Opus 4.6 subagent reviews the implementation and fixes issues
6. **Final review** -- Main session reviews the implementation considering the larger architectural context
7. **Commit** -- Main session commits the implementation
8. **Next task briefing** -- Main session presents the next task, explains its role and how it fits into the overall plan

---

## Plan

### 1. Overview

Task 9 makes committed recording trees play back correctly as ghosts. After Task 7, all recordings in a committed tree are added to `CommittedRecordings` via `RecordingStore.CommitTree`. The existing `UpdateTimelinePlayback` loop already iterates all committed recordings and manages ghost lifecycle per-recording. The key question is: **what additional behavior is needed for tree recordings vs standalone recordings?**

This plan traces through every aspect of the existing playback loop, identifies what already works, what is broken, and proposes targeted fixes for each gap.

**Files modified:**
- `Source/Parsek/ParsekFlight.cs` -- Playback loop changes in `UpdateTimelinePlayback`, new helpers for background-phase ghost positioning, spawn suppression for non-leaf recordings
- `Source/Parsek/GhostVisualBuilder.cs` -- No changes needed (already handles `GhostVisualSnapshot` correctly)

**No new files.**

### 2. What Already Works (No Changes Needed)

#### 2.1 Individual ghost creation/positioning per recording

Each tree recording is added to `CommittedRecordings` by `CommitTree` (RecordingStore.cs line 348). The playback loop iterates by index over all committed recordings (line 3545). For recordings that have trajectory points (`Points.Count >= 2`), ghost creation, interpolation, and positioning all work correctly -- each recording is treated as an independent entity.

**Verdict: Works out of the box.** Each tree recording with trajectory data gets its own ghost, positioned independently.

#### 2.2 Part events per recording

`ApplyPartEvents` (line 4123) is called per-recording with the recording's own `PartEvents` list and the ghost's own `GhostPlaybackState`. Each ghost has its own `partTree`, `partEventIndex`, etc. Part events apply correctly to the correct ghost.

**Verdict: Works out of the box.**

#### 2.3 Engine/RCS FX per recording

Engine and RCS effects are stored per-`GhostPlaybackState` (fields `engineInfos`, `rcsInfos`). Each ghost is built from its own snapshot, so effects are correctly scoped.

**Verdict: Works out of the box.**

#### 2.4 Resource deltas per recording

`ApplyResourceDeltas` (line 3799) is called per-recording. Each recording tracks its own `LastAppliedResourceIndex`. Note: Task 10 will migrate to tree-level resource tracking, but per-recording deltas still work for now.

**Verdict: Works out of the box for now.** Task 10 will change this.

#### 2.5 Ghost visual snapshot selection

`GhostVisualBuilder.GetGhostSnapshot` (GhostVisualBuilder.cs line 589) returns `rec.GhostVisualSnapshot ?? rec.VesselSnapshot`. Each tree recording has its own `GhostVisualSnapshot` set at branch time (`CreateSplitBranch` lines 1327-1330), so each ghost is built from the correct vessel state.

**Verdict: Works out of the box.**

#### 2.6 Orbit segment playback for active-phase recordings

`InterpolateAndPosition(ghost, points, segments, ...)` (line 5344) checks orbit segments first, falls back to point interpolation. Active-phase recordings that went through time warp have orbit segments embedded alongside trajectory points. This works correctly per-recording.

**Verdict: Works out of the box for recordings with trajectory points.**

#### 2.7 Branch point transitions (ghost appear/disappear timing)

This is a critical question: do parent ghosts naturally disappear when children appear?

**Analysis:** A parent recording's `ExplicitEndUT` is set to the branch UT (`CreateSplitBranch` line 1302). Child recordings start at the branch UT (their first trajectory point or orbit segment begins at that time). The playback loop checks `inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT`. At the branch UT:

- Parent recording: `currentUT == rec.EndUT` -- still `inRange = true` (ghost visible on its final frame)
- Child recordings: `currentUT == rec.StartUT` -- `inRange = true` (ghosts appear)

One frame later (UT > branch UT):
- Parent: `inRange = false`, `pastEnd = true` -- ghost destroyed
- Children: `inRange = true` -- ghosts continue playing

This is correct. The parent ghost and child ghosts overlap for exactly one frame at the branch UT, which is visually imperceptible. The transition is seamless.

For merge branch points (dock/board): two parent recordings end at merge UT, one child starts. Same timing logic applies.

**Verdict: Works out of the box.** Branch point transitions are handled naturally by the UT range checks. No explicit branch point transition logic is needed.

### 3. What Does NOT Work (Gaps)

#### 3.1 GAP: Background-only recordings are completely skipped

**Location:** `UpdateTimelinePlayback` line 3548: `if (rec.Points.Count < 2) continue;`

This guard skips ALL recordings with fewer than 2 trajectory points. Background-only recordings (vessels that stayed on rails for the entire recording duration) have zero trajectory points -- they only have orbit segments or a surface position. These recordings are completely invisible during playback: no ghost is created, no positioning happens, no spawn occurs.

This is the primary gap. Background vessels in a recording tree -- undocked stages in stable orbit, landed vessels, etc. -- will not appear as ghosts at all.

**Impact:** A recording tree with an orbiting stage that was never the active vessel will show no ghost for that stage. When UT passes its EndUT, the vessel will never be spawned (the entire recording is skipped).

#### 3.2 GAP: Non-leaf tree recordings will incorrectly trigger vessel spawning

**Location:** `UpdateTimelinePlayback` line 3579: `bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed && !rec.TakenControl;`

Non-leaf parent recordings in a tree have `VesselSnapshot != null` (set during initial recording or during `CreateSplitBranch`). They also have `ChildBranchPointId != null`. But `needsSpawn` does not check `ChildBranchPointId`. So when UT passes a parent recording's EndUT, the playback loop will attempt to spawn a vessel for it -- a vessel that should NOT be spawned because it was superseded by child recordings at the branch point.

The chain-related spawn suppression guards (`ChainBranch > 0`, active chain, looping chain) are all inapplicable to tree recordings (tree recordings do not have ChainId set).

**Impact:** Without a fix, each non-leaf parent recording in a tree will trigger a vessel spawn at its EndUT, creating duplicate/phantom vessels.

#### 3.3 GAP: Warp stop for tree recordings is too aggressive

**Location:** `UpdateTimelinePlayback` warp stop section (lines 3512-3540).

The warp stop logic checks each recording individually: `if (rec.VesselSnapshot != null || rec.VesselSpawned || ...)`. For a tree with N recordings, this could stop warp N separate times (once per recording entering range). The design doc says: "stop time warp once when UT enters any recording's range that has an unspawned leaf vessel."

The existing `warpStoppedForRecording` HashSet prevents stopping warp more than once per recording, but it doesn't coordinate across tree recordings. The `break` after the first warp stop (line 3537) prevents multiple stops in the same frame, but on subsequent frames, other recordings in the same tree will trigger additional warp stops.

Additionally, the warp stop also uses the `rec.Points.Count < 2` guard (line 3520), so background-only tree recordings that need vessel spawning will never trigger a warp stop.

**Impact:** The design doc wants warp to stop once when entering a tree's leaf range. Currently, warp stops once per recording (could be many times for a multi-recording tree), and never for background-only recordings.

### 4. Proposed Solutions

#### 4.1 Background-phase ghost playback (Gap 3.1)

**Approach:** Modify the early-return guard at line 3548 to allow background-only recordings through. Add a new code path for recordings that have orbit segments or surface positions but no trajectory points.

**Detailed changes in `UpdateTimelinePlayback`:**

Replace:
```csharp
if (rec.Points.Count < 2) continue;
```

With:
```csharp
bool hasPoints = rec.Points.Count >= 2;
bool hasOrbitSegments = rec.OrbitSegments != null && rec.OrbitSegments.Count > 0;
bool hasSurfacePos = rec.SurfacePos.HasValue;

// Skip recordings with no playback data at all
if (!hasPoints && !hasOrbitSegments && !hasSurfacePos) continue;
```

Then, the `inRange` calculation needs to work for recordings without points. Currently `StartUT` and `EndUT` fall back to `ExplicitStartUT`/`ExplicitEndUT` when `Points.Count == 0` (RecordingStore.cs lines 146-149). `FinalizeTreeRecordings` sets these (lines 3259-3274). So `inRange` already works.

For ghost positioning of background-only recordings, add a new branch inside the `if (inRange)` block:

```csharp
if (inRange)
{
    if (!ghostActive)
    {
        SpawnTimelineGhost(i, rec);
        state = ghostStates[i];
        // ... logging
    }

    if (hasPoints)
    {
        // Existing point-based interpolation + orbit segment fallback
        int playbackIdx = state.playbackIndex;
        InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
            ref playbackIdx, currentUT, i * 10000);
        state.playbackIndex = playbackIdx;
    }
    else if (hasOrbitSegments)
    {
        // Background orbit-only: position from orbit segment
        PositionGhostFromOrbitOnly(state.ghost, rec.OrbitSegments, currentUT, i * 10000);
    }
    else if (hasSurfacePos)
    {
        // Background surface-only: static position
        PositionGhostAtSurface(state.ghost, rec.SurfacePos.Value);
    }

    ApplyPartEvents(i, rec, currentUT, state);
    ApplyResourceDeltas(rec, currentUT);
}
```

**New helper methods:**

```csharp
/// <summary>
/// Positions a ghost using only orbit segments (no trajectory points).
/// Used for background-only recordings (vessels that stayed on rails).
/// </summary>
void PositionGhostFromOrbitOnly(GameObject ghost, List<OrbitSegment> segments, double ut, int orbitCacheBase)
{
    OrbitSegment? seg = FindOrbitSegment(segments, ut);
    if (seg.HasValue)
    {
        int segIdx = segments.IndexOf(seg.Value);
        int cacheKey = orbitCacheBase + segIdx;
        if (loggedOrbitSegments.Add(cacheKey))
            Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.Value.bodyName}, " +
                $"sma={seg.Value.semiMajorAxis:F0}, UT {seg.Value.startUT:F0}-{seg.Value.endUT:F0}");
        PositionGhostFromOrbit(ghost, seg.Value, ut, cacheKey);
    }
    else
    {
        // Gap between orbit segments -- hide ghost
        if (ghost.activeSelf) ghost.SetActive(false);
    }
}

/// <summary>
/// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
/// Used for background vessels that are landed/splashed.
/// </summary>
void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos)
{
    CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == surfPos.body);
    if (body == null)
    {
        ParsekLog.VerboseRateLimited("Flight", "position-ghost-surface-no-body",
            $"PositionGhostAtSurface: body '{surfPos.body}' not found");
        return;
    }
    if (!ghost.activeSelf) ghost.SetActive(true);

    Vector3 worldPos = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
    ghost.transform.position = worldPos;
    ghost.transform.rotation = surfPos.rotation;
}
```

**Edge case -- orbit segment gaps:** A background recording might have gaps between orbit segments (e.g., vessel went off rails and back on). During gaps, the ghost should be hidden. The `PositionGhostFromOrbitOnly` method handles this by hiding the ghost when no segment covers the current UT.

**Edge case -- mixed background recordings:** A background recording that transitions between loaded (trajectory points) and on-rails (orbit segments) within the same recording will have BOTH points and orbit segments. The existing `InterpolateAndPosition` overload (line 5344) already handles this: it checks orbit segments first, falls back to point interpolation. So mixed recordings work with the existing `hasPoints` path.

#### 4.2 Spawn suppression for non-leaf tree recordings (Gap 3.2)

**Approach:** Add a spawn suppression guard for recordings with `ChildBranchPointId != null`. These are non-leaf parent recordings that should never spawn -- their vessel was superseded by child recordings.

**Location:** After the existing chain-related spawn suppression guards (around line 3605), add:

```csharp
// Tree non-leaf recordings should never spawn -- the vessel was superseded by child recordings
if (needsSpawn && rec.ChildBranchPointId != null)
{
    needsSpawn = false;
    if (loggedGhostEnter.Add(i + 400000))
        ParsekLog.Verbose("Flight",
            $"Spawn suppressed for #{i} ({rec.VesselName}): non-leaf tree recording " +
            $"(childBranchPointId={rec.ChildBranchPointId})");
}
```

This is the simplest and most robust fix. `ChildBranchPointId` is only set on recordings that have children in the tree -- exactly the ones that should not spawn.

Additionally, tree recordings with terminal states that prevent spawning (Destroyed, Recovered, Docked, Boarded) already have their spawn correctly suppressed -- `IsSpawnableLeaf` checks terminal state, and `CommitTreeFlight` only spawns spawnable leaves. But the playback loop uses its own `needsSpawn` logic, which does NOT check terminal state. Let me verify:

Looking at line 3579: `needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed && !rec.TakenControl`. It checks `VesselDestroyed` but not `TerminalStateValue`. However, `VesselDestroyed` is set for Destroyed terminals, and `VesselSnapshot` is nulled for Destroyed terminals in `FinalizeTreeRecordings` (line 3308). For Recovered/Docked/Boarded terminals, the terminal state is set but `VesselSnapshot` may still be non-null... wait, let me check.

Actually, for tree recordings, only leaf recordings get terminal states assigned in `FinalizeTreeRecordings`. Non-leaf recordings already have their terminal state implied by the branch type. And `CommitTreeFlight` calls `SpawnTreeLeaves` which uses `IsSpawnableLeaf` (checks TerminalState). But the playback loop's `needsSpawn` is separate.

For safety, also add a terminal state check:

```csharp
// Tree recordings with terminal states that prevent spawning
if (needsSpawn && rec.TerminalStateValue.HasValue)
{
    var ts = rec.TerminalStateValue.Value;
    if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
        || ts == TerminalState.Docked || ts == TerminalState.Boarded)
    {
        needsSpawn = false;
    }
}
```

Wait -- this guard should apply to ALL recordings, not just tree recordings. Standalone recordings with terminal state Destroyed already have `VesselDestroyed = true` and `VesselSnapshot = null`, so `needsSpawn` is already false. But for robustness, adding this guard is safe.

Actually, checking more carefully: the `VesselDestroyed` field is set separately from `TerminalStateValue`. For standalone recordings, both are set. For tree recordings finalized by `FinalizeTreeRecordings`, only `TerminalStateValue` is set; `VesselDestroyed` may not be. So the terminal state guard is genuinely needed for tree recordings.

Let me consolidate the spawn suppression into a clean block:

```csharp
// Suppress spawning for non-leaf tree recordings (superseded by children)
if (needsSpawn && rec.ChildBranchPointId != null)
{
    needsSpawn = false;
    if (loggedGhostEnter.Add(i + 400000))
        ParsekLog.Verbose("Flight",
            $"Spawn suppressed for #{i} ({rec.VesselName}): non-leaf tree recording");
}

// Suppress spawning for recordings with terminal states that prevent it
if (needsSpawn && rec.TerminalStateValue.HasValue)
{
    var ts = rec.TerminalStateValue.Value;
    if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
        || ts == TerminalState.Docked || ts == TerminalState.Boarded)
    {
        needsSpawn = false;
        if (loggedGhostEnter.Add(i + 500000))
            ParsekLog.Verbose("Flight",
                $"Spawn suppressed for #{i} ({rec.VesselName}): terminal state {ts}");
    }
}
```

#### 4.3 Warp stop coordination for trees (Gap 3.3)

**Approach:** The design doc says "stop time warp once when UT enters any recording's range that has an unspawned leaf vessel." This means:

1. Only leaf recordings should trigger warp stops (non-leaf recordings are ghost-only).
2. Only recordings with unspawned vessels (same `needsSpawn` semantics).
3. Warp should stop at most once per tree (not per recording).

The current warp stop section has these guards:
- `rec.Points.Count < 2` -- PROBLEM: skips background-only recordings
- `rec.VesselSnapshot == null || rec.VesselSpawned || rec.VesselDestroyed || IsChainMidSegment(rec)` -- needs tree extensions

**Changes to the warp stop section:**

```csharp
for (int i = 0; i < committed.Count; i++)
{
    var rec = committed[i];

    // Must have some playback data
    bool hasPlaybackData = rec.Points.Count >= 2
        || (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
        || rec.SurfacePos.HasValue;
    if (!hasPlaybackData) continue;

    if (ShouldLoopPlayback(rec)) continue;
    if (!rec.PlaybackEnabled) continue;

    // Only spawn-eligible recordings trigger warp stop
    if (rec.VesselSnapshot == null || rec.VesselSpawned || rec.VesselDestroyed ||
        RecordingStore.IsChainMidSegment(rec)) continue;

    // Non-leaf tree recordings don't trigger warp stop
    if (rec.ChildBranchPointId != null) continue;

    // Terminal states that prevent spawning don't trigger warp stop
    if (rec.TerminalStateValue.HasValue)
    {
        var ts = rec.TerminalStateValue.Value;
        if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
            || ts == TerminalState.Docked || ts == TerminalState.Boarded)
            continue;
    }

    bool crossedInto = lastTimelineUT < rec.StartUT && currentUT >= rec.StartUT;
    bool approaching = currentUT < rec.StartUT &&
                       rec.StartUT - currentUT <= System.Math.Max(1.0, timelineStep);
    if (crossedInto || approaching)
    {
        if (warpStoppedForRecording.Add(i))
        {
            ExitAllWarpForPlaybackStart(rec.VesselName);
            Log($"Stopped warp for recording #{i} ({rec.VesselName}) ghost playback");
            ScreenMessage($"Time warp stopped -- '{rec.VesselName}' playback", 3f);
        }
        break;
    }
}
```

**Design decision -- per-recording vs per-tree warp stops:**

The design doc says "stop time warp once when UT enters any recording's range that has an unspawned leaf vessel." This is about stopping warp for spawn-eligible vessels. Each spawnable leaf is an independent vessel that the player may want to observe. So warp should stop once per *spawnable leaf*, not once per tree.

Consider a tree with two leaf vessels at different EndUTs: an orbiter (ends at t=500) and a lander (ends at t=800). The player warps forward. Warp should stop at t=500 to show the orbiter ghost/spawn, then the player resumes warp, and warp stops again at t=800 for the lander.

The existing `warpStoppedForRecording` HashSet already prevents re-stopping for the same recording index. The `break` ensures only one stop per frame. This is already the correct behavior -- one stop per leaf that enters range.

So **no tree-level coordination is needed**. The existing per-recording warp stop is correct. We only need to:
1. Fix the `Points.Count < 2` guard to allow background-only recordings.
2. Add the non-leaf / terminal state filters.

### 5. pastEnd/pastChainEnd handling for background-only recordings

The `pastEnd` and `pastChainEnd` branches in the playback loop need to handle background-only recordings for vessel spawning. Currently, the spawn path at line 3651-3667 positions the ghost at the last trajectory point before spawning. For background-only recordings, there is no last trajectory point.

**Changes needed for the spawn path:**

For the `pastChainEnd && needsSpawn && ghostActive` branch (line 3651):
```csharp
else if (pastChainEnd && needsSpawn && ghostActive)
{
    Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} -- spawning vessel");
    // Position ghost at final state before spawning
    if (hasPoints)
        PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
    // For orbit/surface-only, ghost is already at its last positioned state
    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
    DestroyTimelineGhost(i);
    ApplyResourceDeltas(rec, currentUT);
}
```

For the `pastChainEnd && needsSpawn && !ghostActive` branch (line 3660):
```csharp
else if (pastChainEnd && needsSpawn && !ghostActive)
{
    // UT already past chain EndUT on scene load -- spawn immediately, no ghost
    Log($"Ghost SKIPPED (UT already past EndUT): #{i} \"{rec.VesselName}\" at UT {currentUT:F1} " +
        $"(EndUT={rec.EndUT:F1}) -- spawning vessel immediately");
    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
    ApplyResourceDeltas(rec, currentUT);
}
```

These spawn paths already call `VesselSpawner.SpawnOrRecoverIfTooClose` which uses the `VesselSnapshot` for spawning, not the ghost position. So they work for background-only recordings without changes -- except for the `PositionGhostAt` call in the first branch, which now has a `hasPoints` guard.

### 6. pastEnd handling for mid-chain mid-tree recordings

The `pastEnd && ghostActive && isMidChain && !pastChainEnd` branch (line 3668) holds a mid-chain ghost at its final position. Tree recordings are not chains, so `isMidChain` is always false for tree recordings. This is correct -- no tree recording should hold at its final position past its EndUT. When a parent recording passes its EndUT, the ghost should disappear (children take over).

**Verdict: No change needed.**

### 7. Summary of All Code Changes

#### 7.1 ParsekFlight.cs -- UpdateTimelinePlayback

**Change 1: Replace Points.Count < 2 early-return with data-presence check**

In the main playback loop (line 3548), and in the warp stop loop (line 3520):

```csharp
// OLD:
if (rec.Points.Count < 2) continue;

// NEW:
bool hasPoints = rec.Points.Count >= 2;
bool hasOrbitSegments = rec.OrbitSegments != null && rec.OrbitSegments.Count > 0;
bool hasSurfacePos = rec.SurfacePos.HasValue;
if (!hasPoints && !hasOrbitSegments && !hasSurfacePos) continue;
```

**Change 2: Add spawn suppression for non-leaf tree recordings and terminal states**

After the existing chain-related spawn suppression guards (~line 3605), add:

```csharp
// Tree non-leaf recordings should never spawn
if (needsSpawn && rec.ChildBranchPointId != null)
{
    needsSpawn = false;
    if (loggedGhostEnter.Add(i + 400000))
        ParsekLog.Verbose("Flight",
            $"Spawn suppressed for #{i} ({rec.VesselName}): non-leaf tree recording");
}

// Terminal states that prevent spawning
if (needsSpawn && rec.TerminalStateValue.HasValue)
{
    var ts = rec.TerminalStateValue.Value;
    if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
        || ts == TerminalState.Docked || ts == TerminalState.Boarded)
    {
        needsSpawn = false;
        if (loggedGhostEnter.Add(i + 500000))
            ParsekLog.Verbose("Flight",
                $"Spawn suppressed for #{i} ({rec.VesselName}): terminal state {ts}");
    }
}
```

**Change 3: Add non-leaf and terminal state filters to warp stop**

In the warp stop loop (lines 3517-3539), after the existing skip guards, add:

```csharp
if (rec.ChildBranchPointId != null) continue;
if (rec.TerminalStateValue.HasValue)
{
    var ts = rec.TerminalStateValue.Value;
    if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
        || ts == TerminalState.Docked || ts == TerminalState.Boarded)
        continue;
}
```

**Change 4: Background-phase ghost positioning in the `inRange` block**

Replace the single interpolation call with a branching check:

```csharp
if (inRange)
{
    if (!ghostActive)
    {
        SpawnTimelineGhost(i, rec);
        state = ghostStates[i];
        if (loggedGhostEnter.Add(i))
            Log($"Ghost ENTERED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1}");
    }

    if (hasPoints)
    {
        int playbackIdx = state.playbackIndex;
        InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
            ref playbackIdx, currentUT, i * 10000);
        state.playbackIndex = playbackIdx;
    }
    else if (hasOrbitSegments)
    {
        PositionGhostFromOrbitOnly(state.ghost, rec.OrbitSegments, currentUT, i * 10000);
    }
    else if (hasSurfacePos)
    {
        PositionGhostAtSurface(state.ghost, rec.SurfacePos.Value);
    }

    ApplyPartEvents(i, rec, currentUT, state);
    ApplyResourceDeltas(rec, currentUT);
}
```

**Change 5: Guard PositionGhostAt call in spawn path**

In the `pastChainEnd && needsSpawn && ghostActive` branch (line 3655):

```csharp
// OLD:
PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);

// NEW:
if (rec.Points.Count > 0)
    PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
```

#### 7.2 ParsekFlight.cs -- New helper methods

Add two new methods in the `#region Ghost Positioning` section:

1. `PositionGhostFromOrbitOnly(GameObject ghost, List<OrbitSegment> segments, double ut, int orbitCacheBase)` -- Positions a ghost using only orbit segments. Calls existing `PositionGhostFromOrbit` for the matching segment, hides ghost if no segment covers the UT.

2. `PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos)` -- Positions a ghost at a fixed surface position using `CelestialBody.GetWorldSurfacePosition`.

#### 7.3 GhostVisualBuilder.cs -- No changes

The ghost visual builder already handles `GhostVisualSnapshot` correctly via `GetGhostSnapshot`. Each tree recording has its own `GhostVisualSnapshot` set at branch time. No changes needed.

### 8. Edge Cases

#### 8.1 Background recording with no orbit segments AND no surface position AND no trajectory points

This can happen if a background vessel was created at a branch point and the tree was committed in the same frame (edge case). The `!hasPoints && !hasOrbitSegments && !hasSurfacePos` guard will skip it. The vessel will be spawned at the tree's EndUT by `CommitTreeFlight`/`SpawnTreeLeaves` (which happens at commit time, not during playback). After a revert, the vessel was already spawned -- `VesselSpawned = true` prevents re-spawning.

Wait -- for the revert path, `CommitTreeRevert` stashes the tree as pending, and then after the merge dialog, `RecordingStore.CommitPendingTree` adds recordings to `CommittedRecordings`. Vessels are NOT spawned at commit time for the revert path -- they are spawned during playback. If a background recording has no playback data, it will be skipped and never spawn during playback.

**Fix:** In `FinalizeTreeRecordings`, ensure that background-only leaf recordings with no orbit segments and no surface position get at least one synthetic trajectory point or a surface position captured, so they have some playback data. But this is a recording-side concern, not a playback concern. For Task 9, we should handle this gracefully: if a recording has no playback data but needs spawning, the existing `pastChainEnd && needsSpawn && !ghostActive` path (spawn immediately, no ghost) would handle it -- BUT it is currently gated by the `Points.Count < 2` guard which we are replacing.

With the new guard `if (!hasPoints && !hasOrbitSegments && !hasSurfacePos) continue;`, such a recording would be skipped. But it still needs spawning.

**Resolution:** Add a safety net after the main playback loop -- a second pass that catches any recordings that were skipped but need spawning:

Actually, re-reading the code more carefully: `VesselSpawner.SpawnOrRecoverIfTooClose` is called in two places:
1. `pastChainEnd && needsSpawn && ghostActive` (line 3656)
2. `pastChainEnd && needsSpawn && !ghostActive` (line 3665)

Path 2 handles the "UT already past EndUT" case where no ghost was ever created. This path would catch our edge case IF the recording passes the initial data-presence guard. Since it doesn't (no data), it won't.

The safest approach: do NOT skip recordings that have no playback data if they need spawning. Refine the early-return:

```csharp
bool hasPoints = rec.Points.Count >= 2;
bool hasOrbitSegments = rec.OrbitSegments != null && rec.OrbitSegments.Count > 0;
bool hasSurfacePos = rec.SurfacePos.HasValue;
bool hasPlaybackData = hasPoints || hasOrbitSegments || hasSurfacePos;

// Skip recordings with no playback data, UNLESS they need spawning
// (VesselSnapshot != null and not already spawned/destroyed)
bool possibleSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned
    && !rec.VesselDestroyed && !rec.TakenControl
    && rec.ChildBranchPointId == null;
if (!hasPlaybackData && !possibleSpawn) continue;
```

Then in the main loop body, the `inRange` block should guard against null ghost positioning when there's no data:

```csharp
if (inRange && hasPlaybackData)
{
    // ... ghost creation and positioning
}
else if (inRange && !hasPlaybackData)
{
    // No data to display -- skip ghost for this frame
    // (The recording may still need spawning at pastChainEnd)
}
```

This ensures the recording stays in the loop so the `pastChainEnd && needsSpawn` path can pick it up.

#### 8.2 Surface-position ghost rotation

`SurfacePosition.rotation` is a surface-relative quaternion captured at recording start. For a surface ghost, we apply it directly. This may not be perfectly correct if the body has rotated (different local orientation at playback time vs recording time). However, this is the same approximation used for trajectory points (which also store body-relative lat/lon/alt and a quaternion). So this is consistent and acceptable.

#### 8.3 Multiple trees committed simultaneously

Multiple trees can be committed (e.g., two separate launches). Each tree's recordings are all in `CommittedRecordings`. The playback loop treats them all independently. This is correct -- recordings from different trees don't interact during playback.

#### 8.4 Looping playback for tree recordings

Tree recordings should NOT loop -- they are part of a one-time mission tree. `ShouldLoopPlayback` checks `rec.LoopPlayback` which defaults to false. Tree recordings don't set `LoopPlayback = true`. So looping is not triggered.

**Verdict: No change needed.**

#### 8.5 Disabled playback for tree recordings

`rec.PlaybackEnabled` defaults to true. If a user disables a tree recording, the existing `!rec.PlaybackEnabled` guard (line 3557) handles it correctly -- the ghost is destroyed and resource deltas still apply.

**Verdict: No change needed.**

### 9. Testing Strategy

#### 9.1 Unit tests

Tree ghost playback is primarily visual and involves Unity runtime (ghosts, orbit computation, celestial bodies). Most of the logic is not easily unit-testable without a full KSP runtime. However, the following can be tested:

1. **Spawn suppression logic:** Test that `needsSpawn` is correctly false for recordings with `ChildBranchPointId != null` and for recordings with terminal states Destroyed/Recovered/Docked/Boarded. This requires extracting the spawn eligibility logic into a static method.

2. **Data-presence guard:** Test that recordings with no points but with orbit segments or surface positions are NOT skipped. Test that recordings with no data at all ARE skipped.

However, these tests would require refactoring `UpdateTimelinePlayback` to extract testable methods, which would increase the change scope significantly. Given that this is fundamentally a visual/integration feature, in-game testing is more appropriate.

#### 9.2 Synthetic recording injection

Update the synthetic recording test suite to include a tree with multiple recordings:

1. **Tree with active + background recordings:** Root recording with trajectory points (active phase), plus a background-only recording with orbit segments (simulating an undocked stage in orbit).

2. **Tree with surface-position background:** A tree where one branch is a landed vessel (surface position only, no trajectory points).

3. **Tree with multiple branch points:** Root -> branch at t1 -> branch at t2. Three recordings with overlapping time ranges.

This would go into `Tests/Generators/` using the existing `RecordingBuilder` and `VesselSnapshotBuilder` fluent API, plus `ScenarioWriter` for injection.

**Note:** The existing synthetic recording infrastructure (`RecordingBuilder`, `VesselSnapshotBuilder`, `ScenarioWriter`) was built for standalone recordings. It would need to be extended to support `RecordingTree` serialization. This extension should be its own sub-task.

#### 9.3 In-game testing

1. **Basic tree playback:** Launch vessel -> record -> undock -> record both -> revert -> merge. Watch ghosts: parent ghost plays, at branch UT parent ghost disappears, two child ghosts appear and continue.

2. **Background orbit ghost:** Launch -> establish orbit -> undock second stage -> let it orbit (background) -> revert -> merge. Watch: orbiting stage ghost follows orbital path.

3. **Background surface ghost:** Launch -> land on launchpad -> EVA -> walk away -> revert -> merge. Watch: vessel ghost stays at pad, EVA ghost walks away.

4. **Warp stop:** Same setups but time warp forward. Verify warp stops once when entering a leaf recording's range, not for every recording in the tree.

5. **Spawn at EndUT:** Let ghosts play through to EndUT. Verify vessels spawn at correct positions. Verify no phantom vessels from non-leaf recordings.

### 10. Implementation Order

1. **Add spawn suppression for non-leaf tree recordings** (Change 2) -- prevents phantom vessel spawning. This is the most critical fix.

2. **Add terminal state spawn suppression** (Change 2, second guard) -- safety net for tree recordings with Destroyed/Recovered/Docked/Boarded terminal states.

3. **Replace `Points.Count < 2` guard** (Change 1) -- allows background-only recordings into the playback loop. This unblocks background ghost playback.

4. **Add `PositionGhostFromOrbitOnly` and `PositionGhostAtSurface` helper methods** (Section 7.2) -- provides positioning logic for background-only ghosts.

5. **Add background-phase positioning in the `inRange` block** (Change 4) -- connects the new helpers to the playback loop.

6. **Guard `PositionGhostAt` call in spawn path** (Change 5) -- prevents IndexOutOfRange for background-only recordings at spawn time.

7. **Update warp stop guards** (Change 3) -- prevents warp stop for non-leaf recordings and allows background-only recordings to trigger warp stop.

8. **Testing** -- synthetic recordings first, then in-game testing.

Steps 1-2 can be done together (spawn suppression). Steps 3-6 can be done together (background ghost playback). Step 7 is independent. Step 8 comes last.

### 11. Risk Assessment

**Low risk:**
- Spawn suppression (Changes 2, 3) -- additive guards, cannot break existing behavior
- `PositionGhostAtSurface` -- simple new method, isolated
- PositionGhostAt guard (Change 5) -- additive check

**Medium risk:**
- Replacing `Points.Count < 2` guard (Change 1) -- this is the most impactful change. The old guard protected against recordings with degenerate data. The new guard must still protect against recordings with no data. The `possibleSpawn` refinement in edge case 8.1 adds complexity.
- Background orbit positioning (`PositionGhostFromOrbitOnly`) -- reuses existing `PositionGhostFromOrbit` but adds gap handling

**Mitigation:** All changes are additive (new code paths for new recording types). Existing standalone recordings are unaffected because:
- They have `Points.Count >= 2`, so `hasPoints` is true -> existing code path
- They have `ChildBranchPointId == null`, so spawn suppression guards are not triggered
- They have no `TreeId`, so no tree-specific behavior applies

### 12. Files Changed Summary

| File | Changes |
|------|---------|
| `Source/Parsek/ParsekFlight.cs` | UpdateTimelinePlayback: replace early-return guard, add spawn suppression, add background positioning, add warp stop filters. New methods: PositionGhostFromOrbitOnly, PositionGhostAtSurface |
| `Source/Parsek/GhostVisualBuilder.cs` | No changes |
| `Source/Parsek/RecordingStore.cs` | No changes |
| `Source/Parsek/RecordingTree.cs` | No changes |
| `Source/Parsek/BranchPoint.cs` | No changes |

---

## Orchestrator Review Fixes

### Fix 1 (CRITICAL) - `SurfacePosition.rotation` is surface-relative, not world-space

**Problem:** The plan's `PositionGhostAtSurface` directly assigns `surfPos.rotation` to `ghost.transform.rotation`. But `SurfacePosition.rotation` is captured as `v.srfRelRotation` (surface-relative quaternion, see BackgroundRecorder.cs line 564). `transform.rotation` expects world-space. The ghost would be oriented incorrectly.

**Resolution:** Convert surface-relative rotation to world-space at playback time:
```csharp
CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == surfPos.body);
if (body != null)
{
    ghost.transform.position = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
    ghost.transform.rotation = body.bodyTransform.rotation * surfPos.rotation;
}
```
The conversion `body.bodyTransform.rotation * surfPos.rotation` converts from surface-relative to world-space. This is the standard KSP pattern.

### Fix 2 (IMPORTANT) - Guard `PositionGhostAt` at line 3671 for empty points

**Problem:** The mid-chain hold path at line 3671 calls `PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1])` without guarding for empty points. While tree recordings won't hit this path (they're not chains), broadening the `Points.Count < 2` guard makes this path reachable for edge cases.

**Resolution:** Add a defensive guard: `if (rec.Points.Count > 0) PositionGhostAt(...)` at line 3671 and similarly at line 3655.

### Fix 3 (IMPORTANT) - Simplify the `possibleSpawn` refinement

**Problem:** The plan's edge case 8.1 adds significant complexity with `possibleSpawn` and dual `inRange` branches for a scenario that shouldn't happen in practice (leaf recording with zero data).

**Resolution:** Drop the `possibleSpawn` refinement entirely. Instead, add a warning in `FinalizeTreeRecordings` when a leaf recording has no playback data:
```csharp
if (isLeaf && rec.Points.Count == 0 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue)
    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data");
```
The early-continue guard becomes simply:
```csharp
bool hasPoints = rec.Points.Count >= 2;
bool hasOrbitData = rec.OrbitSegments.Count > 0;
bool hasSurfaceData = rec.SurfacePos.HasValue;
if (!hasPoints && !hasOrbitData && !hasSurfaceData) continue;
```
If a leaf somehow has no data, it won't get a ghost or spawn - but this is a recording pipeline bug, not a playback bug. The warning in `FinalizeTreeRecordings` catches it early.

### Fix 4 (MINOR) - Add defensive guard in `InterpolateAndPosition` for empty points

**Resolution:** Add at the top of `InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT)`:
```csharp
if (points == null || points.Count == 0) { ghost.SetActive(false); return; }
```

### Fix 5 (MINOR) - Resource deltas for background recordings

**Clarification:** `ApplyResourceDeltas` is a no-op for background-only recordings (they have no trajectory points with resource data). This is correct by design - background vessels don't generate per-point resource snapshots. Tree-level resource tracking (Task 10) will handle aggregate deltas. No code change needed.

### Summary for implementation agent

1. **`PositionGhostAtSurface`**: Use `body.bodyTransform.rotation * surfPos.rotation` for world-space orientation (Fix 1)
2. **Guard `PositionGhostAt`** at lines 3655 and 3671 with `rec.Points.Count > 0` (Fix 2)
3. **Drop `possibleSpawn`** - use simple `!hasPoints && !hasOrbitData && !hasSurfaceData` guard (Fix 3)
4. **Add empty-points guard** in `InterpolateAndPosition` point-only overload (Fix 4)
5. **Add warning** in `FinalizeTreeRecordings` for data-less leaf recordings (Fix 3)
