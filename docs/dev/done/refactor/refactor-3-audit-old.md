# Refactor-3 Architecture Audit

**Produced from PR #85 branch (March 2026). Read-only analysis — no code changes.**

This audit identifies refactoring opportunities NOT already tracked in `refactor-2-deferred.md` or the todo file. Items already done or explicitly deferred are excluded.

---

## File Size Summary (post-PR #85)

| File | Lines | Trend | Notes |
|------|-------|-------|-------|
| ParsekFlight.cs | 8,092 | -1,800 from peak | Still the largest file. T25/T26 extractions helped significantly |
| GhostVisualBuilder.cs | 6,283 | -342 from peak | EngineFxBuilder split helped; AddPartVisuals still 1,075 lines |
| FlightRecorder.cs | 4,914 | stable | Layer 1 shared statics well-designed |
| ParsekUI.cs | 3,558 | stable | God object — 15+ responsibilities, 150+ fields |
| BackgroundRecorder.cs | 2,760 | stable | 17 mirrored Layer 2 wrappers (tracked as T29) |
| RecordingStore.cs | 2,533 | stable | Rewind state fragility is the main concern |
| GhostPlaybackLogic.cs | 2,286 | new | ApplyPartEvents 222-line monolith |
| ParsekScenario.cs | 1,757 | -969 from peak | CrewReservation/Resource/Group extractions helped |
| GhostPlaybackEngine.cs | 1,594 | new | Loop/overlap methods are large |

---

## Tier 1 — High-Value Structural Extractions

### R3-1. ParsekUI God Object Decomposition

**Severity: Critical. 3,558 lines, 15+ distinct responsibilities, 150+ fields.**

ParsekUI is the clearest god object in the codebase. It handles main window, recordings table, per-row editing, group tree, group picker popup, actions window, settings window, spawn control window, map markers, style caching, sort state, tooltip rendering, loop period editing, and budget display — all in one class.

**Recommended splits (by self-containedness):**

| # | Extraction | Methods | Fields | Lines | Risk |
|---|-----------|---------|--------|-------|------|
| A | **GroupPickerUI** — popup lifecycle, tree rendering, apply/cancel | 8 | ~10 | ~250 | Low |
| B | **UIStyleCache** — all `Ensure*Style` methods + GUIStyle fields | 5 | ~12 | ~120 | Low |
| C | **ActionsWindowUI** — DrawActionsWindow, BuildSortedActionEvents, sort state | 4 | ~8 | ~200 | Low |
| D | **RecordingsTableUI** — DrawRecordingsWindow, DrawRecordingRow, DrawGroupTree, sort state, rename state | 10+ | ~30 | ~800 | Medium |
| E | **SpawnControlWindowUI** — DrawSpawnControlWindowIfOpen, spawn candidates, countdown | 3 | ~8 | ~150 | Low |

**Total extractable:** ~1,500 lines. ParsekUI would drop to ~2,000 lines.

**Start with A+B** (lowest risk, most self-contained). C and E are independent windows with no shared state. D is the largest win but needs careful field migration.

---

### R3-2. GhostVisualBuilder.AddPartVisuals Decomposition

**Severity: High. 1,075-line method processing 11 visual module types sequentially.**

This is the largest single method in the codebase. It processes parachutes, engines, deployables, lights, fairings, RCS, robotics, color changers, cargo bays, gear, and variants — all in one method with 20+ local variables tracked across iterations.

**Recommended approach:** Extract per-module-type handlers as private methods:

| # | Handler | Approx Lines | Dependencies |
|---|---------|-------------|-------------|
| A | `ProcessParachuteVisuals` | ~80 | canopyInfos, canopyCache |
| B | `ProcessDeployableVisuals` | ~60 | deployableInfos |
| C | `ProcessLightVisuals` | ~40 | lightInfos |
| D | `ProcessGearVisuals` | ~50 | gearInfos |
| E | `ProcessCargoBayVisuals` | ~50 | cargoBayInfos |
| F | `ProcessFairingVisuals` | ~60 | fairingInfos |
| G | `ProcessRcsVisuals` (calls TryBuildRcsFX) | ~40 | rcsInfos |
| H | `ProcessRoboticVisuals` | ~50 | roboticInfos |
| I | `ProcessColorChangerVisuals` | ~30 | colorChangerInfos |
| J | `ProcessVariantVisuals` | ~80 | variant state |

**Risk:** Medium — the local variables shared across module handlers need to become parameters. No logic change, pure mechanical extraction.

**Complementary extractions from GhostVisualBuilder (lower priority):**

| # | Group | Methods | Lines |
|---|-------|---------|-------|
| K | **VariantResolver** — variant node resolution + texture application | 7 methods | ~400 |
| L | **RoboticGhostHelper** — servo transform resolution + axis parsing | 6 methods | ~200 |
| M | **TransformHelper** — hierarchy walking utilities | 8 methods | ~150 |

---

### R3-3. RewindContext Encapsulation

**Severity: High (fragility). 8 static fields in RecordingStore, mutated by 4+ external classes.**

