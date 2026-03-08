# Task 10: Tree-Level Resource Tracking

## Overview

Migrate resource delta tracking from per-recording to per-tree for tree recordings. The tree captures a pre-tree resource snapshot at creation, computes a single aggregate delta at commit time, and applies it as a lump sum during playback. Per-recording resource fields remain for granular per-segment display and chain-level budget accounting.

## Current System: End-to-End Resource Flow

### 1. Capture (FlightRecorder.StartRecording)

`FlightRecorder.StartRecording` (line ~3103 of FlightRecorder.cs) captures the game's current funds, science, and reputation into `PreLaunchFunds`, `PreLaunchScience`, `PreLaunchReputation` on the recorder instance. This happens before the first physics frame is recorded.

When `isPromotion = true` (a background recording being promoted to active), the capture is skipped because the resources "belong to the tree root" -- the comment at line 3104 says exactly this.

Each `TrajectoryPoint` sampled during recording stores the absolute game state: `point.funds`, `point.science`, `point.reputation` at that physics tick.

### 2. Stop and CaptureAtStop

When recording stops (`ForceStop` / `HandleVesselSwitch`), the recorder creates `CaptureAtStop` -- a `Recording` object that copies `PreLaunchFunds/Science/Reputation` from the recorder (lines 3302-3304).

### 3. Playback: ApplyResourceDeltas (ParsekFlight.cs, line 3861)

`UpdateTimelinePlayback` iterates `RecordingStore.CommittedRecordings`. For each recording, when UT progresses past trajectory points, `ApplyResourceDeltas` computes deltas between consecutive points and applies them to the live game:

```
fundsDelta = toPoint.funds - fromPoint.funds
```

This is a **per-point incremental** approach. `rec.LastAppliedResourceIndex` tracks progress so quickload does not double-apply. Deltas are clamped to never reduce funds/science/reputation below zero.

`ApplyResourceDeltas` is called:
- During in-range ghost playback (line 3687)
- During background-only recording playback (line 3708)
- At ghost exit / vessel spawn (lines 3719, 3727)
- For disabled segments past their end (line 3581)
- For mid-chain segments past their end (line 3735)
- On catch-up for recordings UT has passed (line 3747)

### 4. Budget Display: ResourceBudget.ComputeTotal (ResourceBudget.cs, line 162)

`ComputeTotal` receives the flat list `RecordingStore.CommittedRecordings` (which includes all tree recordings) and all milestones. For each recording, it calls `CommittedFundsCost`, `CommittedScienceCost`, `CommittedReputationCost`:

```
totalImpact = rec.PreLaunchFunds - rec.Points[last].funds
```

Minus `alreadyApplied` (based on `LastAppliedResourceIndex`). The UI (`DrawResourceBudget` in ParsekUI.cs, line 295) shows "X available to use (Y committed out of Z total)".

**Key insight:** `ComputeTotal` currently sums per-recording deltas from ALL committed recordings, including tree recordings. For trees, every recording node in the tree has its own `PreLaunchFunds` and trajectory points with resource values. The sum of all per-recording deltas equals the total resource impact of the tree -- but this is fragile and may double-count if multiple recordings share the same PreLaunch snapshot.

## Current Tree Resource State

### What exists already (from Task 1):

`RecordingTree` has these fields (RecordingTree.cs, lines 21-26):
- `PreTreeFunds`, `PreTreeScience`, `PreTreeReputation` -- captured at tree creation
- `DeltaFunds`, `DeltaScience`, `DeltaReputation` -- intended for tree-level delta

These are fully serialized in `Save`/`Load` (lines 45-50, 79-88).

### Where pre-tree state is captured:

In `ParsekFlight.CreateSplitBranch` (line 1242-1245), when the tree is first created (on first split/undock):

```csharp
activeTree.PreTreeFunds = splitRecorder.PreLaunchFunds;
activeTree.PreTreeScience = splitRecorder.PreLaunchScience;
activeTree.PreTreeReputation = splitRecorder.PreLaunchReputation;
```

This captures the resources from the root recording's pre-launch snapshot -- i.e., the game state before the flight that later branched into a tree. This is correct.

### What is NOT yet implemented:

