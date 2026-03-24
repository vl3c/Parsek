# Parsek Code Refactor-2 Plan

## Overview

Second-pass structural refactoring of ~52,000 lines across 68 C# source files. Since refactor-1 (March 16, 2026):

- **PR #59** "Recording system redesign" added **38,902 lines** across 114 files (78.5% of all new code)
- **6 substantial follow-up PRs** (#63, #64, #65, #66, #72, #78) added **7,498 lines**
- **9 small bugfix PRs** added **347 lines**
- **Total: ~48,675 net new lines** across 26 new files + 15 modified files (= 41 files needing audit)

**Hard requirement: no logic changes.** Only structural reshuffling, logging additions, and new unit tests. Same rules as refactor-1.

**Baseline:** 3227 tests pass, 0 warnings on `refactor-2` branch at `3055a29`.

## Setup Phase

1. **Worktree created:** `Parsek-refactor-2` branch `refactor-2` off `main` at `3055a29`
2. **Research doc:** `docs/dev/plans/code-refactor-2-inventory.md` — exhaustive per-file inventory of everything needing work
3. **Review checklist:** `docs/dev/plans/refactor-2-review-checklist.md` — 13-item checklist (R1's 12 items + new #13: Conflict Resolution Principle)

## Scope

### What changed since refactor-1

| Category | Files | Description |
|----------|-------|-------------|
| Tier 1 — Critical (>2000 lines, heavily modified) | 8 | Sequential processing, individual commits |
| Tier 2 — Large (200-1100 lines, new or modified) | 13 | 3 parallel batches |
| Tier 3 — Small/data (scan only) | 20 | Single pass, most need no changes |
| Skip (already clean from R1) | 27 | No action needed |
| **Total** | **68** | |

**Source of new code:** PR #59 (38,902 lines, 78.5%), 6 feature PRs (7,498 lines, 15%), 9 bugfix PRs (347 lines, 0.7%), docs (1,532 lines, 3%)

### What does NOT need re-refactoring

27 files skip (refactored in R1, no significant changes since): TrajectoryMath, ActionReplay, MilestoneStore, ResourceBudget, GameStateRecorder, GameStateStore, GameStateEvent, GameStateBaseline, RecordingPaths, ParsekLog, Milestone, ParsekSettings, ParsekHarmony, ParsekToolbarRegistration, CommittedActionDialog, PartEvent, OrbitSegment, SurfacePosition, TerminalState, BranchPoint, TrajectoryPoint, 5 Patches/ files, Properties/AssemblyInfo (auto-generated).

---

## Subagent Rules (all passes)

- **All subagents use Opus** (`model: "opus"`) — non-negotiable for reliability
- Each subagent receives the Extraction Rules below as part of its prompt
- Orchestrator reviews every diff before accepting

---

## Extraction Rules for Subagents

These rules are given verbatim to every Pass 1 subagent. **Identical to refactor-1.**

### ALLOWED

- Moving a contiguous block of code from method A into a new `private` method B, called from the exact same position in A
- Adding parameters to the new method for any locals it needs (prefer passing values over `ref`/`out` when the caller doesn't need the mutation back)
- Reordering `using` directives
- Adding `#region` markers for organizational clarity
- Adding `ParsekLog` calls (see Logging Carve-out below)
- Marking **newly extracted** methods as `internal static` when they are pure functions testable without Unity, or `private` otherwise
- **C# accessibility rule:** A method marked `internal static` can only use parameter types that are `internal` or `public`. If any parameter type is `private` (e.g., a `private enum`), the method MUST be `private static` instead. Do NOT change the access modifier on the pre-existing type to work around this.

### NOT ALLOWED

- Changing any access modifier on **pre-existing** methods (methods that existed before this refactoring pass)
- Converting a local variable to a field to make extraction work
- Changing a captured closure to a parameter (changes when the value is read)
- Splitting a single loop into two calls (changes iteration order of side effects)
- Extracting the body of `switch` cases into individual methods unless the case body exceeds ~20 lines
- Extracting single-use guard clauses (`if (x == null) return;`) at the top of methods — these are standard patterns
- Extracting code shorter than 5 lines into its own method
- Changing any logic, adding new conditions, reordering operations, or altering control flow

### COROUTINE RULE

Methods with `IEnumerator` return type are **off-limits for method extraction** because `yield` cannot live in a sub-method. **Logging additions and tests ARE permitted** for coroutine methods.

### NAMING

Follow existing codebase conventions — verb-prefixed descriptive names (e.g., `CheckParachuteTransition`, `ClassifyGearState`, `TryComputeLoopPlaybackUT`). The extracted method name should describe **what** the block does, not **where** it came from.

### CONFLICT RESOLUTION PRINCIPLE

**Preservation rules ALWAYS override improvement rules.** If making an extracted method `internal static` (for testability) would require changing a pre-existing type's access modifier, the correct resolution is to make the method `private static` (untestable but rule-compliant). Never "fix" a compilation error by modifying pre-existing code that the rules say not to touch.

### METHOD SIZE GUIDANCE

Extract when a method exceeds ~30 lines AND contains multiple logical steps that each have a clear single-purpose name. Do not extract purely for line count — a 50-line method that does one coherent thing is fine.

---

## Logging Carve-out

Adding `ParsekLog` calls is the **one permitted non-structural change**. These calls must be:
- Purely observational — must not change control flow
- Must not appear inside conditional branches that would alter existing behavior
- Must not be added inside performance-critical tight inner loops

### Logging API (from ParsekLog.cs)

- `ParsekLog.Info(subsystem, message)` — important events (start/stop, state changes, errors)
- `ParsekLog.Warn(subsystem, message)` — warnings
- `ParsekLog.Error(subsystem, message)` — errors
- `ParsekLog.Verbose(subsystem, message)` — diagnostic detail. Has internal guard: `if (!IsVerboseEnabled) return;`
- `ParsekLog.VerboseRateLimited(subsystem, key, message)` — per-frame data with rate limiting
- `ParsekLog.Log(message)` — shorthand for `Info("General", message)`

### Verbose Performance Rule

`Verbose()` has an internal `if (!IsVerboseEnabled) return;` guard, so the method call itself is cheap when verbose is off. However, **string interpolation at the call site happens unconditionally**.

- The existing codebase has 400+ `Verbose()` calls with `$"..."` interpolation and no external guard. This is the accepted pattern — **do NOT retrofit external guards onto existing calls**.
- For **newly added** verbose calls in hot paths (per-frame, per-part iteration), prefer the external guard:
  ```csharp
  if (ParsekLog.IsVerboseEnabled)
      ParsekLog.Verbose("Subsystem", $"expensive {interpolation}");
  ```
- For newly added verbose calls in cold paths (one-time init, event handlers), the internal guard is sufficient.
- `VerboseRateLimited` is already guarded — use it for per-frame diagnostic data.

### Logging Scope

Every action, state transition, guard-condition skip, and error path should have a log statement. Use:
- `Info` for important lifecycle events
- `Verbose` for diagnostic breadcrumbs
- `VerboseRateLimited` for per-frame data only
- Format: `[Parsek][LEVEL][Subsystem] message` (handled automatically by ParsekLog.Write)

---

## Unit Test Rules

For every extracted method that is testable without Unity:

- Mark the method `internal static` (newly created methods only — do not change access on pre-existing methods)
- Write tests covering: normal path, edge cases (null, empty, boundary values), and at least one failure/skip path
- **Every test must have a comment stating what bug it would catch** — no vacuous tests that pass by default
- Use `ParsekLog.TestSinkForTesting` to capture and assert on log output where logging behavior matters
- Use `[Collection("Sequential")]` for any test touching shared static state
- **Do NOT write tests for methods that require Unity types as live objects** (MonoBehaviour, GameObject, Transform, etc.) — only test pure logic
- xUnit auto-discovers test files — no need to modify the test `.csproj`

### Test File Naming

Follow the existing feature-based naming convention (e.g., `AdaptiveSamplingTests.cs`, `RewindLoggingTests.cs`). Add tests to existing test files when they cover the same feature area. Create new feature-named files (not source-file-named) when needed.

---

## Pass 1 — Method Extraction + Logging + Tests

**Goal:** For each file: (a) extract logical units into well-named methods, (b) ensure comprehensive logging, (c) add practical unit tests where possible. **No cross-file changes.**

### Per-file Workflow

1. **Read & analyze** — subagent reads the entire file (or section-by-section for Tier 1), identifies extraction candidates, notes methods lacking logging, notes testable methods without tests
2. **Extract methods** — apply the Extraction Rules
3. **Add/verify logging** — per the Logging Carve-out rules
4. **Add unit tests** — per the Unit Test Rules
5. **Update inventory doc** — mark file status as `Pass1-Done`

### Processing Order

#### Tier 1 — Critical (>2000 lines, heavily modified), one file at a time

These files were refactored in pass 1 but have grown 17-81% since. Focus on NEW code only — don't re-refactor already-extracted methods.

| File | Lines | Growth | Top Extraction Targets |
|------|-------|--------|------------------------|
| ParsekFlight.cs | 9,876 | +1,651 (20%) | `PromoteToTreeForBreakup` (265), `CreateSplitBranch` (170), `OnSceneChangeRequested` (205), `CommitTreeFlight` (115), `FinalizeTreeRecordings` (115), commit pattern dedup |
| GhostVisualBuilder.cs | 7,642 | +1,247 (19%) | Animation sampling methods (SampleXxxStates ~150 each), FX prefab resolution chain (170), `AddPartVisuals` per-part block |
| FlightRecorder.cs | 4,956 | +849 (21%) | `StartRecording` (165), `OnPhysicsFrame` (210), `LogVisualRecordingCoverage` (200), `ResetPartEventTrackingState` (90) |
| ParsekUI.cs | 3,595 | +672 (23%) | `DrawRecordingsWindow` (307), `DrawRecordingRow` (205), `DrawGroupTree` (221), `DrawActionsWindow` (155), `DrawSpawnControlWindow` (116) |
| BackgroundRecorder.cs | 2,759 | +1,235 (81%) | `HandleBackgroundVesselSplit` (177), `InitializeLoadedState` (73), `InitializeOnRailsState` (81), Check*State dedup opportunity |
| RecordingStore.cs | 2,673 | +678 (34%) | `DeserializeTrackSections` (187), `SerializeTrackSections` (110), `InitiateRewind` (139), `PreProcessRewindSave` dedup, POINT serialization dedup |
| ParsekScenario.cs | 2,693 | +396 (17%) | `OnLoad` (586! — extract HandleRewindOnLoad/HandleRevertOnLoad/HandleInitialLoad), `LoadStandaloneRecordingsFromNodes` (145), `SwapReservedCrewInFlight` (104) |
| GhostPlaybackLogic.cs | 2,185 | +771 (54%) | `PopulateGhostInfoDictionaries` (94), `SetEngineEmission`/`SetRcsEmission` dedup, `ApplyHeatState` (77) |

**After each file:** `cd Source/Parsek && dotnet build` then `cd Source/Parsek.Tests && dotnet test`. Commit individually per file (8 commits).

**Processing strategy for large files:** Subagent reads field declarations + one region at a time. Coroutine methods (IEnumerator) are off-limits for extraction (logging + tests only). Focus on new code since refactor-1.

**High-scrutiny files:**
- **ParsekScenario.OnLoad** (587 lines, lines 224-810) — load-order-sensitive. When extracting sub-methods, verify that state set in phase N is NOT accidentally passed as a parameter to phase N+1 when the original code read it from a field at that later point. The review agent gets extra attention on this file.
- **RecordingStore POINT serialization dedup** — 4 duplicated blocks (2 serialize + 2 deserialize). Serialization blocks are field-for-field identical (verified). Deserialization blocks are nearly identical but `DeserializePoints` has `parseFailCount` tracking that the TrackSection version lacks — handle carefully. Review checklist item #10 (deduplication correctness) is critical here.
- **GhostPlaybackLogic SetEngineEmission/SetRcsEmission** — only ~50-60% identical (verified), not 80% as initially estimated. Different diagnostic tracking code paths. Evaluate during Pass 1 whether dedup is worthwhile — do NOT force it.

**Known logging gaps (must fix during Pass 1):**
- **GhostChainWalker.cs** — `IsIntermediateChainLink` (35 lines) and `WalkToLeaf` (52 lines) have **zero logging**. Also `TraceLineagePids` (30 lines) and `ResolveTermination` (18 lines).

**Known test coverage gap:**
- **SpawnCollisionDetector.cs** — only 16 tests for 378 lines (thinnest coverage of any file with >300 lines). Add edge-case tests for `ComputeVesselBounds` snapshot parsing and `WalkbackAlongTrajectory`.

**Explicit non-targets:**
- **BackgroundRecorder's 17 Check\*State methods** (736 lines) — these follow an identical pattern but vary in module types, key types, and classification logic. A generic `PollModuleState<T>` helper would change the call pattern and is **not allowed** under the extraction rules. Same ruling as R1's FlightRecorder Check\* methods. Leave as-is; extract sub-steps within individual methods only if they exceed 30 lines.

#### Tier 2 — Large new/modified files (200-1100 lines), 2-3 parallel Opus subagents

| Batch | Files |
|-------|-------|
| 2A | VesselSpawner.cs (1,031), RecordingTree.cs (953), MergeDialog.cs (862), ParsekKSC.cs (919) |
| 2B | PartStateSeeder.cs (722), VesselGhoster.cs (682), GhostChainWalker.cs (592), TimeJumpManager.cs (552) |
| 2C | SessionMerger.cs (476), GhostCommNetRelay.cs (389), SpawnCollisionDetector.cs (378), GhostExtender.cs (266), SelectiveSpawnUI.cs (201) |

**After batch:** Merge-check, then `dotnet build` + `dotnet test`. Commit per batch (3 commits).

#### Tier 3 — Small/data files (scan only), single pass

20 files. Quick scan for missing logging and test opportunities. Most will need zero or minimal changes. Includes data containers up to ~250 lines that have no complex logic.

Files: Recording.cs (251), GhostTypes.cs (218), EnvironmentDetector.cs (190), GhostingTriggerClassifier.cs (181), CrashCoalescer.cs (163), SpawnWarningUI.cs (160), SegmentBoundaryLogic.cs (156), GhostSoftCapManager.cs (150), GhostMapPresence.cs (130), AntennaSpec.cs (127), AnchorDetector.cs (116), TerrainCorrector.cs (104), RenderingZoneManager.cs (101), GhostPlaybackState.cs (73), TrackSection.cs (73), GhostChain.cs (50), ProximityRateSelector.cs (42), SegmentEvent.cs (30), FlagEvent.cs (22), ControllerInfo.cs (21)

**After scan:** `dotnet build` + `dotnet test`. Commit once (1 commit).

---

## Quality Gates

| Gate | When | Criteria |
|------|------|----------|
| `dotnet build` | After every file (Tier 1) or batch (Tier 2-3) | Zero errors, zero new warnings from our code |
| `dotnet test` | After every build gate | 3227+ tests pass, 0 failures. Target: ~3350-3400 after all extractions. |
| Orchestrator diff review | Every file change | Verify no logic changes, logging is correct, tests are meaningful |
| Merge-check | After parallel batches | No file-level conflicts between parallel agents |
| Supervisor build verify | Before every commit | Orchestrator runs `dotnet build` independently |

### Build Commands (relative to worktree root)

```bash
cd Source/Parsek && dotnet build
cd Source/Parsek.Tests && dotnet test
```

---

## Pass 2 — Architecture Analysis (read-only)

**Goal:** Systematic analysis of the full 52k-line codebase BEFORE any cross-file restructuring. **No code changes.** 41 files needing audit and ~48,675 net new lines have been added since R1 — the dependency graph, static state landscape, and file responsibilities have shifted.

**Starts only after Pass 1 is complete.**

### Deliverables

1. **Dependency graph** — which files/classes depend on which others. Focus on new cross-cutting dependencies introduced since R1 (e.g., BackgroundRecorder→FlightRecorder static method coupling, ParsekKSC→ParsekFlight constant/logic duplication)
2. **Static mutable state inventory** — every static field that gets mutated, and which files mutate it. New files like VesselGhoster, TimeJumpManager, GhostSoftCapManager all have static state.
3. **Cross-reference map** — for every `internal static` and `public` method in files >500 lines, list all call sites in both production code and test code. Critical for Pass 3 — every moved method requires updating all call sites.
4. **Nested type inventory** — all classes/structs/enums defined inside other classes. Known candidates: `PendingDestruction`/`GhostPosMode`/`GhostPosEntry`/`ZoneRenderingResult` in ParsekFlight, `GhostedVesselInfo` in VesselGhoster, `BackgroundVesselState`/`BackgroundOnRailsState` in BackgroundRecorder, `PartTrackingSets` in PartStateSeeder, `VesselSwitchDecision` in FlightRecorder
5. **Duplication analysis** — identify duplicated logic across files:
   - BackgroundRecorder's 50-field `BackgroundVesselState` mirrors FlightRecorder's tracking state 1:1
   - ParsekKSC's `SpawnKscGhost` duplicates ParsekFlight's ghost spawn setup
   - GhostPlaybackLogic's `SetEngineEmission`/`SetRcsEmission` are ~80% identical
   - ParsekKSC duplicates constants from ParsekFlight (DefaultLoopIntervalSeconds, MinLoopDurationSeconds, etc.)
   - `CommitBoundarySplit`/`CommitDockUndockSegment`/`CommitChainSegment`/`HandleVesselSwitchChainTermination` share stash-tag-commit-advance pattern
   - RecordingStore POINT serialization duplicated 4x (2 ser + 2 deser)
6. **Concrete split recommendations** with dependency-aware ordering

**Output:** Analysis document presented to user for approval before Pass 3 begins.

---

## Pass 3 — SOLID Restructuring (cross-file changes)

**Goal:** Split bloated files into focused single-responsibility classes. Extract shared helpers. One class per file.

**Starts only after Pass 2 analysis is approved by user.**

### Phase 3A — Zero-risk type extractions (no method moves)

- Extract nested types from ParsekFlight (`PendingDestruction`, `GhostPosMode`, `GhostPosEntry`, `ZoneRenderingResult`) → individual files or shared types file
- Extract `GhostedVesselInfo` from VesselGhoster → own file
- Extract `BackgroundVesselState`/`BackgroundOnRailsState` from BackgroundRecorder → own file
- Extract `PartTrackingSets` from PartStateSeeder → own file (or merge tracking state with BackgroundVesselState if analysis recommends)
- Extract `MaterialCleanup` MonoBehaviour from GhostVisualBuilder → own file
- Update all references
- `dotnet build` + `dotnet test` + commit

### Phase 3B — Logical class splits (informed by Pass 2 analysis)

Likely candidates (to be confirmed by Pass 2):

- **ParsekFlight.cs (9,876 lines)** — tree management (CreateSplitBranch, CommitTreeFlight, FinalizeTreeRecordings, PromoteToTreeForBreakup) could become `TreeRecordingManager`. Chain management (EvaluateAndApplyGhostChains, PositionChainGhosts, SpawnVesselOrChainTip) could become `GhostChainController`. Crash/breakup handling could go with tree management.
- **GhostVisualBuilder.cs (7,642 lines)** — animation sampling infrastructure (6 SampleXxxStates methods + caches) could become `AnimationSampler`. Reentry FX (fire envelope, fire shell) could become `ReentryFxBuilder`.
- **FlightRecorder.cs (4,956 lines)** — the Part Event Subscription region (3,075 lines) is a strong candidate for `PartEventPoller` class, mirroring the R1 recommendation.
- **BackgroundRecorder.cs (2,759 lines)** — if Part Event Subscription is extracted from FlightRecorder into `PartEventPoller`, BackgroundRecorder's 17 Check*State methods could share that same infrastructure instead of duplicating it.
- **ParsekScenario.cs (2,693 lines)** — group hierarchy management (IsInAncestorChain, SetGroupParent, GetDescendantGroups, etc.) could become `GroupHierarchy`.
- **Shared FX emission helper** — unify GhostPlaybackLogic's `SetEngineEmission`/`SetRcsEmission` into a common `SetFxEmission` method.
- **Shared commit helper** — unify the stash-tag-commit-advance pattern across ParsekFlight's 4 commit methods.

Each split:
1. Present the specific move plan to user
2. Execute the move
3. Update ALL call sites (production + tests)
4. `cd Source/Parsek && dotnet build` + `cd Source/Parsek.Tests && dotnet test`
5. Commit

### Phase 3C — Final cleanup

- Verify one class per file everywhere
- Verify namespace consistency
- Final `dotnet build` + `dotnet test`
- Update inventory doc — mark all files `Pass3-Done`

---

## Rollback Strategy

- Each tier/file gets its own commit — `git revert` targets a single unit of work
- If tests break mid-tier and root cause is unclear: revert the last file's changes, investigate, re-attempt with tighter constraints
- The worktree is disposable — worst case, `git worktree remove` and start fresh from main

---

## Commit Strategy

| Phase | Granularity | Est. Commits |
|-------|-------------|--------------|
| Pass 1 Tier 1 | 1 per file | 8 |
| Pass 1 Tier 2 | 1 per batch | 3 |
| Pass 1 Tier 3 | 1 | 1 |
| Pass 3 Phase A | 1 (type extractions) | 1 |
| Pass 3 Phase B | 1 per split group | ~5-6 |
| Pass 3 Phase C | 1 (cleanup) | 1 |
| **Total** | | **~19-20** |

No `Co-Authored-By` lines (per CLAUDE.md).