Current state:
```
RecordingStore.IsRewinding          — read by ParsekScenario, ParsekFlight, ParsekPlaybackPolicy, ResourceApplicator
RecordingStore.RewindUT             — read by ParsekFlight, ActionReplay
RecordingStore.RewindAdjustedUT     — read by ParsekFlight
RecordingStore.RewindReserved       — set by ParsekScenario, read by ResourceApplicator
RecordingStore.RewindBaselineFunds  — set by ParsekScenario, read by ResourceApplicator
RecordingStore.RewindBaselineScience — set by ParsekScenario, read by ResourceApplicator
RecordingStore.RewindBaselineRep    — set by ParsekScenario, read by ResourceApplicator
```

**Recommendation:** Encapsulate into a `RewindContext` struct or static class with:
- `BeginRewind(double ut, BudgetSummary reserved, double funds, double science, float rep)`
- `EndRewind()`
- Read-only properties for all fields
- Single mutation point instead of 4+ files writing individual fields

**Risk:** Low — mechanical field migration, no logic change.

---

## Tier 2 — Medium-Value Extractions

### R3-4. ParsekFlight Watch Mode State Extraction

**13 camera-follow fields (lines 232-244) → `WatchModeState` class or struct.**

Fields: `watchedRecordingIndex`, `watchedRecordingId`, `watchStartTime`, `watchedOverlapCycleIndex`, `overlapRetargetAfterUT`, `overlapCameraAnchor`, `savedCameraVessel`, `savedCameraDistance`, `savedCameraPitch`, `savedCameraHeading`, `watchEndHoldUntilUT`, `savedPivotSharpness`, `watchNoTargetFrames`.

Plus duplicated camera event handler code:
- `HandleLoopCameraAction` ≈ `HandleOverlapCameraAction` (~40-50 lines duplicated)
- Shared blocks: `RetargetCameraToGhost`, `SetupExplosionHold`

**Extraction:** `WatchModeState` struct + `EnterWatchMode`/`ExitWatchMode`/`RetargetCamera`/`SetupExplosionHold` methods. Would clean up the 558-line Camera Follow region.

**Risk:** Medium — watch mode interacts with engine events via Policy callbacks.

---

### R3-5. ParsekFlight Dock/Undock State Extraction

**5 fields (lines 134-139, 171) + `HandleDockUndockCommitRestart` has 4x repeated cleanup + 4x repeated restart.**

Fields: `pendingDockMergedPid`, `pendingDockAsTarget`, `dockConfirmFrames`, `pendingUndockOtherPid`, `undockConfirmFrames`, `pendingDockAbsorbedPid`.

**Duplication in HandleDockUndockCommitRestart (103 lines):**
- `ClearDockUndockState()` — 3-line block repeated 4 times
- `RestartRecordingAfterDockUndock(message)` — 6-line block repeated 4 times

**Extraction:** Move fields to a struct, extract `ClearDockUndockState()` and `RestartRecordingAfterDockUndock(string)`.

**Risk:** Low — isolated state, self-contained method.

---

### R3-6. GhostPlaybackLogic.ApplyPartEvents Decomposition

**222-line method with 31-case switch statement.**

This is the second-largest single method. Each case handles a different `PartEventType` with 3-5 operations.

**Recommended approach:** Group cases by subsystem and extract handlers:

| # | Handler | Cases | Lines |
|---|---------|-------|-------|
| A | `ApplyParachuteEvent` | 3 cases | ~40 |
| B | `ApplyEngineEvent` | 3 cases | ~30 |
| C | `ApplyRcsEvent` | 3 cases | ~30 |
| D | `ApplyHeatEvent` | 3 cases | ~25 |
| E | `ApplyDeployableEvent` | 2 cases | ~15 |
| F | `ApplyLightEvent` | 5 cases | ~20 |
| G | `ApplyStructuralEvent` (jettison/fairing/cargo) | 3 cases | ~20 |
| H | `ApplyMechanicalEvent` (gear/robotic) | 5 cases | ~25 |

Switch body would reduce from ~200 to ~30 lines (one-liner calls per case group).

**Risk:** Low — pure mechanical extraction, switch statement preserved.

---

### R3-7. GhostPlaybackEngine Loop/Overlap Decomposition

**UpdateLoopingPlayback: 182 lines. UpdateOverlapPlayback: 142 lines.**

These are the two largest methods in the engine. Both contain mixed responsibilities: cycle computation, ghost spawn/destroy, camera events, pause window handling, and visual effects.

**Recommended approach:** Extract state-machine phases:

| Method | Extract | Lines |
|--------|---------|-------|
| UpdateLoopingPlayback | `HandleLoopCycleChange` (spawn/destroy/events) | ~60 |
| UpdateLoopingPlayback | `HandleLoopPauseWindow` (position-at-end, hide parts) | ~30 |
| UpdateOverlapPlayback | `HandleOverlapCycleChange` (primary promotion, cleanup) | ~50 |
| UpdateOverlapPlayback | `CleanupExpiredOverlaps` (iterate, destroy expired) | ~30 |

**Risk:** Medium — loop state mutations require careful `ref` parameter design.

