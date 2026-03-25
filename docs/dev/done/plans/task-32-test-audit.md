# T32: Deep Test Suite Audit

**Date:** 2026-03-25
**Scope:** All 110 test files (~55,000 lines) in `Source/Parsek.Tests/`
**Method:** 9 Opus subagents each read every line of their assigned batch, reporting findings in 6 categories
**Reviewed by:** Independent Opus reviewer spot-checked 18+ findings against source code; corrections applied below

---

## Executive Summary

| Category | Finding Count | Severity |
|---|---|---|
| Always-passing / tautological tests | 28 | High |
| Redundant tests (exact or near-duplicates) | 38 | Medium |
| Test isolation issues (missing Dispose / Collection) | 25 | Medium |
| Not testing production code | 12 | High |
| Missing edge cases | ~45 | Low |
| Dead/stub tests | 4 | Low |
| Unused log capture (setup but never asserted) | 5 | Low |

---

## 1. Always-Passing / Tautological Tests (High Priority)

Tests that can never fail due to missing assertions, tautological checks, or guarded execution paths.

### Zero-assertion "no crash" tests
| File | Test | Line | Issue |
|---|---|---|---|
| ActionReplayTests | `ReplayCommittedActions_EmptyList_NoOp` | 94 | No assertion at all |
| ActionReplayTests | `ReplayCommittedActions_NullList_NoOp` | 232 | No assertion at all |
| BackgroundPartEventAuditTests | `Shutdown_LogsCompletionMessage` | 358 | Catches NRE so shutdown path never executes; zero coverage |

### Implicit does-not-throw regression tests (low-value but not zero-value)
These call real production code with edge-case inputs but have no explicit assertions. They have regression value (e.g., NRE crash guards) but should add explicit `Record.Exception` wrapping for clarity.

| File | Test | Line | Issue |
|---|---|---|---|
| DiagnosticLoggingTests | `LogSpawnContext_*` | 26-112 | 6 tests call `VesselSpawner.LogSpawnContext` with null/edge inputs, no explicit assertion |
| PartEventTests | `RemoveSpecificCrew_NullSnapshot_NoException` | 352 | Calls production code with null, no explicit assertion |
| PartEventTests | `RemoveSpecificCrew_NullExcludeSet_NoException` | 359 | Same |

### Tests that never call production code
| File | Test | Line | Issue |
|---|---|---|---|
| BackgroundPartEventAuditTests | `OnBackgroundPartDie_VesselNotInBackgroundMap_Ignored` | 206 | Never calls `OnBackgroundPartDie`; asserts default empty state |
| GhostPlaybackEngineTests | `DestroyAllGhosts_Precondition_CollectionsPopulated` | 734 | Sets up state but never calls `DestroyAllGhosts` |
| GroupManagementTests | `HiddenGroups_OnSave_WritesHiddenGroupsNode` | 807 | Hand-writes save logic inline instead of calling production `OnSave` |
| GroupManagementTests | `HiddenGroups_RoundTrip_SaveThenLoad` | 861 | Same -- save side is hand-written |
| EnvironmentTrackingIntegrationTests | `CloseCurrentTrackSection_WithFrames_ComputesSampleRate` | 129 | Computes expected rate in test body, asserts `2.0f == 2.0f` |
| RecordingStoreTests | `CrewReplacements_SaveNode_EmptyMappingSkipsNode` | 746 | Only asserts on ConfigNode API, no production code called |
| RecordingStoreTests | `CrewReplacements_LoadNode_MissingNodeReturnsCleanState` | 776 | Same |
| RecordingStoreTests | `CrewReplacements_LoadNode_HandlesNullValues` | 756 | Same |
| RelativePlaybackTests | `OffsetInterpolation_Midpoint_AveragesCorrectly` | 189 | Inline math, no production method (4 tests, lines 189-227) |
| RelativeRecordingTests | `ModeTransition_AbsoluteRelativeAbsolute_ProducesThreeSections` | 184 | Asserts on manually-constructed structs, no production code |
| RelativeRecordingTests | `TrajectoryPoint_CanStoreOffsetInLatLonAlt` | 146 | Tests language struct field assignment |

### Conditional guards around assertions (vacuous pass)
| File | Test | Line | Issue |
|---|---|---|---|
| FlagEventTests | `StashPending_FlagEvents_RetimeBeforeTrim` | 193 | Assertions inside `if (HasPending && Count > 0)` -- passes with zero assertions if condition is false |

