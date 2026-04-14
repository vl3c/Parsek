# Parsek Code Refactor-2 Inventory

**Total: 78 source files** (excluding obj/ generated files)
**Files needing audit: 41** (Tier 1: 8, Tier 2: 13, Tier 3: 20)
**Baseline tests: 3420 pass** (3311 existing + 109 T25 engine tests)

**T25 extraction:** 5 new files (GhostPlaybackEngine, ParsekPlaybackPolicy, IPlaybackTrajectory, IGhostPositioner, GhostPlaybackEvents).
**T26 extraction:** ChainSegmentManager (686 lines) extracted from ParsekFlight.
**Post-refactor extractions:** GroupHierarchyStore, ResourceApplicator, CrewReservationManager from ParsekScenario.

See [code-refactor-2-plan.md](code-refactor-2-plan.md) for the full refactoring plan.

---

## Status Tracking

| File | Lines | Tier | Status | Notes |
|------|-------|------|--------|-------|
| ParsekFlight.cs | 8,098 | 1 | T26-Done | 17 extractions, 25 log calls. T25: GhostPlaybackEngine extraction. T26: ChainSegmentManager extraction (-620 lines). Pass3: SanitizeQuaternion wrapper removed |
| GhostPlaybackEngine.cs | 1,594 | — | T25-New | Ghost playback mechanics engine. Zero Recording refs. D5: ApplyFrameVisuals. D8: RenderInRangeGhost + HandlePastEndGhost. T5: ReduceFidelity + SimplifyToOrbitLine. |
| ChainSegmentManager.cs | 686 | — | T26-New | Chain segment state + commit methods. CommitSegmentCore shared pattern. 16 fields + 14 methods. |
| ParsekPlaybackPolicy.cs | 192 | — | T25-New | Event subscriber: spawn, resources, camera policy |
| IPlaybackTrajectory.cs | 48 | — | T25-New | 19-property interface boundary for trajectory data |
| IGhostPositioner.cs | 52 | — | T25-New | 7-method positioning interface (implemented by ParsekFlight) |
| GhostPlaybackEvents.cs | 169 | — | T25-New | Event types, TrajectoryPlaybackFlags, FrameContext |
| GhostVisualBuilder.cs | 6,625 | 1 | Pass3-Done | 10 extractions, 9 log calls. AddPartVisuals 802→454. Pass3: EngineFxBuilder + MaterialCleanup split out |
| FlightRecorder.cs | 4,921 | 1 | Pass1-Done | 6 extractions, 3 log calls. FinalizeRecordingState triple-dedup, CreateOrbitSegmentFromVessel ×4 |
| ParsekUI.cs | 3,557 | 1 | D19-Done | BuildGroupTreeData, DrawGhostCapSlider dedup, T30: HandleResizeDrag/DrawResizeHandle. D19: DrawSortableHeaderCore<TCol>. T33: accessor migration. |
| BackgroundRecorder.cs | 2,754 | 1 | Pass1-Done | 4 extractions. BuildPartTrackingSetsFromState dedup, CreateOrbitSegmentFromVessel ×3 |
| RecordingStore.cs | 2,533 | 1 | Pass1-Done | 6 extractions (-140 lines). POINT/ORBIT ser/deser dedup ×4, PreProcessRewindSave delegation |
| ParsekScenario.cs | 2,726 | 1 | Pass1-Done | 5 extractions. OnLoad split: HandleRewindOnLoad, DiscardStalePendingState, LoadRecordingTrees |
| GhostPlaybackLogic.cs | 2,274 | 1 | T5-Done | BuildDictByPid generic helper, 7 logging additions. Pass3: shared methods from ParsekKSC. T5: ReduceGhostFidelity/RestoreGhostFidelity. T10: RealVesselExists HashSet cache. |
| VesselSpawner.cs | 1,031 | 2A | Pass1-Done | 3 extractions (ResolveSpawnPosition, FindNearestVesselDistance, LogSpawnFailure), 1 log |
| RecordingTree.cs | 953 | 2A | Pass1-Done | No changes needed — already well-structured |
| ParsekKSC.cs | 784 | 2A | Pass3-Done | PopulateGhostInfoDictionaries extracted from SpawnKscGhost. Pass3: shared methods moved to TrajectoryMath/GhostPlaybackLogic |
| MergeDialog.cs | 862 | 2A | Pass1-Done | No changes needed — helpers already extracted |
| PartStateSeeder.cs | 665 | 2B | Pass1-Done | EmitFromUintSet local helper (-60 lines), EmitHeat/Engine/RcsSeedEvents extracted |
| VesselGhoster.cs | 709 | 2B | Pass1-Done | ResolveSpawnPosition, TryWalkbackSpawn extracted |
| GhostChainWalker.cs | 687 | 2B | Pass1-Done | **Logging gaps FIXED**: IsIntermediateChainLink, WalkToLeaf, TraceLineagePids, ResolveTermination. MergeCrossTreeLinks extracted |
| TimeJumpManager.cs | 613 | 2B | Pass1-Done | CaptureOrbitalStates, ApplyEpochShifts, SpawnCrossedChainTips, PutLoadedVesselsOnRails, TakeVesselsOffRails |
| SessionMerger.cs | 476 | 2C | Pass1-Done | LogMergeDiagnostics extracted from MergeTree |
| GhostCommNetRelay.cs | 389 | 2C | Pass1-Done | No changes needed — well-structured |
| SpawnCollisionDetector.cs | 378 | 2C | Pass1-Done | ParsePartPositions extracted (internal static). Entry-point logging added |
| GhostExtender.cs | 266 | 2C | Pass1-Done | ComputeOrbitalPosition + CartesianToGeodetic extracted. PropagateOrbital 83→15 |
| SelectiveSpawnUI.cs | 201 | 2C | Pass1-Done | No changes needed |
| Recording.cs | 251 | 3 | Pass1-Done | No changes needed — scanned, already well-logged |
| GhostTypes.cs | 218 | 3 | Pass1-Done | No changes needed — pure data types |
| EnvironmentDetector.cs | 190 | 3 | Pass1-Done | No changes needed — already well-logged |
| GhostingTriggerClassifier.cs | 181 | 3 | Pass1-Done | No changes needed — all paths logged |
| CrashCoalescer.cs | 163 | 3 | Pass1-Done | No changes needed |
| SpawnWarningUI.cs | 160 | 3 | Pass1-Done | No changes needed |
| SegmentBoundaryLogic.cs | 156 | 3 | Pass1-Done | No changes needed |
| GhostSoftCapManager.cs | 150 | 3 | Pass1-Done | No changes needed |
| GhostMapPresence.cs | 130 | 3 | Pass1-Done | No changes needed |
| AntennaSpec.cs | 127 | 3 | Pass1-Done | No changes needed |
| AnchorDetector.cs | 116 | 3 | Pass1-Done | No changes needed |
| TerrainCorrector.cs | 104 | 3 | Pass1-Done | No changes needed |
| RenderingZoneManager.cs | 101 | 3 | Pass1-Done | No changes needed |
| GhostPlaybackState.cs | 75 | 3 | T5-Done | T5: added fidelityReduced + simplified flags |
| TrackSection.cs | 73 | 3 | Pass1-Done | No changes needed |
| GhostChain.cs | 53 | 3 | T9-Done | T9: added CachedTrajectoryIndex field |
| ProximityRateSelector.cs | 42 | 3 | Pass1-Done | No changes needed |
| SegmentEvent.cs | 30 | 3 | Pass1-Done | No changes needed |
| FlagEvent.cs | 22 | 3 | Pass1-Done | No changes needed |
| ControllerInfo.cs | 21 | 3 | Pass1-Done | No changes needed |
| TrajectoryMath.cs | 671 | — | Pass3-Done | Refactored in R1. Pass3: received InterpolatePoints from ParsekFlight/ParsekKSC |
| ActionReplay.cs | 504 | — | Skip | Refactored in R1, no changes since |
| MilestoneStore.cs | 474 | — | Skip | Refactored in R1, no changes since |
| ResourceBudget.cs | 407 | — | Skip | Refactored in R1, no changes since |
| GameStateRecorder.cs | 757 | — | Skip | Refactored in R1, no changes since |
| GameStateStore.cs | 709 | — | Skip | Refactored in R1, no changes since |
| GameStateEvent.cs | 302 | — | Skip | Refactored in R1, no changes since |
| GameStateBaseline.cs | 274 | — | Skip | Refactored in R1, no changes since |
| RecordingPaths.cs | 166 | — | Skip | Refactored in R1, no changes since |
| ParsekLog.cs | 169 | — | Skip | Refactored in R1, minor thread-static fix |
| Milestone.cs | 75 | — | Skip | Refactored in R1, pure data class |
| ParsekSettings.cs | 64 | — | Skip | Refactored in R1, no changes since |
| ParsekHarmony.cs | 54 | — | Skip | Refactored in R1, no changes since |
| ParsekToolbarRegistration.cs | 32 | — | Skip | Refactored in R1, no changes since |
| CommittedActionDialog.cs | 36 | — | Skip | Refactored in R1, no changes since |
| EngineFxBuilder.cs | 988 | — | Pass3-New | Extracted from GhostVisualBuilder (TryBuildEngineFX + helpers) |
| MaterialCleanup.cs | 18 | — | Pass3-New | Extracted from GhostVisualBuilder (MonoBehaviour for material cleanup) |
| PartEvent.cs | 61 | — | Skip | Pure data type, no changes since R1 |
| OrbitSegment.cs | 51 | — | Skip | Pure data type, no changes since R1 |
| SurfacePosition.cs | 65 | — | Skip | Pure data type, no changes since R1 |
| TerminalState.cs | 14 | — | Skip | Pure enum, no changes since R1 |
| BranchPoint.cs | 59 | — | Skip | Pure data type, no changes since R1 |
| TrajectoryPoint.cs | 30 | — | Skip | Pure data type, no changes since R1 |
| Patches/PhysicsFramePatch.cs | 72 | — | Skip | Refactored in R1, no changes since |
| Patches/FlightResultsPatch.cs | 103 | — | Skip | Refactored in R1, no changes since |
| Patches/FacilityUpgradePatch.cs | 62 | — | Skip | Refactored in R1, no changes since |
| Patches/TechResearchPatch.cs | 61 | — | Skip | Refactored in R1, no changes since |
| Patches/ScienceSubjectPatch.cs | 50 | — | Skip | Refactored in R1, no changes since |
| Properties/AssemblyInfo.cs | 23 | — | Skip | Auto-generated |

