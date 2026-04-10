# Fix Plan: Bug #285 — Empty parent-continuation recordings

**Bug:** #285 (renumbered from #282)
**Branch:** `fix/bug-285-empty-parent-continuation`
**Severity:** Low (cosmetic log noise + tiny disk waste, no gameplay impact)

## Problem

When a background debris vessel splits (joint break) and the **parent vessel has already been destroyed** by the time the deferred split check runs one frame later, `HandleBackgroundVesselSplit` still creates a parent-continuation `Recording`. This empty recording:

1. Gets an empty 61-byte `.prec` sidecar file on commit
2. Triggers `Trajectory file missing for … — recording degraded (0 points)` warnings on every reload
3. Triggers `FinalizeTreeRecordings: leaf '…' has no playback data` warnings
4. Clutters the recordings directory

The #284 cascade-depth cap (`MaxRecordingGeneration=1`) incidentally eliminated all **observed** cases because every parent in the reference log was gen-1+. Bug #285 stays open because a gen-0 parent (the active rocket destroyed in the same frame as a deferred split check) could still reach this path.

## Root Cause

In `BackgroundRecorder.HandleBackgroundVesselSplit` (line ~470):

```
CloseParentRecording(parentRec, parentPid, bp.Id, branchUT);  // removes from BackgroundMap
tree.BranchPoints.Add(bp);

// ← No check whether parentPid vessel still exists
string parentContRecId = Guid.NewGuid().ToString("N");
var parentContRec = new Recording { ... };
bp.ChildRecordingIds.Insert(0, parentContRecId);
tree.Recordings[parentContRecId] = parentContRec;
tree.BackgroundMap[parentPid] = parentContRecId;

OnVesselBackgrounded(parentPid);  // vessel not found → minimal on-rails state, never sampled
```

`OnVesselBackgrounded` hits the "vessel not found" path, creates a placeholder on-rails state, and the recording never receives any trajectory data. It persists as an empty leaf.

## Fix (Option 1 from bug doc)

**Don't create the parent continuation if the parent vessel is dead.**

### Code change: `BackgroundRecorder.cs`

In `HandleBackgroundVesselSplit`, after `CloseParentRecording` and before the parent-continuation block:

```csharp
// Check if parent vessel still exists before creating continuation
Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
if (parentVessel != null)
{
    // [existing parent continuation creation code, lines 479-505]
}
else
{
    ParsekLog.Info("BgRecorder",
        $"Skipping parent continuation — parent vessel destroyed: " +
        $"parentPid={parentPid} parentRec={parentRec.DebugName}");
}
```

The child recordings are still registered regardless (the `RegisterChildRecordingsFromSplit` call stays outside the guard).

The summary log at the end of the method needs adjustment to reflect whether a continuation was created:

```csharp
int continuationCount = parentVessel != null ? 1 : 0;
ParsekLog.Info("BgRecorder",
    $"Background split branch complete: bp={bp.Id} type={branchType} " +
    $"parentRecId={parentRecordingId} children={bp.ChildRecordingIds.Count} " +
    $"({continuationCount} parent continuation + {newVesselInfos.Count} new vessels)");
```

### What stays the same

- `CloseParentRecording` still runs — the parent recording gets `ChildBranchPointId` set, `ExplicitEndUT` stamped, and is removed from tracking dicts. This is correct: the parent recording is closed as a branch point regardless.
- `tree.BranchPoints.Add(bp)` still runs — the branch point exists, connecting the parent to its children.
- `RegisterChildRecordingsFromSplit` still runs — the new child vessels still get their recordings.
- The parent recording's existing trajectory data is preserved (it was closed, not deleted).

### What changes

- No empty `Recording` object created for a dead parent vessel
- No empty `.prec` file on disk
- No `Vessel backgrounded (not found)` log for the dead parent
- No `Trajectory file missing` warning on reload
- No `FinalizeTreeRecordings: leaf has no playback data` warning
- The branch point's `ChildRecordingIds` only contains the new child vessels (no continuation entry at index 0)

## Tests

### 1. Simulation test: tree structure when parent is dead

Mirror the existing `BuildBackgroundSplitBranchData_TreeStructure_ParentClosedChildCreated` test but simulate the "parent dead" path — verify:
- Parent recording has `ChildBranchPointId` set
- Branch point exists with only child recording IDs (no continuation)
- No extra recording in `tree.Recordings` for the parent continuation
- `tree.BackgroundMap` does NOT contain `parentPid`

### 2. Log assertion test

Verify the skip log message fires when parent vessel is dead. Use existing `ParsekLog.TestSinkForTesting` pattern to capture and assert on the "Skipping parent continuation" message.

### 3. Existing regression tests

All existing `BackgroundSplitTests` must continue to pass — the "parent alive" path is unchanged.

## Doc updates

- `CHANGELOG.md`: one-line entry under current version
- `docs/dev/todo-and-known-bugs.md`: strikethrough #285, add "Fix:" note
