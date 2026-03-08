# Task 8: Tree-Aware Merge Dialog

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

Task 8 replaces the placeholder auto-commit for pending recording trees with a proper merge dialog. Currently, when the player reverts with an active recording tree, `ParsekFlight.OnFlightReady` auto-commits the tree without asking. Task 8 adds `MergeDialog.ShowTreeDialog` which presents a vessel summary and lets the player choose between "Merge to Timeline" and "Discard".

This is a focused UI task. The infrastructure for tree commit (`RecordingStore.CommitPendingTree`), tree discard (`RecordingStore.DiscardPendingTree`), and crew reservation (`ParsekScenario.ReserveSnapshotCrew`) already exists. We are wiring these into a proper dialog.

**Files modified:**
- `Source/Parsek/MergeDialog.cs` -- Add `ShowTreeDialog` static method
- `Source/Parsek/ParsekFlight.cs` -- Replace auto-commit placeholder with `ShowTreeDialog` call

**No new files.** No test files (UI code -- tested in-game).

### 2. Existing Dialog Pattern Analysis

The existing `MergeDialog` class (`Source/Parsek/MergeDialog.cs`) is a static class with the following structure:

#### 2.1 Entry point: `Show(Recording pending)`
- Detects chain vs. standalone recording
- Routes to `ShowChainDialog` or `ShowStandaloneDialog`

#### 2.2 `ShowStandaloneDialog(Recording pending)` (line 44)
Pattern:
1. Compute display data (duration, recommended action)
2. Log the dialog parameters
3. Build `DialogGUIButton[]` array based on recommended action
4. Each button callback: perform the action (commit/discard/unreserve) + show screen message + log
5. Build message string via `BuildMergeMessage`
6. Spawn dialog:
```csharp
PopupDialog.SpawnPopupDialog(
    new Vector2(0.5f, 0.5f),    // anchor min (center)
    new Vector2(0.5f, 0.5f),    // anchor max (center)
    new MultiOptionDialog(
        "ParsekMerge",           // dialog ID
        message,                 // body text
        "Parsek -- Merge Recording",  // title
        HighLogic.UISkin,        // skin
        buttons                  // button array
    ),
    false,                       // dismissable
    HighLogic.UISkin             // skin again
);
```

#### 2.3 `ShowChainDialog(Recording pending, List<Recording> siblings, int totalSegments)` (line 127)
Pattern:
1. Log chain parameters
2. Build buttons based on whether vessel snapshot exists + not destroyed
3. "Merge to Timeline" callback: `RecordingStore.CommitPending()` + `ParsekScenario.ReserveSnapshotCrew()` + `ParsekScenario.SwapReservedCrewInFlight()`
4. "Discard All" callback: calls `DiscardChain()` helper which unreserves crew from pending + all siblings, removes chain recordings, discards pending
5. Build message string inline (segment label, vessel name, duration, distance, status line)
6. Spawn dialog with same `PopupDialog.SpawnPopupDialog` pattern

#### 2.4 Key conventions
- Dialog ID: `"ParsekMerge"` (same for all dialogs -- ensures only one merge dialog at a time)
- Title format: `"Parsek -- <description>"`
- Logging: `ParsekLog.Info("MergeDialog", ...)` for user actions, `ParsekLog.Warn` for errors
- Screen messages: `ParsekLog.ScreenMessage(msg, seconds)` for user feedback after button press
- Crew handling: "Merge" path calls `ReserveSnapshotCrew()` + `SwapReservedCrewInFlight()`. "Discard" path calls `UnreserveCrewInSnapshot()` on all snapshots
- `CultureInfo.InvariantCulture` for all number formatting

### 3. ShowTreeDialog Design

#### 3.1 Signature

```csharp
internal static void ShowTreeDialog(RecordingTree tree)
```

Called from `ParsekFlight.OnFlightReady` when `RecordingStore.HasPendingTree` is true. The tree is `RecordingStore.PendingTree`.

The method is `internal static` (consistent with the class being `public static` but the individual dialog methods being non-public).

#### 3.2 Data Gathering

From the `RecordingTree tree` parameter, compute:

```csharp
// All leaves (any recording with ChildBranchPointId == null)
var allLeaves = tree.GetAllLeaves();

// Spawnable leaves (not destroyed/recovered/docked/boarded, has snapshot)
var spawnableLeaves = tree.GetSpawnableLeaves();

// Total tree duration: max EndUT - min StartUT across all recordings
double minStartUT = double.MaxValue;
double maxEndUT = double.MinValue;
foreach (var rec in tree.Recordings.Values)
{
    double start = rec.StartUT;
    double end = rec.EndUT;
    if (start < minStartUT) minStartUT = start;
    if (end > maxEndUT) maxEndUT = end;
}
double duration = maxEndUT - minStartUT;

// Surviving vs destroyed leaf counts
int survivingCount = spawnableLeaves.Count;
int destroyedCount = 0;
foreach (var leaf in allLeaves)
{
    if (leaf.TerminalStateValue.HasValue && leaf.TerminalStateValue.Value == TerminalState.Destroyed)
        destroyedCount++;
}
```

