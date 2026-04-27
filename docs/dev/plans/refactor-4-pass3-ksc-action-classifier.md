# Refactor-4 Pass 3 - KSC Action Expectation Classifier Proposal

**Date:** 2026-04-27.
**Worktree:** `Parsek-refactor-4-pass3-ksc-classifier-proposal`, branch
`refactor-4-pass3-ksc-classifier-proposal`.
**Base:** `f83744b8` (`origin/main`, after `git fetch origin main` and
fast-forward).
**Status:** Proposal plus implementation checkpoints. The classifier-only
extraction behind `LedgerOrchestrator.ClassifyAction` is complete, and the
DTO/enum type migration to `KscActionExpectationClassifier` is complete.
The KSC per-action reconciler helper extraction is also complete behind
`LedgerOrchestrator` compatibility facades.

## Goal

Create a focused extraction point for the KSC action
expectation/classification concern in `LedgerOrchestrator` without changing
production behavior. The proposed class is
`KscActionExpectationClassifier` in
`Source/Parsek/GameActions/KscActionExpectationClassifier.cs`. The first slice
is intentionally an extraction-with-back-references, not a fully independent
owner: it returns the existing nested `LedgerOrchestrator.KscActionExpectation`
type and reads one existing `LedgerOrchestrator` constant.

This is a zero-logic-changes refactor slice. The classifier's conditions,
expected resource-event keys, expected delta signs, reputation-curve leg mode,
skip reasons, and default/no-impact behavior must remain byte-for-byte
equivalent in behavior to the current `LedgerOrchestrator.ClassifyAction`
implementation.

## Current Surface

`LedgerOrchestrator.cs` is 7,137 lines on the proposal base. The KSC
classification/reconciliation region begins around line 4355 and includes:

- `KscReconcileClass`
- `KscExpectationLegMode`
- `KscExpectationLeg`
- `KscActionExpectation`
- `KscExpectedLegMatch`
- `CreateExpectationLeg`
- `ClassifyAction`
- `ReconcileKscAction`
- `ReconcileKscExpectationLeg`
- `CollectMatchingLegs`
- `AddMatchingLeg`
- `ComputeExpectedDeltaForLeg`
- `ResourceChannelTag`
- `KscReconcileEpsilonSeconds`
- `TechResearchScienceReasonKey` is not part of the region, but
  `ClassifyAction` references it at line 4490. The constant is declared on
  `LedgerOrchestrator` at line 3420, so the first extraction must read it as
  `LedgerOrchestrator.TechResearchScienceReasonKey`.

Only the expectation/classification concern is a good Pass 3 slice. The
reconciliation concern is adjacent but not part of the first move:

- `OnKscSpending` writes the KSC action and calls `ReconcileKscAction`.
- `OnVesselRolloutSpending` writes the rollout action and calls
  `ReconcileKscAction`.
- `ReconcileKscAction` calls `ClassifyAction`, logs transformed-action skips,
  and dispatches each present leg to `ReconcileKscExpectationLeg`.
- `CollectMatchingLegs` calls `ClassifyAction` again to aggregate coalesced
  same-window ledger actions for comparison.
- `ComputeExpectedDeltaForLeg` is tested through reflection for the
  reputation-curve same-UT sequence tiebreaker.

Current direct tests call `LedgerOrchestrator.ClassifyAction` and compare
against `LedgerOrchestrator.KscReconcileClass` /
`LedgerOrchestrator.KscExpectationLegMode`. Some reconciliation tests also
reflect nested private KSC helper types on `LedgerOrchestrator`.

## Proposed Extraction Class

Add an internal static class:

```csharp
namespace Parsek
{
    internal static class KscActionExpectationClassifier
    {
        internal static LedgerOrchestrator.KscActionExpectation ClassifyAction(GameAction action);
    }
}
```

The first implementation slice should move only the pure construction and type
classification body:

- `CreateExpectationLeg`
- the body of `ClassifyAction`

`LedgerOrchestrator.ClassifyAction(GameAction)` stays as a stable wrapper that
delegates to `KscActionExpectationClassifier.ClassifyAction(action)`.

This first slice still depends on `LedgerOrchestrator` for the nested return
and leg types plus `TechResearchScienceReasonKey`. That asymmetry is deliberate
so PR B can be reviewed as a moved method body in a new file while test-facing
type names stay stable.

The full target shape, after a later approved cleanup, is:

- `KscReconcileClass`
- `KscExpectationLegMode`
- `KscExpectationLeg`
- `KscActionExpectation`
- `CreateExpectationLeg`
- `ClassifyAction`

Do not move the four DTO/enum types in the first implementation slice. C# does
not provide nested type forwarding, and existing tests/callers use the nested
`LedgerOrchestrator.Ksc*` type names directly. Moving those types in the first
slice would force test/caller migration or duplicate facade structs, neither of
which improves the safety of the zero-logic extraction.

## Policy Stays Out

The new classifier class must stay pure:

- no ledger mutation;
- no `GameStateStore` reads;
- no `Ledger.Actions` reads;
- no logging;
- no resource-tracker availability checks;
- no event-window matching;
- no reconciliation summary or warning policy;
- no KSP state patching or recalc calls.

The classifier may only inspect the supplied `GameAction`, read
`LedgerOrchestrator.TechResearchScienceReasonKey`, and return the same
expectation data the current method returns.

## Do Not Move

The first implementation slice must not move or change:

- `ReconcileKscAction`
- `ReconcileKscExpectationLeg`
- `KscExpectedLegMatch`
- `CollectMatchingLegs`
- `AddMatchingLeg`
- `ComputeExpectedDeltaForLeg`
- `ResourceChannelTag`
- `KscReconcileEpsilonSeconds`
- post-walk reconciliation (`ClassifyPostWalk`, `ReconcilePostWalk`, and
  helper methods)
- legacy migration/load repair
- ledger mutation
- logging policy
- resource/currency mutation order
- any classification condition or return shape

The implementation must also leave `OnKscSpending` and
`OnVesselRolloutSpending` call order unchanged: write action, log write,
reconcile, recalculate/patch.

## Wrapper And Call-Site Strategy

First implementation slice:

- Keep `LedgerOrchestrator.ClassifyAction` with the same signature and return
  type.
- Keep nested `LedgerOrchestrator.KscReconcileClass`,
  `LedgerOrchestrator.KscExpectationLegMode`,
  `LedgerOrchestrator.KscExpectationLeg`, and
  `LedgerOrchestrator.KscActionExpectation` definitions in place.
- Have `LedgerOrchestrator.ReconcileKscAction` and `CollectMatchingLegs`
  continue to call `ClassifyAction` by the wrapper name.
- Both internal callers, `ReconcileKscAction` at line 4679 and
  `CollectMatchingLegs` at line 4812, should resolve to the
  `LedgerOrchestrator.ClassifyAction` wrapper without source changes.
- Do not migrate production callers, tests, or reflection helpers to
  `KscActionExpectationClassifier` in this slice.

Optional later cleanup, only after separate approval:

- Move the DTO/enum types to the classifier class.
- Migrate direct tests and any production call sites that want the new class
  name.
- Delete the nested compatibility facade only after grep confirms no direct
  `LedgerOrchestrator.Ksc*` references remain.
- Also manually scan for reflection-based references such as
  `typeof(LedgerOrchestrator).GetNestedType(...)` and
  `GetMethod(..., BindingFlags.NonPublic)` before deleting nested compatibility
  types or moving private KSC helpers. Grep for direct type names will not catch
  every string-based reflection dependency.

## Validation Plan

For an approved implementation slice, run a build first:

```powershell
dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo
```