**Status values:** `Pending` | `Pass1-InProgress` | `Pass1-Done` | `Pass3-Done` | `Pass3-New` | `Skip`

---

## Detailed File Inventory

### Tier 1 — Critical (>2000 lines, heavily modified since R1)

---

#### `ParsekFlight.cs` -- 8,098 lines (was 9,899 pre-T25, 8,657 post-T25, now 8,098 post-T26)
**Types:**
- `ParsekFlight` (public class, MonoBehaviour) — main flight-scene controller
- `GhostPosMode` (private enum) — PointInterp, SinglePoint, Orbit, Surface, Relative
- `GhostPosEntry` (private struct) — floating-origin correction data per ghost
- `PendingDestruction` (internal struct) — vessel state captured before destruction
- `ZoneRenderingResult` (private struct) — zone rendering output

**Methods:** public: ~15 + ~44 properties, internal: ~45, private: ~133
**Regions:** State, Public Accessors, Unity Lifecycle, Scene Change Handling, Split Event Detection, Terminal Event Detection, Scene Change Helpers, Flight Ready Helpers, Update Helpers, Input Handling, Recording, Manual Playback, Timeline Auto-Playback, Camera Follow, Zone Rendering, Ghost Positioning, Utilities
**Dependencies:** FlightRecorder, GhostVisualBuilder, ParsekUI, RecordingStore, ParsekScenario, VesselSpawner, MergeDialog, GhostPlaybackLogic, TrajectoryMath, RecordingTree, BackgroundRecorder, CrashCoalescer, SegmentBoundaryLogic, GhostChainWalker, VesselGhoster, GhostSoftCapManager, ResourceBudget, AnchorDetector, TerrainCorrector, SelectiveSpawnUI, EnvironmentDetector, GameStateRecorder, MilestoneStore, ParsekSettings

