# T98: Policy Modularity Refactor and Tree Recording Loop Range

## Goal

1. **Modularity**: Consolidate scattered chain-vs-tree policy conditionals into testable, named methods and properties. Make the mode distinction explicit and searchable rather than buried in inline `TreeId != null` checks across 7 files.

2. **Loop range**: Enable per-phase looping for tree recordings. Currently, `LoopPlayback` loops the entire trajectory (StartUT→EndUT). Add `LoopStartUT`/`LoopEndUT` fields so users can loop just a visually interesting phase (reentry, landing, docking approach).

---

## Existing Infrastructure

### What already works

1. **Chain mode per-phase looping** — Chain recordings auto-split at atmosphere/altitude/SOI boundaries (T97). Each segment is a separate Recording with its own `LoopPlayback` toggle.

2. **TrackSection environment classification** — Both chain and tree recordings tag trajectory chunks with `SegmentEnvironment` (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary). Environment transitions are detected with hysteresis.

3. **Engine loop math** — `GhostPlaybackEngine.TryComputeLoopPlaybackUT` computes cyclic UT from `traj.StartUT` / `traj.EndUT`. Clean, well-tested, pure computation.

4. **IPlaybackTrajectory abstraction** — The engine has zero knowledge of chains/trees. It accesses recordings only through this interface.

5. **RecordingOptimizer.SplitAtSection** — Algorithm for splitting a Recording at a TrackSection boundary. Fully implemented but not wired into production (only called from tests).

### Current loop flow (unchanged by this plan)

```
GhostPlaybackEngine.UpdateGhostStates
  -> for each trajectory:
     -> if ShouldLoopPlayback(traj):
        -> TryComputeLoopPlaybackUT(traj, currentUT, ...)
           -> duration = EndUT - StartUT
           -> cycleDuration = duration + intervalSeconds
           -> elapsed = currentUT - StartUT
           -> loopUT = StartUT + (elapsed % cycleDuration)
```

---

## Part 1: Policy Modularity

### Problem

~19 sites across 7 files check `TreeId != null` / `ChainId != null` / `activeTree != null` and branch. The checks are correct but scattered — intent is unclear without reading surrounding code.

### Non-goals

- No strategy pattern or `IRecordingPolicy` interface. This is a 15k-line KSP mod with exactly two modes. A strategy abstraction would be over-engineering.
- No new files. Properties go on `Recording.cs`, extracted methods stay in their host files.
- No behavioral changes. Every refactoring must be a pure mechanical extraction that preserves exact existing behavior.

### Phase 1A: Query properties on Recording

Add three computed properties to `Recording.cs`:

```csharp
/// <summary>True if this recording belongs to a RecordingTree.</summary>
internal bool IsTreeRecording => TreeId != null;

/// <summary>True if this recording belongs to a chain (has ChainId and valid ChainIndex).</summary>
internal bool IsChainRecording => !string.IsNullOrEmpty(ChainId);

/// <summary>
/// True if this recording's resources are tracked individually (per-recording deltas).
/// False for tree recordings, whose resources are tracked at tree level.
/// </summary>
internal bool ManagesOwnResources => !IsTreeRecording;
```

**Migration sites** (mechanical find-and-replace):

| File | Line(s) | Old | New |
|------|---------|-----|-----|
| `ResourceBudget.cs` | 209, 364 | `recordings[i].TreeId != null` | `recordings[i].IsTreeRecording` |
| `ResourceApplicator.cs` | 24 | `rec.TreeId != null` | `rec.IsTreeRecording` |
| `ParsekScenario.cs` | 434 | `recordings[i].TreeId == null` | `!recordings[i].IsTreeRecording` |
| `ParsekScenario.cs` | 1356, 1505 | `rec.TreeId != null` | `rec.IsTreeRecording` |
| `ParsekFlight.cs` | 5854, 5764 | `rec.TreeId != null` / `rec.TreeId == null` | `rec.IsTreeRecording` / `!rec.IsTreeRecording` |
| `GhostPlaybackLogic.cs` | 508, 2168 | `string.IsNullOrEmpty(rec.TreeId)` | `!rec.IsTreeRecording` |
| `RecordingStore.cs` | 1324 | `committedRecordings[i].TreeId == null` | `!committedRecordings[i].IsTreeRecording` |

