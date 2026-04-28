# LedgerOrchestrator Load Migration Refactor Plan

Date: 2026-04-28

Status: implemented in `investigate-ledger-migration-refactor` after syncing to `origin/main` at `d8db60c3`.

## Goal

Split legacy save migration, load repair, broken-ledger recovery, and tightly related helpers out of
`Source/Parsek/GameActions/LedgerOrchestrator.cs` without changing behavior.

This is a zero-logic refactor. It must preserve save compatibility and the exact runtime behavior of old
saves, repaired loads, broken-ledger recovery, and migration diagnostics.

## Hard Boundaries

- Do not change save schema.
- Do not change migration predicates.
- Do not change event/action synthesis order.
- Do not change ledger mutation order.
- Do not change log text or log tags.
- Do not change compatibility behavior for old saves, partial/broken ledger files, or recovery dedup keys.
- Do not move unrelated commit, recalculation, KSC spending, vessel recovery pairing, or module patch logic.

## Current Load Order To Preserve

`LedgerOrchestrator.OnKspLoad` must remain the lifecycle entry point and preserve this sequence:

1. `Initialize()`
2. Reset `migrateOldSaveEventsRanThisLoad = false`
3. `consumedRecoveryEventKeys.Clear()`
4. `FlushStalePendingRecoveryFunds("KSP load")`
5. `Ledger.Reconcile(validRecordingIds, maxUT)`
6. `RepairMissingRecoveryDedupKeys()`
7. `MigrateOldSaveEvents(validRecordingIds)` only when the ledger is empty and committed recording ids exist
8. `MigrateKerbalAssignments()`
9. `MigrateLegacyTreeResources()`
10. `TryRecoverBrokenLedgerOnLoad()` inside the existing warning-only catch
11. `RecalculateAndPatch()`
12. `KerbalLoadRepairDiagnostics.EmitAndReset()`

Important: do not collapse steps 2-4 into generic queue clearing. `pendingRecoveryFunds` must still be
flushed with diagnostics before it is dropped.

## Proposed Shape

Add an internal static helper such as:

`Source/Parsek/GameActions/LedgerLoadMigration.cs`

`LedgerOrchestrator` remains the externally visible coordinator. Existing internal/test-facing APIs should
remain on `LedgerOrchestrator` as exact forwarding wrappers unless a separate test/API migration is approved.

The helper must preserve the current log tags. Promote `LedgerOrchestrator.Tag` from `private const` to
`internal const` and reference `LedgerOrchestrator.Tag` from the helper so there is still one source of truth
while preserving byte-for-byte log output. Move `LegacyFormatGateTag` with the legacy-tree helper because it
is only used by `WarnLegacyFormatGate`. Do not introduce a `"LedgerLoadMigration"` log tag for these paths.

## Safe Move Set

### Old-save event migration

Move as one unit:

- `migrateOldSaveEventsRanThisLoad`
- `ResetMigrateOldSaveEventsForLoad`
- `ResetMigrateOldSaveEventsForTesting`
- `SetMigrateOldSaveEventsRanThisLoadForTesting`
- `GetMigrateOldSaveEventsRanThisLoadForTesting`
- `MigrateOldSaveEvents`

Why safe:

The old-save event migration only reads `GameStateStore.Events`, converts through
`GameStateEventConverter.ConvertEvents`, mutates `Ledger`, and logs. Its only subtle state is the
per-load flag. That flag must move with both the producer (`MigrateOldSaveEvents`) and the consumer
(`HasAnyLedgerCoverage`) or remain shared through exact accessors.

Risk to avoid:

Do not split `MigrateOldSaveEvents` and `HasAnyLedgerCoverage` across separate state owners. The flag is the
guard that prevents both first-load double credit and later-load silent residual loss from null-tagged
recovery actions.

Two current resets cross this ownership boundary and must be preserved explicitly:

- `LedgerOrchestrator.OnKspLoad` resets the flag at the start of every KSP load.
- `LedgerOrchestrator.ResetForTesting` resets the flag during test cleanup.

If the flag moves to the helper, add dedicated helper APIs and call them from those two sites:

- `LedgerLoadMigration.ResetMigrateOldSaveEventsForLoad()`
- `LedgerLoadMigration.ResetMigrateOldSaveEventsForTesting()`

Do not reuse the test setter as the production load reset API.

### Legacy tree resource migration

Move as one unit with the old-save flag state:

- `MigrateLegacyTreeResources`
- `LegacyMigrationFundsTolerance`
- `LegacyMigrationScienceTolerance`
- `LegacyMigrationReputationTolerance`
- `LegacyFormatGateTag`
- `TryInjectLegacyFundsEarning`
- `InjectLegacyScienceEarning`
- `InjectLegacyScienceSpending`
- `InjectLegacyReputationEarning`
- `InjectLegacyReputationPenalty`
- `FindLegacyMigrationSyntheticMatches`
- `WarnLegacyFormatGate`
- `ComputeTreeStartUT`
- `IsResourceImpactingAction`
- `HasAnyLedgerCoverage`
- `IsMatchingLegacyFundsMigration`
- `IsMatchingLegacyScienceMigration`
- `IsMatchingLegacyReputationMigration`

