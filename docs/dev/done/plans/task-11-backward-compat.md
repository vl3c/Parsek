# Task 11: Backward Compatibility + Chain Migration

## Overview

Ensure existing saves load correctly with the new tree-aware code. Single recordings with no tree ID must continue to work as before. Chain recordings (ChainId/ChainIndex/ChainBranch) must remain fully functional. Existing committed recordings must continue to play back as individual ghosts.

**Verdict after investigation: The tree-aware code already handles backward compatibility correctly. The existing `TreeId == null` guards are comprehensive and cover all code paths. No wrapping of legacy recordings into single-node trees is needed. This task is primarily a verification and testing task, not an implementation task.**

## Current State Analysis

### TreeId == null Guards Already in Place

Every code path that touches tree-specific behavior already checks for `rec.TreeId == null` (or `rec.TreeId != null`) and falls through to the legacy behavior when TreeId is null. Here is the complete audit:

#### 1. ParsekScenario.cs -- OnSave (line 49)

```csharp
if (rec.TreeId != null)
    continue;
```

**Effect:** Tree recordings are skipped when saving standalone RECORDING nodes. They are instead saved under RECORDING_TREE nodes. Legacy recordings (TreeId == null) are saved as before.

**Backward compat:** Correct. Legacy recordings have TreeId == null, so they are always written as RECORDING nodes, exactly as before the tree feature existed.

#### 2. ParsekScenario.cs -- OnLoad revert path (line 261)

```csharp
if (recordings[i].TreeId != null) continue;
```

**Effect:** When restoring mutable state from saved RECORDING nodes (for standalone recordings), tree recordings are skipped (they get their state from RECORDING_TREE nodes).

**Backward compat:** Correct. Legacy recordings have TreeId == null, so they always enter the restore loop. The `standaloneIdx` counter properly maps only standalone recordings to the saved RECORDING nodes.

#### 3. ParsekScenario.cs -- OnLoad tree recording reset (line 307)

```csharp
if (recordings[i].TreeId == null) continue;
```

**Effect:** Only tree recordings get their mutable state reset. Legacy recordings are untouched.

**Backward compat:** Correct. Legacy recordings skip this block entirely.

#### 4. ParsekScenario.cs -- OnLoad initial load path (lines 480-509)

Tree nodes are loaded after standalone recordings:
```csharp
ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
if (treeNodes.Length > 0) { ... }
```

**Backward compat:** Correct. Old save files simply have zero RECORDING_TREE nodes. The `if (treeNodes.Length > 0)` guard means this entire block is skipped. All recordings come from the RECORDING nodes, exactly as before.

#### 5. ParsekScenario.cs -- Revert detection (lines 235-242)

```csharp
int savedTreeRecCount = 0;
for (int t = 0; t < savedTreeNodesForRevert.Length; t++)
    savedTreeRecCount += savedTreeNodesForRevert[t].GetNodes("RECORDING").Length;
int totalSavedRecCount = savedRecNodes.Length + savedTreeRecCount;
```

**Backward compat:** Correct. Old saves have zero tree nodes, so `savedTreeRecCount = 0` and `totalSavedRecCount = savedRecNodes.Length`, which is exactly the pre-tree behavior.

#### 6. ResourceBudget.ComputeTotal (line 194)

```csharp
if (recordings[i].TreeId != null) continue; // handled by tree-level delta
```

**Backward compat:** Correct. Legacy recordings have TreeId == null, so they always enter the per-recording cost computation. The `trees` parameter defaults to `null`, and the tree loop (lines 201-209) safely checks `if (trees != null)`, so passing null (or an empty list) works.

This is already tested in `ResourceBudgetTests.ComputeTotal_BackwardCompat_NullTrees` (line 1082).

#### 7. ParsekFlight.UpdateTimelinePlayback -- Resource deltas (lines 3621, 3728, 3750, 3762, 3771, 3779, 3791)

Seven places check `rec.TreeId == null` before calling `ApplyResourceDeltas`:
```csharp
if (rec.TreeId == null)
    ApplyResourceDeltas(rec, currentUT);
```

**Backward compat:** Correct. Legacy recordings always have TreeId == null, so `ApplyResourceDeltas` is always called. This is exactly the pre-tree behavior.

For tree recordings, resource deltas are handled by `ApplyTreeResourceDeltas` / `ApplyTreeLumpSum` instead.

#### 8. ParsekFlight.UpdateTimelinePlayback -- Spawn suppression (lines 3663-3669)

```csharp
// Non-leaf tree recordings should never spawn (they branched into children)
if (needsSpawn && rec.ChildBranchPointId != null) { needsSpawn = false; ... }
```

