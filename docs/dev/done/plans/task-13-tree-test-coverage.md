# Plan: Task 13 - Tree Test Coverage

## Overview

Add 18 non-vacuous tests + 3 synthetic tree recording builders. All tests have concrete "what makes it fail" justifications.

**Totals:** 18 new tests (B: 7, C: 6, D: 5), 3 synthetic tree builders (E), ~1000 new lines across 7 files.

## Duplication Check

All 18 tests verified as NOT COVERED by existing tests. See `docs/plans/task-12-test-ideas.md` for the original specifications.

## File Organization

| Target File | Tests | Status |
|---|---|---|
| `MergeDialogTests.cs` (NEW) | B1, B2 | Pure static tests, no shared state |
| `TreeCommitTests.cs` (existing) | B4, B6, B7, D3 | Uses existing MakeRecording/MakeMinimalSnapshot helpers |
| `RecordingTreeTests.cs` (existing) | B5, D1, D2, D5 | Extends existing tree tests |
| `ResourceBudgetTests.cs` (existing) | B3, D4 | Extends existing budget tests |
| `TreeLogVerificationTests.cs` (NEW) | C1-C6 | TestSinkForTesting pattern, [Collection("Sequential")] |
| `SyntheticRecordingTests.cs` (existing) | E1-E3 | Tree recording builders + injection |
| `ScenarioWriter.cs` (existing) | Infrastructure | AddTree(ConfigNode) support |

## Part B: Pure Method Tests (7)

### B1. FormatDuration - all branches
**File:** `MergeDialogTests.cs` (new)
**Method:** `MergeDialog.FormatDuration(double)` - `internal static`
**Approach:** Theory with InlineData covering NaN, Infinity, negative, 0, 45, 60, 61, 3600, 3661, 86400
**Non-vacuous:** 10 unique expected strings, one per code path

### B2. GetLeafSituationText - all 8 terminal states + fallbacks
**File:** `MergeDialogTests.cs` (new)
**Method:** `MergeDialog.GetLeafSituationText(Recording)` - `internal static`
**Approach:** 13 facts covering Orbiting (with/without body), Landed (with/without position), Splashed, SubOrbital (with/without body), Destroyed, Recovered, Docked, Boarded, null-terminal with VesselSituation, null-terminal without VesselSituation
**Non-vacuous:** 13 unique expected strings from switch statement

### B3. ComputeTotal - multiple trees, mixed ResourcesApplied
**File:** `ResourceBudgetTests.cs` (existing)
**Setup:** Tree A: applied, DeltaFunds=-3000. Tree B: not applied, DeltaFunds=-7000
**Assert:** reservedFunds == 7000 (only Tree B)
**Non-vacuous:** Only one value is correct (10000 if both, 0 if neither, 3000 if inverted)

### B4. IsSpawnableLeaf - 5 missing truth-table cases
**File:** `TreeCommitTests.cs` (existing)
**Cases:** null-snapshot-no-terminal (false), Recovered (false), Landed (true), Splashed (true), SubOrbital (true)
**Non-vacuous:** Tests the terminal state guard boundary and snapshot guard independently

### B5. RebuildBackgroundMap - realistic multi-level
**File:** `RecordingTreeTests.cs` (existing)
**Setup:** 5 recordings, each excluded for a different reason. Then change ActiveRecordingId → one included
**Non-vacuous:** All-excluded case tests no false positives; one-included case tests inclusion logic

### B6. GetAllLeaves vs GetSpawnableLeaves - Recovered leaf
**File:** `TreeCommitTests.cs` (existing)
**Setup:** 2 leaves: Recovered (no snapshot) + Orbiting (with snapshot)
**Assert:** GetAllLeaves=2, GetSpawnableLeaves=1
**Non-vacuous:** Tests that Recovered is in GetAllLeaves but excluded from GetSpawnableLeaves

### B7. GetSpawnableLeaves after dock merge - DAG
**File:** `TreeCommitTests.cs` (existing)
**Setup:** root → split → 2 children → dock → merged child (Orbiting, with snapshot)
**Assert:** GetSpawnableLeaves returns exactly 1 (merged child). Docked parents excluded
**Non-vacuous:** DAG structure must not confuse leaf detection

## Part C: Log Verification Tests (6)

All use `ParsekLog.TestSinkForTesting` pattern from `ParsekLogTests.cs`.

### C1. CommitTree logs tree name and recording count
**Assert:** Line contains "Committed tree 'Mun Mission' (3 recordings)"

### C2. StashPendingTree logs tree name
**Assert:** Line contains "Stashed pending tree 'Mun Mission' (3 recordings)"

### C3. DiscardPendingTree logs tree name
**Assert:** Line contains "Discarded pending tree 'Mun Mission'"

### C4. ComputeTotal tree loop - per-tree verbose log
**Assert:** Lines for each tree with resourcesApplied state and cost values

### C5. RecordingTree.Save logs summary
**Assert:** Line contains tree name, recording count, branch point count

### C6. RecordingTree.Load logs summary
**Assert:** Line contains tree ID and recording count

## Part D: Edge Case Tests (5)

### D1. Empty tree - query methods safe
**Assert:** GetSpawnableLeaves/GetAllLeaves return empty (not null). RebuildBackgroundMap doesn't crash

### D2. Load with unknown fields - forward compat
**Assert:** Unknown fields silently ignored, standard fields load correctly

### D3. CommitTree(null) - no crash, no state change
**Assert:** Committed counts unchanged

### D4. All-terminal leaves - budget delta still counts
**Assert:** reservedFunds == 3000 even though all leaves are Destroyed/Recovered

### D5. BackgroundMap after save/load round-trip
**Assert:** BackgroundMap populated after Load (verifies RebuildBackgroundMap called during Load)

## Part E: Synthetic Tree Recordings (3)

### Infrastructure
Extend `ScenarioWriter` with `AddTree(ConfigNode)` method for RECORDING_TREE injection.

### E1. Simple Undock Tree (baseUT+270 to baseUT+390)
root → split → active child (orbit) + background child (orbit segment)

### E2. EVA Tree (baseUT+390 to baseUT+480)
root on pad → EVA → vessel continues + kerbal walks

### E3. Destruction Tree (baseUT+480 to baseUT+570)
root → split → child A (orbiting, spawnable) + child B (destroyed, not spawnable)

## Implementation Sequence

1. Phase 1: B1-B7 (pure methods, no dependencies)
2. Phase 2: D1-D5 (edge cases, no dependencies)
3. Phase 3: C1-C6 (log verification, requires TestSinkForTesting setup)
4. Phase 4: E1-E3 (synthetic builders, requires ScenarioWriter extension)

## Verification

`dotnet test` - all existing + 18 new tests pass. `dotnet test --filter InjectAllRecordings` works for tree injection.