Why safe:

This block is internally cohesive. It depends on `RecordingStore`, `RecordingTree`, `Ledger`, `GameAction`,
`ParsekLog`, `CultureInfo`, and the old-save migration flag. It does not depend on initialized module
instances or recalculation internals.

What must remain visible on `LedgerOrchestrator`:

- `MigrateLegacyTreeResources`
- `IsResourceImpactingAction`

These are current internal/test entry points and should forward to the helper.

### Legacy part-purchase load repair

Move as one unit:

- `RepairLegacyPartPurchaseActionsOnLoad`
- `IsPartPurchaseFundsSpendingAction`
- `TryResolveCanonicalPartPurchaseChargeForAction`
- `TryFindMatchingPartPurchasedEvent`

Why safe:

The repair operates on provided `GameStateEvent` and `GameAction` lists and uses `GameStateStore` helpers to
canonicalize legacy part-purchase semantics. It mutates only the passed ledger action rows.

Dependency to preserve:

`TryFindMatchingPartPurchasedEvent` currently uses `KscReconcileEpsilonSeconds`. Keep the compatibility
facade on `LedgerOrchestrator` and have the helper reference
`LedgerOrchestrator.KscReconcileEpsilonSeconds` directly. Do not parameter-thread this value and do not move
the broader KSC reconciler surface in this refactor.

What must remain visible on `LedgerOrchestrator`:

- `RepairLegacyPartPurchaseActionsOnLoad`

### Broken-ledger recovery

Move as one unit:

- `TryRecoverBrokenLedgerOnLoad`
- `IsRecoverableEventType`
- `LedgerHasMatchingAction`
- `LedgerHasMatchingScienceEarning`
- `GetLedgerScienceEarningTotal`
- `MapEventTypeToActionType`

Why safe:

This recovery block reads `GameStateStore`, checks current-timeline visibility, checks existing `Ledger`
actions, synthesizes missing actions through `GameStateEventConverter` or direct `GameAction` construction,
and logs. It is already explicitly load-recovery scoped and does not depend on module state beyond the
existing `Initialize()` call at entry.

Dependency to preserve:

`TryRecoverBrokenLedgerOnLoad` currently calls `Initialize()` before recovery. After the move, the helper must
still call `LedgerOrchestrator.Initialize()` at the same point. Keep `Initialize` reachable from the helper
inside the assembly.

What must remain visible on `LedgerOrchestrator`:

- `TryRecoverBrokenLedgerOnLoad`
- `IsRecoverableEventType`
- `LedgerHasMatchingScienceEarning`
- `MapEventTypeToActionType`

These are current internal/test entry points and should forward to the helper.

## Keep In LedgerOrchestrator

- `OnLoad`
- `OnKspLoad`
- `Initialize`
- module fields and properties
- seed flags
- `RecalculateAndPatch` and post-rewind variants
- `MigrateKerbalAssignments`
- `KerbalAssignmentRepairRename`
- `KerbalAssignmentEndStateRewrite`
- `KerbalAssignmentRepairStats`
- `ClassifyKerbalAssignmentRepair`
- `FindRepairRowMatch`
- `KerbalAssignmentActionsMatch`
- `RepairMissingRecoveryDedupKeys`
- `TryFindLegacyRecoveryDedupKey`
- vessel recovery pairing state and helpers, including `consumedRecoveryEventKeys`, `pendingRecoveryFunds`,
  `BuildRecoveryEventDedupKey`, `VesselRecoveryReasonKey`, and `VesselRecoveryEventEpsilonSeconds`
- `KscReconcileEpsilonSeconds` compatibility facade

Why:

`MigrateKerbalAssignments` is load repair, but it is coupled to crew action creation, crew end-state
population, diagnostics, and existing commit-path helpers. Moving it in the first pass would broaden the
refactor beyond legacy migration/load-repair isolation.

`RepairMissingRecoveryDedupKeys` is load repair, but it is coupled to vessel recovery replay dedup behavior
and recovery event fingerprints. Keep it with the recovery pairing subsystem unless that subsystem is
extracted separately.

## Required Wrappers

Keep these exact existing `LedgerOrchestrator` methods as wrappers to avoid changing the internal test
surface:

- `SetMigrateOldSaveEventsRanThisLoadForTesting`
- `GetMigrateOldSaveEventsRanThisLoadForTesting`
- `MigrateLegacyTreeResources`
- `IsResourceImpactingAction`
- `RepairLegacyPartPurchaseActionsOnLoad`
- `TryRecoverBrokenLedgerOnLoad`
- `IsRecoverableEventType`
- `MapEventTypeToActionType`