**Backward compat:** Correct. Legacy recordings always have `ChildBranchPointId == null` (it defaults to null), so this check never triggers for them.

#### 9. ParsekFlight.UpdateTimelinePlayback -- Terminal state spawn suppression (lines 3671-3682)

```csharp
if (needsSpawn && rec.TerminalStateValue.HasValue) { ... }
```

**Backward compat:** Correct. Legacy recordings always have `TerminalStateValue == null` (defaults to null), so this check never triggers for them.

#### 10. ParsekFlight.TakeControlOfGhost (lines 5327-5336)

```csharp
if (rec.TreeId != null)
{
    var tree = FindCommittedTree(rec.TreeId);
    if (tree != null && !tree.ResourcesApplied)
        ApplyTreeLumpSum(tree);
}
else
{
    ApplyResourceDeltas(rec, ut);
}
```

**Backward compat:** Correct. Legacy recordings always take the `else` branch, calling `ApplyResourceDeltas` as before.

#### 11. ParsekFlight.ApplyTreeResourceDeltas (lines 3989-4010)

Iterates `RecordingStore.CommittedTrees`. For legacy saves, this list is empty.

**Backward compat:** Correct. The loop body never executes when there are no trees.

#### 12. ParsekUI.DrawResourceBudget / DrawCompactBudgetLine (lines 300, 372)

```csharp
RecordingStore.CommittedTrees
```

Passes `CommittedTrees` (which is empty for legacy saves) to `ComputeTotal`.

**Backward compat:** Correct. Empty tree list means zero tree cost contribution.

### New Fields with Safe Defaults

All new fields on `Recording` added for tree support have null/zero/NaN defaults that are inert for legacy recordings:

| Field | Default | Legacy Behavior |
|---|---|---|
| `TreeId` | `null` | All TreeId guards fall through to legacy path |
| `VesselPersistentId` | `0` | Not used for standalone recordings |
| `TerminalStateValue` | `null` | Never triggers spawn suppression |
| `ParentBranchPointId` | `null` | Never referenced for standalone recordings |
| `ChildBranchPointId` | `null` | Never triggers non-leaf spawn suppression |
| `ExplicitStartUT` | `double.NaN` | Falls through to `Points[0].ut` / `Points[last].ut` |
| `ExplicitEndUT` | `double.NaN` | Falls through to `Points[0].ut` / `Points[last].ut` |
| `SurfacePos` | `null` | Background positioning check skips null |

### What About the Design Doc's "Wrap in Single-Node Tree"?

The design doc says: "Single recordings with no tree ID -> wrap in single-node RecordingTree on load."

**This is NOT needed.** The existing null guards handle legacy recordings correctly throughout:
- OnSave: skips tree recordings when writing RECORDING nodes (legacy recordings are always written)
- OnLoad: loads standalone RECORDING nodes as before, tree nodes are separate
- Playback: resource deltas use per-recording path when TreeId == null
- Budget: per-recording cost calculation when TreeId == null
- Spawn: all spawn logic works on recordings directly, tree-specific checks have null guards

Wrapping legacy recordings in single-node trees would add complexity and risk:
1. Would need to generate synthetic tree IDs for every legacy recording
2. Would need to compute synthetic `DeltaFunds`/`DeltaScience`/`DeltaReputation` for each wrapper tree
3. Would change the save format (RECORDING -> RECORDING_TREE) on next save, making downgrades impossible
4. Would break chain recordings that span multiple standalone recordings (chains operate on flat CommittedRecordings list)
5. No actual benefit -- all code paths already work correctly with TreeId == null

**Recommendation: Do NOT wrap legacy recordings. The null guards are complete and correct.**

## Chain Recording Compatibility

Chains use a completely separate linkage system (ChainId/ChainIndex/ChainBranch) from trees (TreeId/BranchPoint/ParentBranchPointId/ChildBranchPointId). These two systems are orthogonal:

### Chain Fields Are NOT Tree Fields

| Feature | Chain System | Tree System |
|---|---|---|
| Grouping key | `ChainId` (string) | `TreeId` (string) |
| Ordering | `ChainIndex` (int) | BranchPoint graph |
| Branching | `ChainBranch` (int, 0=primary) | BranchPoint split/merge |
| Purpose | Dock/undock/EVA continuation, atmo split | Multi-vessel tracking |
| Storage | Flat in CommittedRecordings | Nested in RECORDING_TREE + CommittedRecordings |
| Resource model | Per-recording deltas | Tree-level lump sum |

### Chain Code Paths (All Untouched by Tree Changes)