**Tests**: Property assertions in existing Recording test class. Trivial: set `TreeId`, check `IsTreeRecording`; set `ChainId`, check `IsChainRecording`.

### Phase 1B: Extract boundary suppression decision

The three boundary handlers in `ParsekFlight.cs` (atmosphere ~4192, SOI ~4226, altitude ~4272) have identical guard structure:

```csharp
if (activeTree != null)
{
    ParsekLog.Info("Flight", $"... boundary suppressed in tree mode: ...");
    recorder.ClearBoundaryFlags();
    return;
}
```

Extract:

```csharp
/// <summary>
/// Returns true if the current recording mode suppresses environment boundary
/// splits (tree mode does — chain mode does not).
/// </summary>
internal static bool ShouldSuppressBoundarySplit(RecordingTree activeTree)
{
    return activeTree != null;
}
```

This is trivially simple, but makes the design decision named and searchable. The three call sites become `if (ShouldSuppressBoundarySplit(activeTree))`.

**Tests**: Not individually tested — too trivial (single null check). Covered implicitly by existing boundary split tests.

### Phase 1C: Extract vessel destruction classification

`ParsekFlight.OnVesselWillDestroy` (lines ~965-992) has a three-way mode branch. Extract:

```csharp
internal enum DestructionMode { None, TreeDeferred, StandaloneMerge, TreeAllLeavesCheck }

internal static DestructionMode ClassifyVesselDestruction(
    bool hasActiveTree,
    bool isRecording,
    bool vesselDestroyedDuringRecording,
    bool isActiveVessel,
    bool shouldDeferForTree,
    bool treeDestructionDialogPending)
{
    if (hasActiveTree && shouldDeferForTree)
        return DestructionMode.TreeDeferred;

    if (!hasActiveTree && isRecording && vesselDestroyedDuringRecording && isActiveVessel)
        return DestructionMode.StandaloneMerge;

    if (hasActiveTree && isRecording && vesselDestroyedDuringRecording
        && isActiveVessel && !treeDestructionDialogPending)
        return DestructionMode.TreeAllLeavesCheck;

    return DestructionMode.None;
}
```

**Tests**: 4 cases — one per enum value, verifying the priority order (TreeDeferred checked first).

---

## Part 2: Loop Range for Tree Recordings

### Problem

Tree recordings store all flight phases in one Recording. `LoopPlayback = true` loops StartUT→EndUT — the entire trajectory. Users cannot loop just the atmospheric reentry or the docking approach.

### Design: LoopStartUT / LoopEndUT

Add two optional fields to `Recording`:

```csharp
public double LoopStartUT = double.NaN;  // NaN = use StartUT (loop entire recording)
public double LoopEndUT = double.NaN;    // NaN = use EndUT (loop entire recording)
```

#### Engine changes — three loop functions

There are three loop computation functions that all need range-aware updates:

1. **`GhostPlaybackEngine.TryComputeLoopPlaybackUT`** (line 846) — instance method, takes `IPlaybackTrajectory`. Uses `traj.StartUT`/`traj.EndUT`. Update to use `EffectiveLoopStartUT`/`EffectiveLoopEndUT`. Also fix pause window UT: `loopUT = traj.EndUT` (line 886) must become `loopUT = loopEnd`.

2. **`GhostPlaybackLogic.TryComputeLoopPlaybackUT`** (line 96) — static method, takes raw `startUT`/`endUT` doubles. No signature change needed — callers pass effective loop bounds instead of `traj.StartUT`/`traj.EndUT`.

3. **`GhostPlaybackLogic.ComputeLoopPhaseFromUT`** (line 278) — static method, takes raw `recordingStartUT`/`recordingEndUT`. Same approach — callers pass effective bounds.

