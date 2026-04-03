# Test Audit — v0.6 (game-action-recording-redesign)

**Date:** 2026-04-03
**Scope:** 30 test files (21 new + 9 modified) added/changed in PR #106 (game-action-recording-redesign merge)
**Method:** 4 parallel Opus subagents, each auditing a batch of 7-9 files across 6 categories
**Prior audit:** T32 (2026-03-25, 110 files) — see `docs/dev/done/plans/task-32-test-audit.md`

---

## Executive Summary

| Category | Finding Count | Severity |
|---|---|---|
| Always-passing / tautological tests | 3 | High |
| Not testing production code | 9 | High |
| Redundant/duplicate tests (cross-file) | 14 | Medium |
| Test isolation issues | 2 | Medium |
| Unused setup | 3 | Low |
| Misleading names | 3 | Low |

Overall quality is good — the majority of the ~2100 new tests call production code with meaningful assertions. The main pattern to address is cross-file duplicates in KerbalReservationTests (8 findings) and hand-written inline logic in BugFixTests/RewindLoggingTests that duplicates production serialization code.

---

## 1. Always-Passing / Tautological Tests (High Priority)

| File | Test | Line | Issue |
|---|---|---|---|
| BugFixTests.cs | `VesselName_FallbackChain_UsesStateFirst` | 363 | Assigns `vesselName = "From State"` then asserts `state.vesselName ?? "Unknown"` equals `"From State"`. Tests C# `??` operator, not production code. |
| BugFixTests.cs | `VesselName_FallbackChain_FallsToDefault` | 371 | Asserts `null ?? "Unknown"` equals `"Unknown"`. Tests C# null-coalescing operator. |
| RewindContextTests.cs | `DelegateProperties_RecordingStore_MatchRewindContext` | 199 | `RecordingStore.IsRewinding` is a trivial pass-through (`=> RewindContext.IsRewinding`). Test asserts `RewindContext.X == RewindContext.X` via intermediary — tautological. |

---

## 2. Not Testing Production Code (High Priority)

### Hand-written inline logic duplicating production code

| File | Test | Line | Issue |
|---|---|---|---|
| RewindLoggingTests.cs | `RewindFields_SurviveSerializationRoundTrip` | 522 | Uses hand-written `SerializeRewindFields`/`DeserializeRewindFields` helpers (lines 479-519) that duplicate `ParsekScenario.OnSave`/`OnLoad`. Never calls actual production serialization. |
| RewindLoggingTests.cs | `RewindFields_MissingInNode_DefaultGracefully` | 544 | Same hand-written helpers. |
| RewindLoggingTests.cs | `RewindFields_NullSaveName_SkipsAllFields` | 558 | Same hand-written helpers. |
| RewindLoggingTests.cs | `RewindFields_LocaleSafe_NoCommasInOutput` | 570 | Same hand-written helpers. |
| RewindLoggingTests.cs | `ResourceCorrection_ResetsToBaseline_NotAbsoluteTarget` | 738 | Inline arithmetic `correction = baseline - currentFunds; result = currentFunds + correction` is tautologically `baseline`. No production correction method called. |
| BugFixTests.cs | `FreshStash_SurvivesRevertGuard` | 852 | Calls `RecordingStore.StashPending` (production) but the revert guard logic is hand-written inline, duplicating `ParsekScenario.OnLoad`. |
| BugFixTests.cs | `StaleStash_DiscardedByRevertGuard` | 877 | Same: hand-written revert guard duplicating production code. |
| BugFixTests.cs | `FreshTreeStash_SurvivesRevertGuard` | 899 | Same pattern. |

### Tests that never call the method their name implies

| File | Test | Line | Issue |
|---|---|---|---|
| LedgerOrchestratorTests.cs | `OnRecordingCommitted_AddsActionsToLedger` | 106 | Never calls `OnRecordingCommitted`. Calls `Ledger.AddAction` directly. Comment admits "we can't call the full OnRecordingCommitted." |

---

## 3. Redundant/Duplicate Tests (Medium Priority)

### KerbalReservationTests vs KerbalEndStateTests (8 cross-file duplicates)

`KerbalReservationTests` contains a block of `InferCrewEndState_*` tests (lines 894-940) that are exact or near-exact duplicates of tests in `KerbalEndStateTests`. The EndState versions include log assertions; the Reservation versions do not.

| KerbalReservationTests | KerbalEndStateTests | Notes |
|---|---|---|
| `InferCrewEndState_NullTerminalState_ReturnsUnknown` (894) | Same name (38) | Exact duplicate |
| `InferCrewEndState_Destroyed_ReturnsDead` (901) | Same name (51) | Exact duplicate |
| `InferCrewEndState_Recovered_ReturnsRecovered` (909) | Same name (64) | Exact duplicate |
| `InferCrewEndState_LandedInSnapshot_ReturnsAboard` (917) | `OrbitingInSnapshot_ReturnsAboard` (77) | Same code branch (intact + in snapshot = Aboard) |
| `InferCrewEndState_LandedNotInSnapshot_ReturnsDead` (925) | Same name (90) | Exact duplicate |
| `InferCrewEndState_BoardedInSnapshot_ReturnsAboard` (932) | Same name (103) | Exact duplicate |
| `InferCrewEndState_BoardedNotInSnapshot_ReturnsUnknown` (940) | `DockedNotInSnapshot_ReturnsUnknown` (116) | Same code path |
| `IsManaged_ReservedKerbal_ReturnsTrue` (407) | KerbalDismissalTests:`IsManaged_ReservedKerbal_ReturnsTrue` (67) | Same core logic |

### Other duplicates