**New since refactor-1 (+1,651 lines):**
- Tree branching/merging: `CreateSplitBranch` (~170), `CreateMergeBranch` (~100), `BuildSplitBranchData`/`BuildMergeBranchData` (pure static)
- Crash coalescer: `TickCrashCoalescer`, `ProcessBreakupEvent` (~150), `PromoteToTreeForBreakup` (~265)
- Terminal event detection: `ShouldDeferDestructionCheck`, `IsTrulyDestroyed`, `ApplyTerminalDestruction`, etc. (pure static)
- Background recorder orchestration: `HandleTreeBackgroundFlush`, `HandleTreeDockMerge`, `FlushRecorderToTreeRecording`
- Tree commit paths: `CommitTreeFlight` (~115), `FinalizeTreeRecordings` (~115), `SpawnTreeLeaves`
- Ghost chain: `EvaluateAndApplyGhostChains` (~65), `PositionChainGhosts` (~100), `SpawnVesselOrChainTip` (~65)
- Flag/inventory events, debris persistence enforcement, SOI change splitting

**Extraction candidates:**
- `PromoteToTreeForBreakup` (~266 lines, lines 2470-2735) — 13 numbered steps. **Top priority.**
- `OnSceneChangeRequested` (~205 lines) — 4 logical phases
- `CreateSplitBranch` (~170 lines) — tree-or-create, snapshot, branch wiring
- `DeferredJointBreakCheck` (~165 lines) — vessel scanning, classification, coalescer
- `CommitTreeFlight` (~115 lines) — stop, finalize, mark, reserve, commit, spawn, swap
- `FinalizeTreeRecordings` (~115 lines) — 3 numbered phases
- Commit pattern dedup: `CommitBoundarySplit`, `CommitDockUndockSegment`, `CommitChainSegment`, `HandleVesselSwitchChainTermination` all share stash-tag-commit-advance pattern

**Notes:**
- 8 coroutines (IEnumerator) — ALL off-limits for extraction
- ~60+ private instance fields, heavy mutable state
- Several pure `internal static` methods already extracted for testability

---

#### `GhostVisualBuilder.cs` -- 6,625 lines (was 6,395 at refactor-1, was 7,642 pre-Pass3)
**Types:**
- `GhostVisualBuilder` (internal static class)
- `MaterialCleanup` (private class, MonoBehaviour, nested line 7182)

**Methods:** public: 1, internal: ~73, private: ~103
**Regions:** Extracted helpers for TryBuildEngineFX, Extracted helpers for AddPartVisuals
**Dependencies:** Recording, RecordingStore, ParsekLog, PartLoader, GameDatabase, UnityEngine

**New since refactor-1 (+1,247 lines):**
- Color changer ghost support, robotic ghost info
- Animate heat visual support with cache infrastructure
- Ladder, cargo bay, aero surface caches
- Reentry fire FX enhancements (fire envelope, fire shell overlay)
- FX prefab fallback improvements with self-healing cache
- 7+ new `TryGetXxxAnimation` helper methods
- Multiple new `SampleXxxStates` methods (~100-150 lines each): cargo bay, ladder, animate heat, aero surface

**Extraction candidates:**
- Individual `SampleXxxStates` methods (~150 lines each) — same pattern: find animation, clone model, sample t=0/t=1, compute deltas, cache. Could share generic animation-sampling framework.
- FX prefab resolution chain (~170 lines across 5 methods)
- `BuildTimelineGhostFromSnapshot` (~150 lines) — split part iteration from result assembly

**Notes:**
- Entirely static class, all methods static
- Heavy static caches: fxPrefabCache, 6 animation caches
- No coroutines

---

#### `FlightRecorder.cs` -- 4,956 lines (was 4,107 at refactor-1)
**Types:**
- `FlightRecorder` (public class)
- `VesselSwitchDecision` (internal enum, 8 values)

**Methods:** public: ~50, internal: ~58, private: ~118
**Regions:** Part Event Subscription (3,075 lines!), Atmosphere Boundary Detection, Environment Tracking, Anchor detection helpers
**Dependencies:** ParsekLog, RecordingStore, TrajectoryMath, VesselSpawner, ParsekSettings, EnvironmentDetector, AnchorDetector, ResourceBudget, MilestoneStore, GameStateRecorder, RecordingPaths, RecordingTree