Focused ledger/career tests:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~StrategyCaptureTests|FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~FullCareerTimelineTests|FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests"
```

Full non-injection xUnit gate:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter FullyQualifiedName!~InjectAllRecordings
```

No in-game canary is required for these zero-logic refactor slices because they
move already-covered, parameterized helper code behind the existing
`LedgerOrchestrator` facades. If a future slice changes reconciliation policy,
event matching, logging text, mutation order, or post-walk behavior, that
future proposal needs a fresh validation plan before implementation.

## Rollback And PR Granularity

- PR A: this proposal only.
- Planned PR B: classifier-only extraction behind
  `LedgerOrchestrator.ClassifyAction`.
- Planned PR C: optional DTO/enum cleanup and caller/test migration.
- Actual PR #621 landing shape: PR B, PR C, and the follow-up KSC action
  reconciler helper extraction are folded together after review. See the
  implementation checkpoints below for the exact completed slices.

Rollback for the combined PR #621 shape is still source-only, but it must revert
all completed slices: delete `KscActionExpectationClassifier.cs` and
`KscActionReconciler.cs`; restore the classifier body, the four `Ksc*` DTO/enum
types, `KscExpectedLegMatch`, `ReconcileKscAction`,
`ReconcileKscExpectationLeg`, `CollectMatchingLegs`, `AddMatchingLeg`,
`ComputeExpectedDeltaForLeg`, `ResourceChannelTag`, and
`KscReconcileEpsilonSeconds` inside `LedgerOrchestrator`; and revert the direct
test callers, production XML doc references, and `EarningsReconciliationTests`
reflection helpers back to `LedgerOrchestrator.Ksc*` /
`typeof(LedgerOrchestrator)`. Because no schemas, persisted fields, ledger
mutation paths, or runtime state contracts change, no save migration or cleanup
is needed.

Post-walk reconciliation, KSC event entry points, ledger writes, sequence
assignment, legacy migration, and resource/currency mutation order remain out
of scope for PR #621.

## What Remains In LedgerOrchestrator

After the completed PR #621 slices, `LedgerOrchestrator` still owns:

- KSC event/action entry points (`OnKscSpending`,
  `OnVesselRolloutSpending`);
- ledger action insertion and sequence assignment;
- KSC classifier/reconciler compatibility facades
  (`ClassifyAction`, `ReconcileKscAction`, `ResourceChannelTag`,
  `KscReconcileEpsilonSeconds`);
- post-walk reconciliation;
- recalculate/patch orchestration;
- legacy migration/load repair and ledger mutation.

The classifier body, KSC DTO/enum types, and the per-action KSC reconciliation
helper body now live in dedicated KSC action helper classes. The extracted
reconciler preserves the original `LedgerOrchestrator` log tag string so log
output stays byte-stable.

## Zero-Logic-Changes Statement

The approved implementation must be a mechanical extraction move only. It must not
change any `switch` case, predicate, event key, expected delta sign, skip reason,
leg count, tolerance, logging text, rate-limit key, call order, resource
mutation order, or public/internal test-facing wrapper signature.

The implementation diff should be reviewable as the same classifier and KSC
per-action reconciliation helper bodies in new files, with existing
`LedgerOrchestrator` wrappers delegating to them.

## Approval Resolution

The original PR B classifier slice and PR C DTO/enum migration were folded into
PR #621 after review. After that review was green, the follow-up KSC
per-action reconciler helper extraction was approved to build on the same open
PR rather than waiting for merge.

## Implementation Checkpoint - Classifier-Only Extraction

Approved PR B slice completed in `refactor-4-pass3-ksc-classifier`, based on
`30183aa1`:

- Added `Source/Parsek/GameActions/KscActionExpectationClassifier.cs`.
- Moved only `CreateExpectationLeg` and the `ClassifyAction` body into the new
  classifier class.
