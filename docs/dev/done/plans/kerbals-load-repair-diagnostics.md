# Kerbals Load Repair Diagnostics Plan

This plan covers `T64` from `docs/dev/todo-and-known-bugs.md`: concise load-time diagnostics for
kerbal reservation repairs.

## Investigation Summary

The current kerbal repair logs are real but fragmented.

Existing signals already in the code:

- `CrewReservationManager.LoadCrewReplacements(...)` logs only replacement counts
- `KerbalsModule.LoadSlots(...)` logs only loaded-slot counts or legacy migration counts
- `KerbalsModule.PopulateCrewEndStates(...)` can log per-recording reverse-maps, but only in the
  end-state path and not as a load summary
- `LedgerOrchestrator.MigrateKerbalAssignments(...)` logs only repaired recording counts and row
  totals
- `KerbalsModule.ApplyToRoster(...)` logs final totals plus per-stand-in retirement / deletion
  lines

That means the information exists, but a player log still does not clearly answer:

- did this load repair stale stand-in assignment rows
- did persisted slot data get normalized, migrated, or ignored
- were retired historical stand-ins recreated
- were unused displaced stand-ins deleted
- were tourist assignment rows dropped during migration

The TODO is asking for a one-load explanation, not more scattered implementation detail.

## Recommended Diagnostics Model

Introduce one ephemeral summary object for the kerbal load path, for example:

- `KerbalLoadRepairSummary`

It should span the whole kerbal-load path, not just `LedgerOrchestrator.OnKspLoad(...)`.

That means one of these two ownership models:

- create it early in `ParsekScenario.OnLoad(...)` before `LoadCrewAndGroupState(...)`, thread it
  through the kerbal-specific load steps, then discard it after `OnKspLoad(...)`
- or let earlier steps produce smaller summaries such as `SlotLoadSummary`, then merge them into a
  final load summary later

Do not attach the summary solely to `OnKspLoad(...)`, because slot loading happens earlier.

The summary should track counts plus a bounded set of samples:

- slot source and counts:
  - `slotsLoaded`
  - `chainEntriesLoaded`
  - `loadedFromLegacyCrewReplacements`
  - `ignoredSlotEntries`
- ledger repair counts:
  - `repairedRecordings`
  - `oldRows`
  - `newRows`
  - `remappedStandInRows`
  - `touristRowsSkipped`
- roster repair counts:
  - `retiredStandInsRecreated`
  - `unusedStandInsDeleted`
  - `retiredStandInsKept`
- sample names / recording IDs:
  - capped to a very small number, such as `3`

## Instrumentation Points

### 1. `KerbalsModule.LoadSlots(...)`

Today this method logs only the source and raw counts.

Update it to return a small `SlotLoadSummary` instead of `void`, or to optionally fill one passed
in by the caller. That summary should capture:

- whether data came from `KERBAL_SLOTS` or legacy `CREW_REPLACEMENTS`
- how many slots and chain entries were loaded
- whether any malformed or empty entries were ignored

Important constraint:

- do not claim a slot "repair" unless the code actually normalized or dropped something
- if the current implementation only loads data as-is, the summary should say exactly that

### 2. `LedgerOrchestrator.MigrateKerbalAssignments(...)`

This is the best place to detect row-level repair reasons because it already compares existing
rows against the desired derived rows.

Extend the comparison / replacement path so the summary can record:

- stand-in name remaps to owner name
- end-state rewrites from stale `Unknown` to finite values
- rows dropped because the resolved role is `Tourist`

This requires an explicit diff-classification step. The current boolean
`KerbalAssignmentActionsMatch(...)` path is not enough on its own.

Recommended direction:

- keep the fast equality check if useful
- add a second helper that classifies differences only when a recording actually needs repair
- make tourist-skip counting come from the extraction / desired-row generation path, because
  tourist filtering happens before replacement

### 3. `KerbalsModule.EnsureChainDepth(...)` and `ApplyToRoster(...)`

The TODO mentions slot-chain repair/normalization. That is not only a load concern; chain mutation
also happens later during recalculation in `EnsureChainDepth(...)`.

The diagnostics should therefore distinguish between:

- what was loaded from persisted slot data
- what was extended or normalized during recalculation
- what final roster effects happened afterward

`ApplyToRoster(...)` already knows whether a displaced stand-in:

- was recreated
- was kept as retired history
- was deleted as unused

Return a compact `RosterApplySummary` from the decision / mutation pass so load-time diagnostics can
report the final roster effects without scraping ad hoc log lines afterward.

### 4. Keep the diagnostics load-only

`ApplyToRoster(...)` runs from `RecalculateAndPatch()` on many non-load triggers. The new one-shot
summary therefore needs an explicit load context.

Recommended options:

- pass the load summary object only during the load-triggered `OnKspLoad(...)` recalculation
- or use a scoped load-only diagnostics context that lower layers can consult while a load is in
  progress

Do not make the summary depend on scraping generic `ApplyToRoster(...)` logs after the fact.

### 5. `LedgerOrchestrator.OnKspLoad(...)`

After migration and recalculation finish, emit one concise summary line when repairs happened, plus
an optional second line with capped samples.

Recommended logging policy:

- emit nothing extra when the load is clean
- emit one summary line when any repair counter is non-zero
- emit a second sample line only when the names / recording IDs materially help debugging

## Log Shape

The log output should stay grep-friendly and one-shot. Example shape:

```text
[Parsek][INFO][KerbalLoad] repair summary: slots=1 chainEntries=2 source=KERBAL_SLOTS repairedRecordings=2 remappedRows=2 touristRowsSkipped=1 retiredRecreated=1 deletedUnused=1
[Parsek][INFO][KerbalLoad] repair samples: remap rec-slot-repair Hanley Kerman->Jebediah Kerman; retired Kirrim Kerman; deleted Hanley Kerman
```

Guard rails:

- no per-frame logs
- no unbounded per-row spam
- no dumping full save-node contents
- keep names / IDs capped and deterministic

## Harness Requirements

Any new diagnostics tests in this area should explicitly include:

- `[Collection("Sequential")]`
- `LedgerOrchestrator.ResetForTesting()`
- `RecordingStore.ResetForTesting()`
- `CrewReservationManager.ResetReplacementsForTesting()`
- `GameStateStore.ResetForTesting()` when tourist trait fallback or baselines are involved
- `KspStatePatcher.SuppressUnityCallsForTesting = true` when exercising
  `LedgerOrchestrator.OnKspLoad(...)`
- `ParsekLog.TestSinkForTesting` with log assertions on the final summary lines

## Test Plan

Recommended new test file:

- `Source/Parsek.Tests/KerbalLoadDiagnosticsTests.cs`

Core test cases:

1. Stand-in row remap on load emits a summary containing the repaired recording count and remap
   count.
2. Tourist rows dropped during migration increment the tourist-skip counter and appear in the load
   summary.
3. Legacy `CREW_REPLACEMENTS` load emits the correct source summary without falsely claiming slot
   repairs that did not happen.
4. Slot-chain extension / normalization that happens during recalculation is distinguishable from
   the raw persisted slot-load summary.
5. Retired stand-in recreation and unused stand-in deletion both appear in the final load summary.
6. Clean load emits no extra kerbal-repair summary line.

Secondary log assertions can stay in the existing module test files if that is more convenient, but
the new file should own the cross-step summary behavior.

## Recommended File Touches

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
- `Source/Parsek/KerbalsModule.cs`
- `Source/Parsek/CrewReservationManager.cs`
- `Source/Parsek.Tests/LedgerOrchestratorTests.cs`
- `Source/Parsek.Tests/KerbalReservationTests.cs`
- `Source/Parsek.Tests/RecordingStoreTests.cs`
- new `Source/Parsek.Tests/KerbalLoadDiagnosticsTests.cs`

## Open Question

The main implementation choices are:

- whether the final summary object is owned by `ParsekScenario.OnLoad(...)` or assembled from
  smaller summaries later
- whether the final summary lines use the existing module tags or a dedicated `KerbalLoad` tag

Recommendation: use a dedicated `KerbalLoad` tag for the final one-shot summary lines, while
leaving the lower-level module logs in their existing tags. That keeps the new diagnostics easy to
grep without reworking the rest of the logging style.