**New since refactor-1 (+849 lines):**
- Environment tracking (v6+): hysteresis-based TrackSection management
- Anchor detection (Phase 3a): RELATIVE frame detection, dx/dy/dz offsets from anchor
- Tree mode vessel switch decisions: TransitionToBackground, PromoteFromBackground
- Rewind save capture: `CaptureRewindSave` (~50)
- 6 new part event tracking systems: animate heat, ladder, animation groups, aero/control surfaces, robot arm scanner, standalone animate generic
- `LogVisualRecordingCoverage` (~200 lines) — comprehensive diagnostic dump

**Extraction candidates:**
- `StartRecording` (~165 lines) — 8 logical initialization steps
- `OnPhysicsFrame` (~212 lines, lines 4279-4490) — 18 Check* calls + environment hysteresis (4313-4327) + anchor/RELATIVE logic (4329-4472) + adaptive sampling (4402-4489)
- `LogVisualRecordingCoverage` (~200 lines) — pure diagnostic, could be helper class
- `ResetPartEventTrackingState` (~90 lines) — 30+ .Clear() calls
- `HandleVesselSwitchDuringRecording` (~85 lines) — 7-branch dispatch

**Notes:**
- No coroutines
- ~30+ HashSets/Dictionaries for part event tracking
- Many `internal static` pure methods already extracted
- Part Event Subscription region is 3,075 lines — majority of file

---

#### `ParsekUI.cs` -- 3,600 lines (was 2,923 at refactor-1)
**Types:**
- `ParsekUI` (public class)
- `UIMode` (public enum)
- `ActionsSortColumn` (private enum)
- `SortColumn` (internal enum)
- `SpawnSortColumn` (private enum)

**Methods:** public: ~14, internal: ~16, private: ~186
**Regions:** Settings sections
**Dependencies:** ParsekFlight, RecordingStore, ParsekScenario, ParsekSettings, MilestoneStore, GameStateStore, ResourceBudget, GhostPlaybackLogic, SelectiveSpawnUI, MergeDialog, VesselSpawner, ClickThroughFix

**New since refactor-1 (+672 lines):**
- Game Actions window: `DrawActionsWindow` (~155), sortable event table
- Spawn Control window: `DrawSpawnControlWindow` (~120), proximity candidates
- Group picker popup: hierarchical group assignment (~85+75+55+50 lines)
- Recording rename/loop period editing
- Settings sections split into 6 sub-methods
- Expanded stats columns, wipe/FF confirmations

**Extraction candidates:**
- `DrawRecordingsWindow` (~304 lines, lines 935-1238) — header, tree dispatch, footer, wipe
- `DrawRecordingRow` (~205 lines) — 12+ columns per row
- `DrawGroupTree` (~221 lines) — recursive group rendering
- `DrawActionsWindow` (~155 lines) — header, sorted events, per-event rows
- `DrawSpawnControlWindow` (~116 lines) — header, sorted candidates, per-candidate rows
- `DrawLoopPeriodCell` (~100 lines) — inline editable loop period

**Notes:**
- No coroutines, no static mutable state
- ~50+ instance fields for window/sort/popup state
- Window resize handling duplicated across 4 windows
- Many `internal static` utility methods already extracted

---

#### `BackgroundRecorder.cs` -- 2,759 lines (was 1,524 at refactor-1)
**Types:**
- `BackgroundRecorder` (internal class)
- `BackgroundOnRailsState` (private nested class)
- `BackgroundVesselState` (private nested class, ~50 tracking fields)

**Methods:** public: 10, internal: 12 + 18 testing accessors, private: 21, internal static: 4
**Regions:** Inner Classes, GameEvent Subscriptions, Background Vessel Split Detection, Public API, Internal State Management, Background Environment Classification & TrackSection Management, Part Event Polling, Jettison Name Cache, Testing Support
**Dependencies:** RecordingTree, FlightRecorder (static transition methods), ParsekFlight.IsTrackableVessel, VesselSpawner, ProximityRateSelector, EnvironmentDetector, PartStateSeeder, TrajectoryMath, ParsekSettings, ParsekLog

**New since refactor-1 (+1,235 lines, 81% growth!):**
- Proximity-based sampling with ProximityRateSelector
- TrackSection management (environment classification, section lifecycle)
- Full part event polling: 17 Check*State methods mirroring FlightRecorder
- Seed events via PartStateSeeder integration
- Debris TTL management
- Background split detection: `HandleBackgroundVesselSplit` (~177)
- Orbital checkpointing
- BackgroundVesselState expanded to ~50 fields

**Extraction candidates:**
- `HandleBackgroundVesselSplit` (~178 lines, lines 371-548) — 7 phases: validation, vessel discovery, branch data, parent closure, parent continuation, child creation, summary log. **Top priority.**
- `InitializeLoadedState` (~73 lines) — cache, seed, emit, init environment
- `InitializeOnRailsState` (~81 lines) — 3-branch (landed/orbiting/no-orbit)
- `CheckAnimateGenericState` (~76 lines) — duplicate-module exclusion block candidate
- 17 Check*State methods (736 lines total) follow identical pattern but vary in module/key/value types — do NOT unify into a generic helper (same ruling as R1 FlightRecorder). Extract sub-steps within individual methods only if >30 lines

**Notes:**
- No coroutines (safe for extraction)
- No static mutable state (all instance, owned by ParsekFlight)
- BackgroundVesselState mirrors FlightRecorder's fields 1:1 — heavy duplication

---

#### `RecordingStore.cs` -- 2,673 lines (was 1,995 at refactor-1)
**Types:**
- `RecordingStore` (public static class)