All callers of #2 and #3 must be audited to pass effective bounds. Key call sites:
- `ParsekFlight.cs:3150` calls `ComputeLoopPhaseFromUT` — must pass effective bounds
- Engine's instance method (#1) wraps #2 — update the wrapper

Add two pure static helpers (on `GhostPlaybackEngine` or `GhostPlaybackLogic`):

```csharp
internal static double EffectiveLoopStartUT(IPlaybackTrajectory traj)
{
    double loopStart = traj.LoopStartUT;
    return !double.IsNaN(loopStart) && loopStart >= traj.StartUT && loopStart < traj.EndUT
        ? loopStart : traj.StartUT;
}

internal static double EffectiveLoopEndUT(IPlaybackTrajectory traj)
{
    double loopEnd = traj.LoopEndUT;
    return !double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > traj.StartUT
        ? loopEnd : traj.EndUT;
}
```

Cross-validation: if `EffectiveLoopStartUT >= EffectiveLoopEndUT` after clamping, both fall back to full range (StartUT/EndUT). This handles inverted ranges and degenerate cases.

#### Interface changes

Add to `IPlaybackTrajectory`:

```csharp
double LoopStartUT { get; }
double LoopEndUT { get; }
```

**Both implementations must be updated:**
- `Recording.cs` — add fields + explicit interface properties (~line 289)
- `MockTrajectory.cs` (in `Parsek.Tests`) — add properties with NaN defaults

#### ShouldLoopPlayback changes

Update duration check to use effective bounds:

```csharp
internal static bool ShouldLoopPlayback(IPlaybackTrajectory traj)
{
    if (traj == null || !traj.LoopPlayback || traj.Points == null || traj.Points.Count < 2)
        return false;
    double start = EffectiveLoopStartUT(traj);
    double end = EffectiveLoopEndUT(traj);
    return end - start > GhostPlaybackLogic.MinLoopDurationSeconds;
}
```

#### Save/load

Add `LoopStartUT` and `LoopEndUT` to `ParsekScenario` save/load. Follow existing conditional-write pattern (like `LoopAnchorVesselId`): only write to save file when non-NaN, read with `double.TryParse` using NaN as fallback. Old saves without these keys load as NaN = full range. NaN serialization via `ToString("R")` produces `"NaN"` which round-trips correctly in .NET.

#### RecordingOptimizer update

`CanAutoMerge` (line 18) must block merge when either recording has non-NaN `LoopStartUT`/`LoopEndUT`, since these represent user customization. Add after the existing `LoopPlayback` check:

```csharp
if (!double.IsNaN(a.LoopStartUT) || !double.IsNaN(a.LoopEndUT)) return false;
if (!double.IsNaN(b.LoopStartUT) || !double.IsNaN(b.LoopEndUT)) return false;
```

#### Loop phase offset interaction

When loop range changes, clear `loopPhaseOffsets` for the affected recording index. Phase offsets were calibrated against the old elapsed calculation (relative to `traj.StartUT`); after a range change, stale offsets could push `elapsed` outside the loop range. The UI code that sets `LoopStartUT`/`LoopEndUT` should call `ClearLoopPhaseOffset(recIdx)` if exposed, or the engine detects range changes and auto-clears.

#### Rendering behavior