Also add two new internal reset wrappers on `LedgerOrchestrator`, both delegating to the helper:

- `ResetMigrateOldSaveEventsForLoad`
- `ResetMigrateOldSaveEventsForTesting`

If additional tests directly call private methods via reflection, either preserve the reflected method name
on `LedgerOrchestrator` or update those tests in the same refactor with no behavior change.

`LedgerHasMatchingScienceEarning` is safe to wrap as a defensive handle if desired, but it is not currently a
direct test entry point. Do not justify that wrapper as required by existing tests unless a new caller is
added.

## Test Coverage To Preserve

Existing coverage that should remain green:

- `LedgerRecoveryMigrationTests`
  - broken-ledger recovery no-op, synthesis, idempotence, current-timeline filtering, existing-action
    deduplication, partial science top-up, recoverable event predicates, event/action mapping
- `LegacyTreeMigrationTests`
  - zero coverage migration, partial coverage skip, degraded tree skip, empty root warning, residual
    tolerance, negative resource synthetics, duplicate synthetic guards, null-tagged coverage flag,
    coverage action classifier exhaustiveness, load-order regression around kerbal backfill
- `LegacyPartPurchaseLoadCompatibilityTests`
  - old part-purchase event/action repair, zero-cost bypass shape, ambiguous event preservation
- `GameStateRecorderLedgerTests`
  - legacy recovery action missing dedup key repair and large float rounding tolerance
- `KerbalLoadPipelineTests`
  - cold-start kerbal assignment repair behavior, because `MigrateKerbalAssignments` remains in the
    load sequence
- `LegacyTreeReconciliationRepro438Tests`
  - `OnKspLoad` migration/reconciliation integration for legacy tree residuals
- `PhaseFLoadMigrationInteropTests`
  - 0.7 format legacy save load, format-gate warnings, no double credit on second load
- `NoLumpSumRegressionTests`
  - no return to tree lump-sum resource application

## New Test To Add Before Moving Code

Add one pipeline-level `OnKspLoad` guard before any extraction. It should pass on pre-refactor `origin/main`,
then run again after each move step. This test covers the exact ownership boundary the refactor touches:

Scenario:

1. Ledger starts empty.
2. At least one committed recording id exists.
3. `GameStateStore.Events` contains a convertible old-save resource-impacting event whose converted action
   will be null-tagged.
4. A committed legacy tree has a non-zero residual and a UT window containing that converted action.
5. Call `LedgerOrchestrator.OnKspLoad(validRecordingIds, maxUT)`.

Assertions:

- `MigrateOldSaveEvents` synthesizes the null-tagged action.
- The per-load flag is true while `MigrateLegacyTreeResources` runs.
- The legacy tree residual is not additionally injected because the first-load null-tagged action counts as
  coverage.
- A second load resets the flag and does not let prior persisted null-tagged recovery/KSC actions count as
  coverage for a different legacy tree.
- Existing log text remains unchanged.

This test closes the gap between the isolated flag tests and the lifecycle ordering this refactor could
accidentally break.

## Implementation Steps

1. Add the pipeline-level `OnKspLoad` test and verify it passes on pre-refactor HEAD.
2. Add the new helper file with copied code only. Preserve log tags through `LedgerOrchestrator.Tag` and the
   moved `LegacyFormatGateTag`.
3. Move old-save event migration and legacy tree migration together with the shared flag state.
4. Add `LedgerOrchestrator` forwarding wrappers for every existing internal/test-facing API and the two new
   load/test reset helpers.
5. Move legacy part-purchase load repair and preserve the `KscReconcileEpsilonSeconds` dependency through the
   existing facade.
6. Move broken-ledger recovery and preserve existing wrappers.
7. Leave `OnLoad`, `OnKspLoad`, `MigrateKerbalAssignments`, and recovery dedup-key repair in
   `LedgerOrchestrator`.
8. Run focused tests:
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter LedgerRecoveryMigrationTests`
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter LegacyTreeMigrationTests`
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter LegacyPartPurchaseLoadCompatibilityTests`
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter GameStateRecorderLedgerTests`
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter PhaseFLoadMigrationInteropTests`
9. Run the full headless suite:
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`

## Review Checklist

- `git diff --word-diff` shows only code movement, wrapper calls, and the new test.
- `OnKspLoad` statement order is unchanged.
- `OnLoad` ledger load and part-purchase repair ordering is unchanged.
- All moved log strings and tags are byte-for-byte identical.
- `migrateOldSaveEventsRanThisLoad` is reset once per load and set only when old-save event migration
  actually synthesizes actions.
- Legacy migration synthetic action ordering remains funds, science, reputation.
- `Ledger.AddAction`, `Ledger.AddActions`, and `Ledger.ReplaceActionsForRecording` call order is unchanged.
- `RepairMissingRecoveryDedupKeys` remains in `LedgerOrchestrator`.
- `MigrateKerbalAssignments` remains in `LedgerOrchestrator`.