| Test A | Test B | Notes |
|---|---|---|
| KerbalReservationTests:`IsManaged_UnmanagedKerbal_ReturnsFalse` (418) | KerbalDismissalTests:`IsManaged_UnmanagedKerbal_ReturnsFalse` (107) | Same assertion |
| KerbalReservationTests:`MiaRespawnOverride_DeadKerbal_SurvivesMultipleRecalculations` (799) | `MiaRespawnOverride_DeadKerbal_StaysReservedAcrossRecalculations` (733) | Same invariant, 5 loops vs 2 calls |
| FundsModuleTests:`ContractFailPenalty_DeductsFromBalance` (748) | `ContractFail_DeductsPenalty` (355) | Same code path, different contract ID string |
| FundsModuleTests:`ContractCancelPenalty_DeductsFromBalance` (757) | `ContractCancel_DeductsPenalty` (363) | Same code path, different contract ID string |
| KspStatePatcherTests:`PatchScience_NullSingleton_SkipsPerSubjectPatching` (127) | `PatchScience_NullSingleton_DoesNotCrash` (47) | Same early-return path; extra subjects never reached |
| RewindTests:`ResetForTesting_ClearsRewindFlags` (126) | RewindLoggingTests:`ResetForTesting_ClearsRewindAdjustedUT` (306) | Strict subset |

---

## 4. Test Isolation Issues (Medium Priority)

| File | Line | Issue |
|---|---|---|
| BugFixTests.cs | 980 | `FindNextWatchTargetTests` inner class resets static state (`RecordingStore`, `MilestoneStore`, `GameStateStore`, `ParsekLog`) in constructor but does not implement `IDisposable`. State leaks on test failure. Every other class in the same file that touches static state has `IDisposable`. |
| ScienceModuleTests.cs | 357 | `RecalculationWalk_RetroactivePriority` calls `RecalculationEngine.RegisterModule` but `Dispose()` does not call `RecalculationEngine.ClearModules()`. If the test fails between Register and the manual Clear at line 375, the module leaks. All other test classes touching RecalculationEngine clean up in Dispose. |
| GameActionSerializationTests.cs | 7 | Missing `[Collection("Sequential")]`. The `UnknownActionType_DefaultsToZero` test sets `ParsekLog.SuppressLogging = true` (with finally-restore) but the class has no sequential collection, risking parallel state conflicts. |

---

## 5. Unused Setup (Low Priority)

| File | Line | Issue |
|---|---|---|
| MilestonePatchingTests.cs | 31-32 | Constructor sets `GameStateRecorder.SuppressResourceEvents` and `IsReplayingActions` but no test reads or depends on these flags. |
| KerbalEndStateTests.cs | 16 | `RecordingStore.SuppressLogging` and `ResetForTesting()` called but no test interacts with RecordingStore. |
| BugFixTests.cs | 637 | `Bug122_CrewIdentityTransitionTests` captures `logLines` via `TestSinkForTesting` but no test asserts on log output. |

---

## 6. Misleading Names (Low Priority)

| File | Test | Line | Issue |
|---|---|---|---|
| LedgerOrchestratorTests.cs | `OnRecordingCommitted_AddsActionsToLedger` | 106 | Never calls `OnRecordingCommitted` — calls `Ledger.AddAction` directly. |
| LedgerOrchestratorTests.cs | `OnRecordingCommitted_LogsSummary` | 131 | Same: calls `RecalculateAndPatch`, not `OnRecordingCommitted`. |
| MilestonePatchingTests.cs | `PatchMilestones_WithCreditedMilestones_LogsCountBeforeEarlyReturn` | 275 | Name says "logs count before early return" but assertion only checks `"ProgressTracking.Instance is null"` — no count verified. |
| KerbalDismissalTests.cs | `IsManaged_RetiredKerbal_ReturnsTrue` | 86 | Creates two independently reserved kerbals — neither is "retired" in Parsek's sense (displaced stand-in). Tests independent reservation, not retirement. |

---

## Recommendations

### P0 - Fix immediately
1. **Delete 8 KerbalReservationTests `InferCrewEndState_*` duplicates** (lines 894-940) — KerbalEndStateTests already covers these with better assertions.
2. **Delete 2 FundsModuleTests duplicates** (`ContractFailPenalty_DeductsFromBalance`, `ContractCancelPenalty_DeductsFromBalance`).

### P1 - Fix soon
3. **Add `IDisposable` to `FindNextWatchTargetTests`** (BugFixTests.cs:980).
4. **Add `RecalculationEngine.ClearModules()` to `ScienceModuleTests.Dispose()`**.
5. **Add `[Collection("Sequential")]` to `GameActionSerializationTests`**.
6. **Rewrite RewindLoggingTests serialization tests** to call actual `ParsekScenario` serialization methods instead of hand-written helpers. Or delete if untestable without Unity.
7. **Delete or rewrite 3 BugFixTests stash guard tests** (852, 877, 899) to call production revert-guard logic.

### P2 - Fix when convenient
8. **Delete tautological tests**: `VesselName_FallbackChain_*` (2), `DelegateProperties_RecordingStore_MatchRewindContext`.
9. **Rename misleading tests**: `OnRecordingCommitted_*` → `Ledger_AddAction_*`, etc.
10. **Remove unused setup** from MilestonePatchingTests, KerbalEndStateTests, Bug122 tests.
11. **Delete `KerbalReservationTests.IsManaged_*` duplicates** (407, 418) — covered in KerbalDismissalTests.
12. **Delete remaining same-file duplicates** (MiaRespawnOverride, PatchScience, RewindTests:ResetForTesting).
