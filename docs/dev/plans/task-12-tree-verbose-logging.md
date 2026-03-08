# Plan: Task 12 — Tree Verbose Logging

## Overview

Add verbose diagnostic logging to 11 tree-related code paths that currently lack it. All changes are logging-only — no behavioral changes. The gaps span 3 files: `RecordingTree.cs`, `ParsekFlight.cs`, and `ResourceBudget.cs`. No new tests (those come in Task 13).

## Existing Logging Conventions

- **Subsystem strings**: `"Flight"` (ParsekFlight via `Log()` helper → `ParsekLog.Verbose("Flight", ...)`), `"ResourceBudget"` (ResourceBudget.cs), `"RecordingStore"` (RecordingStore.cs), `"RecordingTree"` (new, for RecordingTree.cs).
- **Log levels**: `Info` for significant one-time events, `Verbose` for detailed diagnostics, `VerboseRateLimited(subsystem, key, msg)` for per-frame code (default 5s rate limit).
- **Message format**: `$"MethodName: key=value key=value"` style.
- **`Log()` helper in ParsekFlight**: `void Log(string message) => ParsekLog.Verbose("Flight", message);`
- **Rate-limit key convention**: kebab-case descriptive keys.

## Gap-by-Gap Plan

### A1: RecordingTree.Save — log summary

**File:** `RecordingTree.cs`, method `Save(ConfigNode treeNode)` (~line 37)
**Insert:** After BranchPoints loop, before closing brace (~line 67)
**Level:** `ParsekLog.Verbose("RecordingTree", ...)`
**Log:** Tree name, recording count, branch point count, ResourcesApplied.
**Lines:** +2

### A2: RecordingTree.Load — log summary

**File:** `RecordingTree.cs`, method `Load(ConfigNode treeNode)` (~line 69)
**Insert:** After `RebuildBackgroundMap()` call (~line 116), before `return tree`
**Level:** `ParsekLog.Verbose("RecordingTree", ...)`
**Log:** Tree ID, name, recording count, branch point count, which fields were present vs defaulted (use `resourcesAppliedStr` variable already in scope at line 92).
**Lines:** +3

### A3: ApplyTreeResourceDeltas — log outer loop decisions

**File:** `ParsekFlight.cs`, method `ApplyTreeResourceDeltas(double currentUT)` (~line 3989)
**Insert:** Two places inside loop: after `ResourcesApplied` skip (~line 3995), after `currentUT <= treeEndUT` skip (~line 4005). Convert one-line if/continue to braced blocks.
**Level:** `ParsekLog.VerboseRateLimited("Flight", key, ...)`
**Keys:** `"tree-res-skip-applied"`, `"tree-res-wait-ut"`
**Log:** Tree name, skip reason, current UT vs end UT for waiting case.
**Lines:** +10

### A4: ApplyTreeLumpSum — log clamping

**File:** `ParsekFlight.cs`, method `ApplyTreeLumpSum(RecordingTree tree)` (~line 4012)
**Insert:** For each of 3 resource blocks (funds/science/rep ~lines 4023, 4032, 4041): capture `original` before clamp, log if `delta != original`.
**Level:** `Log()` helper (Verbose, "Flight")
**Log:** Resource type, tree name, original value, clamped value, current balance.
**Lines:** +9

### A5: PositionGhostAtSurface — log positioning

**File:** `ParsekFlight.cs`, method `PositionGhostAtSurface(...)` (~line 5570)
**Insert:** body==null branch (~line 5575) and success path (~line 5580).
**Level:** body-not-found: `ParsekLog.Warn("Flight", ...)` (this is a real error). Success: `ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned", ...)`.
**Keys:** `"surface-ghost-positioned"` for success.
**Log:** Body name, lat/lon/alt for success. Body name and "not found" for error.
**Lines:** +4

### A6: FindCommittedTree — log miss

**File:** `ParsekFlight.cs`, method `FindCommittedTree(string treeId)` (~line 4056)
**Insert:** Before `return null` (~line 4063).
**Level:** `Log()` helper (Verbose, "Flight")
**Log:** Requested treeId, count of committed trees checked.
**Lines:** +1

### A7: ResourceBudget.ComputeTotal — log per-tree cost breakdown