**Methods:** public: 14, internal: 25, private: 8, internal static: ~30
**Regions:** Rewind, Trajectory Serialization, Recording File I/O
**Dependencies:** RecordingPaths, Recording, RecordingTree, ParsekScenario, ResourceBudget, GameStateStore, GameStateRecorder, MilestoneStore, SessionMerger, ParsekLog, TrajectoryMath, GhostChainWalker

**New since refactor-1 (+678 lines):**
- Group management (~243 lines): CRUD for recording group tags + chain group ops
- Chain query helpers (~276 lines): IsChainMidSegment, GetChainRecordings, ValidateChains, etc.
- Rewind PID-based stripping: PreProcessRewindSave extended overload
- Orphan file cleanup: `CleanOrphanFiles` (~84 lines)
- Playback state management
- TrackSection serialization (~304 lines): full round-trip for nested POINT/ORBIT_SEGMENT in TRACK_SECTION

**Extraction candidates:**
- **POINT duplication (4 blocks, verified identical for serialization):**
  - Serialize: `SerializeTrajectoryInto` (lines 1730-1749, 20 lines) and `SerializeTrackSections` inner block (lines 2131-2150, 20 lines) — **field-for-field identical**
  - Deserialize: `DeserializePoints` (lines 1836-1877, 42 lines) and `DeserializeTrackSections` inner block (lines 2296-2332, 37 lines) — **nearly identical** but `DeserializePoints` has `parseFailCount` tracking that the TrackSection version lacks
- `DeserializeTrackSections` (~187 lines total) — heavy deserialization with nested POINT/ORBIT_SEGMENT parsing
- `InitiateRewind` (~139 lines) — file copy + preprocess extractable
- `PreProcessRewindSave` 2-arg and 3-arg overloads (~60% shared body) — unify
- `CommitTree` (~89 lines) — 5 distinct steps
- `ValidateChains` (~98 lines) — 3 phases
- POINT serialization duplicated 4x (2 ser + 2 deser) — strong candidate for shared `SerializePoint`/`DeserializePoint`

**Notes:**
- MASSIVE static mutable state: pendingRecording, committedRecordings, committedTrees, pendingTree, rewind state, cleanup state
- No coroutines
- POINT dedup is the single highest-value extraction in this file

---

#### `ParsekScenario.cs` -- 2,693 lines (was 2,297 at refactor-1)
**Types:**
- `ParsekScenario` (public class, KSPScenario)

**Methods:** public: 9, internal: 7, private: 13, internal static: 9
**Regions:** Game State Recording, Crew Replacements, Deferred Merge Dialog, Budget Deduction, Resource Ticking, Crew Reservation, Vessel Lifecycle Events
**Dependencies:** RecordingStore, Recording, RecordingTree, ResourceBudget, GameStateStore, GameStateRecorder, MilestoneStore, ActionReplay, ParsekSettings, MergeDialog, ParsekLog, RecordingPaths, GhostPlaybackLogic

**New since refactor-1 (+396 lines):**
- Group hierarchy (~149 lines): parent-child with cycle detection, rename, descendant collection
- Hidden groups, group hierarchy persistence
- Vessel lifecycle events (~144 lines): OnVesselRecovered, OnVesselTerminated, UpdateRecordingsForTerminalEvent
- Antenna spec persistence, terrain height persistence

**Extraction candidates:**
- `OnLoad` (~586 lines!) — **Top priority.** Extract HandleRewindOnLoad (~75), HandleRevertOnLoad (~175), HandleInitialLoad (~165)
- `LoadStandaloneRecordingsFromNodes` (~145 lines) — 15 field reads per recording
- `SwapReservedCrewInFlight` (~104 lines) — part crew swap + EVA cleanup
- `RestoreStandaloneMutableState` (~63 lines)

**Notes:**
- 3 coroutines (IEnumerator) — off-limits: ShowDeferredMergeDialog, ApplyBudgetDeductionWhenReady, ApplyRewindResourceAdjustment
- Heavy static mutable state: crewReplacements, groupParents, hiddenGroups, etc.
- `OnLoad` at 586 lines is the single most complex method in the codebase

---

#### `GhostPlaybackLogic.cs` -- 2,289 lines (was ~1,414 at refactor-1 creation)
**Types:**
- `GhostPlaybackLogic` (internal static class)

**Methods:** public: 0, internal static: ~70, private static: ~8-9 (total ~78-79 methods, all static)
**Regions:** Warp/Loop Policy, External Vessel Ghost Policy, Ghost Info Population, Explosion/Visibility, Part Events, Canopy Management, Engine FX, RCS FX, Robotic, Heat/Reentry, Deployables/Jettison, Lights, Spawn-at-Recording-End Decision, Zone-Based Rendering
**Dependencies:** FlightRecorder, GhostVisualBuilder, RecordingStore, GhostPlaybackState, GhostBuildResult, GhostChainWalker, RenderingZoneManager, Recording, PartEvent, ParsekLog

**New since refactor-1 (+771 lines):**
- Part event application: `ApplyPartEvents` (~254 lines, 30+ case switch)
- Engine FX (~172 lines): SetEngineEmission, StopEngineFxForPart
- RCS FX (~142 lines): SetRcsEmission, StopRcsFxForPart (near-clone of engine FX)
- Robotic playback (~102 lines)
- Heat/Reentry (~110 lines)
- Lights (~153 lines): power, blink mode/rate, color changer
- Flag events (~58 lines)
- Spawn-at-end decision (~176 lines)
- Zone-based rendering (~82 lines)