1. **Delta computation at commit time** -- `DeltaFunds/Science/Reputation` are never populated. They stay 0.
2. **Tree-level delta application during playback** -- `ApplyResourceDeltas` operates on individual recordings, not trees.
3. **Budget computation for trees** -- `ResourceBudget.ComputeTotal` sums per-recording deltas for tree recordings, which may be incorrect (per-recording PreLaunch values within a tree may not reflect real game state at recording start, especially for branch children that inherit from the parent vessel).

## Problem with Per-Recording Deltas in Trees

Consider a tree with a split at UT=100:
- Root recording: PreLaunch=50000, Points[last].funds=45000 (spent 5000 during flight up to split)
- Active child (new vessel after undock): PreLaunch=??? -- this is the key issue

The child recording's PreLaunch is captured at the moment its recorder starts. For the active child after a split, `StartRecording(isPromotion: false)` captures the current game state. For a promoted background recording, `isPromotion=true` skips the capture, leaving PreLaunch=0.

This means:
- Active child: PreLaunch captured correctly at split time
- Background child (promoted later): PreLaunch=0, so `CommittedFundsCost` returns 0 (because both PreLaunch and end funds are 0 -- the early-out on line 19 of ResourceBudget.cs)
- New children from subsequent splits: PreLaunch captured at their start time

The per-recording approach within a tree is unreliable because:
1. Background recordings with `isPromotion=true` skip PreLaunch capture
2. Multiple recordings may be running concurrently -- game state at each one's start is hard to attribute
3. Resources are a global pool -- changes affect all concurrent recordings simultaneously

Tree-level delta solves this cleanly: one measurement at tree start, one at tree end.

## Design

### 1. Pre-tree state capture (already implemented)

No change needed. `CreateSplitBranch` already captures `PreTreeFunds/Science/Reputation` from the root recorder's PreLaunch values. This is the game state before the entire tree's flight began.

### 2. Compute tree-level delta at commit

At commit time, compute:
```
DeltaFunds = currentGameFunds - PreTreeFunds
DeltaScience = currentGameScience - PreTreeScience
DeltaReputation = currentGameReputation - PreTreeReputation
```

**Where:** In `FinalizeTreeRecordings` (ParsekFlight.cs, line 3235) -- this is called by all three commit paths (`CommitTreeFlight`, `CommitTreeRevert`, `CommitTreeSceneExit`).

Add delta computation at the end of `FinalizeTreeRecordings`, after all recordings are finalized:

```csharp
// Compute tree-level resource delta
tree.DeltaFunds = ComputeTreeDeltaFunds(tree);
tree.DeltaScience = ComputeTreeDeltaScience(tree);
tree.DeltaReputation = ComputeTreeDeltaReputation(tree);
```

The computation is:
```csharp
internal static double ComputeTreeDeltaFunds(RecordingTree tree)
{
    // Current game state minus pre-tree state
    double currentFunds = 0;
    try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
    return currentFunds - tree.PreTreeFunds;
}
```

Similarly for science and reputation.

**Alternative location consideration:** `FinalizeTreeRecordings` is the right place because it is the single finalization point called by all three commit paths. The game state at this moment reflects the entire tree's resource impact.

**Note for CommitTreeFlight:** The active vessel is still live. After commit, the player continues flying. The delta captures everything up to the commit point, which is correct -- the active vessel's future resource changes belong to a future recording, not this tree.

### 3. New field on RecordingTree: ResourcesApplied

Add a boolean field `ResourcesApplied` to track whether the tree-level lump sum has been applied during playback. This is analogous to `LastAppliedResourceIndex` on individual recordings but simpler -- the tree delta is a single lump sum, not incremental.

```csharp
public bool ResourcesApplied;  // serialized
```

Serialize in `Save`/`Load`:
```csharp
treeNode.AddValue("resourcesApplied", ResourcesApplied.ToString());
```

### 4. Playback: Apply tree-level delta as lump sum

**Approach:** Instead of applying per-recording deltas for tree recordings, apply the tree-level delta as a single lump sum when UT passes the tree's max EndUT.

**Implementation in `UpdateTimelinePlayback`:**

For recordings that belong to a tree (`rec.TreeId != null`), skip the per-recording `ApplyResourceDeltas` call entirely. Instead, after the per-recording loop, add a tree-level resource application loop:

```csharp
// After the per-recording loop, apply tree-level resource deltas
ApplyTreeResourceDeltas(currentUT);
```