- Kept `LedgerOrchestrator.ClassifyAction` as the stable wrapper.
- Kept nested `LedgerOrchestrator.Ksc*` DTO/enum types in place.
- Kept `ReconcileKscAction`, `ReconcileKscExpectationLeg`,
  `KscExpectedLegMatch`, `CollectMatchingLegs`, `AddMatchingLeg`,
  `ComputeExpectedDeltaForLeg`, `ResourceChannelTag`,
  `KscReconcileEpsilonSeconds`, post-walk reconciliation, legacy migration,
  ledger mutation, logging policy, and resource/currency mutation order outside
  the slice.

Validation completed:

```powershell
dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~StrategyCaptureTests|FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~FullCareerTimelineTests|FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter FullyQualifiedName!~InjectAllRecordings
```

Latest focused run passed 326 tests; the non-injection gate passed 9,261 tests.

## Implementation Checkpoint - DTO/Enum Type Migration

Follow-up slice completed on top of PR #621:

- Moved `KscReconcileClass`, `KscExpectationLegMode`,
  `KscExpectationLeg`, and `KscActionExpectation` from
  `LedgerOrchestrator` to `KscActionExpectationClassifier`.
- Migrated direct production XML doc references and direct unit-test
  assertions from `LedgerOrchestrator.Ksc*` to
  `KscActionExpectationClassifier.Ksc*`.
- Migrated the `EarningsReconciliationTests` reflection helper for
  `KscExpectationLeg` and `KscExpectationLegMode` to
  `typeof(KscActionExpectationClassifier)`.
- Kept `KscExpectedLegMatch`, `CollectMatchingLegs`, `AddMatchingLeg`,
  `ComputeExpectedDeltaForLeg`, `ReconcileKscAction`,
  `ReconcileKscExpectationLeg`, post-walk reconciliation, legacy migration,
  ledger mutation, logging policy, and resource/currency mutation order in
  `LedgerOrchestrator`.

Validation completed:

```powershell
dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~StrategyCaptureTests|FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~FullCareerTimelineTests|FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter FullyQualifiedName!~InjectAllRecordings
```

Latest focused run passed 326 tests; the non-injection gate passed 9,261 tests.

## Implementation Checkpoint - KSC Action Reconciler Extraction

Follow-up slice completed on top of PR #621 after the classifier/DTO review was
green and continuation on the open PR was approved:

- Added `Source/Parsek/GameActions/KscActionReconciler.cs`.
- Moved `KscExpectedLegMatch`, the `ReconcileKscAction` body,
  `ReconcileKscExpectationLeg`, `CollectMatchingLegs`, `AddMatchingLeg`,
  `ComputeExpectedDeltaForLeg`, the `ResourceChannelTag` switch, and the
  canonical `KscReconcileEpsilonSeconds` constant source into the new class.
- Kept `LedgerOrchestrator.ReconcileKscAction`,
  `LedgerOrchestrator.ResourceChannelTag`, and
  `LedgerOrchestrator.KscReconcileEpsilonSeconds` as compatibility facades for
  existing production and test call sites.
- Kept `OnKscSpending`, `OnVesselRolloutSpending`, ledger action insertion,
  sequence assignment, post-walk reconciliation, legacy migration, ledger
  mutation, and resource/currency mutation order in `LedgerOrchestrator`.
- Preserved the existing reconciliation logging policy and text inside the
  extracted helper; only the helper's file/class owner changed.
- Migrated the `EarningsReconciliationTests` reflection hooks for
  `KscExpectedLegMatch` and `ComputeExpectedDeltaForLeg` to
  `typeof(KscActionReconciler)`.
- Preserved the original reconciliation log tag string (`LedgerOrchestrator`)
  in the extracted class so emitted log lines do not change.

Validation completed:

```powershell
dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~StrategyCaptureTests|FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~FullCareerTimelineTests|FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests" --nologo
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName!~InjectAllRecordings" --nologo
```

Latest focused run passed 326 tests; the non-injection gate passed 9,261 tests.