**Extraction candidates:**
- `PopulateGhostInfoDictionaries` (~94 lines) — 10 repetitive list-to-dict blocks. Could use generic `BuildDictByPid<T>`
- `SetEngineEmission` (54 lines, 1215-1268) / `SetRcsEmission` (57 lines, 1347-1403) — only ~50-60% identical (shared particle on/off core, but different diagnostic tracking). Dedup may not be worthwhile — evaluate during Pass 1.
- `ApplyHeatState` (~77 lines) — 2 phases: transform animation + material animation

**Notes:**
- No coroutines, no significant mutable state (test overrides only)
- All ~78-79 methods are static — excellent testability
- `ApplyPartEvents` switch at 254 lines is well-structured, each case 5-20 lines — leave as-is

---

### Tier 2 — Large new/modified files (200-1000 lines)

---

#### `VesselSpawner.cs` -- 1,031 lines (was 978 at refactor-1)
**Types:**
- `VesselSpawner` (public static class)

**Methods:** public: 6, internal: 8, private: 7
**Regions:** Extracted helpers
**Dependencies:** FlightGlobals, HighLogic, ProtoVessel, Recording, RecordingStore, ParsekScenario, SpawnCollisionDetector, ParsekLog

**Extraction candidates:**
- `SpawnOrRecoverIfTooClose` (~143 lines) — crew protection, collision check, spawn retry
- `SnapshotVessel` (~74 lines) — destroyed vs alive paths
- `BuildExcludeCrewSet` (~68 lines) — chain-aware EVA crew exclusion
- `RemoveDeadCrewFromSnapshot` (~60 lines)
- Crew snapshot iteration duplicated across 4 methods

**Notes:** Minor growth (+53). Many pure decision methods already `internal static`. Good test coverage already.

---

#### `RecordingTree.cs` -- 953 lines (was 691 at refactor-1)
**Types:**
- `RecordingTree` (public class)

**Methods:** public: 4, internal: 5 static, private: 4 static
**Regions:** LoadRecording Extracted Helpers
**Dependencies:** Recording, BranchPoint, TerminalState, SurfacePosition, ControllerInfo, ParsekLog

**Extraction candidates:**
- `LoadRecordingResourceAndState` (~132 lines) — mechanical TryParse chains
- `LoadBranchPointFrom` (~90 lines) — mechanical ConfigNode parsing
- `AreAllLeavesTerminal` (~65 lines) — complex leaf classification

**Notes:** +262 lines from BranchPoint metadata, ghost geometry, AreAllLeavesTerminal. Extraction pattern established in R1.

---

#### `ParsekKSC.cs` -- 784 lines (was 852 at refactor-1, was 919 pre-Pass3)
**Types:**
- `ParsekKSC` (public class, MonoBehaviour, KSPAddon)

**Methods:** public: 0 (Unity lifecycle), internal: 3 static + 1 instance, private: 8 instance
**Regions:** Ghost Playback
**Dependencies:** GhostVisualBuilder, GhostPlaybackLogic, GhostPlaybackState, RecordingStore, TrajectoryMath, FlightRecorder, ParsekUI, ParsekFlight, ParsekSettings

**Extraction candidates:**
- `SpawnKscGhost` (~132 lines) — ghost build result assignment, nearly identical to ParsekFlight's ghost spawn
- `UpdateOverlapKsc` (~122 lines) — primary ghost + overlap ghost iteration
- `UpdateSingleGhostKsc` (~78 lines) — in-range vs out-of-range
- `Update` (~82 lines) — main loop with per-recording branching
- `StopParticleSystems`/`StopRcsParticleSystems` nearly identical — generic helper candidate

**Notes:** Minor growth (+67). Constants duplicated from ParsekFlight.

---

#### `MergeDialog.cs` -- 862 lines (was 532 at refactor-1)
**Types:**
- `MergeDialog` (public static class)

**Methods:** public: 1, internal: 9, private: 4
**Regions:** Extracted helpers
**Dependencies:** RecordingStore, ParsekScenario, Recording, RecordingTree, PopupDialog, MultiOptionDialog

**Extraction candidates:**
- `ShowStandaloneDialog` (~91 lines) — button construction + popup
- `ShowChainDialog` (~101 lines) — similar pattern
- `ShowMultiVesselTreeDialog` (~106 lines) — decisions + rows + popup
- Three dialog methods share structural pattern — potential shared builder

**Notes:** +330 lines from tree merge dialog and per-vessel decisions. Pure decision methods already `internal static`.

---

#### `PartStateSeeder.cs` -- 665 lines [NEW]
**Types:**
- `PartStateSeeder` (internal static class)
- `PartTrackingSets` (internal class, 20 public mutable fields)

**Methods:** public: 0, internal: 2, private: 12
**Dependencies:** FlightRecorder (~15 static methods/constants), ParsekLog

**Extraction candidates:**
- `EmitSeedEvents` (~278 lines, lines 413-690) — ~15 repetitive emit blocks. Uses local function `EmitDeployableFromUlongSet` to consolidate 6 blocks.

**Notes:** Heavy coupling to FlightRecorder. `EmitSeedEvents` is longest method — purely linear, no branching, but very repetitive.

---

#### `VesselGhoster.cs` -- 682 lines [NEW]
**Types:**
- `VesselGhoster` (internal class)
- `GhostedVesselInfo` (internal nested class)

**Methods:** public: 0, internal: 10 + 2 static, private: 4 static + 1 instance
**Dependencies:** FlightRecorder, RecordingStore, VesselSpawner, SpawnCollisionDetector, GhostExtender, TerrainCorrector, GhostChain, Recording, ParsekLog