New method `ApplyTreeResourceDeltas`:
```csharp
void ApplyTreeResourceDeltas(double currentUT)
{
    var trees = RecordingStore.CommittedTrees;
    for (int i = 0; i < trees.Count; i++)
    {
        var tree = trees[i];
        if (tree.ResourcesApplied) continue;

        // Compute tree EndUT: max EndUT across all recordings
        double treeEndUT = 0;
        foreach (var rec in tree.Recordings.Values)
        {
            double recEnd = rec.EndUT;
            if (recEnd > treeEndUT) treeEndUT = recEnd;
        }

        if (currentUT <= treeEndUT) continue;

        // Apply lump sum delta
        ApplyTreeLumpSum(tree);
        tree.ResourcesApplied = true;
    }
}
```

**Where to skip per-recording deltas:** In the existing `UpdateTimelinePlayback` loop, guard `ApplyResourceDeltas` calls:

```csharp
if (rec.TreeId == null)  // non-tree recording: apply per-recording deltas
    ApplyResourceDeltas(rec, currentUT);
```

All 7 `ApplyResourceDeltas` call sites in `UpdateTimelinePlayback` (lines 3581, 3687, 3708, 3719, 3727, 3735, 3747) need this guard.

### 5. ApplyTreeLumpSum method

```csharp
void ApplyTreeLumpSum(RecordingTree tree)
{
    if (ShouldPauseTimelineResourceReplay(IsRecording))
        return;

    GameStateRecorder.SuppressResourceEvents = true;
    try
    {
        if (tree.DeltaFunds != 0 && Funding.Instance != null)
        {
            double delta = tree.DeltaFunds;
            if (delta < 0 && Funding.Instance.Funds + delta < 0)
                delta = -Funding.Instance.Funds;
            Funding.Instance.AddFunds(delta, TransactionReasons.None);
            Log($"Tree resource: funds {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
        }
        // Similarly for science and reputation
    }
    finally
    {
        GameStateRecorder.SuppressResourceEvents = false;
    }
}
```

### 6. Budget computation change: ResourceBudget

`ResourceBudget.ComputeTotal` currently receives `CommittedRecordings` (flat list including tree recordings) and sums per-recording deltas. For tree recordings, this should be replaced with the tree-level delta.

**Option A: Filter tree recordings and add tree-level deltas separately**

Change `ComputeTotal` signature to also accept `IReadOnlyList<RecordingTree>`:

```csharp
internal static BudgetSummary ComputeTotal(
    IList<RecordingStore.Recording> recordings,
    IReadOnlyList<Milestone> milestones,
    IReadOnlyList<RecordingTree> trees = null)
```

In the recording loop, skip recordings with `TreeId != null`:
```csharp
for (int i = 0; i < recordings.Count; i++)
{
    if (recordings[i].TreeId != null) continue;  // handled by tree-level delta
    result.reservedFunds += CommittedFundsCost(recordings[i]);
    // ...
}
```

Then add tree-level deltas:
```csharp
if (trees != null)
{
    for (int i = 0; i < trees.Count; i++)
    {
        var tree = trees[i];
        if (!tree.ResourcesApplied)
        {
            result.reservedFunds += TreeCommittedFundsCost(tree);
            result.reservedScience += TreeCommittedScienceCost(tree);
            result.reservedReputation += TreeCommittedReputationCost(tree);
        }
    }
}
```

New tree cost methods:
```csharp
internal static double TreeCommittedFundsCost(RecordingTree tree)
{
    if (tree == null) return 0;
    if (tree.ResourcesApplied) return 0;
    // DeltaFunds is negative when funds were spent (currentFunds < preTreeFunds)
    // The "cost" is the negative of the delta (positive cost = funds lost)
    return -tree.DeltaFunds;
}
```

Wait -- the sign convention needs careful analysis.

**Sign convention analysis:**

Per-recording: `CommittedFundsCost = PreLaunchFunds - Points[last].funds`. If funds decreased (spent money), this is positive. If funds increased (earned money), this is negative. A positive cost means "game owes you this deduction when replaying".

Tree-level: `DeltaFunds = currentGameFunds - PreTreeFunds`. If funds decreased (spent money), DeltaFunds is negative. If funds increased (earned money), DeltaFunds is positive.

So `TreeCommittedFundsCost = -DeltaFunds` to match the per-recording sign convention (positive = cost).

But we also need partial application tracking. The tree delta is applied as a lump sum, so it is either fully applied (`ResourcesApplied=true` -> cost=0) or not yet applied (`ResourcesApplied=false` -> full cost). No partial state.