When a loop range is set and `LoopPlayback` is true, the ghost **immediately loops within the range** — it never renders trajectory outside `[LoopStartUT, LoopEndUT]`. This matches how chain recordings work (they only play their segment's time range). There is no "first pass" concept — `ShouldLoopPlayback` returns true on the first frame, and the engine enters the loop dispatch branch immediately.

### Phase 2A: UI — Phase Picker

In the Recording row's loop configuration (ParsekUI.cs), when a recording has multiple TrackSections with different environments, show a phase picker:

```
[Loop ✓] [Phase: Atmospheric Descent ▼] [Interval: 10s]
```

The dropdown lists phases derived from TrackSections:
- Group consecutive same-environment sections
- Label format: "{Environment} ({duration}s)" — e.g., "Atmospheric (47s)", "Exo Propulsive (12s)"
- "Entire Recording" option (sets LoopStartUT/LoopEndUT to NaN)

Selecting a phase sets `LoopStartUT` / `LoopEndUT` to the TrackSection group's time range.

**Implementation**: Pure static method to build phase options from TrackSections:

```csharp
internal static List<LoopPhaseOption> BuildLoopPhaseOptions(List<TrackSection> sections)
```

Where `LoopPhaseOption` is a lightweight struct: `{ string label, double startUT, double endUT }`.

When loading a recording that already has `LoopStartUT`/`LoopEndUT` set, the UI reverse-maps the stored UT range to the closest matching phase option. If no phase matches exactly (e.g., TrackSections were modified by an optimizer pass), display "Custom Range" as the selected option.

**Tests**: Build options from synthetic TrackSection lists, verify grouping and labels. Test reverse-mapping with exact match, no match (Custom), and empty sections.

---

## Execution Order

1. **Phase 1A** — Query properties (smallest, unblocks everything). Commit.
2. **Phase 1B** — Boundary suppression extraction. Commit.
3. **Phase 1C** — Destruction classification extraction. Commit.
4. **Phase 2** — LoopStartUT/LoopEndUT fields + engine changes + save/load. Commit.
5. **Phase 2A** — Phase picker UI. Commit.
6. All tests run green. Final review pass.

Each phase is independently committable and testable. No phase depends on a later phase.

---

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Mechanical rename introduces typo | Compile + full test run after Phase 1A |
| Destruction classification changes behavior | Extract preserves exact condition order and short-circuit logic. Tests verify all 4 paths. |
| Loop range breaks existing loop behavior | NaN defaults preserve current behavior. Existing loop tests remain unchanged. New tests cover range narrowing. |
| Phase picker UI complexity | Pure static `BuildLoopPhaseOptions` — no UI framework coupling in the logic. UI is just a dropdown calling the static method. |
| IPlaybackTrajectory interface change breaks implementations | Two implementations: `Recording` and `MockTrajectory` (tests). Both get NaN defaults. |
| Stale loop phase offsets after range change | Clear offsets when loop range is set. |
| Inverted/degenerate loop range | `EffectiveLoopStartUT`/`EffectiveLoopEndUT` fall back to full range when start >= end. |
| Auto-merge loses loop range data | `CanAutoMerge` blocks when LoopStartUT/LoopEndUT are non-NaN. |

---

## Test Plan

| Phase | Method/Property | Tests |
|-------|----------------|-------|
| 1A | `IsTreeRecording` | TreeId set → true, null → false |
| 1A | `IsChainRecording` | ChainId set → true, null/empty → false |
| 1A | `ManagesOwnResources` | Tree → false, standalone → true |
| 1C | `ClassifyVesselDestruction` | 4 cases: None, TreeDeferred, StandaloneMerge, TreeAllLeavesCheck |
| 2 | `EffectiveLoopStartUT` | NaN → StartUT, valid → LoopStartUT, below StartUT → StartUT |
| 2 | `EffectiveLoopEndUT` | NaN → EndUT, valid → LoopEndUT, above EndUT → EndUT |
| 2 | `EffectiveLoop*` cross-validation | start >= end → fall back to full range |
| 2 | `ShouldLoopPlayback` with range | Range too short → false, valid range → true |
| 2 | `TryComputeLoopPlaybackUT` with range | Verify loopUT stays within [LoopStartUT, LoopEndUT] |
| 2 | `TryComputeLoopPlaybackUT` pause window | Pause UT = loopEnd, not traj.EndUT |
| 2 | `CanAutoMerge` with loop range | Non-NaN LoopStartUT/LoopEndUT blocks merge |
| 2 | Save/load round-trip | NaN values and valid values serialize/deserialize correctly |
| 2A | `BuildLoopPhaseOptions` | Empty sections, single section, multi-env grouping, consecutive same-env merge |
| 2A | Phase reverse-mapping | Exact match, no match → "Custom Range" |
