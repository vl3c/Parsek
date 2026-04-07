# Recording Optimizer & Post-Processing Review

Investigation date: 2026-04-07
Scope: RecordingOptimizer, RecordingStore.RunOptimizationPass, tree-mode recording flow, branch point handling during splits.

## Context

Traced a Kerbin-launch-to-Mun-landing scenario and a Duna-rover-to-Kerbin-sample-return scenario through the full recording pipeline: tree-mode capture, background recording, optimizer split/merge passes, and final player-facing UI output.

---

## Findings: What's Working Correctly

### 1. Tree-mode boundary suppression is correct

`ShouldSuppressBoundarySplit` (ParsekFlight.cs:4247) correctly suppresses atmosphere, SOI, and altitude boundary splits in tree mode. The recorder accumulates one monolithic recording per vessel segment, with TrackSections marking all environment transitions. The optimizer handles splitting post-commit.

### 2. Environment split classes are correct

`SplitEnvironmentClass` (RecordingOptimizer.cs:527) produces the right granularity:
- Atmo(0), Exo(1), Surface(2), Approach(3) — four classes
- ExoPropulsive/ExoBallistic merged (engine on/off too granular)
- SurfaceMobile/SurfaceStationary merged (same visual context)
- Approach is its own class — correct for loopability (loop just the landing approach, don't bundle 30 min of surface driving)

### 3. ChildBranchPointId re-parenting during optimizer splits is correct

The unconditional move of `ChildBranchPointId` to the second half (RecordingStore.cs:931-933) is safe because:
- In tree mode, `ChildBranchPointId` is always set at recording termination (`CreateSplitBranch`, ParsekFlight.cs:1769-1770)
- The recorder stops at `branchUT` via `StopRecordingForChainBoundary()`
- No trajectory data exists past `branchUT`
- Any optimizer environment split is at an internal boundary, always before `branchUT`
- Therefore `splitUT < ChildBranchPointId.UT` always holds

The `BranchPoint.ParentRecordingIds` update (RecordingStore.cs:937-968) correctly re-links the moved BP to the second half's new RecordingId.

### 4. Permanent visual state forwarding is correct

`ForwardPermanentStateEvents` (RecordingOptimizer.cs:582) seeds the second half with one-way visual state events (shroud jettisoned, fairing jettisoned, decoupled parts) so the ghost renders correctly after a split. Good design.

### 5. Boundary point interpolation is correct

`SplitAtSection` (RecordingOptimizer.cs:310-354) interpolates a synthetic trajectory point at the split boundary when no exact point exists, ensuring both halves have continuous coverage with no gap.

---

## Issues Found

### Issue 1: Rover continuation recording is a ghost without a body

**Severity:** Low (cosmetic/UX)
**Location:** BackgroundRecorder.cs:1356-1377

When a landed vessel goes to background recording, BackgroundRecorder sets `SurfacePos` and `ExplicitEndUT` on the tree recording but creates **no TrackSections and no trajectory Points**. The recording is a time-range placeholder with a position marker but no ghost trail.

**Consequence:** In the Recordings Window, the rover continuation appears as a recording entry with no visual playback. It has no ghost (no points to interpolate), just a map marker at the SurfacePosition. This is correct behavior for a stationary landed vessel, but it may confuse players who expect to see their rover in the ghost playback.

**Possible improvement:** The rover continuation could be marked with a flag (e.g., `IsStaticBackground`) so the UI can display it differently — perhaps as "(stationary)" or with a pin icon instead of a ghost trail entry. Or it could be hidden from the recordings list entirely and only shown as a map marker.

### Issue 2: All-boring leaf recordings survive boring tail trim

**Severity:** Low (UX clutter)
**Location:** RecordingOptimizer.cs:737-804

`TrimBoringTail` calls `FindLastInterestingUT`, which returns NaN when the entire recording is boring (all SurfaceStationary TrackSections, no events). When NaN, the trim is skipped: "entire recording is boring — no reference point."

**Scenario:** After the optimizer splits Approach(3) from Surface(2) on the Mun, the Surface recording is entirely SurfaceStationary. It's the chain leaf, has the VesselSnapshot for spawning, but visually shows nothing interesting.

**Consequence:** A short boring recording clutters the Recordings Window. It can't be trimmed because there's no "last interesting" reference point. It serves a structural purpose (leaf with VesselSnapshot) but has zero visual value.

**Possible improvement:** Instead of trimming, such recordings could be collapsed into the preceding chain segment's display in the UI (shown as a terminal marker rather than a full recording entry). Or `TrimBoringTail` could have an "all-boring leaf" path that trims to a minimal window (e.g., 5 seconds) from the start, keeping just enough for spawn timing.

### Issue 3: No body-change split in the optimizer

**Severity:** Low (correctness OK, labeling could be better)
**Location:** RecordingOptimizer.cs:148-207, FindSplitCandidatesForOptimizer

The optimizer only splits at environment class changes. It does NOT split at body changes (e.g., Kerbin Exo → Mun Exo across an SOI transition). Both sides have the same environment class (Exo=1), so no split occurs.

**Consequence:** For a Kerbin-to-Mun transfer, the Exo recording spans both Kerbin orbit and Mun orbit. The `SegmentBodyName` is derived from the first trajectory point (RecordingStore.cs:926-929), so it shows "Kerbin" even though the recording includes Mun orbit time. This is correct for a single loopable recording (you'd loop the whole transfer), but the labeling could be confusing.

**Possible improvement:** Either:
- (a) Add body name to TrackSection metadata and use body change as a split criterion (produces more granular recordings: Kerbin orbit, transfer coast, Mun orbit)
- (b) Show multiple body names in the UI for recordings that span SOI changes (e.g., "Kerbin → Mun")
- (c) Leave as-is — the transfer is one cohesive phase, splitting it would fragment it unnecessarily

Recommendation: (c) for now, (b) if players report confusion. Splitting at SOI boundaries would over-fragment transfers.

### Issue 4: Optimizer doesn't update BranchPoint.ChildRecordingIds after splits

**Severity:** Very low (no known functional impact)
**Location:** RecordingStore.cs:931-968

When the optimizer splits a recording, it updates `BranchPoint.ParentRecordingIds` for the moved `ChildBranchPointId`. But the **parent** branch point's `ChildRecordingIds` (which lists the original recording as a child) is NOT updated to reflect the split.

Example: BP1.ChildRecordingIds = [rover_cont, rocket]. After optimizer splits rocket into rocket_atmo + rocket_exo_etc, BP1.ChildRecordingIds still says [rover_cont, rocket] — where "rocket" is now just the first chain segment (rocket_atmo).

**Consequence:** Navigation from BP1 to its children would find rocket_atmo (the truncated first half) but not the rest of the chain. The chain linkage (ChainId) provides the missing connection, so playback works. But any code that walks the tree via BranchPoint.ChildRecordingIds alone would miss post-split segments.

**Possible improvement:** After each split, scan the tree's BranchPoints for any whose ChildRecordingIds contains the split recording's ID. No update is needed (the first chain segment IS the child — the chain links to the rest). But a comment documenting this invariant would prevent future confusion.

### Issue 5: No test coverage for optimizer splits on tree recordings with branch points

**Severity:** Medium (testing gap)
**Location:** Source/Parsek.Tests/

The optimizer has tests for split logic (CanAutoSplit, SplitAtSection, environment classification). But there are no tests that exercise the full `RunOptimizationPass` on a tree with branch points — verifying that after splits:
- ChildBranchPointId ends up on the correct chain segment
- BranchPoint.ParentRecordingIds is correctly re-linked
- Chain indexing is correct across branch point boundaries
- Ghost playback can navigate the split tree correctly

This is the scenario most likely to surface edge cases (multi-staging + multi-environment + optimizer splits).

---

## Scenario Verification: Duna Rover to Kerbin Sample Return

### Setup
- Existing rover on Duna (from earlier committed mission)
- Player switches to rover, drives around, launches mini rocket
- Rocket transfers to Kerbin, lands sample return capsule

### Verified tree structure (no staging on rocket)

Before optimizer (3 recordings):
```
Root: Rover [Surface, Duna, driving data] --BP1--> 
  Rover_cont [SurfacePos only, Duna, background]
  Rocket [Atmo(Duna) -> Exo(transfer) -> Atmo(Kerbin) -> Surface(Kerbin)]
```

After optimizer (6 recordings):
```
Root:        Rover        [Surface]  Duna        (has TrackSections, ghost trail)
             Rover_cont   [--]       Duna        (SurfacePos only, no ghost)
Chain idx 0: Rocket       [Atmo]     Duna        (Duna ascent)
Chain idx 1: Rocket       [Exo]      Duna        (orbit + transfer + Mun orbit)
Chain idx 2: Rocket       [Atmo]     Kerbin      (re-entry)
Chain idx 3: Rocket       [Surface]  Kerbin      (landing, leaf -> spawn)
```

### With capsule separation during Exo (8 recordings)

Before optimizer (5 recordings):
```
Root: Rover [Surface] --BP1-->
  Rover_cont [SurfacePos, background]
  Rocket [Atmo(Duna) -> Exo(partial)] --BP2-->
    TransferStage [Exo, debris]
    Capsule [Exo -> Atmo(Kerbin) -> Surface(Kerbin)]
```

After optimizer (8 recordings):
```
Root:          Rover          [Surface]  Duna
               Rover_cont     [--]       Duna
Rocket chain:  Rocket         [Atmo]     Duna       (idx 0)
               Rocket         [Exo]      Duna       (idx 1, ChildBP=BP2)
Branch:        TransferStage  [Exo]      debris
Capsule chain: Capsule        [Exo]      Transfer   (idx 0)
               Capsule        [Atmo]     Kerbin     (idx 1)
               Capsule        [Surface]  Kerbin     (idx 2, leaf -> spawn)
```

---

## Summary

| # | Issue | Severity | Action |
|---|-------|----------|--------|
| 1 | Rover continuation has no ghost (SurfacePos only) | Low | Consider UI indicator for static background recordings |
| 2 | All-boring leaf recordings survive trim | Low | Consider minimal-window trim or UI collapse |
| 3 | No body-change split in optimizer | Low | Leave as-is; consider multi-body label in UI later |
| 4 | BranchPoint.ChildRecordingIds not updated after split | Very low | Add code comment documenting invariant |
| 5 | No test for optimizer splits on tree recordings with BPs | Medium | Add integration test with multi-staging + splits |