1. **MergeDialog.Show** (line 23): Detects chain via `!string.IsNullOrEmpty(pending.ChainId)`. Routes to `ShowChainDialog` for chains. Unaffected by tree code.

2. **MergeDialog.ShowChainDialog** (line 128): Shows chain-specific dialog with segment count and branch count. No tree references.

3. **RecordingStore.IsChainMidSegment** (line 416): Searches `committedRecordings` by ChainId/ChainIndex. No tree references.

4. **RecordingStore.GetChainEndUT** (line 434): Finds max EndUT for chain branch 0. No tree references.

5. **RecordingStore.GetChainRecordings** (line 452): Returns all recordings with matching ChainId. No tree references.

6. **RecordingStore.IsChainLooping** (line 644): Checks if all branch-0 segments are looping. No tree references.

7. **RecordingStore.IsChainFullyDisabled** (line 660): Checks if all segments are disabled. No tree references.

8. **ParsekFlight.UpdateTimelinePlayback**: Chain mid-segment hold (line 3774), chain spawn suppression (line 3648), looping/disabled chain check (line 3656). All use ChainId, not TreeId.

9. **ParsekFlight chain state**: `activeChainId`, `activeChainNextIndex`, `activeChainPrevId`, `activeChainCrewName`, `pendingChainContinuation`. All separate from tree state (`activeTree`).

10. **ParsekScenario OnSave/OnLoad**: Chain fields (chainId, chainIndex, chainBranch) serialized alongside standalone recording metadata. Unaffected by tree serialization.

### Can a Recording Be Both Chain AND Tree?

No, and this is by design. The tree system supersedes chains for multi-vessel tracking:
- **Chains** handle linear continuations: EVA exit/board, dock/undock sequences, atmospheric splits. Each segment is a standalone recording with chain linkage.
- **Trees** handle multi-vessel branching: split/merge events create a graph of recordings grouped under a RecordingTree.

In practice, a recording could technically have both ChainId and TreeId set (the fields are independent), but the current code never does this. Tree-mode recordings set TreeId; legacy chain recordings set ChainId. The code paths do not conflict because:
- Chain logic keys on `ChainId != null`
- Tree logic keys on `TreeId != null`
- Resource budget skips TreeId != null recordings from per-recording sum
- Resource budget processes chains (TreeId == null) via per-recording sum

## Gap Analysis

After thorough review of all source files, **no gaps were found.** Every code path that touches tree-specific behavior has appropriate null/empty guards for the legacy case.

### Specific checks performed:

1. **ParsekScenario.OnSave**: Tree recordings skipped with `TreeId != null` guard (line 49). Tree nodes saved separately (lines 92-109). Legacy recordings unaffected.

2. **ParsekScenario.OnLoad (initial)**: Tree nodes loaded after standalone recordings (lines 480-509). Old saves have zero tree nodes. Legacy recording load (lines 386-475) is identical to pre-tree code.

3. **ParsekScenario.OnLoad (revert/scene change)**: Tree recording mutable state handled separately (lines 299-356). Standalone recordings restored from RECORDING nodes with `standaloneIdx` counter that skips TreeId != null recordings (lines 257-297).

4. **ResourceBudget.ComputeTotal**: TreeId != null recordings excluded from per-recording sum (line 194). `trees` parameter is null-safe (line 201). Already tested.

5. **ParsekFlight.UpdateTimelinePlayback**: All seven `ApplyResourceDeltas` calls guarded by `TreeId == null`. Tree resource deltas handled separately by `ApplyTreeResourceDeltas` (line 3797).

6. **Spawn logic**: `ChildBranchPointId` and `TerminalStateValue` checks only suppress spawning when those fields are non-null (both null for legacy recordings).

7. **MergeDialog**: Legacy standalone dialog (line 45) and chain dialog (line 128) both work independently of tree dialog (line 298). Tree dialog only called from `OnFlightReady` when `RecordingStore.HasPendingTree` is true.

8. **GhostVisualBuilder**: No tree-specific code. Ghost building works on individual recordings regardless of tree membership.

9. **VesselSpawner**: No tree-specific code. Vessel spawning works on individual recordings regardless of tree membership.

10. **RecordingTree.Load**: All fields use `GetValue` which returns null for missing keys. All numeric fields use `TryParse` which defaults to 0 on failure. All string fields default to null or empty. No crash risk from loading a tree with missing fields.

## Specific Changes Needed

**None.** The code is already backward compatible. All TreeId == null guards are in place, chain logic is untouched, and the save/load format gracefully handles the absence of RECORDING_TREE nodes.