### Property getter/setter tests (always-passing trivially)
| File | Test | Line | Issue |
|---|---|---|---|
| RewindTimelineTests | `InitiateRewind_SetsIsRewinding` | 541 | Sets bool, asserts bool |
| RewindTimelineTests | `InitiateRewind_SetsRewindUT` | 551 | Sets double, asserts double |
| RewindTimelineTests | `InitiateRewind_SetsRewindReserved` | 558 | Sets value, asserts value |
| GameStateEventTests | `CrewSuppression_FlagCanBeSet` | 625 | Sets static bool, asserts bool, inline restore |
| GameStateEventTests | `ResourceSuppression_FlagCanBeSet` | 641 | Same pattern |
| GhostChainTests | `ChainLink_DefaultValues_AreNullOrZero` | 28 | Tests C# auto-property defaults (low-value, not zero-value) |
| GhostChainTests | `GhostChain_SpawnUT_MatchesAssignedValue` | 113 | Property set/get |
| RewindLoggingTests | `UTFlow_MustNotBeSetBeforeLoadScene` | 750 | Sets then reads property |
| FormatVersionTests | `PlaybackGate_V5HasNoTrackSections_V6Has` | 184 | v6 side: adds item then asserts count > 0 |

### Log tests that emit log manually instead of testing production logging
| File | Test | Line | Issue |
|---|---|---|---|
| SpawnCleanupGuardTests | `Guard_LogsSkipMessage_WhenAlreadySet` | 207 | Calls `ParsekLog.Info` directly in test body |
| SpawnCleanupGuardTests | `RewindPath_LogsCleanupDataSet` | 242 | Same |
| SpawnCleanupGuardTests | `RevertPath_LogsCollected_WhenNotAlreadySet` | 264 | Same |
| ProximityRateSelectorTests | `BackgroundRecorder_LogsSampleRateChange_WhenIntervalChanges` | 194 | Same |
| ProximityRateSelectorTests | `BackgroundRecorder_LogsSampleRateChange_OutOfRange_ShowsNone` | 225 | Same |

---

## 2. Redundant Tests (Medium Priority)

### Exact duplicates (cross-file)
| Test A | Test B | Code Path |
|---|---|---|
| ChainEvalOnLoadTests:`NoCommittedTrees_ProducesNullChains` (117) | ChainSaveLoadTests:`NoCommittedTrees_NoChainsOnLoad` (232) | `ComputeAllGhostChains(empty)` |
| ChainEvalOnLoadTests:`NoTrees_LogsNoCommittedTrees` (298) | ChainSaveLoadTests:`EmptyTrees_LogsNoCommittedTrees` (612) | Same log assertion |
| RewindTests:`CanRewind_AlreadyRewinding_ReturnsFalse` (49) | RewindTimelineTests:`CanRewind_AlreadyRewinding_ReturnsFalse` (447) | Exact duplicate |
| RewindTests:`CanRewind_NoRewindSave_ReturnsFalse` (40) | RewindTimelineTests:`CanRewind_NoSaveFile_ReturnsFalse` (458) | Exact duplicate |
| RewindTests:`CanRewind_Recording_ReturnsFalse` (59) | RewindTimelineTests:`CanRewind_RecordingInProgress_ReturnsFalse` (478) | Exact duplicate |
| RewindTests:`ApplyPersistenceArtifactsFrom_CopiesRewindFields` (147) | RewindLoggingTests:`ApplyPersistenceArtifactsFrom_CopiesAllRewindFields` (389) | Exact duplicate |
| DockUndockChainTests:`DecideOnVesselSwitch_UndockSiblingPid_TakesPriorityOverStop` (607) | DockUndockChainTests:`DecideOnVesselSwitch_UndockSibling_ReturnsUndockSwitch` (311) | Same inputs, same assertion |
| GameStateEventTests:`ScienceSubjects_SerializeEmpty_NoNode` (1592) | GameStateEventTests:`SerializeEmpty_NoScienceSubjectsNode_WhenBothEmpty` (1756) | Exact duplicate |
| TrackSectionSourceTests:`RoundTrip_SourceBackground_PreservedCorrectly` (93) | TrackSectionSerializationTests:`RoundTrip_BackgroundFlag_PreservedCorrectly` (418) | Same round-trip |