**Extraction candidates:**
- `TrySpawnBlockedChain` (~149 lines) — propagation, recheck, walkback, terrain correction, spawn
- `SpawnAtChainTip` (~107 lines) — collision check, terrain correction, spawn, cleanup
- `ComputePropagatedPosition` (~67 lines) — switch over 4 strategies

**Notes:** Two long methods with deeply nested try/catch and collision retry logic. Pure decision methods already `internal static`.

---

#### `GhostChainWalker.cs` -- 592 lines [NEW]
**Types:**
- `GhostChainWalker` (internal static class)

**Methods:** public: 0, internal: 3, private: 9
**Regions:** Private Helpers
**Dependencies:** RecordingTree, Recording, BranchPoint, GhostChain, GhostingTriggerClassifier, ParsekLog

**Extraction candidates:**
- `ComputeAllGhostChains` (~64 lines) — 7 steps
- `ResolveTipsAndCrossTreeLinks` (~86 lines) — 2 passes
- `ScanBranchPointClaims` (~66 lines) — complex MERGE/SPLIT classification
- `WalkToLeaf` (~52 lines) — recursive with cycle protection

**Logging gaps (must fix):**
- `IsIntermediateChainLink` (35 lines) — makes decision with zero logging. Needs at least result + reason.
- `WalkToLeaf` (52 lines) — traversal with PID-matching decisions, zero logging. Needs per-step verbose.
- `TraceLineagePids` (30 lines) — recursive accumulation, no logging. Add entry verbose.
- `ResolveTermination` (18 lines) — sets IsTerminated silently. Add info log.

**Notes:** Entirely pure static. No Unity dependency. All methods operate on passed-in data. Recursive `TraceLineagePids` — check stack depth.

---

#### `TimeJumpManager.cs` -- 552 lines [NEW]
**Types:**
- `TimeJumpManager` (internal static class)

**Methods:** public: 0, internal: 6, private: 1
**Dependencies:** FlightGlobals, Planetarium, VesselGhoster, GhostChain, FlightRecorder, SegmentEvent, ParsekLog

**Extraction candidates:**
- `ExecuteJump` (~147 lines) — 4 steps (capture state, set UT, epoch-shift orbits, process spawn queue)
- `ExecuteForwardJump` (~104 lines) — 3 steps (on-rails, set UT + fix converters, off-rails)

**Notes:** Uses reflection for `BaseConverter.lastUpdateTime`. `ComputeEpochShiftedMeanAnomaly` is pure.

---

#### `SessionMerger.cs` -- 476 lines [NEW]
**Types:**
- `SessionMerger` (internal static class)

**Methods:** public: 0, internal: 3, private: 4
**Dependencies:** RecordingTree, Recording, TrackSection, PartEvent, TrajectoryPoint, OrbitSegment, SegmentEvent, ParsekLog

**Extraction candidates:**
- `MergeTree` (~111 lines) — per-recording loop with section resolution + event merge
- `ResolveOverlaps` (~170 lines) — O(n*m) sweep algorithm, "currentWins" vs "existingWins" branches

**Notes:** Entirely pure static. Hardcodes Kerbin radius (600,000m) in `ComputeBoundaryDiscontinuity`.

---

#### `GhostCommNetRelay.cs` -- 389 lines [NEW]
**Types:**
- `GhostCommNetRelay` (internal class)

**Methods:** public: 0, internal: 8 + 4 static, private: 1 static
**Dependencies:** CommNet, AssemblyLoader, AntennaSpec, ParsekLog

**Extraction candidates:**
- `ComputeCombinedAntennaPowerFromList` (~49 lines) — combinability formula

**Notes:** Instance class with activeGhostNodes dict. Static cached `remoteTechDetected`. Pure decision methods already `internal static`.

---

#### `SpawnCollisionDetector.cs` -- 378 lines [NEW]
**Types:**
- `SpawnCollisionDetector` (internal static class)

**Methods:** public: 0, internal: 7 static, private: 0
**Dependencies:** TrajectoryPoint, FlightGlobals, Vessel, ParsekLog

**Extraction candidates:**
- `CheckOverlapAgainstLoadedVessels` (~60 lines)
- `ComputeVesselBounds` (~43 lines)

**Notes:** Clean static. Clear separation between pure methods and KSP-runtime methods. Func delegates for testability.

---

#### `GhostExtender.cs` -- 266 lines [NEW]
**Types:**
- `GhostExtensionStrategy` (internal enum)
- `GhostExtender` (internal static class)

**Methods:** public: 0, internal: 4, private: 3
**Dependencies:** Recording, ParsekLog

**Extraction candidates:**
- `PropagateOrbital` (~83 lines) — Keplerian propagator, could split orbital→cartesian + cartesian→lat/lon/alt

**Notes:** Entirely pure static. Kepler solver with Newton-Raphson. All value-tuple returns.

---

#### `SelectiveSpawnUI.cs` -- 201 lines [NEW]
**Types:**
- `NearbySpawnCandidate` (internal struct)
- `SelectiveSpawnUI` (internal static class)

**Methods:** public: 0, internal: 7, private: 0
**Dependencies:** GameSettings
**Notes:** Clean pure static. SharedSB for allocation reduction. Likely no changes needed.

---

### Tier 3 — Small/data files (scan only)

---

#### `EnvironmentDetector.cs` -- 190 lines [NEW]
**Types:** `EnvironmentDetector` (internal static), `EnvironmentHysteresis` (internal class)
**Methods:** internal: 4
**Dependencies:** ParsekLog, SegmentEnvironment
**Notes:** Well-structured. Classify/GetDebounceFor are pure static. No changes needed.

