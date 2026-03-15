# Parsek Code Refactor Inventory

**Total: 39 source files, 35,667 lines** (excluding obj/ generated files)

See [code-refactor-plan.md](../plans/code-refactor-plan.md) for the full refactoring plan.

---

## Status Tracking

| File | Lines | Tier | Status | Notes |
|------|-------|------|--------|-------|
| TrajectoryMath.cs | 548 | Canary | Pass1-Done | Extracted AccumulateOrbitSegmentStats, DeterminePrimaryBody; added logging to FindFirstMovingPoint + ComputeStats; 18 new tests |
| ParsekFlight.cs | 8,493 | 1 | Pass1-Done | 28 methods extracted across 4 groups; Update() 400→35 lines; deduped PopulateGhostInfoDictionaries; coroutines untouched |
| GhostVisualBuilder.cs | 6,268 | 1 | Pass1-Done | 11 methods extracted; deduped 7+2 copy patterns (-127 lines); 3 region markers added |
| FlightRecorder.cs | 4,103 | 1 | Pass1-Done | 4 methods extracted; deduped BuildCaptureRecording (3 copies); 46 new tests; OnPhysicsFrame 168→65 lines |
| ParsekScenario.cs | 2,297 | 2A | Pending | ScenarioModule, mixed concerns |
| RecordingStore.cs | 1,995 | 2A | Pending | Central data store, Recording class 45+ fields |
| ParsekUI.cs | 2,985 | 2B | Pass1-Done | 6 methods extracted; DrawResourceLine deduped 3 copies; 18 methods rejected (sequential IMGUI) |
| BackgroundRecorder.cs | 1,532 | 2B | Pass1-Done | 3 rate-limited logging additions; no extraction needed |
| VesselSpawner.cs | 978 | 3A | Pass1-Done | 2 methods extracted (DetermineSituation pure); 10 tests |
| ParsekKSC.cs | 852 | 3A | Pass1-Done | 2 particle cleanup helpers extracted (dedup) |
| MergeDialog.cs | 532 | 3A | Pass1-Done | 3 methods extracted (tree dialog stats); 18 tests |
| GameStateRecorder.cs | 757 | 3B | Pass1-Done | No changes needed — already well-structured |
| GameStateStore.cs | 694 | 3B | Pass1-Done | 1 method extracted (BuildEventTypeDistribution); 6 tests |
| RecordingTree.cs | 691 | 3B | Pass1-Done | 4 serialization helpers extracted; LoadRecordingFrom 226→75 lines |
| ActionReplay.cs | 504 | 3C | Pass1-Done | AccumulateReplayResult deduped 4 copies (-25 lines); 5 tests |
| MilestoneStore.cs | 474 | 3C | Pass1-Done | BuildStateMap extracted; 5 tests |
| ResourceBudget.cs | 408 | 3C | Pass1-Done | Logging additions only |
| GameStateEvent.cs | 302 | 4 | Pass1-Done | No changes needed |
| GameStateBaseline.cs | 274 | 4 | Pass1-Done | No changes needed |
| RecordingPaths.cs | 166 | 4 | Pass1-Done | No changes needed |
| ParsekLog.cs | 161 | 4 | Pass1-Done | No changes needed (logging infrastructure) |
| FlightResultsPatch.cs | 103 | 4 | Pass1-Done | No changes needed |
| Milestone.cs | 75 | 4 | Pass1-Done | No changes needed |
| PhysicsFramePatch.cs | 72 | 4 | Pass1-Done | No changes needed |
| SurfacePosition.cs | 65 | 4 | Pass1-Done | No changes needed |
| FacilityUpgradePatch.cs | 62 | 4 | Pass1-Done | No changes needed |
| TechResearchPatch.cs | 61 | 4 | Pass1-Done | No changes needed |
| ParsekSettings.cs | 58 | 4 | Pass1-Done | No changes needed |
| PartEvent.cs | 56 | 4 | Pass1-Done | No changes needed |
| ParsekHarmony.cs | 54 | 4 | Pass1-Done | No changes needed |
| OrbitSegment.cs | 51 | 4 | Pass1-Done | No changes needed |
| ScienceSubjectPatch.cs | 50 | 4 | Pass1-Done | No changes needed |
| CommittedActionDialog.cs | 36 | 4 | Pass1-Done | No changes needed |
| ParsekToolbarRegistration.cs | 32 | 4 | Pass1-Done | No changes needed |
| TrajectoryPoint.cs | 30 | 4 | Pass1-Done | No changes needed |
| BranchPoint.cs | 28 | 4 | Pass1-Done | No changes needed |
| TerminalState.cs | 14 | 4 | Pass1-Done | No changes needed |
| Properties/AssemblyInfo.cs | 23 | 4 | Skip | Auto-generated |