```csharp
internal static double TreeCommittedFundsCost(RecordingTree tree)
{
    if (tree == null || tree.ResourcesApplied) return 0;
    return -tree.DeltaFunds;  // negate: negative delta (spent) -> positive cost
}

internal static double TreeCommittedScienceCost(RecordingTree tree)
{
    if (tree == null || tree.ResourcesApplied) return 0;
    return -tree.DeltaScience;
}

internal static double TreeCommittedReputationCost(RecordingTree tree)
{
    if (tree == null || tree.ResourcesApplied) return 0;
    return -(double)tree.DeltaReputation;
}
```

**Call site update:** `DrawResourceBudget` and `DrawCompactBudgetLine` in ParsekUI.cs pass `RecordingStore.CommittedRecordings` to `ComputeTotal`. Update both to also pass `RecordingStore.CommittedTrees`:

```csharp
var budget = ResourceBudget.ComputeTotal(
    RecordingStore.CommittedRecordings,
    MilestoneStore.Milestones,
    RecordingStore.CommittedTrees);
```

### 7. CommitTreeFlight: Mark tree resources as already applied

When `CommitTreeFlight` is called (the in-flight commit that does not revert), the active vessel stays live. The tree delta represents changes that already happened in the live game -- no replay needed. So:

```csharp
// In CommitTreeFlight, after computing delta:
activeTree.ResourcesApplied = true;
```

And also mark all per-recording `LastAppliedResourceIndex` to the end:
```csharp
foreach (var rec in activeTree.Recordings.Values)
{
    if (rec.Points.Count > 0)
        rec.LastAppliedResourceIndex = rec.Points.Count - 1;
}
```

This is already partially done for the active recording at line 3126.

### 8. CommitTreeRevert and CommitTreeSceneExit: Leave ResourcesApplied=false

On revert, the game state resets to pre-flight. The tree delta needs to be replayed during playback. `ResourcesApplied` defaults to `false`, which is correct.

### 9. Interaction with TakeControlOfGhost

`TakeControlOfGhost` (line ~5200) calls `ApplyResourceDeltas(rec, ut)` to catch up resources before spawning. For tree recordings, this needs to apply the tree lump sum instead. However, taking control of a ghost mid-tree is complex. The simplest correct approach: when taking control of a tree ghost, apply the full tree lump sum at that point (if not already applied).

```csharp
// In TakeControlOfGhost, replace the ApplyResourceDeltas call for tree recs:
if (rec.TreeId != null)
{
    var tree = FindCommittedTree(rec.TreeId);
    if (tree != null && !tree.ResourcesApplied)
    {
        ApplyTreeLumpSum(tree);
        tree.ResourcesApplied = true;
    }
}
else
{
    ApplyResourceDeltas(rec, ut);
}
```

Add helper:
```csharp
RecordingTree FindCommittedTree(string treeId)
{
    var trees = RecordingStore.CommittedTrees;
    for (int i = 0; i < trees.Count; i++)
    {
        if (trees[i].Id == treeId) return trees[i];
    }
    return null;
}
```

## Edge Cases

### Tree with zero resource change
DeltaFunds/Science/Reputation = 0. `ApplyTreeLumpSum` skips zero deltas (the `!= 0` guards). `TreeCommittedFundsCost` returns 0. No budget impact. Correct.

### Partial playback (UT between recordings in tree)
Tree delta is applied as a lump sum at tree EndUT (max EndUT across all recordings). If UT is between individual recording EndUTs within the tree, per-recording ghosts play out visually but no resource deltas are applied until the entire tree's timespan has passed. This is the designed behavior -- the tree delta represents the aggregate outcome.

### Save/load round-trip
`ResourcesApplied` is serialized. `DeltaFunds/Science/Reputation` are already serialized. After load, playback resumes correctly: if `ResourcesApplied=false`, the lump sum is applied when UT passes tree EndUT.

### Tree committed in-flight (CommitTreeFlight)
`ResourcesApplied=true` immediately. Budget shows zero for this tree (resources already in the game). Correct.

### Tree committed on revert (CommitTreeRevert -> merge dialog -> CommitPendingTree)
After revert, game state is reset to pre-flight. `ResourcesApplied=false`. Tree delta is replayed during playback. Budget shows the tree's committed cost until replay. Correct.