**File:** `ResourceBudget.cs`, method `ComputeTotal(...)` (~line 180)
**Insert:** Inside tree loop (~lines 201-209). Capture individual costs into locals before adding.
**Level:** `ParsekLog.Verbose("ResourceBudget", ...)`
**Log:** Tree name, ResourcesApplied state, funds/science/rep cost.
**Lines:** +7

### A8: FinalizeTreeRecordings — log per-recording detail

**File:** `ParsekFlight.cs`, method `FinalizeTreeRecordings(...)` (~line 3245)
**Insert:** At end of the per-recording foreach loop body (~line 3326). `isLeaf` variable already in scope (line 3288).
**Level:** `Log()` helper (Verbose, "Flight")
**Log:** Recording ID, vessel name, point count, orbit segment count, terminal state (or "none"), snapshot yes/no, leaf yes/no.
**Lines:** +4

### A9: CommitTreeFlight — log marking detail

**File:** `ParsekFlight.cs`, method `CommitTreeFlight()` (~line 3088)
**Insert:** Replace the simple foreach marking loop (~lines 3133-3137) with version that counts marked recordings. Add log after.
**Level:** `ParsekLog.Info("Flight", ...)` (significant commit event, matches existing Info logs in this method at line 3097).
**Log:** ResourcesApplied=true, marked count / total count.
**Lines:** +4

### A10: TakeControlOfGhost — log tree branch

**File:** `ParsekFlight.cs`, method `TakeControlOfGhost(int index)` (~line 5242)
**Insert:** Expand the tree resource application block (~lines 5327-5332) with logging for each decision: applying, already applied, tree not found.
**Level:** `Log()` helper (Verbose, "Flight")
**Log:** Tree name, applied state, or "not found" with tree ID.
**Lines:** +10

### A11: RebuildBackgroundMap — log result (from reviewer)

**File:** `RecordingTree.cs`, method `RebuildBackgroundMap()` (~line 120)
**Insert:** After the foreach loop ends (~line 133), before closing brace.
**Level:** `ParsekLog.Verbose("RecordingTree", ...)`
**Log:** Background map entry count, total recording count (helps diagnose "why isn't my vessel in the background map?").
**Lines:** +2

## Summary

| Gap | File | Method | Level | Per-frame? | Lines |
|-----|------|--------|-------|-----------|-------|
| A1 | RecordingTree.cs | Save | Verbose | No | +2 |
| A2 | RecordingTree.cs | Load | Verbose | No | +3 |
| A3 | ParsekFlight.cs | ApplyTreeResourceDeltas | VerboseRateLimited | Yes | +10 |
| A4 | ParsekFlight.cs | ApplyTreeLumpSum | Verbose (Log) | No | +9 |
| A5 | ParsekFlight.cs | PositionGhostAtSurface | Warn + VerboseRateLimited | Yes | +4 |
| A6 | ParsekFlight.cs | FindCommittedTree | Verbose (Log) | No | +1 |
| A7 | ResourceBudget.cs | ComputeTotal | Verbose | No | +7 |
| A8 | ParsekFlight.cs | FinalizeTreeRecordings | Verbose (Log) | No | +4 |
| A9 | ParsekFlight.cs | CommitTreeFlight | Info | No | +4 |
| A10 | ParsekFlight.cs | TakeControlOfGhost | Verbose (Log) | No | +10 |
| A11 | RecordingTree.cs | RebuildBackgroundMap | Verbose | No | +2 |
| **Total** | | | | | **~56** |

## Files to Modify

1. `Source/Parsek/RecordingTree.cs` — Gaps A1, A2, A11
2. `Source/Parsek/ParsekFlight.cs` — Gaps A3, A4, A5, A6, A8, A9, A10
3. `Source/Parsek/ResourceBudget.cs` — Gap A7

## Orchestrator Review Fixes

1. **A5 body-not-found → Warn**: Reviewer correctly noted a missing celestial body is a real error, not verbose noise. Use `ParsekLog.Warn` for this case, not VerboseRateLimited.
2. **A11 added**: Reviewer caught that `RebuildBackgroundMap` has zero logging and silently builds a lookup table. Added as Gap A11.
3. All other reviewer issues (line number discrepancies, decision point wording) were verified as already correct in the plan or relating to the test-ideas doc, not this plan.

## Verification

`dotnet build` clean, `dotnet test` all pass (no new tests — logging only). Tests with `SuppressLogging = true` won't be affected by new log calls.