### Near-duplicates (same code path, different inputs with no additional branch coverage)
| File | Tests | Issue |
|---|---|---|
| AnchorLifecycleTests | `IsAnchorLoaded_ZeroAnchorPid_*` (34, 77, 86) | 3 tests for same early-return path |
| AnchorLifecycleTests | `LogsLoadedStatus` / `LogsNotLoaded` / `LogsNullSet` (280, 289, 300) | Duplicate inline log assertions |
| BackgroundSplitTests | "Log Assertion Tests" region (470-508) | 4 tests duplicate earlier tests with misleading names |
| BackgroundSplitTests | `SingleDebrisChild_IsMarkedAsDebris` (148) vs `DebrisChild_TTLWouldBeSet` (334) | Same path |
| BackgroundRecorderTests | Constructor tests (62, 199, 231) | 3 tests for same constructor behavior |
| BackgroundRecorderTests | `ShouldRecordPoint_*` (378, 396, 411) | Duplicate TrajectoryMath tests from AdaptiveSamplingTests |
| GhostPlaybackEngineTests | `*_AcceptsMockTrajectory_NoCast` tests (781, 790, 804) | Duplicate earlier tests |
| SpawnCollisionDetectorTests vs SpawnCollisionWiringTests | `BoundsOverlap_*`, `ComputeVesselBounds_*` | ~5 duplicates across files |
| CrashCoalescerTests vs CrashCoalesceWiringTests | Multiple coalescer behavior tests | ~4 overlaps |
| ComputeStatsExtractedTests vs ComputeStatsTests | Orbit segment null/unknown body tests | 3 overlaps |

### Tests testing .NET framework behavior (not Parsek code)
| File | Test | Line | Issue |
|---|---|---|---|
| AnchorLifecycleTests | `LoadedSet_RemoveNonexistent_NoError` | 119 | Tests `HashSet.Remove` |
| AnchorLifecycleTests | `LoadedSet_DuplicateAdd_Idempotent` | 128 | Tests `HashSet.Add` |
| AnchorLifecycleTests | `LoadedSet_Clear_RemovesAll` | 138 | Tests `HashSet.Clear` |

---

## 3. Test Isolation Issues (Medium Priority)

### Missing `IDisposable` / `Dispose()` (state leaks on failure)
| File | Line | Shared State Modified |
|---|---|---|
| CommittedActionTests | 8 | ParsekLog, RecordingStore |
| DiagnosticLoggingTests | 8 | ParsekLog, RecordingStore |
| DockUndockChainTests | 8 | RecordingStore |
| FlagEventTests | 9 | RecordingStore |
| GameStateEventTests | 10 | Multiple static flags |
| MergeEventDetectionTests | 8 | RecordingStore, MilestoneStore |
| MilestoneTests | 8 | GameStateStore, MilestoneStore |
| OrbitSegmentTests | 9 | RecordingStore |
| PartEventTests | 11 | RecordingStore |
| RewindTests | 8 | RecordingStore |
| RewindTimelineTests | 8 | RecordingStore |
| SplitEventDetectionTests | 8 | RecordingStore |
| SyntheticRecordingTests | 13 | RecordingStore |
| TerminalEventTests | 7 | RecordingStore |
| TreeCommitTests | 10 | RecordingStore |
| TreeLogVerificationTests | 8 | RecordingStore, ParsekLog |
| VesselSwitchTreeTests | 7 | RecordingStore |

### Missing `[Collection("Sequential")]` (parallel execution risk)
| File | Line | Static State Touched |
|---|---|---|
| FxDiagnosticsTests | 6 | ParsekLog (indirect via production methods) |
| GhostChainTests | 8 | None (low risk) |
| LoopPhaseTests (ComputeLoopPhaseFromUT_Tests) | 10 | None (low risk) |
| MergeDialogTests (MergeDialogFormatTests) | 5 | None (low risk) |
| RuntimePolicyTests | 8 | None (low risk) |
| ~~SelectiveSpawnUITests~~ | ~~7~~ | ~~Already has attribute -- reviewer-corrected~~ |
| TrackSectionTests | 8 | None (low risk) |
| TrajectoryPointTests | 7 | None (low risk) |
| VesselPersistenceTests | 5 | None (low risk) |
| WaypointSearchTests | 12 | ParsekLog |

### Fragile manual state cleanup (not in Dispose)
| File | Test | Line | Issue |
|---|---|---|---|
| GameStateEventTests | `CrewSuppression_FlagCanBeSet` | 625 | Inline restore of `SuppressCrewEvents` |
| GameStateEventTests | `ResourceSuppression_FlagCanBeSet` | 641 | Inline restore of `SuppressResourceEvents` |
| MilestoneTests | `GameStateStore_AddEvent_StampsCurrentEpoch` | 571 | Manual `CurrentEpoch = 0` reset at line 586 |