### Tree committed on scene exit (CommitTreeSceneExit)
Same as revert -- `ResourcesApplied=false`, delta replayed during next flight session.

### Non-tree recordings (legacy, chains)
No change. `rec.TreeId == null` means per-recording resource logic is used exactly as before. `ComputeTotal` skips tree recordings and adds tree-level deltas separately. Fully backward compatible.

### Pause during recording
`ShouldPauseTimelineResourceReplay` pauses both per-recording and tree-level delta application. The tree lump sum call in `ApplyTreeResourceDeltas` checks this flag.

### Multiple trees
Each tree is independent. `ApplyTreeResourceDeltas` iterates all committed trees. Each has its own `ResourcesApplied` flag.

## Files Changed

### RecordingTree.cs
- Add `public bool ResourcesApplied;` field
- Serialize/deserialize in `Save`/`Load`

### ResourceBudget.cs
- Add `TreeCommittedFundsCost`, `TreeCommittedScienceCost`, `TreeCommittedReputationCost` static methods
- Change `ComputeTotal` signature to accept optional `IReadOnlyList<RecordingTree> trees`
- Skip `TreeId != null` recordings in the recording loop
- Add tree-level delta summation

### ParsekFlight.cs
- Add `ComputeTreeDeltaFunds`/`Science`/`Reputation` (or a single method) in `FinalizeTreeRecordings`
- Add `ApplyTreeResourceDeltas` method (post-loop tree delta application)
- Add `ApplyTreeLumpSum` method
- Guard all 7 `ApplyResourceDeltas` call sites with `rec.TreeId == null`
- In `CommitTreeFlight`: set `activeTree.ResourcesApplied = true`
- In `TakeControlOfGhost`: handle tree recording case
- Add `FindCommittedTree` helper

### ParsekUI.cs
- Update `DrawResourceBudget` and `DrawCompactBudgetLine` to pass `RecordingStore.CommittedTrees` to `ComputeTotal`

## Testing Strategy

### Unit Tests (ResourceBudgetTests.cs)

1. **TreeCommittedFundsCost_BasicCost**: Tree with DeltaFunds=-5000 (spent money), ResourcesApplied=false -> cost=5000.
2. **TreeCommittedFundsCost_Profit**: Tree with DeltaFunds=+3000 (earned money), ResourcesApplied=false -> cost=-3000.
3. **TreeCommittedFundsCost_AlreadyApplied**: Tree with ResourcesApplied=true -> cost=0.
4. **TreeCommittedFundsCost_NullTree**: Returns 0.
5. **TreeCommittedScienceCost_Works**: Analogous to funds.
6. **TreeCommittedReputationCost_Works**: Analogous to funds.
7. **ComputeTotal_TreeRecordingsSkipped**: Tree recordings (TreeId != null) are excluded from per-recording sum.
8. **ComputeTotal_TreeDeltaAdded**: Tree-level delta is added to budget.
9. **ComputeTotal_MixedTreeAndStandalone**: A tree and a standalone recording both contribute correctly.
10. **ComputeTotal_TreeApplied_ZeroCost**: Applied tree contributes 0.
11. **ComputeTotal_BackwardCompat_NullTrees**: `trees=null` works same as before.

### Unit Tests (RecordingTreeTests.cs)

12. **ResourcesApplied_SaveLoad_RoundTrips**: Serialize with ResourcesApplied=true, load, verify.
13. **ResourcesApplied_DefaultsFalse**: New tree has ResourcesApplied=false.
14. **DeltaFields_SaveLoad_RoundTrips**: Verify DeltaFunds/Science/Reputation survive serialization (already tested implicitly, but good to have explicit test).

### Unit Tests (new or extended)

15. **ComputeTreeDelta_PureMethod**: Extract delta computation as a pure static method for testability. Test: preTree=50000, currentFunds=45000 -> delta=-5000.
16. **ApplyTreeResourceDeltas_Integration**: Test that tree delta is applied when currentUT > tree EndUT. (May need mock or be in-game only.)

### In-Game Verification

1. **Basic tree resource delta**: Launch in career, undock (creates tree), spend funds (stage expensive parts), revert. Verify:
   - Budget shows correct committed funds for the tree
   - During playback, no per-recording deltas applied for tree recordings
   - At tree EndUT, lump sum delta applied
   - After full playback, funds match expected value

2. **CommitTreeFlight path**: Launch, undock, commit in-flight. Verify ResourcesApplied=true, no replay needed, budget shows 0 for this tree.