## Testing Strategy

### Automated Tests (Already Passing)

All 1028 existing tests pass (plus 1 expected skip: `QuaternionSlerp_WithNaNFactor_ProducesNaN`). Key backward-compat tests already in the suite:

1. **ResourceBudgetTests.ComputeTotal_BackwardCompat_NullTrees** (line 1082): Verifies `trees=null` works identically to the pre-tree behavior.

2. **ResourceBudgetTests.ComputeTotal_TreeRecordingsSkipped** (line 1001): Verifies recordings with TreeId set are excluded from per-recording budget calculation.

3. **ResourceBudgetTests.ComputeTotal_MixedTreeAndStandalone** (line 1038): Verifies standalone and tree recordings coexist correctly in the budget.

4. **ChainTests, DockUndockChainTests, AtmosphereSplitTests**: All chain-related tests continue to pass, confirming chain functionality is intact.

5. **RecordingTreeTests**: Comprehensive tree serialization round-trip tests confirming all new fields save/load correctly with safe defaults for missing keys.

6. **TreeCommitTests**: CommitTree, StashPendingTree, DiscardPendingTree, GetSpawnableLeaves, IsSpawnableLeaf -- all tree commit operations verified.

7. **VesselPersistenceTests, RecordingStoreTests, RecordingsManagerTests**: Core recording store operations verified (no tree regressions).

8. **SyntheticRecordingTests**: All 8 synthetic recordings (Pad Walk, KSC Hopper, Flea Flight, Suborbital Arc, KSC Pad Destroyed, Orbit-1, Close Spawn Conflict, Island Probe) build and inject correctly. These are all standalone recordings (no trees) and exercise the legacy save format.

### New Tests to Add (Verification Tests)

While no code changes are needed, adding explicit backward-compat verification tests would document the invariants and prevent future regressions:

#### Test 1: Legacy recording fields are inert for tree checks
```
LegacyRecording_TreeFieldsDefaultToNull
- Create a Recording via the legacy path (no TreeId, no TerminalState, etc.)
- Assert TreeId == null
- Assert TerminalStateValue == null
- Assert ChildBranchPointId == null
- Assert ParentBranchPointId == null
- Assert ExplicitStartUT == double.NaN
- Assert ExplicitEndUT == double.NaN
- Assert SurfacePos == null
- Assert VesselPersistentId == 0
```

#### Test 2: Legacy save format loads correctly (no RECORDING_TREE nodes)
```
OnLoad_NoTreeNodes_LoadsStandaloneRecordings
- Build a ConfigNode with RECORDING nodes only (no RECORDING_TREE)
- Load via ParsekScenario.OnLoad path
- Verify all recordings load with TreeId == null
- Verify CommittedTrees is empty
```

#### Test 3: Mixed save format loads correctly
```
OnLoad_MixedTreeAndStandalone_BothLoad
- Build a ConfigNode with both RECORDING and RECORDING_TREE nodes
- Load via ParsekScenario.OnLoad path
- Verify standalone recordings have TreeId == null
- Verify tree recordings have TreeId matching their tree
- Verify CommittedTrees has the correct tree(s)
```

#### Test 4: Chain recording with tree recordings coexist
```
ComputeTotal_ChainAndTree_NoInterference
- Create standalone chain recordings (ChainId set, TreeId null)
- Create tree recordings (TreeId set)
- Create the corresponding RecordingTree
- Call ComputeTotal with all three lists
- Verify chain recordings contribute to per-recording sum
- Verify tree recordings are excluded from per-recording sum
- Verify tree delta is added separately
```

#### Test 5: Revert detection with tree recordings
```
RevertDetection_TreeRecordingsCounted
- Simulate a save with 2 standalone + 1 tree (3 total in memory)
- Simulate revert save with 2 standalone + 0 tree (2 total in save)
- Verify isRevert == true (3 in memory > 2 in save)
```

### Manual Verification Steps

These require loading KSP with the mod installed:

1. **Load existing save with standalone recordings**
   - Start KSP, load a career save that has committed recordings from before the tree feature
   - Verify KSP.log shows correct recording count with no errors
   - Enter flight, verify ghosts appear and play back at correct UTs
   - Verify resource deltas apply correctly (check funds before/after ghost playback)
   - Verify vessel spawning works at EndUT

2. **Load existing save with chain recordings**
   - Load a save that has dock/undock chain recordings or atmosphere-split chains
   - Verify all chain segments play back in sequence
   - Verify mid-chain segments hold at final position
   - Verify vessel spawns at the chain's EndUT (not mid-chain)
   - Verify chain merge dialog appears on revert