---

### R3-8. RecordingStore Serialization Extraction

**6+ methods (~400 lines) of pure ConfigNode serialization.**

Methods: `SerializePoint`, `DeserializePoint`, `SerializeOrbitSegment`, `DeserializeOrbitSegment`, `SerializeTrajectoryInto`, `DeserializeTrajectoryFrom`.

**Recommendation:** Extract to `TrajectorySerializer` static class. Zero state dependencies — all methods are pure transforms between ConfigNode and data types.

**Risk:** Low — pure static methods, no state. 15+ test files reference these (need import update).

---

### R3-9. ParsekFlight Split Detection State Extraction

**7 fields (lines 152-165) for joint break / decouple detection.**

Fields: `lastBranchUT`, `lastBranchVesselPids`, `pendingSplitInProgress`, `pendingSplitRecorder`, `preBreakVesselPids`, `decoupleCreatedVessels`, `decoupleControllerStatus`.

These fields support the 1,360-line "Split Event Detection" region. Extracting the state into a `SplitDetectionState` struct would make the region's data flow explicit.

**Risk:** Medium — state is read/written across multiple event handlers in the region.

---

## Tier 3 — Low-Value / Deferred

### R3-10. Engine/Logic Boundary Clarification

**Current issue:** GhostPlaybackLogic mutates Engine-owned state (ghostState.partEventIndex, info dictionaries). Zone rendering decisions are scattered across Engine, Logic, and IGhostPositioner.

**Recommendation:** Move `PopulateGhostInfoDictionaries` and `HideAllGhostParts`/`HidePartSubtree` to Engine (they mutate Engine-owned GameObjects). Keep Logic as pure decision/application layer.

**Risk:** Medium — cross-file method moves affect 3+ callers. Deferred until standalone ghost mod extraction.

---

### R3-11. ParsekFlight Preview Playback State Extraction

**7 fields (lines 41-48) for manual preview playback.**

Fields: `isPlaying`, `playbackStartUT`, `recordingStartUT`, `ghostObject`, `previewGhostMaterials`, `previewGhostState`, `previewRecording`, `lastPlaybackIndex`.

Low complexity, isolated usage. Only used by `StartPlayback`/`StopPlayback`/`UpdateManualPlayback`.

**Risk:** Low. **Priority:** Low — the preview feature is rarely used and may be removed.

---

### R3-12. RecordingStore Pending Lifecycle Extraction

`StashPending`, `CommitPending`, `DiscardPending`, `ClearCommitted` + 3 static fields → `PendingRecordingManager`.

**Risk:** Low but high test churn (15+ test files reference `RecordingStore.StashPending` etc).

---

### R3-13. RecordingStore Event Retime Duplication

`StashPending` has identical event-retime loops for `partEvents` and `flagEvents` (~15 lines duplicated). Extract `RetimeEventsToTrimUT(List<T>, double, Func<T,double>, Func<T,double,T>)`.

**Risk:** Low. **Priority:** Low — small savings.

---

### R3-14. EvaluateSoftCaps Finish Extraction

GhostPlaybackEngine.EvaluateSoftCaps (112 lines) already delegates to `GhostSoftCapManager.EvaluateCaps` but retains the host-side logic for applying cap results (fidelity reduction, despawn, restore). The apply logic could move to GhostSoftCapManager.

**Risk:** Low. **Priority:** Low — the split is already clean enough.

---

## Cross-Reference: Already Tracked Items

These items from `todo-and-known-bugs.md` and `refactor-2-deferred.md` are confirmed still relevant:

| ID | Item | Status |
|----|------|--------|
| T29 | BackgroundRecorder 17 Check*State polling dedup (D11) | Open — intentional design |
| T31 | CreateBreakupChildRecording dedup (D1) | Open — conditional flags needed |
| T34 | ChainSegmentManager unit tests | Open — medium priority |
| T35 | ChainSegmentManager field encapsulation | Open — low priority |
| T36 | Continuation recording index fragility | Open — low priority |

---

## Recommended Execution Order

**Phase A — Quick Wins (low risk, high clarity gain):**
1. R3-3 RewindContext encapsulation
2. R3-5 Dock/undock state + HandleDockUndockCommitRestart cleanup
3. R3-6 ApplyPartEvents decomposition
4. R3-1A GroupPickerUI extraction from ParsekUI
5. R3-1B UIStyleCache extraction from ParsekUI

**Phase B — Structural (medium risk, high line reduction):**
6. R3-2 AddPartVisuals decomposition (handlers A-J)
7. R3-4 Watch mode state extraction
8. R3-7 Loop/overlap decomposition
9. R3-1C-E Remaining ParsekUI window extractions
10. R3-8 RecordingStore serialization extraction

**Phase C — Cleanup:**
11. R3-9 Split detection state extraction
12. R3-2K-M GhostVisualBuilder helper extractions (Variant/Robotic/Transform)
13. R3-1D RecordingsTableUI extraction (largest, most coupled)

**Estimated total line reduction:** ~2,500-3,000 lines moved to focused classes. No lines deleted (structural moves only).