3. **Multiple trees**: Commit two trees with different resource impacts. Verify independent tracking.

4. **Save/load**: Commit a tree, save, load. Verify ResourcesApplied flag persists, budget correct.

5. **Mixed tree + standalone**: Have both tree recordings and standalone recordings committed. Verify standalone recordings still apply per-recording deltas, tree recordings use tree-level delta.

## Implementation Order

1. **RecordingTree.cs**: Add `ResourcesApplied` field + serialization (trivial).
2. **ParsekFlight.cs / FinalizeTreeRecordings**: Add delta computation at commit (extract as pure static for testability).
3. **ParsekFlight.cs / UpdateTimelinePlayback**: Guard `ApplyResourceDeltas` calls with `rec.TreeId == null`.
4. **ParsekFlight.cs**: Add `ApplyTreeResourceDeltas` + `ApplyTreeLumpSum` + `FindCommittedTree`.
5. **ParsekFlight.cs / CommitTreeFlight**: Set `ResourcesApplied = true`.
6. **ParsekFlight.cs / TakeControlOfGhost**: Handle tree recording case.
7. **ResourceBudget.cs**: Add tree cost methods, update `ComputeTotal`.
8. **ParsekUI.cs**: Pass `CommittedTrees` to `ComputeTotal`.
9. **Tests**: Write all unit tests.
10. **In-game testing**: Verify all paths.

## Risk Notes

- **Per-recording resource fields remain untouched** in tree recordings. They still store data (trajectory points have funds/science/reputation). They are simply not used for delta application or budget computation when `rec.TreeId != null`. This preserves the option for per-segment display in the Actions window UI (future feature).
- **No change to standalone recording flow**. The entire change is gated on `rec.TreeId != null` / `trees != null`. Non-tree recordings work exactly as before.
- **The `isPromotion` skip in FlightRecorder.StartRecording remains correct**. With tree-level deltas, per-recording PreLaunch values for promoted recordings do not matter for budget or playback. They may still be useful for display, but that is a UI concern, not a correctness concern.

---

## Orchestrator Review Fixes

### Fix 1 - CRITICAL: `ApplyBudgetDeductionWhenReady` must include trees in `ComputeTotal`

**Problem:** `ParsekScenario.ApplyBudgetDeductionWhenReady` (line 620) calls `ResourceBudget.ComputeTotal(RecordingStore.CommittedRecordings, MilestoneStore.Milestones)` WITHOUT passing `CommittedTrees`. After the plan's changes, tree recordings are skipped in the recording loop (TreeId != null → continue), but the tree-level deltas are never added because `trees` is null. Result: tree resource costs are silently not deducted on revert.

**Fix:** Update the call site at line 620 to pass `RecordingStore.CommittedTrees`:

```csharp
var budget = ResourceBudget.ComputeTotal(
    RecordingStore.CommittedRecordings,
    MilestoneStore.Milestones,
    RecordingStore.CommittedTrees);
```

### Fix 2 - CRITICAL: `ApplyBudgetDeductionWhenReady` must mark `tree.ResourcesApplied = true`

**Problem:** Lines 673-685 mark per-recording `LastAppliedResourceIndex` to the end so ghost replay doesn't re-apply deltas. But trees are never marked - `ResourcesApplied` stays `false`, so tree lump sums get double-applied (once by budget deduction, once by `ApplyTreeResourceDeltas` during playback).

**Fix:** After the recording marking loop (line 685), add a tree marking loop:

```csharp
var committedTrees = RecordingStore.CommittedTrees;
int treeMarked = 0;
for (int i = 0; i < committedTrees.Count; i++)
{
    if (!committedTrees[i].ResourcesApplied)
    {
        committedTrees[i].ResourcesApplied = true;
        treeMarked++;
    }
}
ParsekLog.Verbose("Scenario", $"  Marked {treeMarked} tree(s) as ResourcesApplied");
```

### Fix 3 - IMPORTANT: Set `ResourcesApplied = true` BEFORE `CommitTree` in `CommitTreeFlight`

**Problem:** The plan says to set `activeTree.ResourcesApplied = true` in `CommitTreeFlight`, but doesn't specify exactly where. If it's set after `RecordingStore.CommitTree(activeTree)` (line 3151) and after `SpawnTreeLeaves` (line 3154), there is a window where tree is committed but `ResourcesApplied` is false. If an unexpected scene change fires in between, `ComputeTotal` would report it as unreplayed and budget deduction would double-count.