### ~~Incorrect teardown~~ (reviewer-corrected: these are correct)
Both ResolveLocalizedNameTests and TerrainCorrectorTests correctly restore `SuppressLogging` to `false` (the runtime default) in Dispose. `ResetTestOverrides()` does not handle `SuppressLogging`, so explicit restore is the right pattern.

---

## 4. Dead / Stub Code

| File | Line | Issue |
|---|---|---|
| OrbitalRotationTests | 1-4 | Empty file with comment (intentional, documented) |
| FlightRecorderExtractedTests | 284-286 | Empty `#region CheckAnimateHeatTransition` with removal comment |
| EnvironmentDetectorTests | 357-362 | Empty region, tests removed |
| EnvironmentDetectorTests | 643-645 | Comment about removed tests |
| FastForwardTests | 41 | `MakePoints` helper defined but never used |
| ChainTipSpawnTests | 1-29 | Only 2 trivial tests for a boolean negation method |

---

## 5. Unused Log Capture (setup but never asserted)

| File | Issue |
|---|---|
| AutoLoopTests | `logLines` field captured but no log assertions in any test |
| InterpolatePointsTests | `logLines` captured but never asserted |
| MergeDialogExtractedTests | `logLines` captured but never asserted |
| AtmosphereSplitTests | `[Collection("Sequential")]` but no log capture despite likely boundary-crossing logging |
| MergeEventDetectionTests | Logging suppressed entirely, no log verification |
| MilestoneTests | Logging suppressed entirely, no log verification |

---

## 6. Misleading Test Names

| File | Test | Line | Issue |
|---|---|---|---|
| TerminalEventTests | `ApplyTerminalDestruction_DoesNotOverwriteExistingData` | 313 | Actually verifies that it DOES overwrite |
| BackgroundTrackSectionTests | `StartCheckpointTrackSection_ViaGoOnRails_CreatesCheckpointSource` | 289 | Never asserts Checkpoint source, tests environment instead |
| BackgroundSplitTests | "Log Assertion Tests" region | 470 | Zero log assertions in the region |
| GameStateStoreExtractedTests | `BuildEventTypeDistribution_AllEventTypes_HandlesAllEnumValues` | 76 | Tests only 5 values, not all |

---

## 7. Missing Edge Cases (Notable gaps)

### Production code with 0% test coverage
- **ResourceApplicator** KSP-mutation methods (as noted in T32 description)
- **CrewReservationManager** mutation methods
- **ParsekFlight** resource replay paths

### Edge cases missing from existing tests
| Area | Missing Case |
|---|---|
| ActionReplayTests | `Events` list is null (not just empty) |
| CommitFlowTests | `ShouldSpawnAtRecordingEnd` false paths (null snapshot, VesselSpawned=true) |
| CompoundPartDataTests | Return value of `TryParseCompoundPartData` not asserted (lines 126, 163) |
| DeserializeExtractedTests | Round-trip doesn't verify rotation or velocity fields |
| FlagEventTests | `ShouldRecordFlagEvent` happy path (returns true) missing |
| ParsekLogTests | `Warn` and `Log` levels untested; `VerboseRateLimited` reset not tested |
| PartEventTests | `CheckAnimateHeatTransition` exact threshold boundary values |
| RecordingStoreTests | `GetRecommendedAction` missing 3 of 4 input combinations |
| GhostCommNetRelayTests | `antennaCombinableExponent=0` (potential div-by-zero path) |

---

## Recommendations (Priority Order)

### P0 - Fix immediately
1. **Delete exact duplicate tests** (8 pairs identified) -- they add CI time with zero additional coverage
2. **Add assertions to zero-assertion tests** or delete them -- tests that can never fail provide false confidence

### P1 - Fix soon
3. **Add `IDisposable`** to the 17 test classes that modify shared state without cleanup
4. **Fix tests that don't call production code** (12 instances) -- rewrite to exercise the real method or delete
5. **Fix misleading test names** (4 instances) -- names that contradict behavior cause confusion during debugging

### P2 - Fix when convenient
6. **Add `[Collection("Sequential")]`** to `WaypointSearchTests` (touches ParsekLog shared state without the attribute)
7. **Consolidate near-duplicate tests** into `[Theory]` with `[InlineData]` where appropriate
8. **Wire up unused log capture** in 5 test classes or remove the setup

### P3 - Low priority
10. **Add missing edge case tests** per the gaps table above
11. **Remove dead code** (empty regions, unused helpers)
12. **Delete tests for .NET framework behavior** (HashSet operations in AnchorLifecycleTests)
