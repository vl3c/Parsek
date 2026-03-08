# Task 7: Tree Commit + Multi-Vessel Leaf Spawning

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

Task 7 is the keystone task of the Recording Tree feature. It ties together all the infrastructure built in Tasks 1-6 (data model, vessel switching, background recording, split detection, merge detection, terminal detection) into the two user-facing commit paths:

1. **Commit Flight button** -- Player presses "Commit Flight" while in the Flight scene with an active tree. The current vessel stays live (`VesselSpawned=true`), all other leaf vessels are spawned at their current positions, all crew are reserved, and the tree is committed to the timeline for ghost playback.

2. **Scene exit auto-commit** -- Player leaves the Flight scene (to Space Center, Tracking Station, etc.) with an active tree. The tree is finalized, committed to the timeline, but NO vessels are spawned (snapshots are nulled). This matches the existing standalone auto-commit behavior (commit path #9 in the existing code).

Additionally, this task establishes the **tree persistence layer**: how committed trees are stored in `RecordingStore` and saved/loaded through `ParsekScenario`, replacing the per-recording committed list for tree recordings.

The core challenges:
- Finalizing all recordings simultaneously (active + all background)
- Identifying which recordings are spawnable leaves vs. terminal (destroyed/recovered/docked/boarded)
- Spawning N vessels with crew reservation, proximity checks, and orbital propagation
- Integrating with the existing recording commit pipeline without breaking standalone recording functionality
- Persisting committed trees through save/load cycles

### 2. Existing Commit Flow Audit

The standalone recording commit flow in `ParsekFlight.cs` works as follows:

#### 2.1 CommitFlight (line 2965) -- "Commit Flight" button path

Step-by-step:

1. **Guard checks**: recorder must exist with >= 2 points; must not be mid-chain (`activeChainId != null` blocks)
2. **Stop recording** if still active: calls `StopRecording()`
3. **Stop continuations**: `RefreshContinuationSnapshot` + `StopContinuation`; same for undock continuations
4. **Capture vessel name** from `FlightGlobals.ActiveVessel`
5. **Stash as pending**: `RecordingStore.StashPending(recorder.Recording, vesselName, ...)` -- creates a `Recording` object in `pendingRecording` with trajectory points, orbit segments, part events
6. **Guard**: if `StashPending` rejected (too few points), return
7. **Apply snapshot artifacts**: if `CaptureAtStop` exists (stop-time capture), call `pending.ApplyPersistenceArtifactsFrom(captured)`. Else fallback: `VesselSpawner.SnapshotVessel(pending, ...)` to capture vessel snapshot now
8. **Mark as non-revert commit**: `pending.VesselSpawned = true`, `pending.SpawnedVesselPersistentId = recorder.RecordingVesselId`, `pending.LastAppliedResourceIndex = pending.Points.Count - 1`
9. **Commit to timeline**: `RecordingStore.CommitPending()` -- moves from `pendingRecording` to `committedRecordings` list, creates milestone, captures baseline
10. **Clear state**: `recorder = null`, `lastPlaybackIndex = 0`

Key insight: `VesselSpawned = true` prevents the timeline playback loop from trying to spawn the vessel again (it already exists -- the player is flying it).

#### 2.2 OnSceneChangeRequested (line 704) -- scene exit auto-commit path

Step-by-step:

1. **Stop continuations** (continuation + undock continuation)
2. **Clear dock/undock/merge/split pending state**
3. **Tree mode cleanup** (lines 742-758): if `activeTree` exists and recorder is recording, flush to tree then null the tree. Currently does NOT commit -- just cleans up. Task 7 must add actual tree commit here.
4. **Standalone recording stash** (lines 761-810): if recorder has data, stash as pending recording with full snapshot/geometry artifacts
5. The pending recording is then picked up by `OnFlightReady` on the next scene load for the merge dialog

Key insight: Scene exit does NOT call `CommitPending()` directly. It stashes the recording, and the merge dialog on the next `OnFlightReady` handles committing. For trees, we need a different approach since the tree merge dialog is Task 8 -- Task 7 should auto-commit trees on scene exit (no dialog, same as the existing scene-exit auto-commit for standalone recordings in certain paths).

#### 2.3 OnFlightReady (line 2745) -- post-revert / post-scene-change

Step-by-step:

1. **Shutdown background recorder**, clear tree state
2. **Clear all pending state** (split, chain, dock/undock, merge)
3. **Check for pending recording**: if exists, show merge dialog based on type (chain, EVA child, or standalone)
4. **Crew swap**: `ParsekScenario.SwapReservedCrewInFlight()` -- removes reserved crew from the active vessel and replaces them with hired replacements

### 3. Tree Commit vs. Standalone Commit

| Aspect | Standalone Commit | Tree Commit |
|--------|-------------------|-------------|
| Number of recordings | 1 | N (all recordings in tree) |
| Number of vessels to spawn | 0 or 1 | 0 to N (all leaf vessels) |
| Active vessel handling | `VesselSpawned=true` | `VesselSpawned=true` on active leaf |
| Crew reservation | 1 vessel's crew | All leaf vessels' crew |
| Proximity check | Against existing vessels | Against existing vessels AND other leaves |
| Storage | `RecordingStore.CommittedRecordings` list | `RecordingStore.CommittedTrees` list (new) |
| Scene exit behavior | Stash pending, dialog on revert | Auto-commit, no spawning (snapshots nulled) |
| Resources | Per-recording deltas | Tree-level deltas (Task 10, deferred) |

What is reused from the standalone flow:
- `VesselSpawner.RespawnVessel` / `SpawnAtPosition` for actual vessel spawning
- `ParsekScenario.ReserveCrewIn` for crew reservation
- `ParsekScenario.SwapReservedCrewInFlight` for crew swapping
- Ghost playback in `UpdateTimelinePlayback` (Task 9 extends this, but the existing loop already processes `CommittedRecordings`)
- `RecordingStore.SaveRecordingFiles` / `LoadRecordingFiles` for external file storage per recording

What is new:
- `RecordingTree.Commit()` method to finalize all recordings
- Leaf identification algorithm
- Multi-vessel spawn orchestration
- Tree-level storage in `RecordingStore` and `ParsekScenario`
- Orbital propagation for leaf spawn positions

### 4. Leaf Identification

A **leaf recording** is one that has no children and whose terminal state allows spawning. The algorithm:

```
IsSpawnableLeaf(recording):
    1. ChildBranchPointId is null (no outgoing branch -- it is a leaf)
    2. TerminalStateValue is NOT one of: Destroyed, Recovered, Docked, Boarded
       (Destroyed/Recovered are terminal with no spawn; Docked/Boarded mean this recording
       was absorbed into another vessel that continues as a child recording)
    3. VesselSnapshot is not null (needs a snapshot to spawn from)
```

Terminal state assignment for leaf recordings that have no explicit terminal state (they were still recording at commit time):

```
AssignTerminalState(recording, vessel):
    if vessel.situation == ORBITING:  TerminalState.Orbiting
    if vessel.situation == LANDED:    TerminalState.Landed
    if vessel.situation == SPLASHED:  TerminalState.Splashed
    if vessel.situation == SUB_ORBITAL or FLYING or ESCAPING: TerminalState.SubOrbital
    if vessel.situation == PRELAUNCH: TerminalState.Landed  (pad vessel)
```

For the active recording, the vessel object is `FlightGlobals.ActiveVessel`. For background recordings, the vessel is looked up via `FlightRecorder.FindVesselByPid(pid)` from `FlightGlobals.Vessels`. If the vessel object cannot be found (unloaded), use the last known orbit/surface data from the recording.

### 5. Active Vessel Handling

The active vessel is the one the player is currently flying. On Commit Flight:

1. **Stop the active recorder**: `StopRecording()` (or `recorder.ForceStop()` if simpler). This captures `CaptureAtStop` with snapshot.
2. **Flush to tree recording**: `FlushRecorderToTreeRecording(recorder, activeTree)` -- copies trajectory points, orbit segments, part events into the tree's active recording
3. **Capture vessel snapshot**: The active recording needs a vessel snapshot for spawning. Since `CaptureAtStop` was set during `StopRecording()`, use that. Copy `VesselSnapshot` from `CaptureAtStop` into the tree recording.
4. **Set terminal state**: Based on `FlightGlobals.ActiveVessel.situation`
5. **Capture terminal orbit/position**: Orbit parameters or surface position from the active vessel
6. **Mark as already spawned**: `rec.VesselSpawned = true` (the vessel is already in-game)
7. **Set `SpawnedVesselPersistentId`**: From `recorder.RecordingVesselId` (enables duplicate detection)
8. **Set `LastAppliedResourceIndex`**: To `rec.Points.Count - 1` (prevents double-applying resource deltas)

The active vessel is NOT despawned or modified. The player continues flying it normally. It transitions from "being recorded" to "a normal game vessel with a committed recording backing it."

### 6. Background Vessel Finalization

Each background recording in `activeTree.BackgroundMap` must be finalized:

1. **Flush background recorder data**: Call `backgroundRecorder.FinalizeAllForCommit(commitUT)` (new method) -- this closes all open orbit segments and flushes any pending trajectory data from loaded/physics-range background vessels
2. **For each background recording**:
   a. Look up the vessel by `persistentId` in `FlightGlobals.Vessels`
   b. If vessel found and loaded:
      - Capture vessel snapshot: `VesselSpawner.TryBackupSnapshot(vessel)`
      - Set terminal state from `vessel.situation`
      - Capture terminal orbit (from `vessel.orbit`) or surface position (from `vessel.latitude/longitude/altitude`)
      - Set `ExplicitEndUT = commitUT`
   c. If vessel found but unloaded (on rails):
      - Use `vessel.protoVessel` to get a snapshot: build ConfigNode from `vessel.BackupVessel()`
      - Set terminal state: if `vessel.situation == ORBITING` then `TerminalState.Orbiting`, etc.
      - Capture terminal orbit from `vessel.orbit`
      - Set `ExplicitEndUT = commitUT`
   d. If vessel NOT found in `FlightGlobals.Vessels` (should not happen if Tasks 4-6 are correct, but defensive):
      - Mark as `TerminalState.Destroyed` (vessel disappeared without our knowledge)
      - Set `ExplicitEndUT = commitUT`
      - Null the `VesselSnapshot` (cannot spawn)
3. **Handle ghost visual snapshot**: If the recording already has a `GhostVisualSnapshot` (captured at branch time), keep it. If not, use the vessel snapshot as fallback.
4. **Set `ExplicitStartUT`**: If not already set and the recording has trajectory points, set from the first point's UT. If it has orbit segments but no trajectory points, set from the first orbit segment's startUT.

### 7. Multi-Vessel Spawn Sequence

At commit time, after all recordings are finalized:

```
CommitTree(tree, isSceneExit):
    commitUT = Planetarium.GetUniversalTime()

    1. Stop active recorder, flush to tree
    2. Finalize all background recordings (Section 6)
    3. Set ExplicitEndUT on all leaf recordings without one
    4. Collect leaf recordings: filter by IsSpawnableLeaf()

    if isSceneExit:
        5a. Null all VesselSnapshots (no spawning on scene exit)
        5b. Commit tree to storage
        5c. Return

    5. Identify the active vessel's recording
    6. Mark active vessel's recording: VesselSpawned=true, SpawnedVesselPersistentId, LastAppliedResourceIndex

    7. For each OTHER leaf recording (not the active vessel):
        a. Check orbital propagation (Section 7.1)
        b. Reserve crew (Section 8)
        c. Calculate spawn position with proximity offset (Section 9)

    8. Commit tree to storage
    9. Spawn all non-active leaf vessels
    10. Crew swap: ParsekScenario.SwapReservedCrewInFlight()
```

#### 7.1 Orbital Leaf Spawn Position

For orbital/suborbital leaf vessels, the terminal orbit was captured at recording end time. If time has passed between recording end and spawn time (which it hasn't for Commit Flight, but may for scene-exit-then-return scenarios), the vessel has drifted.

For the Commit Flight path, `commitUT` equals "now" and the vessel's orbit was just captured, so propagation is trivially zero. But the spawn position should still be computed from the terminal orbit for correctness:

```
if terminalState == Orbiting or SubOrbital:
    Orbit orbit = new Orbit(inc, ecc, sma, lan, argPe, mna, epoch, body)
    Vector3d pos = orbit.getPositionAtUT(spawnUT)
    double alt = body.GetAltitude(pos)

    // Crash check for suborbital vessels
    if alt < body.TerrainAltitude(lat, lon):
        rec.TerminalStateValue = TerminalState.Destroyed
        rec.VesselSnapshot = null  // cannot spawn underground
        skip this leaf
    else:
        spawn at computed lat/lon/alt from orbit
```

For landed/splashed leaves:
```
if terminalState == Landed or Splashed:
    spawn at terminalPosition (body, lat, lon, alt, rotation)
    // Use VesselSpawner.RespawnVessel which uses the snapshot's stored position
```

### 8. Crew Reservation for N Vessels

The existing `ParsekScenario.ReserveCrewIn` iterates crew in a vessel snapshot and:
1. Sets each crew member to `Assigned` status
2. Hires a replacement kerbal with the same trait
3. Stores the mapping in `crewReplacements`

For tree commit, we iterate ALL spawnable leaf snapshots (not just one):

```
ReserveTreeCrew(tree, leafRecordings):
    roster = HighLogic.CurrentGame.CrewRoster
    SuppressCrewEvents = true

    for each leaf in leafRecordings:
        if leaf.VesselSpawned: continue  // active vessel -- crew already in game
        if leaf.VesselSnapshot == null: continue
        ReserveCrewIn(leaf.VesselSnapshot, false, roster)

    SuppressCrewEvents = false
```

This naturally scales to N vessels. Each vessel's crew gets reserved and a replacement is hired. The key constraint: **crew must be reserved BEFORE spawning**, because `RespawnVessel` calls `ParsekScenario.UnreserveCrewInSnapshot(spawnNode)` which sets crew back to `Available` for KSP to assign them to the spawned vessel. If crew are not reserved first, there is no replacement to swap in.

**Cross-vessel crew overlap**: A crew member cannot appear in two leaf vessel snapshots (they can only be on one vessel at recording end). The tree's branch/merge logic ensures this: at any given time, a crew member is on exactly one vessel. If Jeb undocked from vessel A onto vessel B, vessel A's snapshot does NOT contain Jeb (he left at branch time). Only vessel B's snapshot has him.

**EVA crew exclusion**: In the existing chain system, `BuildExcludeCrewSet` removes EVA'd crew from the parent vessel snapshot. In tree mode, this is handled differently -- EVA kerbals are separate leaf recordings with their own snapshots. The parent vessel's snapshot at branch time already excludes the EVA'd kerbal (the snapshot is captured after the EVA event splits the vessel). However, the vessel snapshot might be the one captured at branch time (the `VesselSnapshot` on the child recording is set during `CreateSplitBranch` from `VesselSpawner.TryBackupSnapshot`). Need to verify this: does the parent vessel's snapshot captured at split time include or exclude the EVA'd kerbal? It should exclude them because KSP has already removed the kerbal from the vessel by the time `onPartUndock` completes and we defer the snapshot by one frame.

### 9. Proximity Offset for N Vessels

The existing `SpawnOrRecoverIfTooClose` checks proximity against:
1. All vessels currently in `FlightGlobals.Vessels`
2. Other committed recordings' final positions (by iterating `RecordingStore.CommittedRecordings`)

For tree spawning, we need proximity checks against:
1. All vessels in `FlightGlobals.Vessels` (includes the active vessel)
2. All OTHER leaf vessels being spawned simultaneously

Approach: maintain a list of already-committed spawn positions as we iterate through leaves:

```
spawnPositions = []  // list of (body, worldPos) for already-committed spawns

for each leaf in leafRecordings (excluding active vessel):
    computedPos = computeSpawnPosition(leaf)

    // Check against existing vessels
    closestDist = minDistanceTo(FlightGlobals.Vessels, computedPos)

    // Check against already-committed spawn positions
    for each (body, pos) in spawnPositions:
        if body == leaf.body:
            dist = distance(pos, computedPos)
            closestDist = min(closestDist, dist)

    if closestDist < 100m:
        offset computedPos to 250m away

    spawnPositions.add((leaf.body, finalPos))
    spawn(leaf, finalPos)
```

This ensures that if two leaves would spawn at the same position (e.g. two probes that were co-orbiting), they get offset from each other.

**Note**: The existing `ProximityOffsetEnabled` flag is currently `false`. Tree spawning should respect this flag. If disabled, all vessels spawn at their exact positions regardless of proximity.

### 10. Scene Exit Auto-Commit

When the player leaves Flight scene with an active tree (`OnSceneChangeRequested`):

```
OnSceneChangeRequested(scene):
    ... existing cleanup (continuations, dock/undock state) ...

    if activeTree != null:
        CommitTree(activeTree, isSceneExit: true)
        // CommitTree with isSceneExit=true:
        //   - Finalizes all recordings (sets ExplicitEndUT, terminal states)
        //   - Nulls all VesselSnapshots (no spawning -- vessels don't exist after revert)
        //   - Commits tree to storage (for ghost playback on next flight)
        //   - Does NOT reserve crew, does NOT spawn vessels
        // Then cleanup:
        activeTree = null
        backgroundRecorder.Shutdown()
        backgroundRecorder = null
```

This is the simplest commit path. The tree is committed purely for ghost playback. When the player next enters Flight and time warp past the tree's time range, ghosts will replay but no vessels will spawn (all snapshots are null).

**Wait -- this needs more thought.** The design doc says (under "Scene exit"):

> leaving the Flight scene auto-commits the entire tree. All active and background recordings are finalized, the tree is committed to the timeline, leaf vessels spawn at EndUT.

But the existing standalone behavior for scene exit auto-commit does NOT spawn vessels -- it stashes as pending and the merge dialog handles it. For trees in v1, the design doc also says:

> No recovery option in the merge dialog. After Parsek spawns all leaf vessels, the player uses KSP's native recovery tools.

The merge dialog for trees is Task 8. For Task 7, we need to handle both paths:

1. **Commit Flight button** -- full commit with spawning (implemented in Task 7)
2. **Scene exit** -- commit without spawning, no dialog (implemented in Task 7). Vessel snapshots are KEPT (not nulled) so that the tree merge dialog (Task 8) can offer spawning on revert.

Actually, re-reading the design doc more carefully:

> Scene exit auto-commit: finalize tree on leaving Flight scene (same as Commit but with all snapshots nulled -- no spawning, same as commit path #9)

This explicitly says snapshots are nulled. So scene exit = ghost-only, no spawning. This matches the intent: if you go to Space Center, the tree's recordings play as ghosts only. Vessels don't magically appear.

But then what about revert? On revert, the tree merge dialog (Task 8) should show and offer "Merge to Timeline" which would commit WITH spawning. The existing revert flow stashes a pending recording which the merge dialog picks up. For trees, we need a different mechanism.

**Resolution**: Scene exit and revert are different code paths:

- **Scene exit (Space Center, Tracking Station)**: The tree is committed ghost-only (snapshots nulled). This happens in `OnSceneChangeRequested`. The committed tree is stored for ghost playback. No spawning.

- **Revert to Launch**: The existing flow goes through `OnSceneChangeRequested` (scene change to FLIGHT) and then `OnFlightReady`. Currently `OnSceneChangeRequested` stashes the tree. But we need to KEEP the tree data for the merge dialog. Approach: instead of committing the tree immediately on scene change, **stash the finalized tree as a pending tree** (parallel to `RecordingStore.pendingRecording`). Then `OnFlightReady` checks for a pending tree and shows the tree merge dialog (Task 8). The merge dialog callback either commits with spawning or discards.

This means Task 7 needs to implement:
- **CommitTreeFlight()** -- commit with spawning (Commit Flight button)
- **StashPendingTree()** -- finalize tree data and stash for merge dialog (scene exit / revert)
- **CommitPendingTree(withSpawning)** -- commit a stashed tree (called by merge dialog in Task 8)
- **DiscardPendingTree()** -- discard a stashed tree (called by merge dialog in Task 8)

For now (Task 7), without the tree merge dialog (Task 8), scene exit should auto-commit ghost-only. On revert, if there is a pending tree, it should also auto-commit ghost-only until Task 8 adds the dialog. The merge dialog will replace this behavior.

**Revised approach for Task 7**:

On `OnSceneChangeRequested`:
- Finalize all recordings in the tree
- Stash the tree as a "pending tree" in `RecordingStore`

On `OnFlightReady`:
- If there is a pending tree:
  - For now (until Task 8): auto-commit ghost-only (null snapshots, commit to timeline)
  - Task 8 will replace this with the tree merge dialog

On `CommitTreeFlight()` (button press):
- Finalize all recordings
- Full commit with spawning, crew reservation, proximity offsets

### 11. Merge Dialog Modifications

**This is Task 8, not Task 7.** Task 7 provides the commit infrastructure that Task 8 will call. Specifically, Task 7 exposes:

- `RecordingTree.CommitWithSpawning(commitUT)` -- full commit with vessel spawning
- `RecordingTree.CommitGhostOnly()` -- commit without spawning (snapshots nulled)
- `RecordingTree.Discard()` -- unreserve all crew, discard tree

Task 8 will build the UI that calls these methods based on the user's choice.

For Task 7, the revert path auto-commits ghost-only as a placeholder. Task 8 replaces this with the merge dialog.

### 12. Tree Persistence

#### 12.1 Storage Architecture

Committed trees need to be persisted through save/load. The existing system uses:
- `RecordingStore.committedRecordings` (static `List<Recording>`) for in-memory storage
- `ParsekScenario.OnSave` writes each recording as a `RECORDING` ConfigNode in `.sfs` + external sidecar files
- `ParsekScenario.OnLoad` reads `RECORDING` nodes and rebuilds the list

For trees, add a parallel storage:
- `RecordingStore.committedTrees` (static `List<RecordingTree>`) for in-memory storage
- `ParsekScenario.OnSave` writes each tree as a `RECORDING_TREE` ConfigNode in `.sfs`
- Each recording within the tree uses its own external sidecar files (same paths as standalone recordings)
- `ParsekScenario.OnLoad` reads `RECORDING_TREE` nodes and rebuilds the list

**Committed tree recordings also need to participate in timeline playback.** The existing `UpdateTimelinePlayback` iterates `RecordingStore.CommittedRecordings`. Tree recordings need to appear there too. Two options:

**Option A**: On tree commit, flatten all tree recordings into `CommittedRecordings`. Ghost playback works unchanged. Tree metadata is stored separately for the merge dialog / UI but playback uses the flat list.

**Option B**: Keep tree recordings only in `committedTrees`. Extend `UpdateTimelinePlayback` to also iterate tree recordings.

**Choose Option A.** Reason: the existing playback loop is complex (1000+ lines of ghost lifecycle, part events, engine FX, resource deltas, warp stop, chain handling). Rewriting it or adding a parallel path is high risk. Instead, on tree commit, add all tree recordings to `CommittedRecordings` just like standalone recordings. The tree metadata (branch points, resource tracking) lives in `committedTrees` for the UI and any tree-specific queries.

This means:
- `RecordingStore.CommitTree(tree)`: adds all recordings from the tree to `committedRecordings`, adds the tree itself to `committedTrees`
- Ghost playback works unchanged -- each recording in the tree is just another entry in `committedRecordings`
- Tree-specific data (branch points, tree name, resource deltas) is stored in `committedTrees`
- Task 9 (tree ghost playback) can extend the playback loop with branch transitions using the tree metadata

#### 12.2 Save Format

In `ParsekScenario.OnSave`:

```
// Existing: save standalone recordings
for each rec in CommittedRecordings:
    if rec.TreeId != null: continue  // skip tree recordings (saved under RECORDING_TREE)
    recNode = node.AddNode("RECORDING")
    ... existing serialization ...

// New: save committed trees
for each tree in CommittedTrees:
    treeNode = node.AddNode("RECORDING_TREE")
    tree.Save(treeNode)  // uses existing RecordingTree.Save()
```

Wait -- `RecordingTree.Save` already handles full recording serialization via `SaveRecordingInto`. But the existing `ParsekScenario.OnSave` also writes external sidecar files. Let me re-examine.

`ParsekScenario.OnSave` calls `RecordingStore.SaveRecordingFiles(rec)` which writes:
- `.prec` file (trajectory points, orbit segments, part events)
- `_vessel.craft` file (vessel snapshot)
- `_ghost.craft` file (ghost visual snapshot)
- `.pcrf` file (ghost geometry)

The tree serialization via `RecordingTree.Save` writes recording metadata INTO the ConfigNode (inline). But the bulk data (trajectory points, snapshots) needs to go to external files too.

**Approach**: For tree recordings, use the same external file pattern. Each recording in the tree has its own `RecordingId` and gets its own sidecar files. The tree node in `.sfs` stores only lightweight metadata (same as the existing v3 format for standalone recordings).

So the save flow is:
1. For each recording in the tree: `RecordingStore.SaveRecordingFiles(rec)` -- writes sidecar files
2. `tree.Save(treeNode)` -- writes metadata to `.sfs`
3. `RecordingTree.SaveRecordingInto` writes the recording metadata (which does NOT include trajectory points, snapshots -- those are in sidecar files)

Actually, looking at `RecordingTree.SaveRecordingInto` (line 128), it writes a LOT of recording metadata but does NOT write trajectory points, orbit segments, part events, or snapshots. Those are in external files. So the pattern works: tree saves metadata in `.sfs`, sidecar files hold bulk data.

On load:
1. Read `RECORDING_TREE` nodes
2. For each tree, `RecordingTree.Load(treeNode)` loads metadata
3. For each recording in the tree, `RecordingStore.LoadRecordingFiles(rec)` loads sidecar files
4. Add recordings to `CommittedRecordings` and tree to `CommittedTrees`

#### 12.3 Revert Handling

On revert, the in-memory lists are the source of truth (existing pattern). The `initialLoadDone` guard in `ParsekScenario.OnLoad` prevents re-loading from the launch quicksave (which has stale data).

For trees, the same applies: committed trees persist in memory across reverts. Mutable state (VesselSpawned, SpawnedVesselPersistentId, LastAppliedResourceIndex) resets on revert (just like standalone recordings).

### 13. UI Integration

#### 13.1 Commit Flight Button

The existing "Commit Flight" button (`ParsekUI.cs` line 239) calls `flight.CommitFlight()`. For tree mode:

```csharp
GUI.enabled = canCommitStandalone || canCommitTree;
if (GUILayout.Button("Commit Flight"))
{
    if (flight.HasActiveTree)
        flight.CommitTreeFlight();
    else
        flight.CommitFlight();
}
```

The button enable condition changes:
- **Standalone**: `!IsRecording && !IsPlaying && recording.Count >= 2 && !HasActiveChain`
- **Tree**: `HasActiveTree` (tree is always committable -- it has at least one recording from the root)

Add a `HasActiveTree` property to `ParsekFlight`:
```csharp
public bool HasActiveTree => activeTree != null;
```

#### 13.2 Commit Feedback

After tree commit, display a screen message with leaf count:
```
"Tree committed to timeline! 3 vessels spawned."
```

Or for scene exit:
```
"Tree committed to timeline (ghost-only, no vessels spawned)."
```

### 14. New Methods and Fields

#### 14.1 RecordingTree.cs

```csharp
/// <summary>
/// Identifies spawnable leaf recordings: no children, not terminal (Destroyed/Recovered/Docked/Boarded),
/// has vessel snapshot.
/// </summary>
public List<Recording> GetSpawnableLeaves()

/// <summary>
/// Identifies ALL leaf recordings (including destroyed/recovered).
/// A leaf is any recording with ChildBranchPointId == null.
/// </summary>
public List<Recording> GetAllLeaves()

/// <summary>
/// Assigns terminal state to a recording based on vessel situation.
/// Uses vessel object if available, otherwise falls back to last known orbit/surface data.
/// </summary>
internal static TerminalState DetermineTerminalState(Vessel.Situations situation)
```

#### 14.2 ParsekFlight.cs

```csharp
/// <summary>
/// Commits the active recording tree from the Commit Flight button.
/// Finalizes all recordings, spawns leaf vessels, reserves crew.
/// The active vessel stays live.
/// </summary>
public void CommitTreeFlight()

/// <summary>
/// Stashes the active tree as a pending tree for the merge dialog.
/// Called on scene exit and revert.
/// </summary>
void StashActiveTree(double commitUT)

/// <summary>
/// Finalizes all recordings in the tree: stops active recorder, flushes data,
/// captures terminal state/orbit/position for all recordings.
/// </summary>
void FinalizeTreeRecordings(RecordingTree tree, double commitUT)

/// <summary>
/// Captures terminal orbit parameters from a vessel's current orbit.
/// </summary>
static void CaptureTerminalOrbit(Recording rec, Vessel vessel)

/// <summary>
/// Captures terminal surface position from a vessel's current state.
/// </summary>
static void CaptureTerminalPosition(Recording rec, Vessel vessel)

/// <summary>
/// Spawns all leaf vessels from a committed tree (except the active vessel).
/// Handles orbital propagation, proximity offsets, crew reservation.
/// </summary>
void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId)
```

#### 14.3 RecordingStore.cs

```csharp
// New storage for committed trees
private static List<RecordingTree> committedTrees = new List<RecordingTree>();
public static List<RecordingTree> CommittedTrees => committedTrees;

// Pending tree for merge dialog
private static RecordingTree pendingTree;
public static bool HasPendingTree => pendingTree != null;
public static RecordingTree PendingTree => pendingTree;

/// <summary>
/// Stashes a finalized tree as pending (for merge dialog on revert).
/// </summary>
public static void StashPendingTree(RecordingTree tree)

/// <summary>
/// Commits a pending tree to the timeline. Adds all recordings to CommittedRecordings
/// and the tree to CommittedTrees.
/// </summary>
public static void CommitPendingTree()

/// <summary>
/// Commits a tree directly (bypasses pending -- used by CommitTreeFlight).
/// </summary>
public static void CommitTree(RecordingTree tree)

/// <summary>
/// Discards the pending tree. Unreserves crew, cleans up.
/// </summary>
public static void DiscardPendingTree()
```

#### 14.4 ParsekScenario.cs

```csharp
// In OnSave: save committed trees
// In OnLoad: load committed trees
// Tree-aware crew reservation: iterate tree leaves for crew reservation
```

#### 14.5 BackgroundRecorder.cs

```csharp
/// <summary>
/// Finalizes all background recordings for tree commit.
/// Closes all open orbit segments, flushes loaded/physics data.
/// </summary>
public void FinalizeAllForCommit(double commitUT)
```

#### 14.6 VesselSpawner.cs

```csharp
/// <summary>
/// Spawns a vessel from a tree leaf recording. Handles orbital propagation for
/// orbital/suborbital leaves, uses terminalPosition for landed/splashed.
/// Returns the spawned vessel's persistentId (0 on failure).
/// </summary>
public static uint SpawnTreeLeaf(Recording rec, List<(CelestialBody, Vector3d)> alreadySpawned)
```

### 15. Unit Tests

#### 15.1 Leaf Identification Tests

```
Test_GetSpawnableLeaves_SimpleTree:
    Tree with root -> 2 children (one Orbiting, one Destroyed)
    Assert: 1 spawnable leaf (the Orbiting one)

Test_GetSpawnableLeaves_AllTerminalStates:
    Tree with 7 leaves, one for each terminal state
    Assert: Orbiting, Landed, Splashed, SubOrbital are spawnable
    Assert: Destroyed, Recovered, Docked, Boarded are not spawnable

Test_GetSpawnableLeaves_NoSnapshot:
    Leaf with Orbiting state but null VesselSnapshot
    Assert: not spawnable

Test_GetSpawnableLeaves_DeepTree:
    Root -> split -> split -> split (chain of splits)
    Only the final two leaves should be spawnable
    Internal nodes (have ChildBranchPointId) are not leaves

Test_GetAllLeaves_IncludesDestroyed:
    Tree with 3 leaves (1 Orbiting, 1 Destroyed, 1 Recovered)
    GetAllLeaves returns 3; GetSpawnableLeaves returns 1
```

#### 15.2 Terminal State Determination Tests

```
Test_DetermineTerminalState_AllSituations:
    ORBITING -> TerminalState.Orbiting
    LANDED -> TerminalState.Landed
    SPLASHED -> TerminalState.Splashed
    SUB_ORBITAL -> TerminalState.SubOrbital
    FLYING -> TerminalState.SubOrbital
    ESCAPING -> TerminalState.SubOrbital
    PRELAUNCH -> TerminalState.Landed
```

#### 15.3 Tree Commit Serialization Tests

```
Test_CommitTree_RoundTrip:
    Build a tree with 3 recordings (root + 2 children)
    Set terminal states, ExplicitEndUT
    Serialize via RecordingTree.Save
    Deserialize via RecordingTree.Load
    Assert all fields match

Test_CommittedTree_PersistsRecordings:
    CommitTree adds all tree recordings to CommittedRecordings
    Assert CommittedRecordings contains all 3 recordings
    Assert CommittedTrees contains 1 tree
```

#### 15.4 Scene Exit Auto-Commit Tests

```
Test_SceneExitAutoCommit_NullsSnapshots:
    Build tree with spawnable leaves
    Call scene exit commit path
    Assert all VesselSnapshots are null
    Assert tree is in CommittedTrees
```

### 16. In-Game Test Scenarios

#### 16.1 Basic Tree Commit (Commit Flight button)

1. Launch rocket with two probe cores (detachable stages)
2. Start recording (auto or F9)
3. Stage to detach one probe core (creates split branch)
4. Press "Commit Flight"
5. Verify: active vessel stays, other probe appears in orbit/on ground
6. Check Tracking Station: both vessels visible
7. Revert to Launch: ghosts replay both vessels

#### 16.2 Multi-Vessel Tree with EVA

1. Launch with crew
2. Recording starts
3. EVA one kerbal (creates split: vessel + EVA kerbal)
4. Switch back to vessel, undock a probe core (creates another split)
5. Press "Commit Flight"
6. Verify: 3 vessels exist (active vessel, EVA kerbal, probe)
7. Check crew: all reserved correctly, replacements hired

#### 16.3 Scene Exit Auto-Commit

1. Launch, start recording, undock to create tree
2. Press Esc > Space Center (leave Flight)
3. Return to Tracking Station: no extra vessels spawned (ghost-only)
4. Enter Flight, time warp past recording: ghosts appear, no vessels spawn

#### 16.4 Destroyed Leaf

1. Launch, record, undock probe
2. Switch to probe, deorbit it (make it crash)
3. Switch back to main vessel
4. Press "Commit Flight"
5. Verify: main vessel stays, crashed probe is NOT spawned
6. Ghost replay shows probe trajectory ending at crash

#### 16.5 Crew Reservation with Multiple Vessels

1. Launch with Jeb, Bill, Bob
2. Bill EVAs (split: vessel with Jeb+Bob, Bill on EVA)
3. Bob EVAs (split: vessel with Jeb, Bob on EVA)
4. Revert to Launch
5. Merge dialog (Task 8, for now auto-commits ghost-only)
6. Verify: Jeb/Bill/Bob are reserved, 3 replacements hired
7. New launch: only replacements available in crew selection

### 17. Files Modified/Created

| File | Change |
|------|--------|
| `Source/Parsek/RecordingTree.cs` | Add `GetSpawnableLeaves()`, `GetAllLeaves()`, `DetermineTerminalState()` |
| `Source/Parsek/ParsekFlight.cs` | Add `CommitTreeFlight()`, `StashActiveTree()`, `FinalizeTreeRecordings()`, `SpawnTreeLeaves()`, `CaptureTerminalOrbit()`, `CaptureTerminalPosition()`. Modify `OnSceneChangeRequested()` for tree commit. Modify `OnFlightReady()` for pending tree. Add `HasActiveTree` property. |
| `Source/Parsek/RecordingStore.cs` | Add `committedTrees`, `pendingTree`, `StashPendingTree()`, `CommitPendingTree()`, `CommitTree()`, `DiscardPendingTree()`. Add `ClearCommittedTrees()`. |
| `Source/Parsek/ParsekScenario.cs` | Extend `OnSave` to write `RECORDING_TREE` nodes (tree recordings skip standalone `RECORDING` path). Extend `OnLoad` to read `RECORDING_TREE` nodes and populate both `CommittedTrees` and `CommittedRecordings`. Extend `ReserveSnapshotCrew()` to also iterate tree leaf recordings. |
| `Source/Parsek/VesselSpawner.cs` | Add `SpawnTreeLeaf()` for orbital propagation + proximity offset with previously-spawned leaves. |
| `Source/Parsek/BackgroundRecorder.cs` | Add `FinalizeAllForCommit()`. |
| `Source/Parsek/ParsekUI.cs` | Update "Commit Flight" button to dispatch to `CommitTreeFlight()` when tree is active. Update enable condition. |
| `Source/Parsek.Tests/TreeCommitTests.cs` | New test file with leaf identification, terminal state, and serialization tests. |

### 18. Implementation Order

The implementation should proceed in this order, with each step building on the previous:

1. **Leaf identification methods** on `RecordingTree` (`GetSpawnableLeaves`, `GetAllLeaves`, `DetermineTerminalState`). These are pure methods, easily unit-tested.

2. **Terminal state capture helpers** on `ParsekFlight` (`CaptureTerminalOrbit`, `CaptureTerminalPosition`). These extract orbit/surface data from a `Vessel` object into a `Recording`.

3. **BackgroundRecorder.FinalizeAllForCommit()** -- closes all open orbit segments.

4. **FinalizeTreeRecordings()** in `ParsekFlight` -- orchestrates stopping the active recorder, flushing data, and capturing terminal state for all recordings.

5. **Tree storage layer** in `RecordingStore` -- `committedTrees`, `pendingTree`, `CommitTree`, `StashPendingTree`, `CommitPendingTree`, `DiscardPendingTree`.

6. **ParsekScenario save/load** -- extend OnSave/OnLoad for `RECORDING_TREE` nodes. Extend crew reservation to include tree recordings.

7. **SpawnTreeLeaves()** in `ParsekFlight` -- the multi-vessel spawn orchestration with crew reservation, orbital propagation, and proximity offsets.

8. **CommitTreeFlight()** in `ParsekFlight` -- ties everything together for the Commit Flight button.

9. **Scene exit path** -- modify `OnSceneChangeRequested` to call `StashActiveTree`. Modify `OnFlightReady` to handle pending trees.

10. **UI** -- update ParsekUI for the Commit Flight button dispatch.

11. **Unit tests** -- leaf identification, terminal state, serialization round-trip.

### 19. Risk Assessment

#### 19.1 HIGH RISK: Crew state corruption

Reserving crew for N vessels simultaneously is the most dangerous operation. If any step fails partway through (e.g. roster full, replacement hire fails), some crew may be reserved without replacements. The existing `ReserveCrewIn` already handles individual failures gracefully (logs warning, continues), but we need to verify that partial reservation leaves the game in a consistent state.

**Mitigation**: Wrap the entire crew reservation in a try/finally block. On failure, unreserve any partially-reserved crew. Test with the maximum number of kerbals (4 crew per vessel * N leaves).

#### 19.2 HIGH RISK: Save/load compatibility

Adding `RECORDING_TREE` nodes to the save file must not break loading of existing saves (no tree nodes present). The `OnLoad` code must gracefully handle the absence of `RECORDING_TREE` nodes.

**Mitigation**: The `node.GetNodes("RECORDING_TREE")` call returns an empty array if no such nodes exist. Existing standalone `RECORDING` nodes are still loaded by the existing code path. Tree recordings are identified by having a non-null `TreeId` field. The tree save path skips recordings with `TreeId != null` in the standalone `RECORDING` loop, so they are not double-saved.

#### 19.3 MEDIUM RISK: Vessel snapshot staleness

Background vessel snapshots captured at branch time may be stale by commit time (fuel burned, parts lost, crew changes during background operation). For the Commit Flight path, background vessels are still alive in the game -- we should re-snapshot them at commit time rather than using the branch-time snapshot.

**Mitigation**: In `FinalizeTreeRecordings`, for each background recording where the vessel is still in `FlightGlobals.Vessels`, capture a fresh snapshot via `VesselSpawner.TryBackupSnapshot(vessel)`.

#### 19.4 MEDIUM RISK: Orbital propagation accuracy

`Orbit.getPositionAtUT()` may give positions underground for suborbital trajectories, inside a body for hyperbolic escapes that shouldn't have been captured, or otherwise invalid. The crash-check logic (altitude below terrain) handles the main case, but edge cases (e.g. vessel between two SOI boundaries) may produce unexpected results.

**Mitigation**: For the Commit Flight path, orbital propagation is trivial (commitUT == now, so propagation delta is zero). The risk is primarily for the timeline playback spawn path (when UT has advanced past recording end). Add defensive checks: if propagated altitude is negative or `NaN`, fall back to the snapshot's stored position.

#### 19.5 MEDIUM RISK: Performance with many leaves

A tree with 20+ leaves (space station assembly with many modules) would trigger 20 vessel spawns, 20 crew reservations, and O(N^2) proximity checks. This could cause a frame hitch.

**Mitigation**: Not a concern for v1 (such trees are rare). Future optimization: spread spawns across multiple frames using a coroutine.

#### 19.6 LOW RISK: Race condition between scene exit and tree commit

`OnSceneChangeRequested` and `CommitTreeFlight` could theoretically race if the player clicks "Commit Flight" at the exact moment a scene change is triggered. The tree would be committed twice.

**Mitigation**: `CommitTreeFlight` sets `activeTree = null` after committing. `OnSceneChangeRequested` checks `if (activeTree != null)` before acting. The first to run wins, the second sees null and skips.

#### 19.7 LOW RISK: Tree with zero leaves

A tree where all leaves are destroyed/recovered (e.g. the player crashed all vessels) should commit successfully with zero spawned vessels. The tree still produces ghosts.

**Mitigation**: `SpawnTreeLeaves` handles empty leaf list gracefully (no-op loop). Display message: "Tree committed to timeline (no surviving vessels)."

#### 19.8 LOW RISK: Duplicate tree recording IDs in CommittedRecordings

When a tree is committed, its recordings are added to `CommittedRecordings`. If the same tree is somehow committed twice, recordings would be duplicated. The `initialLoadDone` guard and `activeTree = null` after commit prevent this, but a defensive check is warranted.

**Mitigation**: `CommitTree` checks if a tree with the same ID already exists in `CommittedTrees` before adding.

---

## Orchestrator Review Fixes

The plan review identified 4 CRITICAL and 7 IMPORTANT issues. Each is analyzed below with the resolution for the implementation agent.

### Fix 1 (CRITICAL) - `SaveRecordingInto` missing mutable state fields

**Problem (C2):** `RecordingTree.SaveRecordingInto` (line 128 of RecordingTree.cs) does NOT save these mutable per-recording fields: `SpawnedVesselPersistentId` (`spawnedPid`), `VesselDestroyed` (`vesselDestroyed`), `TakenControl` (`takenControl`), `LastAppliedResourceIndex` (`lastResIdx`), `Points.Count` (`pointCount`). These fields are saved by `ParsekScenario.OnSave` for standalone recordings (lines 56-83 of ParsekScenario.cs) but NOT by `RecordingTree.SaveRecordingInto`. After save/load, tree recordings lose spawn tracking, resource application state, and playback enabled state.

**Resolution:** Add these 5 fields to `RecordingTree.SaveRecordingInto`:
```csharp
// After ghost geometry fields (line ~221):
// Mutable playback state (parallels ParsekScenario.OnSave standalone fields)
if (rec.SpawnedVesselPersistentId != 0)
    recNode.AddValue("spawnedPid", rec.SpawnedVesselPersistentId);
if (rec.VesselDestroyed)
    recNode.AddValue("vesselDestroyed", rec.VesselDestroyed.ToString());
if (rec.TakenControl)
    recNode.AddValue("takenControl", rec.TakenControl.ToString());
recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
recNode.AddValue("pointCount", rec.Points != null ? rec.Points.Count : 0);
```

### Fix 2 (CRITICAL) - `LoadRecordingFrom` missing mutable state fields

**Problem (C3):** `RecordingTree.LoadRecordingFrom` (line 224 of RecordingTree.cs) does NOT load the mutable fields listed in Fix 1. After loading a tree, all recordings would have `SpawnedVesselPersistentId=0`, `VesselDestroyed=false`, `TakenControl=false`, `LastAppliedResourceIndex=-1` - causing duplicate spawns and duplicate resource deltas.

**Resolution:** Add loading of these fields to `LoadRecordingFrom`:
```csharp
// After ghost geometry loading (line ~395):
// Mutable playback state
string pidStr = recNode.GetValue("spawnedPid");
if (pidStr != null)
{
    uint spawnedPid;
    if (uint.TryParse(pidStr, NumberStyles.Integer, ic, out spawnedPid))
        rec.SpawnedVesselPersistentId = spawnedPid;
}
string destroyedStr = recNode.GetValue("vesselDestroyed");
if (destroyedStr != null)
{
    bool destroyed;
    if (bool.TryParse(destroyedStr, out destroyed))
        rec.VesselDestroyed = destroyed;
}
string takenStr = recNode.GetValue("takenControl");
if (takenStr != null)
{
    bool taken;
    if (bool.TryParse(takenStr, out taken))
        rec.TakenControl = taken;
}
string resIdxStr = recNode.GetValue("lastResIdx");
if (resIdxStr != null)
{
    int resIdx;
    if (int.TryParse(resIdxStr, NumberStyles.Integer, ic, out resIdx))
        rec.LastAppliedResourceIndex = resIdx;
}
// pointCount is informational - Points list is loaded from sidecar file
```

### Fix 3 (CRITICAL) - `ReserveCrewIn` is private

**Problem (C4):** `ParsekScenario.ReserveCrewIn` is `private static` (line 764 of ParsekScenario.cs). The plan calls it directly from `ParsekFlight.CommitTreeFlight` for each leaf vessel. This won't compile.

**Resolution:** Change `private static` to `internal static` on `ReserveCrewIn`. This follows the established pattern - other ParsekScenario methods used by ParsekFlight are already `internal` (e.g., `SaveRecordingMetadata`, `ResetReplacementsForTesting`).

### Fix 4 (CRITICAL) - Design doc contradiction on scene exit behavior

**Problem (C1):** The design doc says "Commit Event fires → each leaf vessel gets a ghost + a vessel spawned at last-known position". But the plan (Section 1, correctly) says scene exit should NOT spawn vessels and should null snapshots. This is consistent with existing standalone behavior.

**Resolution:** The plan is correct. Scene exit auto-commit nulls vessel snapshots (no spawn). The design doc describes the "Commit Flight" button path where vessels DO spawn. The implementation agent should follow the plan as written - no change needed.

### Fix 5 (IMPORTANT) - `OnSceneChangeRequested` must set `recorder = null` after tree handling

**Problem (I3):** After the tree commit block in `OnSceneChangeRequested` (lines 742-750), `recorder` is set to null. But the existing standalone stash block below (line 761) checks `if (recorder != null && recorder.Recording.Count > 0)`. If tree mode took the tree path, `recorder` is already null - the standalone block is safely skipped. **This is actually correct as-is.** But the plan's `CommitTreeSceneExit` method must ensure it sets `recorder = null` after processing to prevent the standalone block from double-processing.

**Resolution:** Confirmed the existing code already handles this correctly. The plan's `CommitTreeSceneExit` should be called from within the existing tree block (lines 742-750), replacing the current placeholder logic. After `CommitTreeSceneExit` completes, `recorder` is set to null (line 749 already does this), so the standalone block at line 761 is safely skipped. No additional change needed.

### Fix 6 (IMPORTANT) - Background recorder finalization order

**Problem (I1):** In `CommitTreeSceneExit`, background recorders must be finalized BEFORE the tree is committed. The current `OnSceneChangeRequested` shuts down `backgroundRecorder` at lines 752-757 AFTER the tree block. For the commit path, the background recorder's data needs to be flushed to its tree recording BEFORE committing.

**Resolution:** The plan's `FinalizeTreeRecordings` (Section 7) handles this correctly - it iterates all recordings and finalizes them. But the `OnSceneChangeRequested` flow must be restructured: move the background recorder shutdown INTO the tree commit block, before `CommitTreeSceneExit`. The implementation agent should restructure `OnSceneChangeRequested` so that for tree mode:
1. Stop/flush active recorder to tree
2. Shutdown background recorder (flush its data to tree recording)
3. Call `CommitTreeSceneExit` (which calls `FinalizeTreeRecordings` then `CommitTree`)
4. Set `recorder = null`, `backgroundRecorder = null`, `activeTree = null`

### Fix 7 (IMPORTANT) - Ghost visual snapshot for tree recordings

**Problem (I5):** `GhostVisualSnapshot` must be set for each tree recording to enable ghost playback. For standalone recordings, this is set during `StopRecording` (the `CaptureAtStop` flow). For tree recordings, background vessels may not have had a snapshot captured since branch time.

**Resolution:** In `FinalizeTreeRecordings` (Section 7), for the Commit Flight path: re-snapshot any background vessel that is still alive in `FlightGlobals.Vessels` via `VesselSpawner.TryBackupSnapshot`. For the scene exit path: null the snapshot (no vessel spawn needed). The plan already mentions this in Section 19.3 (Risk: snapshot staleness). The implementation agent should:
- Commit Flight: call `VesselSpawner.TryBackupSnapshot(vessel)` for each live background vessel, set `rec.GhostVisualSnapshot = snapshotNode`
- Scene exit: set `rec.GhostVisualSnapshot = null` for recordings that don't already have one (existing ones keep theirs for ghost playback)

### Fix 8 (IMPORTANT) - Index-based mutable state restoration breaks with trees

**Problem (I7):** `ParsekScenario.OnLoad` (lines 217-252) restores mutable state by matching `recordings[i]` to `savedRecNodes[i]` - index-based. When tree recordings are added to `CommittedRecordings` (via Option A), they appear in the list but are NOT saved as standalone `RECORDING` nodes (they're saved under `RECORDING_TREE`). The count mismatch means wrong mutable state gets applied to wrong recordings.

**Resolution:** This problem is solved by Fix 1 and Fix 2 - tree recordings save/load their own mutable state within `RECORDING_TREE` → `RECORDING` nodes via `SaveRecordingInto`/`LoadRecordingFrom`. The standalone `ParsekScenario.OnSave` skips tree recordings (`if rec.TreeId != null: continue`), and the standalone `OnLoad` only processes standalone `RECORDING` nodes. The tree recordings are loaded separately from `RECORDING_TREE` nodes with their mutable state already embedded. After both standalone and tree recordings are loaded into `CommittedRecordings`, there is no index-mismatch issue because each source handles its own mutable state independently.

### Fix 9 (IMPORTANT) - Revert snapshot preservation

**Problem (I4):** On revert, the in-memory `CommittedRecordings` list is the source of truth (existing pattern). The `initialLoadDone` guard prevents re-loading. But after tree commit, the tree recordings' `VesselSnapshot` must NOT be nulled out on revert - they need to survive for vessel spawning. The `RecordingTree.Save` → `Load` round-trip handles this for save/load, but on revert the in-memory state persists and the snapshots should already be present.

**Resolution:** This is already correct. On revert, `initialLoadDone` is true, so `OnLoad` takes the early return path (line 200-276). It restores mutable state from the launch quicksave's `RECORDING` nodes (which don't include tree recordings - they were committed during the flight). Tree recordings stay in memory with their snapshots intact. The `VesselSpawned`/`SpawnedVesselPersistentId` fields reset naturally (launch quicksave has no spawned PID for these). No change needed.

### Fix 10 (IMPORTANT) - Scene change cleanup of new fields

**Problem (I6):** The plan must clean up all tree-related fields on scene change. The existing `OnSceneChangeRequested` (lines 727-738) already clears merge and split detection state. The new commit fields must also be cleared.

**Resolution:** Any new fields added for tree commit (e.g., `commitInProgress` flag) should be cleared in `OnSceneChangeRequested`. The plan's `CommitTreeSceneExit` is called FROM `OnSceneChangeRequested`, so tree state is cleaned up as part of that flow. After commit, `activeTree = null` (line 758) handles the primary cleanup. No additional fields expected beyond what the plan describes.

### Minor findings (confirmed correct or trivial)

- **M1-M8**: The plan review's minor findings (try/catch around spawn, proximity check, etc.) are either already handled by the plan or are trivial implementation details that don't need plan-level corrections. The implementation agent should follow the plan's guidance on error handling in Section 19.1.

### Summary for implementation agent

The plan is solid. Apply these specific changes during implementation:
1. **RecordingTree.cs**: Add 5 mutable state fields to `SaveRecordingInto` and `LoadRecordingFrom` (Fixes 1-2)
2. **ParsekScenario.cs**: Change `ReserveCrewIn` from `private` to `internal` (Fix 3)
3. **ParsekFlight.cs `OnSceneChangeRequested`**: Restructure tree block to shutdown background recorder BEFORE commit (Fix 6)
4. **ParsekFlight.cs `FinalizeTreeRecordings`**: Re-snapshot live background vessels for Commit Flight path (Fix 7)
5. All other findings are confirmed correct or already handled by the plan