Note: `rec.StartUT` and `rec.EndUT` are computed properties on `Recording`. `StartUT` returns `ExplicitStartUT` if set, else first point UT. `EndUT` returns `ExplicitEndUT` if set, else last point UT. For background-only recordings that never had trajectory points, `ExplicitStartUT`/`ExplicitEndUT` are used (set during tree finalization in Task 7).

#### 3.3 Duration Formatting

Format the duration in a human-readable way. The existing dialogs use raw seconds (e.g. `"123.4s"`). For trees which can be much longer, use a helper:

```csharp
internal static string FormatDuration(double seconds)
{
    if (seconds < 60)
        return seconds.ToString("F0", CultureInfo.InvariantCulture) + "s";
    if (seconds < 3600)
    {
        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return s > 0 ? $"{m}m {s}s" : $"{m}m";
    }
    int h = (int)(seconds / 3600);
    int min = (int)((seconds % 3600) / 60);
    return min > 0 ? $"{h}h {min}m" : $"{h}h";
}
```

This helper is `internal static` so existing dialogs could use it in the future, but we only call it from `ShowTreeDialog` for now.

#### 3.4 Terminal State Display Text

Map `TerminalState` to user-friendly situation text. This needs the celestial body name for orbital/landed states, which comes from the recording's terminal orbit body or terminal position body.

```csharp
internal static string GetLeafSituationText(RecordingStore.Recording leaf)
{
    if (leaf.TerminalStateValue.HasValue)
    {
        switch (leaf.TerminalStateValue.Value)
        {
            case TerminalState.Orbiting:
                string orbBody = leaf.TerminalOrbitBody ?? "unknown";
                return $"Orbiting {orbBody}";
            case TerminalState.Landed:
                string landBody = leaf.TerminalPosition.HasValue
                    ? leaf.TerminalPosition.Value.bodyName : "unknown";
                return $"Landed on {landBody}";
            case TerminalState.Splashed:
                string splashBody = leaf.TerminalPosition.HasValue
                    ? leaf.TerminalPosition.Value.bodyName : "unknown";
                return $"Splashed on {splashBody}";
            case TerminalState.SubOrbital:
                string subBody = leaf.TerminalOrbitBody ?? "unknown";
                return $"Sub-orbital, {subBody}";
            case TerminalState.Destroyed:
                return "Destroyed";
            case TerminalState.Recovered:
                return "Recovered";
            case TerminalState.Docked:
                return "Docked";
            case TerminalState.Boarded:
                return "Boarded";
        }
    }

    // Fallback: use VesselSituation string if available (legacy/standalone recordings)
    if (!string.IsNullOrEmpty(leaf.VesselSituation))
        return leaf.VesselSituation;

    return "Unknown";
}
```

This method is `internal static` for testability and potential reuse.

#### 3.5 Per-Leaf Summary Lines

Build a summary line for each leaf in the tree. The design doc shows:

```
  Orbit Stage .... Kerbin orbit (180km)
  Mun Lander ..... Landed on Mun
  Capsule B ...... Landed on Kerbin  <- you are here
```