**Fix:** Set `ResourcesApplied = true` immediately after computing the delta in `FinalizeTreeRecordings`, before `CommitTree`. Specifically: in `CommitTreeFlight`, after `FinalizeTreeRecordings` returns (line 3112), before `CommitTree` (line 3151):

```csharp
// Tree resources are already live in the game - mark as applied
activeTree.ResourcesApplied = true;
```

### Fix 4 - IMPORTANT: Mark ALL tree recording `LastAppliedResourceIndex` in `CommitTreeFlight`

**Problem:** The plan mentions marking per-recording `LastAppliedResourceIndex` for all tree recordings (Section 7), but the implementation order puts it as a note. The existing code only marks the active recording at line 3126. Background recordings in the tree that have trajectory points would still have `LastAppliedResourceIndex = 0`, which doesn't cause incorrect delta application (tree recordings skip `ApplyResourceDeltas`), but creates misleading state.

**Fix:** After `FinalizeTreeRecordings` in `CommitTreeFlight`, mark all tree recordings:

```csharp
foreach (var rec in activeTree.Recordings.Values)
{
    if (rec.Points.Count > 0)
        rec.LastAppliedResourceIndex = rec.Points.Count - 1;
}
```

This should be placed before `RecordingStore.CommitTree(activeTree)`. The existing line 3126 (active recording only) can remain since it's a subset of this loop.

### Fix 5 - IMPORTANT: `ApplyTreeResourceDeltas` must check `ShouldPauseTimelineResourceReplay`

**Problem:** The plan's `ApplyTreeLumpSum` method includes the `ShouldPauseTimelineResourceReplay` check, but the outer `ApplyTreeResourceDeltas` method does not. If `ShouldPauseTimelineResourceReplay` returns true, `ApplyTreeLumpSum` returns early WITHOUT setting `ResourcesApplied = true` - which is correct (it should retry next frame). But the plan should make clear that the `ResourcesApplied = true` line is inside `ApplyTreeLumpSum`, after the successful application, NOT in `ApplyTreeResourceDeltas`.

**Fix:** The plan already has this correctly structured - `tree.ResourcesApplied = true` is set after `ApplyTreeLumpSum(tree)` returns in `ApplyTreeResourceDeltas`. But `ApplyTreeLumpSum` does an early return when paused, and the caller still sets `ResourcesApplied = true`. Move the flag set INSIDE `ApplyTreeLumpSum` at the end (after successful application), and remove it from the caller:

```csharp
void ApplyTreeResourceDeltas(double currentUT)
{
    var trees = RecordingStore.CommittedTrees;
    for (int i = 0; i < trees.Count; i++)
    {
        var tree = trees[i];
        if (tree.ResourcesApplied) continue;

        double treeEndUT = 0;
        foreach (var rec in tree.Recordings.Values)
        {
            double recEnd = rec.EndUT;
            if (recEnd > treeEndUT) treeEndUT = recEnd;
        }

        if (currentUT <= treeEndUT) continue;

        ApplyTreeLumpSum(tree);  // sets ResourcesApplied = true internally
    }
}

void ApplyTreeLumpSum(RecordingTree tree)
{
    if (ShouldPauseTimelineResourceReplay(IsRecording))
        return;  // retry next frame

    // ... apply funds/science/reputation ...

    tree.ResourcesApplied = true;  // only after successful application
}
```

### Fix 6 - NOT A BUG: Milestone double-counting with trees

**Analysis:** The review flagged that `ComputeTotal` might double-count milestones because tree recordings exist in the same epoch as milestone events. After tracing the code:

- Tree recordings exist during flight (launch → revert/commit)
- Milestones track non-flight actions: tech research, part purchases, facility upgrades, crew hires
- `MilestoneCommittedFunds/Science` (ResourceBudget.cs lines 120-159) sums costs from `Milestone.Events` - these are `GameStateEvent` entries of types like `TechResearched`, `PartPurchased`, `FacilityUpgraded`, `CrewHired`
- Contract rewards are `GameStateEvent` entries processed by `GameStateRecorder`, NOT milestone costs
- There is no overlap: tree deltas capture flight-time resource changes, milestones capture non-flight actions

**Verdict:** No fix needed. The separation is inherent in the design.