#### `GhostingTriggerClassifier.cs` -- 181 lines [NEW]
**Types:** `GhostingTriggerClassifier` (internal static)
**Methods:** internal: 4
**Dependencies:** ParsekLog, PartEventType, BranchPointType, SegmentEventType, Recording
**Notes:** Pure static switch classifiers. Conservative defaults log unknowns. No changes needed.

#### `CrashCoalescer.cs` -- 163 lines [NEW]
**Types:** `CrashCoalescer` (internal class)
**Methods:** public: 5
**Dependencies:** ParsekLog, BranchPoint
**Notes:** Well-encapsulated stateful class. All paths logged. No changes needed.

#### `SpawnWarningUI.cs` -- 160 lines [NEW]
**Types:** `SpawnWarningUI` (internal static)
**Methods:** internal: 4
**Dependencies:** ParsekLog, GhostChain
**Notes:** Pure static text formatting. No changes needed.

#### `SegmentBoundaryLogic.cs` -- 156 lines [NEW]
**Types:** `JointBreakResult` (internal enum), `SegmentBoundaryLogic` (internal static)
**Methods:** internal: 3
**Dependencies:** ParsekLog, SegmentEvent
**Notes:** Pure static. Logging on all paths. No changes needed.

#### `GhostSoftCapManager.cs` -- 150 lines [NEW]
**Types:** `GhostCapAction` (public enum), `GhostPriority` (public enum), `GhostSoftCapManager` (internal static)
**Methods:** internal: 4
**Dependencies:** ParsekLog, Recording
**Notes:** Pure static threshold logic. No changes needed.

#### `GhostMapPresence.cs` -- 130 lines [NEW]
**Types:** `GhostMapPresence` (internal static)
**Methods:** internal: 2
**Dependencies:** ParsekLog, Recording, GhostChain
**Notes:** Pure data layer. No changes needed.

#### `AntennaSpec.cs` -- 127 lines [NEW]
**Types:** `AntennaSpec` (internal struct), `AntennaSpecExtractor` (internal static)
**Methods:** public: 1, internal: 1
**Dependencies:** ParsekLog, ConfigNode
**Notes:** Pure static extractor. No changes needed.

#### `AnchorDetector.cs` -- 116 lines [NEW]
**Types:** `AnchorDetector` (internal static)
**Methods:** internal: 3
**Dependencies:** ParsekLog
**Notes:** Pure static with hysteresis constants. No changes needed.

#### `TerrainCorrector.cs` -- 104 lines [NEW]
**Types:** `TerrainCorrector` (internal static)
**Methods:** internal: 3
**Dependencies:** ParsekLog, TerminalState
**Notes:** Pure static. Handles NaN and nullable. No changes needed.

#### `RenderingZoneManager.cs` -- 101 lines [NEW]
**Types:** `RenderingZone` (public enum), `RenderingZoneManager` (internal static)
**Methods:** internal: 6
**Dependencies:** ParsekLog
**Notes:** Pure static threshold logic. Logging-only helpers. No changes needed.

#### `GhostTypes.cs` -- 218 lines (modified since R1)
**Types:** 23 data types (classes, structs, enums)
**Methods:** none
**Notes:** Pure data-holder file. Added RoboticGhostInfo, ReentryFxInfo, CompoundPartData, etc. No changes needed.

#### `Recording.cs` -- 251 lines (modified since R1)
**Types:** MergeDefault (enum), LoopTimeUnit (enum), Recording (class)
**Methods:** public: 1, internal: 1 static
**Notes:** Large data class with ~50+ fields. No changes needed.

#### `GhostPlaybackState.cs` -- 73 lines (modified since R1)
**Types:** LightPlaybackState, GhostPlaybackState, InterpolationResult
**Notes:** Runtime state container. No changes needed.

#### `TrackSection.cs` -- 73 lines [NEW]
**Types:** TrackSectionSource, SegmentEnvironment, ReferenceFrame (enums), TrackSection (struct)
**Notes:** Pure data. No changes needed.

#### `GhostChain.cs` -- 50 lines [NEW]
**Types:** ChainLink (struct), GhostChain (class)
**Notes:** Pure data. No changes needed.

#### `ProximityRateSelector.cs` -- 42 lines [NEW]
**Types:** ProximityRateSelector (internal static)
**Notes:** Pure threshold lookup. No changes needed.

#### `SegmentEvent.cs` -- 30 lines [NEW]
**Types:** SegmentEventType (enum), SegmentEvent (struct)
**Notes:** Pure data. No changes needed.

#### `FlagEvent.cs` -- 22 lines [NEW]
**Types:** FlagEvent (struct)
**Notes:** Pure data. No changes needed.

#### `ControllerInfo.cs` -- 21 lines [NEW]
**Types:** ControllerInfo (struct)
**Notes:** Pure data. No changes needed.

---

## Summary Statistics

| Metric | Value |
|--------|-------|
| Total source files | 70 |
| Total source lines | 52,151 |
| Files needing refactoring | 21 (Tier 1: 8, Tier 2: 13) |
| Files needing scan only | 20 (Tier 3) |
| Files skipped (already clean) | 27 (15 named + 6 data types + 5 Patches + AssemblyInfo) |
| New files from Pass 3 splits | 2 (EngineFxBuilder.cs, MaterialCleanup.cs) |
| **Total accounted** | **70** |
| Pass 3 status | 6 files Pass3-Done, 2 files Pass3-New |
| Baseline test count | 3,261 |
| Test files | 133 |
| Test lines | 63,084 |