For the dialog, we use a simpler text format (KSP's `MultiOptionDialog` only supports plain text, not rich formatting or monospace alignment). Each leaf gets one line:

```csharp
string BuildLeafSummary(RecordingTree tree)
{
    var allLeaves = tree.GetAllLeaves();
    var sb = new System.Text.StringBuilder();

    for (int i = 0; i < allLeaves.Count; i++)
    {
        var leaf = allLeaves[i];
        string situation = GetLeafSituationText(leaf);
        string marker = (leaf.RecordingId == tree.ActiveRecordingId) ? "  <-- you are here" : "";
        sb.AppendLine($"  {leaf.VesselName} -- {situation}{marker}");
    }

    return sb.ToString();
}
```

Note: `tree.ActiveRecordingId` identifies the recording that was being actively recorded when the player reverted. This is the player's current vessel -- the one they were flying. This may be null if all recordings were backgrounded (e.g., the player switched to an unrelated vessel before reverting). In that case no marker is shown, which is correct.

#### 3.6 Dialog Message Assembly

```csharp
// Header: tree name + vessel count + duration
string vesselCountText;
if (destroyedCount > 0)
    vesselCountText = $"{survivingCount} vessels ({destroyedCount} destroyed)";
else
    vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")}";

string header = $"\"{tree.TreeName}\" -- {vesselCountText}, {FormatDuration(duration)}\n\n";

// Leaf summary
string leafSummary = BuildLeafSummary(tree);

// Footer: what will happen
string footer;
if (survivingCount > 0)
    footer = $"\nAll surviving vessels will appear after ghost playback.";
else
    footer = $"\nAll vessels were destroyed. Ghosts will replay the mission.";

string message = header + leafSummary + footer;
```

#### 3.7 Button Callbacks

Two buttons only (per design doc):

**"Merge to Timeline":**
```csharp
new DialogGUIButton("Merge to Timeline", () =>
{
    RecordingStore.CommitPendingTree();
    ParsekScenario.ReserveSnapshotCrew();
    ParsekScenario.SwapReservedCrewInFlight();
    int leafCount = spawnableLeaves.Count;
    if (leafCount > 0)
        ParsekLog.ScreenMessage(
            $"Tree merged -- {leafCount} vessel(s) will appear after ghost playback", 3f);
    else
        ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);
    ParsekLog.Info("MergeDialog",
        $"User chose: Tree Merge to Timeline (tree='{tree.TreeName}', " +
        $"recordings={tree.Recordings.Count}, spawnableLeaves={leafCount})");
})
```

The sequence `CommitPendingTree` -> `ReserveSnapshotCrew` -> `SwapReservedCrewInFlight` matches the existing standalone/chain merge pattern:
1. `CommitPendingTree()` moves the tree from `pendingTree` to `committedTrees` and all its recordings to `committedRecordings` (via `CommitTree`).
2. `ReserveSnapshotCrew()` iterates all committed recordings with vessel snapshots and marks crew as Assigned. This handles all tree recordings since they are now in `committedRecordings`.
3. `SwapReservedCrewInFlight()` replaces reserved crew on the player's active vessel with hired replacements.

**"Discard":**
```csharp
new DialogGUIButton("Discard", () =>
{
    // Unreserve crew from all tree recordings that have snapshots
    foreach (var rec in tree.Recordings.Values)
    {
        if (rec.VesselSnapshot != null)
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
    }
    RecordingStore.DiscardPendingTree();
    ParsekLog.ScreenMessage("Recording tree discarded", 2f);
    ParsekLog.Info("MergeDialog",
        $"User chose: Tree Discard (tree='{tree.TreeName}', " +
        $"recordings={tree.Recordings.Count})");
})
```

The discard path:
1. Unreserves crew from ALL recording snapshots in the tree (not just leaves -- some non-leaf recordings may still have snapshots if they were background recordings that branched).
2. `DiscardPendingTree()` deletes recording files and nulls `pendingTree`.

**Important note on crew unreservation before discard:** The tree recordings are NOT yet in `committedRecordings` when the discard button is pressed (they're still in `pendingTree`). So `ReserveSnapshotCrew()` has NOT been called on them. However, crew may have been reserved during scenario load (`ParsekScenario.OnLoad` calls `ReserveSnapshotCrew()` which iterates `committedRecordings`). Since pending tree recordings are NOT in `committedRecordings`, their crew has NOT been reserved through that path either.

The question is: does any crew need unreserving at discard time? Let's trace the flow:
1. Player reverts -> `OnSceneChangeRequested` fires -> `CommitTreeSceneExit` stashes tree as pending
2. Scene loads -> `ParsekScenario.OnLoad` fires -> calls `ReserveSnapshotCrew()` on `committedRecordings` only (pending tree recordings are NOT in this list)
3. `OnFlightReady` fires -> shows tree dialog

So at dialog time, crew in the pending tree's snapshots have NOT been reserved. However, `CommitTreeSceneExit` nulls all vessel snapshots (line 3194-3198 of ParsekFlight.cs). Wait -- that means by the time we reach `OnFlightReady`, the pending tree's snapshots are already null.

Let me re-examine this. `CommitTreeSceneExit` (line 3185):
```csharp
// Null all vessel snapshots (no spawning on scene exit)
foreach (var rec in activeTree.Recordings.Values)
{
    rec.VesselSnapshot = null;
}
RecordingStore.StashPendingTree(activeTree);
```

This nulls ALL snapshots before stashing. This is the scene-exit path which was designed for auto-commit ghost-only. But for Task 8, we want the merge dialog to allow spawning. If we null snapshots before showing the dialog, "Merge to Timeline" can never spawn vessels.

**This is a critical issue that Task 8 must fix.** The `CommitTreeSceneExit` method nulls snapshots because it was designed as a ghost-only commit path. For the revert case (going back to Flight), we need to PRESERVE snapshots so the dialog can offer vessel spawning.

### 4. Revert vs. Scene-Exit: Two Paths for Pending Trees

The current code has only one path for tree finalization on scene change: `CommitTreeSceneExit`, which always nulls snapshots. But there are two distinct scenarios:

**Scenario A: Scene exit (Flight -> Space Center/Tracking Station)**
- The player leaves the Flight scene entirely
- `OnSceneChangeRequested(GameScenes.SPACECENTER)` fires
- Tree is finalized and stashed as pending
- `ParsekScenario.OnLoad` picks it up outside Flight and auto-commits ghost-only (line 574-587 of ParsekScenario.cs)
- Snapshots should be nulled (no spawning outside Flight)

**Scenario B: Revert (Flight -> Flight via quickload/revert)**
- The player reverts to launch or quickload
- `OnSceneChangeRequested(GameScenes.FLIGHT)` fires
- Tree is finalized and stashed as pending
- `OnFlightReady` fires, detects pending tree, shows merge dialog
- Snapshots should be PRESERVED so the dialog can offer vessel spawning

The fix: in `OnSceneChangeRequested`, check the target scene. If `scene == GameScenes.FLIGHT` (revert), preserve snapshots. If going to any other scene, null snapshots.

We accomplish this by splitting the tree finalization into two calls:

```csharp
// In OnSceneChangeRequested:
if (activeTree != null)
{
    double commitUT = Planetarium.GetUniversalTime();

    if (scene == GameScenes.FLIGHT)
    {
        // Revert: finalize but preserve snapshots for merge dialog
        CommitTreeRevert(commitUT);
    }
    else
    {
        // Scene exit: finalize and null snapshots (ghost-only)
        CommitTreeSceneExit(commitUT);
    }

    // Clean up tree state (same for both paths)
    recorder = null;
    if (backgroundRecorder != null)
    {
        backgroundRecorder.Shutdown();
        Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
        backgroundRecorder = null;
    }
    activeTree = null;
}
```

#### 4.1 New method: `CommitTreeRevert`

```csharp
/// <summary>
/// Finalizes the active tree for a revert (going back to Flight).
/// Preserves vessel snapshots so the merge dialog can offer spawning.
/// </summary>
void CommitTreeRevert(double commitUT)
{
    if (activeTree == null) return;

    ParsekLog.Info("Flight", $"CommitTreeRevert: finalizing tree at UT={commitUT:F1}");

    // Finalize all recordings (active + background)
    FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

    // Stash as pending tree -- snapshots preserved for merge dialog
    RecordingStore.StashPendingTree(activeTree);

    ParsekLog.Info("Flight", $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots");
}
```

Key difference from `CommitTreeSceneExit`: no snapshot nulling, and `isSceneExit: false` is passed to `FinalizeTreeRecordings`. This matters because `FinalizeTreeRecordings` calls `VesselSpawner.SnapshotVessel` for active recordings, and the `isSceneExit` flag may affect snapshot capture behavior.

Wait -- let me check what `isSceneExit` does in `FinalizeTreeRecordings`. Looking at the code (line 3211), `isSceneExit` is passed through but the method already handles both paths. The main difference is that for `isSceneExit: true`, the method may skip certain snapshot operations that require the Flight scene to be active. For revert, `isSceneExit: false` is correct since we're still technically in Flight when `OnSceneChangeRequested` fires.

Actually, re-checking: when `OnSceneChangeRequested` fires for a revert, we're about to leave Flight but haven't left yet. The vessel is still loaded, physics is still running. So `isSceneExit: false` is appropriate -- all vessels and their states are still accessible.

#### 4.2 Updated `CommitTreeSceneExit` (unchanged)

The existing `CommitTreeSceneExit` stays as-is. It nulls snapshots and stashes the pending tree. The `ParsekScenario.OnLoad` code outside Flight will auto-commit it ghost-only (this path is already implemented at line 574-587 of ParsekScenario.cs).

### 5. Integration in ParsekFlight.OnFlightReady

Replace the auto-commit placeholder (lines 2799-2807) with the `ShowTreeDialog` call:

**Before (current placeholder):**
```csharp
// Handle pending tree: auto-commit ghost-only for now.
// Task 8 will replace this with a tree merge dialog.
if (RecordingStore.HasPendingTree)
{
    var pt = RecordingStore.PendingTree;
    Log($"Found pending tree '{pt.TreeName}' -- auto-committing ghost-only (Task 8 will add dialog)");
    RecordingStore.CommitPendingTree();
    ParsekScenario.ReserveSnapshotCrew();
}
```

**After:**
```csharp
// Handle pending tree: show tree merge dialog
if (RecordingStore.HasPendingTree)
{
    var pt = RecordingStore.PendingTree;
    Log($"Found pending tree '{pt.TreeName}' ({pt.Recordings.Count} recordings) -- showing tree merge dialog");
    MergeDialog.ShowTreeDialog(pt);
}
```

The `ParsekScenario.ReserveSnapshotCrew()` and `SwapReservedCrewInFlight()` calls move into the dialog's "Merge to Timeline" callback (they should only fire when the user confirms).

Note: the `SwapReservedCrewInFlight()` call at line 2856 of `OnFlightReady` still runs AFTER the dialog is shown. This is fine because it runs synchronously after the dialog is spawned, but the dialog callbacks execute asynchronously (when the user clicks). The crew swap at line 2856 handles previously-committed recordings, not the pending tree. When the user clicks "Merge to Timeline", the callback calls `SwapReservedCrewInFlight()` again to handle the newly-committed tree recordings. Double-swapping is safe because `SwapReservedCrewInFlight` is idempotent (it only swaps crew that match the reservation dict).

### 6. Complete ShowTreeDialog Implementation

```csharp
internal static void ShowTreeDialog(RecordingTree tree)
{
    if (tree == null)
    {
        ParsekLog.Warn("MergeDialog", "Cannot show tree dialog: tree is null");
        return;
    }

    var allLeaves = tree.GetAllLeaves();
    var spawnableLeaves = tree.GetSpawnableLeaves();

    // Compute total duration across all recordings in the tree
    double minStartUT = double.MaxValue;
    double maxEndUT = double.MinValue;
    foreach (var rec in tree.Recordings.Values)
    {
        double start = rec.StartUT;
        double end = rec.EndUT;
        if (start < minStartUT) minStartUT = start;
        if (end > maxEndUT) maxEndUT = end;
    }
    double duration = (minStartUT < double.MaxValue && maxEndUT > double.MinValue)
        ? maxEndUT - minStartUT
        : 0;

    // Count destroyed leaves
    int destroyedCount = 0;
    for (int i = 0; i < allLeaves.Count; i++)
    {
        if (allLeaves[i].TerminalStateValue.HasValue
            && allLeaves[i].TerminalStateValue.Value == TerminalState.Destroyed)
            destroyedCount++;
    }

    int survivingCount = spawnableLeaves.Count;

    ParsekLog.Info("MergeDialog",
        $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
        $"allLeaves={allLeaves.Count}, spawnable={survivingCount}, destroyed={destroyedCount}");

    // Build vessel count text
    string vesselCountText;
    if (destroyedCount > 0)
        vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")} ({destroyedCount} destroyed)";
    else
        vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")}";

    // Build per-leaf summary
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < allLeaves.Count; i++)
    {
        var leaf = allLeaves[i];
        string situation = GetLeafSituationText(leaf);
        string marker = (leaf.RecordingId == tree.ActiveRecordingId) ? "  <-- you are here" : "";
        sb.AppendLine($"  {leaf.VesselName} -- {situation}{marker}");
    }

    // Assemble message
    string header = $"\"{tree.TreeName}\" -- {vesselCountText}, {FormatDuration(duration)}\n\n";
    string footer;
    if (survivingCount > 0)
        footer = "\nAll surviving vessels will appear after ghost playback.";
    else
        footer = "\nAll vessels were lost. Ghosts will replay the mission.";

    string message = header + sb.ToString() + footer;

    // Buttons
    // Capture spawnableLeaves.Count in a local for the lambda
    int spawnCount = survivingCount;

    DialogGUIButton[] buttons = new[]
    {
        new DialogGUIButton("Merge to Timeline", () =>
        {
            RecordingStore.CommitPendingTree();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();
            if (spawnCount > 0)
                ParsekLog.ScreenMessage(
                    $"Tree merged -- {spawnCount} vessel(s) will appear after ghost playback", 3f);
            else
                ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Merge (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count}, spawnable={spawnCount})");
        }),
        new DialogGUIButton("Discard", () =>
        {
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselSnapshot != null)
                    ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            }
            RecordingStore.DiscardPendingTree();
            ParsekLog.ScreenMessage("Recording tree discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Discard (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count})");
        })
    };

    PopupDialog.SpawnPopupDialog(
        new Vector2(0.5f, 0.5f),
        new Vector2(0.5f, 0.5f),
        new MultiOptionDialog(
            "ParsekMerge",
            message,
            "Parsek -- Merge Recording Tree",
            HighLogic.UISkin,
            buttons
        ),
        false,
        HighLogic.UISkin
    );
}
```

### 7. Helper Methods (added to MergeDialog.cs)

#### 7.1 `FormatDuration`

```csharp
internal static string FormatDuration(double seconds)
{
    if (seconds < 0) seconds = 0;
    if (seconds < 60)
        return ((int)seconds).ToString(CultureInfo.InvariantCulture) + "s";
    if (seconds < 3600)
    {
        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return s > 0
            ? m.ToString(CultureInfo.InvariantCulture) + "m " +
              s.ToString(CultureInfo.InvariantCulture) + "s"
            : m.ToString(CultureInfo.InvariantCulture) + "m";
    }
    int h = (int)(seconds / 3600);
    int min = (int)((seconds % 3600) / 60);
    return min > 0
        ? h.ToString(CultureInfo.InvariantCulture) + "h " +
          min.ToString(CultureInfo.InvariantCulture) + "m"
        : h.ToString(CultureInfo.InvariantCulture) + "h";
}
```

#### 7.2 `GetLeafSituationText`

```csharp
internal static string GetLeafSituationText(RecordingStore.Recording leaf)
{
    if (leaf.TerminalStateValue.HasValue)
    {
        switch (leaf.TerminalStateValue.Value)
        {
            case TerminalState.Orbiting:
                return "Orbiting " + (leaf.TerminalOrbitBody ?? "unknown");
            case TerminalState.Landed:
                return "Landed on " + (leaf.TerminalPosition.HasValue
                    ? leaf.TerminalPosition.Value.bodyName : "unknown");
            case TerminalState.Splashed:
                return "Splashed on " + (leaf.TerminalPosition.HasValue
                    ? leaf.TerminalPosition.Value.bodyName : "unknown");
            case TerminalState.SubOrbital:
                return "Sub-orbital, " + (leaf.TerminalOrbitBody ?? "unknown");
            case TerminalState.Destroyed:
                return "Destroyed";
            case TerminalState.Recovered:
                return "Recovered";
            case TerminalState.Docked:
                return "Docked";
            case TerminalState.Boarded:
                return "Boarded";
        }
    }

    // Fallback for legacy recordings or recordings without terminal state
    if (!string.IsNullOrEmpty(leaf.VesselSituation))
        return leaf.VesselSituation;

    return "Unknown";
}
```

### 8. Detailed Change List

#### 8.1 `MergeDialog.cs`

Add at the top of the file (with existing usings):
- `using System.Text;` (for StringBuilder in leaf summary)

Add three new methods to the `MergeDialog` static class:

1. `internal static void ShowTreeDialog(RecordingTree tree)` -- main tree dialog method (see section 6)
2. `internal static string FormatDuration(double seconds)` -- duration formatting helper (see section 7.1)
3. `internal static string GetLeafSituationText(RecordingStore.Recording leaf)` -- leaf situation text helper (see section 7.2)

No changes to existing methods (`Show`, `ShowStandaloneDialog`, `ShowChainDialog`, `BuildMergeMessage`, `NullChainSiblingSnapshots`, `DiscardChain`).

#### 8.2 `ParsekFlight.cs`

**Change 1: `OnSceneChangeRequested` (line ~744)**

Replace:
```csharp
if (activeTree != null)
{
    double commitUT = Planetarium.GetUniversalTime();

    // CommitTreeSceneExit handles: stop/flush recorder, finalize background,
    // capture terminal state, null snapshots, stash pending tree
    CommitTreeSceneExit(commitUT);

    // Clean up tree state
    recorder = null;
    if (backgroundRecorder != null)
    {
        backgroundRecorder.Shutdown();
        Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
        backgroundRecorder = null;
    }
    activeTree = null;
}
```

With:
```csharp
if (activeTree != null)
{
    double commitUT = Planetarium.GetUniversalTime();

    if (scene == GameScenes.FLIGHT)
    {
        // Revert: preserve snapshots for merge dialog
        CommitTreeRevert(commitUT);
    }
    else
    {
        // Scene exit: null snapshots (ghost-only, no spawning outside Flight)
        CommitTreeSceneExit(commitUT);
    }

    // Clean up tree state
    recorder = null;
    if (backgroundRecorder != null)
    {
        backgroundRecorder.Shutdown();
        Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
        backgroundRecorder = null;
    }
    activeTree = null;
}
```

**Change 2: `OnFlightReady` (line ~2799)**

Replace:
```csharp
// Handle pending tree: auto-commit ghost-only for now.
// Task 8 will replace this with a tree merge dialog.
if (RecordingStore.HasPendingTree)
{
    var pt = RecordingStore.PendingTree;
    Log($"Found pending tree '{pt.TreeName}' -- auto-committing ghost-only (Task 8 will add dialog)");
    RecordingStore.CommitPendingTree();
    ParsekScenario.ReserveSnapshotCrew();
}
```

With:
```csharp
// Handle pending tree: show tree merge dialog
if (RecordingStore.HasPendingTree)
{
    var pt = RecordingStore.PendingTree;
    Log($"Found pending tree '{pt.TreeName}' ({pt.Recordings.Count} recordings) -- showing tree merge dialog");
    MergeDialog.ShowTreeDialog(pt);
}
```

**Change 3: Add `CommitTreeRevert` method** (near `CommitTreeSceneExit`, around line 3185)

```csharp
/// <summary>
/// Finalizes the active tree for a revert (Flight -> Flight).
/// Preserves vessel snapshots so the merge dialog can offer spawning.
/// </summary>
void CommitTreeRevert(double commitUT)
{
    if (activeTree == null) return;

    ParsekLog.Info("Flight", $"CommitTreeRevert: finalizing tree at UT={commitUT:F1}");

    // Finalize all recordings (active + background)
    FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

    // Stash as pending tree -- snapshots preserved for merge dialog
    RecordingStore.StashPendingTree(activeTree);

    ParsekLog.Info("Flight",
        $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots preserved");
}
```

### 9. Edge Cases

#### 9.1 Zero leaves

If the tree has zero leaves (all recordings have children -- a degenerate tree), `allLeaves` is empty. The dialog shows "0 vessels" with no leaf summary lines. The "Merge to Timeline" button still works (commits ghosts). This is unlikely in practice but should not crash.

#### 9.2 All leaves destroyed

If all leaves are `TerminalState.Destroyed`, `spawnableLeaves` is empty, `survivingCount` is 0, `destroyedCount` > 0. The dialog shows "0 vessels (N destroyed)". The footer says "All vessels were lost. Ghosts will replay the mission." The "Merge to Timeline" button commits ghosts with no vessel spawning.

#### 9.3 Single-recording tree (degenerate case)

A tree with exactly one recording (no branching ever occurred). This is a valid tree -- the root recording is the only leaf. The dialog shows the single vessel with its situation. This is functionally equivalent to the standalone merge dialog, but the tree dialog handles it correctly without special-casing.

#### 9.4 Null `ActiveRecordingId`

If `tree.ActiveRecordingId` is null (all recordings were backgrounded -- the player switched to an unrelated vessel before reverting), no leaf gets the "you are here" marker. This is correct behavior.

#### 9.5 Recordings with `ExplicitStartUT`/`ExplicitEndUT` set but no trajectory points

Background-only recordings may have no trajectory points. Their `StartUT`/`EndUT` properties return `ExplicitStartUT`/`ExplicitEndUT` when set. The duration calculation handles this correctly since it uses `rec.StartUT`/`rec.EndUT` which already resolve the explicit values.

#### 9.6 Tree stashed from scene exit (not revert)

If the player goes to Space Center (not Flight), `CommitTreeSceneExit` nulls all snapshots and stashes the tree. `ParsekScenario.OnLoad` detects `HasPendingTree` outside Flight (line 574) and auto-commits ghost-only. `ShowTreeDialog` is never called. This path is unchanged.

#### 9.7 Concurrent pending tree and pending recording

`OnFlightReady` handles pending tree BEFORE pending recording (lines 2799 vs. 2809). These are independent: a pending tree comes from tree-mode recording, a pending recording comes from standalone recording. They should not coexist in practice (starting tree mode clears standalone state), but if they did, both dialogs would be spawned. The `"ParsekMerge"` dialog ID ensures only the last one is visible (KSP replaces dialogs with the same ID). Since the tree dialog is spawned first and the standalone second, the standalone would replace the tree dialog. However, this case is impossible in practice because `OnSceneChangeRequested` either stashes a tree OR a standalone recording, never both.

### 10. Testing Strategy

No unit tests needed for UI code. All testing is in-game.

#### 10.1 Basic tree merge dialog

1. Start KSP, load a career save
2. Launch a rocket with separable stages (e.g., a vessel with radial decouplers)
3. Start recording (F9 or auto-record on liftoff)
4. Undock/decouple a stage -- this creates a tree branch
5. Fly both vessels briefly (switch between them with `[`/`]`)
6. Revert to Launch
7. **Verify:** Tree merge dialog appears with:
   - Tree name (original vessel name)
   - Vessel count and duration
   - Per-leaf summary with vessel names and situations
   - "you are here" marker on the vessel that was active at revert
   - "Merge to Timeline" and "Discard" buttons

#### 10.2 Merge to Timeline

1. Perform steps 1-7 above
2. Click "Merge to Timeline"
3. **Verify:** Screen message confirms merge with vessel count
4. **Verify:** No errors in KSP.log
5. Time warp -- ghosts should appear (Task 9 will test this fully)
6. Check Tracking Station -- spawned vessels should appear at their positions

#### 10.3 Discard

1. Perform steps 1-7 above
2. Click "Discard"
3. **Verify:** Screen message confirms discard
4. **Verify:** No recordings in the timeline
5. **Verify:** No crew reservation artifacts (all crew Available in Astronaut Complex)

#### 10.4 Standalone recording still works

1. Launch a simple vessel, record a flight WITHOUT undocking (no tree)
2. Revert to Launch
3. **Verify:** The existing standalone merge dialog appears (NOT the tree dialog)
4. Both buttons work as before

#### 10.5 Scene exit (not revert)

1. Launch, start recording, undock (create tree)
2. Press Esc -> "Space Center" (NOT revert)
3. **Verify:** No dialog appears. Tree is auto-committed ghost-only
4. Go back to Flight. Check timeline -- ghosts should be present, no vessels spawned

#### 10.6 Destroyed vessel in tree

1. Launch a rocket, undock a stage, let the stage crash (get destroyed)
2. Switch to the other vessel
3. Revert
4. **Verify:** Dialog shows "(1 destroyed)" in the vessel count
5. **Verify:** Destroyed vessel's leaf line shows "Destroyed"

#### 10.7 Chain recording still works

1. Launch, record, land, commit. Launch again (chain continuation)
2. Record the second segment, revert
3. **Verify:** Chain merge dialog appears (NOT tree dialog)

#### 10.8 Log validation

After each test, check KSP.log:
```bash
grep "[Parsek]" "Kerbal Space Program/KSP.log" | grep -i "tree\|merge\|dialog"
```

Verify:
- `CommitTreeRevert: finalizing tree...` on revert
- `CommitTreeRevert: stashed pending tree...with snapshots preserved`
- `Tree merge dialog: tree=...` when dialog is shown
- `User chose: Tree Merge...` or `User chose: Tree Discard...` on button click

### 11. Summary of All Changes

| File | Change | Lines |
|------|--------|-------|
| `MergeDialog.cs` | Add `using System.Text;` | 1 |
| `MergeDialog.cs` | Add `ShowTreeDialog` method | ~70 |
| `MergeDialog.cs` | Add `FormatDuration` helper | ~15 |
| `MergeDialog.cs` | Add `GetLeafSituationText` helper | ~30 |
| `ParsekFlight.cs` | Add revert vs. scene-exit branching in `OnSceneChangeRequested` | ~10 (replace existing 8) |
| `ParsekFlight.cs` | Replace auto-commit with `ShowTreeDialog` in `OnFlightReady` | ~5 (replace existing 7) |
| `ParsekFlight.cs` | Add `CommitTreeRevert` method | ~15 |

Total: ~145 lines of new/modified code across 2 files.

---

## Orchestrator Review Fixes

### Fix 1 (CRITICAL) - `SurfacePosition` field is `body`, not `bodyName`

**Problem:** The plan's `GetLeafSituationText` references `leaf.TerminalPosition.Value.bodyName` in the Landed and Splashed cases (sections 3.4, 7.2). The actual field on `SurfacePosition` is `body` (line 15 of SurfacePosition.cs), not `bodyName`.

**Resolution:** Change all occurrences of `.bodyName` to `.body` in `GetLeafSituationText`.

### Fix 2 (MINOR) - Title uses `--` instead of em dash

**Problem:** The plan uses `"Parsek -- Merge Recording Tree"` as the dialog title. The existing dialogs use an actual em dash character `\u2014` (e.g., `"Parsek \u2014 Merge Recording"`).

**Resolution:** Use `"Parsek \u2014 Merge Recording Tree"` to match the existing pattern. The em dash is the UTF-8 character `-`.

### Fix 3 (MINOR) - `FormatDuration` should handle NaN/infinity

**Problem:** If `duration` is `NaN` or negative infinity (from recordings with no points and no explicit UT), `FormatDuration` may produce unexpected output.

**Resolution:** Add a guard at the start: `if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "0s";`

### Summary for implementation agent

The plan is solid. Apply these specific changes:
1. Use `.body` instead of `.bodyName` on `SurfacePosition` (Fix 1 - CRITICAL)
2. Use `\u2014` (em dash) in the dialog title instead of `--` (Fix 2)
3. Add NaN guard to `FormatDuration` (Fix 3)
