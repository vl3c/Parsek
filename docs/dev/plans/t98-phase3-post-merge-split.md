# T98 Phase 3: Post-Merge Split Pass for Tree Recordings

## Goal

After a tree recording is committed and the session merger produces clean merged Recordings, split multi-environment Recordings at environment boundaries into separate per-phase Recordings. Each phase gets its own loop toggle in the UI — identical to chain mode.

**Before:** Tree recording for Kerbin→Mun mission = 1 Recording with 10+ TrackSections.
**After:** Same mission = 5-8 separate Recordings (atmo ascent, transfer, Mun descent, surface, etc.), each independently loopable.

---

## Design

### Where the split runs

In `RecordingStore.RunOptimizationPass()` (called from `ParsekScenario.OnLoad` after migrations). Add a split pass **after** the existing merge pass. The split pass iterates until no more candidates, same pattern as merge.

### How split candidates are found

`RecordingOptimizer.FindSplitCandidates` already exists and works on any recording (no ChainId/TreeId filter). It scans TrackSections for adjacent sections with different environments. `CanAutoSplit` checks:
- ≥ 2 TrackSections
- No ghosting-trigger PartEvents
- Both halves ≥ 5 seconds

### What happens on split

For each split candidate `(recordingIndex, sectionIndex)`:

1. **Split the Recording** — `SplitAtSection(original, sectionIndex)` produces a second Recording. The original is mutated in-place (truncated). Both get `SegmentPhase` from their first TrackSection's environment.

2. **Assign ChainId for UI grouping** — If the original doesn't have a ChainId, generate one (`Guid.NewGuid().ToString("N")`). Assign it to both halves. This makes them display as an expandable chain block in the recordings UI.

3. **Derive SegmentBodyName** — Tree recordings don't have this set, and TrackSection has no body field. Derive from the first TrajectoryPoint's `bodyName` in each half's Points list.

4. **Assign RecordingId to the new half** — Generate a new `Guid.NewGuid().ToString("N")`.

5. **Preserve TreeId** — Both halves keep the original's `TreeId` so resource tracking still works at tree level.

6. **Update tree.Recordings dict** — Find the tree by `TreeId`, add the new Recording to `tree.Recordings[newId]`, keep the mutated original.

7. **Add new half to CommittedRecordings** — Insert after the original's index.

8. **Save sidecar files** — `SaveRecordingFiles(original)` and `SaveRecordingFiles(newHalf)`. Same pattern as the merge pass.

9. **Reindex chain** — `ReindexChain(recordings, chainId)` to assign sequential `ChainIndex` by `StartUT`.

10. **Copy RecordingGroups** — The new half inherits the original's `RecordingGroups` so it appears in the same UI group.

### Iterative splitting

`FindSplitCandidates` returns at most one split per recording per pass (indices shift after split). The split pass loops until no more candidates, same as the merge pass. Cap at 50 splits per load to avoid pathological cases.

### Auto loop range still applies

When the user toggles loop on a split phase, `ApplyAutoLoopRange` runs. For a single-environment recording, `ComputeAutoLoopRange` returns NaN (no trimming needed), so the full segment loops. This is correct — each segment IS a single phase now.

### BranchPoint handling

Environment splits don't create BranchPoints (not structural splits). The original's `ParentBranchPointId` stays on the first half. The original's `ChildBranchPointId` goes on the last half only. Intermediate halves get null for both. This is safe because BranchPoints represent vessel lifecycle events (dock, EVA, staging), not environment transitions.

### VesselSnapshot handling

`SplitAtSection` already handles this: the VesselSnapshot goes to the second half (it represents end-of-recording state). The first half gets null VesselSnapshot (ghost-only), which is correct for mid-chain segments.

---

## Implementation

### Changes to RecordingStore.RunOptimizationPass

After the merge while-loop, add a split while-loop:

```csharp
// Split pass: break multi-environment recordings at environment boundaries
int splitCount = 0;
const int maxSplitsPerPass = 50;
changed = true;
while (changed && splitCount < maxSplitsPerPass)
{
    changed = false;
    var splitCandidates = RecordingOptimizer.FindSplitCandidates(recordings);
    if (splitCandidates.Count == 0) break;

    var (recIdx, secIdx) = splitCandidates[0];
    var original = recordings[recIdx];
    var second = RecordingOptimizer.SplitAtSection(original, secIdx);

    // Assign RecordingId
    second.RecordingId = Guid.NewGuid().ToString("N");

    // Assign ChainId for UI grouping
    if (string.IsNullOrEmpty(original.ChainId))
        original.ChainId = Guid.NewGuid().ToString("N");
    second.ChainId = original.ChainId;

    // Preserve TreeId
    second.TreeId = original.TreeId;

    // Derive SegmentBodyName from trajectory points
    if (original.Points != null && original.Points.Count > 0)
        original.SegmentBodyName = original.Points[0].bodyName;
    if (second.Points != null && second.Points.Count > 0)
        second.SegmentBodyName = second.Points[0].bodyName;

    // Copy vessel metadata
    second.VesselName = original.VesselName;
    second.VesselPersistentId = original.VesselPersistentId;
    second.RecordingGroups = original.RecordingGroups != null
        ? new List<string>(original.RecordingGroups) : null;

    // Handle BranchPoint linkage
    second.ChildBranchPointId = original.ChildBranchPointId;
    original.ChildBranchPointId = null;
    second.ParentRecordingId = original.RecordingId;

    // Add to committed recordings
    recordings.Insert(recIdx + 1, second);

    // Update tree dict if applicable
    if (!string.IsNullOrEmpty(original.TreeId))
    {
        for (int t = 0; t < CommittedTrees.Count; t++)
        {
            if (CommittedTrees[t].Id == original.TreeId)
            {
                CommittedTrees[t].Recordings[second.RecordingId] = second;
                break;
            }
        }
    }

    // Save sidecar files
    try { SaveRecordingFiles(original); } catch (Exception ex) { /* warn */ }
    try { SaveRecordingFiles(second); } catch (Exception ex) { /* warn */ }

    // Reindex chain
    RecordingOptimizer.ReindexChain(recordings, original.ChainId);

    splitCount++;
    changed = true;
}
```

### Changes to RecordingOptimizer.SplitAtSection

Minor: ensure `second.RecordingId` is not set (caller assigns it). Currently the method creates `new Recording()` which has null `RecordingId`. Verify no conflict.

### Tests

1. **Split pass integration test**: Create a Recording with 3 TrackSections of different environments, run optimization pass, verify 3 separate Recordings with ChainId
2. **ChainId assignment**: Verify split recordings share the same ChainId
3. **SegmentBodyName derivation**: Verify body name comes from first point
4. **TreeId preservation**: Verify split recordings keep the original's TreeId
5. **Tree.Recordings updated**: Verify new recording added to tree dict
6. **BranchPoint linkage**: Verify ParentBranchPointId on first, ChildBranchPointId on last
7. **RecordingGroups inherited**: Verify new recording has same groups
8. **Single-environment no-op**: Verify recordings with one environment are not split
9. **Ghosting trigger blocks split**: Verify recordings with ghosting triggers are not split

---

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Split at load time on every load | Idempotent — single-environment recordings won't be re-split. Second load finds no candidates. |
| Sidecar file I/O failure | Same try/catch + warn pattern as merge pass. Recording data is in memory. |
| Tree dict out of sync | Update tree dict in same pass. Verified by test. |
| ChainId conflicts with existing chains | New GUID per split group. No collision possible. |
| VesselSnapshot on wrong half | SplitAtSection already handles this correctly (snapshot goes to second half). |
| SegmentBodyName wrong after SOI change | Derived per-half from first point's bodyName. Correct even across body boundaries. |