**Status values:** `Pending` | `Pass1-InProgress` | `Pass1-Done` | `Pass3-Done` | `Skip`

---

## Detailed File Inventory

### Root Source Files

#### `ActionReplay.cs` -- 529 lines
**Types:**
- `internal enum ReplayDecision` (Skip, Act, Fail)
- `internal static class ActionReplay`

**Methods:** public: 0, internal: 10, private: 0
**Regions:** Phase 2: Part Purchase, Phase 3: Facility Upgrade, Phase 4: Crew Hire, Utilities
**Dependencies:** ParsekLog, GameStateRecorder (suppression flags), GameStateEventType, MilestoneStore
**Notes:** All methods are `internal static` -- pure decision logic separated from KSP API calls for testability.

---

#### `BackgroundRecorder.cs` -- 1,524 lines
**Types:**
- `internal class BackgroundRecorder`
- `private class BackgroundVesselState` (nested in BackgroundRecorder)
- `private class BackgroundOnRailsState` (nested in BackgroundRecorder)

**Methods:** public: 63, internal: 8, private: 33
**Regions:** Inner Classes, Public API, Internal State Management, Part Event Polling, Jettison Name Cache, Testing Support
**Dependencies:** RecordingTree, RecordingStore.Recording, FlightRecorder (mirrors sampling constants), TrajectoryPoint, OrbitSegment, PartEvent, ParsekLog
**Notes:** Not a MonoBehaviour -- owned by ParsekFlight. Manages continuous recording for background vessels in a recording tree. Contains substantial part event polling logic mirroring FlightRecorder. Has its own jettison name cache (parallel to FlightRecorder's).

---

#### `BranchPoint.cs` -- 28 lines
**Types:**
- `public enum BranchPointType` (Undock, EVA, Dock, Board, JointBreak)
- `public class BranchPoint`

**Methods:** public: 1 (ToString), internal: 0, private: 0
**Regions:** none
**Dependencies:** none (standalone data type)
**Notes:** Pure data class with no logic.

---

#### `CommittedActionDialog.cs` -- 36 lines
**Types:**
- `internal static class CommittedActionDialog`

**Methods:** public: 0, internal: 1 (ShowBlocked), private: 0
**Regions:** none
**Dependencies:** ParsekLog, PopupDialog, MultiOptionDialog (Unity/KSP UI)
**Notes:** Tiny dialog helper. Static only.

---

#### `FlightRecorder.cs` -- 4,107 lines
**Types:**
- `public class FlightRecorder`
- `internal enum VesselSwitchDecision` (nested in FlightRecorder)

**Methods:** public: 46, internal: 42, private: 105
**Regions:** Part Event Subscription, Atmosphere Boundary Detection
**Dependencies:** TrajectoryPoint, OrbitSegment, PartEvent, PartEventType, ParsekLog, ParsekSettings, RecordingStore.Recording, RecordingTree, Patches.PhysicsFramePatch
**Notes:** LARGEST non-UI class. Owns all recording state and sampling logic. Called each physics frame by Harmony postfix. Massive mutable state: ~30 HashSets/Dictionaries for tracking part states (engines, RCS, parachutes, fairings, lights, gear, cargo bays, ladders, deployables, animation groups, aero surfaces, control surfaces, robot arm scanners, animate heat, robotics). Many `internal static` transition check methods (CheckParachuteTransition, CheckEngineTransition, CheckDeployableTransition, etc.) -- pure and testable. Contains `OnPhysicsFrame` as the main hot-path entry point. Several methods >50 lines: `CheckLadderState`, `CheckAnimateGenericState`, `CheckJettisonState`, `LogVisualRecordingCoverage`, `StartRecording`, `StopRecording`, `SeedPartEventState`. Static mutable state: none (all instance). Has `EncodeEngineKey` used across multiple files.

---

#### `GameStateBaseline.cs` -- 274 lines
**Types:**
- `public class GameStateBaseline`
- `public struct CrewEntry` (nested in GameStateBaseline)

**Methods:** public: 3, internal: 0, private: 0
**Regions:** none
**Dependencies:** ParsekLog, ConfigNode, KSP career APIs (Funding, R&D, Reputation, ContractSystem, KerbalRoster)
**Notes:** Captures and serializes full career state snapshot. All public.

---

#### `GameStateEvent.cs` -- 302 lines
**Types:**
- `public enum GameStateEventType` (18 values: ContractOffered through ReputationChanged)
- `public struct GameStateEvent`
- `internal static class GameStateEventDisplay`
- `public struct ContractSnapshot`
- `public struct PendingScienceSubject`

**Methods:** public: 4, internal: 3, private: 2
**Regions:** none
**Dependencies:** ParsekLog
**Notes:** Multi-type data file (5 types). GameStateEventDisplay has all the human-readable formatting logic.

---

#### `GameStateRecorder.cs` -- 757 lines
**Types:**
- `internal class GameStateRecorder`
- `private struct PendingCrewEvent` (nested)

**Methods:** public: 0, internal: 6, private: 14
**Regions:** Subscription Management, Contract Handlers, Tech Handlers, Crew Handlers, Resource Handlers, Science Subject Tracking, Facility Polling, Testing Support
**Dependencies:** GameStateStore, GameStateEvent, GameStateEventType, ParsekLog, MilestoneStore, PendingScienceSubject
**Notes:** Static mutable state: `SuppressCrewEvents`, `SuppressResourceEvents`, `SuppressActionReplay`, `SuppressBlockingPatches`, `PendingScienceSubjects`. Crew debouncing via `PendingCrewEvent` dict. Instance state for facility/building polling caches.

---

#### `GameStateStore.cs` -- 694 lines
**Types:**
- `internal static class GameStateStore`

**Methods:** public: 0, internal: 21, private: 2
**Regions:** Event Management, Committed Science Subjects, Baseline Management, File I/O, Testing Support
**Dependencies:** RecordingPaths, ParsekLog, GameStateEvent, GameStateBaseline, ContractSnapshot, PendingScienceSubject, MilestoneStore
**Notes:** Static mutable state: `events`, `contractSnapshots`, `baselines`, `committedScienceSubjects`, `originalScienceValues`, `initialLoadDone`, `lastSaveFolder`, `SuppressLogging`. All static -- singleton pattern via static fields.

---

#### `GhostVisualBuilder.cs` -- 6,395 lines
**Types:**
- `internal class JettisonGhostInfo`
- `internal class ParachuteGhostInfo`
- `internal class EngineGhostInfo`
- `internal struct DeployableTransformState`
- `internal class DeployableGhostInfo`
- `internal struct HeatMaterialState`
- `internal class HeatGhostInfo`
- `internal class LightGhostInfo`
- `internal class RcsGhostInfo`
- `internal enum RoboticVisualMode` (Rotational, Linear, RotorRpm)
- `internal class RoboticGhostInfo`
- `internal struct FxModelDefinition`
- `internal class FairingGhostInfo`
- `internal struct FireShellMesh`
- `internal class ReentryFxInfo`
- `internal static class GhostVisualBuilder`
- `private class MaterialCleanup : MonoBehaviour` (nested in GhostVisualBuilder, ~line 5939)

**Methods:** public: 75, internal: 73, private: 87
**Regions:** none
**Dependencies:** RecordingStore.Recording, ParsekLog, PartLoader, ConfigNode
**Notes:** SECOND LARGEST file. 17 type definitions in one file (15 top-level data types + GhostVisualBuilder + nested MaterialCleanup MonoBehaviour). Static caches: `fxPrefabCache`, `fxLoadedObjectScanCompleted`, `canopyCache`, `deployableCache`, `gearCache`, `ladderCache`, `cargoBayCache`, `animateHeatCache`. Many backward-compat overloads of `BuildTimelineGhostFromSnapshot` (8 overloads). Several methods >50 lines.

---

#### `MergeDialog.cs` -- 532 lines
**Types:**
- `public static class MergeDialog`

**Methods:** public: 1, internal: 5, private: 5
**Regions:** none
**Dependencies:** RecordingStore, ParsekScenario, ParsekLog, Patches.FlightResultsPatch, PopupDialog, RecordingTree, TerminalState, SurfacePosition
**Notes:** All static. Three dialog variants: standalone, chain, tree. UI popup creation with lambda closures capturing recording state.

---

#### `Milestone.cs` -- 75 lines
**Types:**
- `internal class Milestone`

**Methods:** public: 2, internal: 0, private: 0
**Regions:** none
**Dependencies:** GameStateEvent, ParsekLog
**Notes:** Pure data class with serialization.

---

#### `MilestoneStore.cs` -- 461 lines
**Types:**
- `internal static class MilestoneStore`

**Methods:** public: 0, internal: 14, private: 2
**Regions:** File I/O, Removal, Testing Support, Committed Action Queries
**Dependencies:** Milestone, GameStateStore, GameStateEvent, GameStateEventType, RecordingPaths, ResourceBudget, ParsekLog
**Notes:** Static mutable state: `milestones` list, `initialLoadDone`, `lastSaveFolder`, `CurrentEpoch`, `SuppressLogging`. Epoch-based branch isolation for reverts.

---

#### `OrbitSegment.cs` -- 51 lines
**Types:**
- `public struct OrbitSegment`

**Methods:** public: 1 (ToString), internal: 0, private: 0
**Regions:** none
**Dependencies:** none (standalone data type)
**Notes:** Pure data struct. 13 fields including orbital elements + rotation + angular velocity.

---

#### `ParsekFlight.cs` -- 8,225 lines
**Types:**
- `public class ParsekFlight : MonoBehaviour`
- `internal class LightPlaybackState` (nested)
- `internal class GhostPlaybackState` (nested)
- `internal struct InterpolationResult` (nested)
- `private enum GhostPosMode` (nested: PointInterp, SinglePoint, Orbit, Surface)
- `private struct GhostPosEntry` (nested)
- `internal struct PendingDestruction` (nested)

**Methods:** public: 87, internal: 74, private: 91
**Regions:** State, Public Accessors (for ParsekUI), Unity Lifecycle, Scene Change Handling, Split Event Detection (Tree Branching), Terminal Event Detection (Destruction), Input Handling, Recording, Manual Playback (preview), Timeline Auto-Playback, Camera Follow (Watch Mode), Ghost Positioning (shared by manual + timeline playback), Utilities
**Dependencies:** FlightRecorder, RecordingStore, RecordingTree, BackgroundRecorder, ParsekUI, ParsekScenario, GhostVisualBuilder, TrajectoryMath, VesselSpawner, MergeDialog, ParsekLog, ParsekSettings, Patches.PhysicsFramePatch, Patches.FlightResultsPatch, ResourceBudget, MilestoneStore, ActionReplay, GameStateRecorder, GameStateStore, ToolbarControl_NS
**Notes:** LARGEST file. Main flight-scene controller. Massive mutable state: chain tracking (~8 fields), tree branching (~12 fields), dock/undock tracking (~8 fields), ghost playback dicts, overlap ghosts, continuation tracking, camera watch mode. Coroutines (IEnumerator): scene change handling, ghost lifecycle, vessel spawn sequences -- OFF-LIMITS for method extraction. `GhostPlaybackState` and other nested types consumed by ParsekKSC. Several methods >50 lines: `Update`, `LateUpdate`, `OnSceneChange`, `StartRecording`, `StopRecording`, `CommitFlight`, `CommitTreeFlight`, `StartPlayback`, `StopPlayback`.

---

#### `ParsekHarmony.cs` -- 54 lines
**Types:**
- `public class ParsekHarmony : MonoBehaviour` (KSPAddon, Startup.Instantly, once=true)

**Methods:** public: 0, internal: 0, private: 1 (Awake)
**Regions:** none
**Dependencies:** HarmonyLib, ParsekLog
**Notes:** Static mutable state: `initialized` bool. Entry point for all Harmony patches.

---

#### `ParsekKSC.cs` -- 852 lines
**Types:**
- `public class ParsekKSC : MonoBehaviour`

**Methods:** public: 1, internal: 4, private: 13
**Regions:** Ghost Playback
**Dependencies:** ParsekFlight.GhostPlaybackState, RecordingStore, GhostVisualBuilder, ParsekUI, ParsekLog, ToolbarControl_NS
**Notes:** KSC-scene ghost playback. Heavily depends on ParsekFlight nested types -- must change in lockstep during Pass 3.

---

#### `ParsekLog.cs` -- 161 lines
**Types:**
- `public static class ParsekLog`
- `private struct RateLimitState` (nested)

**Methods:** public: 6, internal: 4, private: 2
**Regions:** none
**Dependencies:** ParsekSettings (for verbose flag), UnityEngine.Debug
**Notes:** Central logging. Static mutable state: `SuppressLogging`, `rateLimitStateByKey`, `ClockOverrideForTesting`, `TestSinkForTesting`, `VerboseOverrideForTesting`. `IsVerboseEnabled` is the guard property.

---

#### `ParsekScenario.cs` -- 2,297 lines
**Types:**
- `public class ParsekScenario : ScenarioModule`

**Methods:** public: 13, internal: 13, private: 28
**Regions:** Game State Recording, Crew Replacements, Deferred Merge Dialog, Budget Deduction, Resource Ticking, Crew Reservation, Vessel Lifecycle Events
**Dependencies:** RecordingStore, RecordingTree, GameStateRecorder, GameStateStore, MilestoneStore, ActionReplay, ResourceBudget, RecordingPaths, ParsekLog, Patches.FlightResultsPatch, MergeDialog, GhostVisualBuilder
**Notes:** ScenarioModule -- KSP save/load integration. Static mutable state: `crewReplacements`, `groupParents`, `initialLoadDone`, `lastSaveFolder`, `budgetDeductionEpoch`, `cachedAutoMerge`, `mergeDialogPending`. Coroutines: `ShowDeferredMergeDialog`, `ApplyBudgetDeductionWhenReady`, `ApplyRewindResourceAdjustment` -- off-limits for extraction.

---

#### `ParsekSettings.cs` -- 58 lines
**Types:**
- `public class ParsekSettings : GameParameters.CustomParameterNode`

**Methods:** public: 0, internal: 0, private: 0
**Regions:** none
**Dependencies:** HighLogic.CurrentGame
**Notes:** Pure settings container. 10 fields with KSP UI attributes. `Current` static accessor.

---

#### `ParsekToolbarRegistration.cs` -- 32 lines
**Types:**
- `public class ParsekToolbarRegistration : MonoBehaviour` (KSPAddon, Startup.Instantly, once=true)

**Methods:** public: 0, internal: 0, private: 1 (Start)
**Regions:** none
**Dependencies:** ToolbarControl_NS, ParsekFlight (MODID, MODNAME), ParsekLog
**Notes:** Static mutable state: `registered` bool. One-shot toolbar registration.

---

#### `ParsekUI.cs` -- 2,923 lines
**Types:**
- `public class ParsekUI`
- `public enum UIMode` (nested: Flight, KSC)
- `private enum ActionsSortColumn` (nested: Time, Type, Description, Status)
- `internal enum SortColumn` (nested: Index, Phase, Name, LaunchTime, Duration, Status)

**Methods:** public: 13, internal: 9, private: 151
**Regions:** none
**Dependencies:** ParsekFlight, RecordingStore, ParsekScenario, ParsekLog, GhostVisualBuilder, ResourceBudget, MilestoneStore, GameStateStore, GameStateEvent, GameStateEventDisplay, TrajectoryMath
**Notes:** THIRD LARGEST file. All UI drawing logic (IMGUI). ~50 private fields for window/scroll/sort state. Three sub-windows: Recordings, Actions, Settings. Several methods >50 lines.

---

#### `PartEvent.cs` -- 56 lines
**Types:**
- `public enum PartEventType` (34 values: Decoupled through ParachuteSemiDeployed)
- `public struct PartEvent`

**Methods:** public: 1 (ToString), internal: 0, private: 0
**Regions:** none
**Dependencies:** none
**Notes:** Pure data types.

---

#### `RecordingPaths.cs` -- 166 lines
**Types:**
- `internal static class RecordingPaths`

**Methods:** public: 0, internal: 11, private: 0
**Regions:** none
**Dependencies:** ParsekLog, KSPUtil, HighLogic
**Notes:** All `internal static`. Path construction and directory creation. No mutable state.

---

#### `RecordingStore.cs` -- 1,995 lines
**Types:**
- `public static class RecordingStore`
- `public enum MergeDefault` (nested: GhostOnly, Persist)
- `public class Recording` (nested in RecordingStore)

**Methods:** public: 90, internal: 38, private: 9
**Regions:** Rewind, Trajectory Serialization, Recording File I/O
**Dependencies:** TrajectoryPoint, OrbitSegment, PartEvent, RecordingPaths, RecordingTree, ParsekLog, ParsekScenario, ResourceBudget, MilestoneStore, GameStateStore, GameStateRecorder, GhostVisualBuilder
**Notes:** CENTRAL DATA STORE. Static mutable state: `pendingRecording`, `committedRecordings`, `committedTrees`, `pendingTree`, `SuppressLogging`, `IsRewinding`, `RewindUT` + rewind state. Recording class has ~45 fields. V4→V5 migration. Several methods >50 lines.

---

#### `RecordingTree.cs` -- 691 lines
**Types:**
- `public class RecordingTree`

**Methods:** public: 5, internal: 7, private: 0
**Regions:** none
**Dependencies:** RecordingStore.Recording, BranchPoint, BranchPointType, TerminalState, SurfacePosition, ParsekLog
**Notes:** Tree-level recording container. `BackgroundMap` is runtime-only (rebuilt on load).

---

#### `ResourceBudget.cs` -- 400 lines
**Types:**
- `internal static class ResourceBudget`
- `internal struct BudgetSummary` (nested)

**Methods:** public: 0, internal: 18, private: 0
**Regions:** none
**Dependencies:** RecordingStore.Recording, RecordingTree, Milestone, GameStateEventType, ParsekLog
**Notes:** Static mutable state: `cachedBudget`, `budgetDirty`. Budget computation with caching/invalidation.

---

#### `SurfacePosition.cs` -- 65 lines
**Types:**
- `public enum SurfaceSituation` (Landed, Splashed)
- `public struct SurfacePosition`

**Methods:** public: 3, internal: 0, private: 0
**Regions:** none
**Dependencies:** none
**Notes:** Pure data struct with serialization.

---

#### `TerminalState.cs` -- 14 lines
**Types:**
- `public enum TerminalState` (Orbiting, Landed, Splashed, SubOrbital, Destroyed, Recovered, Docked, Boarded)

**Methods:** none
**Regions:** none
**Dependencies:** none
**Notes:** Pure enum, 8 values.

---

#### `TrajectoryMath.cs` -- 534 lines
**Types:**
- `internal struct RecordingStats`
- `public static class TrajectoryMath`

**Methods:** public: 0, internal: 14, private: 1
**Regions:** none
**Dependencies:** RecordingStore.Recording, TrajectoryPoint, OrbitSegment, ParsekLog
**Notes:** All `internal static` -- pure math, no side effects, no mutable state. Canary file for Pass 1.

---

#### `TrajectoryPoint.cs` -- 30 lines
**Types:**
- `public struct TrajectoryPoint`

**Methods:** public: 1 (ToString), internal: 0, private: 0
**Regions:** none
**Dependencies:** none
**Notes:** Pure data struct.

---

#### `VesselSpawner.cs` -- 978 lines
**Types:**
- `public static class VesselSpawner`

**Methods:** public: 12, internal: 5, private: 8
**Regions:** none
**Dependencies:** RecordingStore.Recording, ParsekScenario, ParsekLog
**Notes:** All static. Static mutable state: `ProximityOffsetEnabled` bool. Several methods >50 lines.

---

### Patches/ Subdirectory

#### `Patches/FacilityUpgradePatch.cs` -- 62 lines
**Types:**
- `internal static class FacilityUpgradePatch` (namespace Parsek.Patches)

**Methods:** public: 0, internal: 0, private: 1 (Prefix)
**Dependencies:** GameStateRecorder, MilestoneStore, GameStateEventType, CommittedActionDialog, ParsekLog
**Notes:** Harmony prefix on `UpgradeableFacility.SetLevel`.

---

#### `Patches/FlightResultsPatch.cs` -- 103 lines
**Types:**
- `internal static class FlightResultsPatch` (namespace Parsek.Patches)

**Methods:** public: 0, internal: 4, private: 2
**Dependencies:** ParsekScenario, RecordingStore, PhysicsFramePatch, ParsekLog
**Notes:** Static mutable state: `Bypass`, `PendingOutcomeMsg`.

---

#### `Patches/PhysicsFramePatch.cs` -- 72 lines
**Types:**
- `internal static class PhysicsFramePatch` (namespace Parsek.Patches)

**Methods:** public: 0, internal: 0, private: 1 (Postfix)
**Dependencies:** FlightRecorder, BackgroundRecorder, ParsekLog
**Notes:** CRITICAL HOT PATH. Static mutable state: `ActiveRecorder`, `BackgroundRecorderInstance`, `lastObservedRecorder`.

---

#### `Patches/ScienceSubjectPatch.cs` -- 50 lines
**Types:**
- `internal static class ScienceSubjectPatch` (namespace Parsek.Patches)

**Methods:** public: 0, internal: 0, private: 3
**Dependencies:** GameStateStore, ParsekLog
**Notes:** Harmony postfix on science subject lookup.

---

#### `Patches/TechResearchPatch.cs` -- 61 lines
**Types:**
- `internal static class TechResearchPatch` (namespace Parsek.Patches)

**Methods:** public: 0, internal: 0, private: 1 (Prefix)
**Dependencies:** GameStateRecorder, MilestoneStore, GameStateEventType, ResourceBudget, CommittedActionDialog, ParsekLog
**Notes:** Harmony prefix on `RDTech.UnlockTech`.

---

### Properties/ Subdirectory

#### `Properties/AssemblyInfo.cs` -- 23 lines
**Types:** none (assembly attributes only)
**Notes:** `[InternalsVisibleTo("Parsek.Tests")]`. Version 0.4.0.0. Skip during refactor.

---

## Summary Statistics

| Metric | Value |
|--------|-------|
| Total files | 39 |
| Total lines | 35,667 |
| Files >1000 lines | 8 (ParsekFlight, GhostVisualBuilder, FlightRecorder, ParsekUI, ParsekScenario, RecordingStore, BackgroundRecorder, VesselSpawner) |
| Files >100 lines | 23 |
| Unique types defined | ~55 (classes, structs, enums) |
| Static classes | 14 (ActionReplay, CommittedActionDialog, GameStateStore, GhostVisualBuilder, MergeDialog, MilestoneStore, RecordingPaths, RecordingStore, ResourceBudget, TrajectoryMath, VesselSpawner, ParsekLog + 3 patches) |
| MonoBehaviours | 4 (ParsekFlight, ParsekKSC, ParsekHarmony, ParsekToolbarRegistration) |
| ScenarioModules | 1 (ParsekScenario) |
| Harmony patches | 5 (PhysicsFrame, FlightResults, FacilityUpgrade, TechResearch, ScienceSubject) |
| Pure data types (no logic) | 7 (BranchPoint, OrbitSegment, PartEvent, TrajectoryPoint, SurfacePosition, TerminalState, BranchPointType) |

### Top concerns for refactoring
1. **ParsekFlight.cs (8,225 lines)** -- monolithic controller with 7 nested types, 13 regions, massive mutable state
2. **GhostVisualBuilder.cs (6,395 lines)** -- 17 types in one file, 8 static caches, massive visual construction logic
3. **FlightRecorder.cs (4,107 lines)** -- 30+ tracking dictionaries/sets, mirrors BackgroundRecorder's part event logic
4. **ParsekUI.cs (2,923 lines)** -- 150+ private methods, all IMGUI drawing in one class
5. **ParsekScenario.cs (2,297 lines)** -- mixed concerns: save/load, crew management, resource ticking, budget deduction, vessel lifecycle
6. **RecordingStore.cs (1,995 lines)** -- Recording class has 45+ fields, mixed static store + serialization + rewind + file I/O