3. **Record standalone + tree in same save**
   - Start fresh career, record a simple flight (no splits) -> revert -> merge
   - Record another flight that involves an undock (triggers tree creation) -> revert -> merge
   - Verify both the standalone ghost and tree ghosts play back correctly
   - Verify budget display shows both standalone and tree costs

4. **Revert with tree recordings present**
   - Commit a tree recording, then launch a new flight
   - Revert to launch
   - Verify the tree recording's ghosts still play back correctly
   - Verify the new recording gets the merge dialog
   - Verify mutable state (SpawnedVesselPersistentId, LastAppliedResourceIndex) resets correctly for tree recordings

5. **Scene transitions with tree recordings**
   - Commit a tree, switch to Tracking Station, switch back to Flight
   - Verify tree recordings persist across the scene transition
   - Verify mutable state is restored from the save (not reset)

6. **Wipe with tree recordings**
   - Commit both standalone and tree recordings
   - Click "Wipe Recordings" in Parsek UI
   - Verify both standalone and tree recordings are cleared
   - Verify CommittedTrees is empty
   - Verify crew reservations are cleared

## Implementation Plan

### Phase 1: Verification (no code changes)

1. Run `dotnet test` -- confirm all 1028 tests pass (DONE: verified 1028 passed, 1 skipped)
2. Run `dotnet build` -- confirm clean build
3. Review all `TreeId == null` guards in ParsekFlight.cs (DONE: 7 resource delta guards + 1 take-control guard, all correct)
4. Review ParsekScenario OnSave/OnLoad (DONE: tree recordings properly segregated from standalone)
5. Review ResourceBudget.ComputeTotal (DONE: TreeId != null skip + null-safe trees parameter)
6. Review MergeDialog (DONE: standalone, chain, and tree dialogs are independent paths)
7. Review GhostVisualBuilder (DONE: no tree references)
8. Review VesselSpawner (DONE: no tree references)

### Phase 2: Add verification tests

Add a new test file `BackwardCompatTests.cs` with the 5 tests outlined above. These are purely additive -- no existing code changes.

File: `Source/Parsek.Tests/BackwardCompatTests.cs`

Tests to add:
- `LegacyRecording_TreeFieldsDefaultToNull`
- `ComputeTotal_StandaloneRecording_TreeIdNull_IncludedInBudget`
- `ComputeTotal_ChainAndTree_NoInterference`
- `RecordingTree_Load_MissingFields_DefaultsSafely`
- `RevertDetection_TreeRecordingsCounted_InTotalSavedRecCount`

### Phase 3: Manual KSP verification

Run through the 6 manual verification scenarios listed above. Check KSP.log for any `[Parsek]` errors or warnings.

## Risk Assessment

**Risk: Very Low.** This task is a verification exercise. The tree-aware code was designed with backward compatibility from the start -- every tree-specific code path has null guards. No code changes are needed.

The only deliverable is the verification test file, which purely documents existing behavior without changing any production code.

## Files Reviewed

| File | Tree References | Backward Compat Status |
|---|---|---|
| `ParsekScenario.cs` | OnSave/OnLoad: TreeId != null skip, RECORDING_TREE save/load | Complete |
| `RecordingStore.cs` | CommittedTrees list, CommitTree, PendingTree | Complete |
| `ParsekFlight.cs` | 7x resource delta guards, spawn suppression, tree dialog | Complete |
| `ResourceBudget.cs` | ComputeTotal: TreeId skip + null trees param | Complete |
| `MergeDialog.cs` | ShowTreeDialog (separate from Show/ShowChainDialog) | Complete |
| `RecordingTree.cs` | Save/Load with TryParse defaults | Complete |
| `ParsekUI.cs` | CommittedTrees passed to ComputeTotal | Complete |
| `GhostVisualBuilder.cs` | No tree references | N/A |
| `VesselSpawner.cs` | No tree references | N/A |

## Summary

Task 11 is effectively a verification task. The tree-aware code in Phase 6 was built with backward compatibility as a core design principle:

1. Every tree-specific code path is guarded by `TreeId != null` or equivalent checks
2. Legacy recordings (TreeId == null) follow exactly the same code paths as before
3. Chain recordings are completely independent of tree recordings
4. The save format is additive (RECORDING_TREE nodes added alongside existing RECORDING nodes)
5. Old saves without RECORDING_TREE nodes load correctly because the tree-loading block is guarded by `treeNodes.Length > 0`
6. All 1028 existing tests pass without modification
7. No production code changes are needed
8. A small set of explicit verification tests should be added to document these invariants
