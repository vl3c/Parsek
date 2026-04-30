# Refactor-4 Pass 3 - Post-Walk Action Reconciler Proposal

**Date:** 2026-04-28.
**Worktree:** `Parsek-proposal-postwalk-reconciler`, branch
`proposal-postwalk-reconciler`.
**Base:** `afdb7046` (`origin/main`, after `git fetch origin main`).
**Status:** Implemented and archived. The `PostWalkActionReconciler` extraction
landed with zero intended production logic changes; this document is retained as
the proposal, boundary, and validation record.

## Goal

Create a focused internal owner for the post-walk reconciliation cluster in
`Source/Parsek/GameActions/LedgerOrchestrator.cs` without changing production
behavior. The proposed owner is `PostWalkActionReconciler` in
`Source/Parsek/GameActions/PostWalkActionReconciler.cs`.

This is a zero-production-logic refactor. The extracted code must preserve the
current classification rules, event-key matching, aggregation windows,
tolerances, live-coverage skip policy, log text, log tag, rate-limit keys,
resource/currency mutation order, ledger mutation order, KSP patch order, and
recalculation call order.

## Current Surface

On this base, `LedgerOrchestrator.cs` is 6,596 lines. The post-walk cluster is
the contiguous region at `LedgerOrchestrator.cs:4386-5825`, plus the production
call site at `LedgerOrchestrator.cs:1558`.

Current post-walk-owned symbols:

- `PostWalkReconcileEpsilonSeconds`
- `PostWalkAggregateContributionEpsilon`
- `PostWalkLeg`
- `PostWalkExpectation`
- `ClassifyPostWalk`
- `ReconcilePostWalk`
- `IsOutsidePostWalkLiveCoverage`
- `LogPostWalkLiveCoverageSkip`
- `TryGetPostWalkSourceAnchor`
- `HasLivePostWalkSourceAnchor`
- `HasLivePostWalkObservedEvent`
- `HasLivePostWalkObservedEventForLeg`
- `IsLivePostWalkObservedEvent`
- `HasAmbiguousLiveCoverageOverlap`
- `PostWalkCompareResult`
- `PostWalkWindowAggregate`
- `CompareLeg`
- `GetPostWalkObservedWindow`
- `GetPostWalkObservedDisplayWindow`
- `TryGetScienceReconcileWindow`
- `GetScienceReconcileAnchorTolerance`
- `GetScienceReconcileBoundaryPadding`
- `AggregatePostWalkWindow`
- `PostWalkEventMatchesAction`
- `FormatPostWalkObservedWindowLabel`
- `LogSciencePostWalkReconcileDumpOnce`
- `PostWalkActionsShareScope`
- `ActionHasRecordingScope`
- `AccumulateMatchingPostWalkLeg`
- `FormatPostWalkContributorLabel`
- `HasTrackedPostWalkLeg`
- `ActionIdForPostWalk`

Adjacent shared helpers are not post-walk-only today:

- `GetResourceTrackingAvailability` is used by commit-window reconciliation and
  post-walk reconciliation.
- `LogReconcileSkippedOnce` is used by commit-window reconciliation and
  post-walk reconciliation.
- `LogReconcileWarnOnce` is used by commit-window reconciliation and post-walk
  reconciliation.
- `EventMatchesRecordingScope` is used by commit-window reconciliation and is
  mirrored in `GameStateEventConverter`.
- `DoesScienceEventMatchActionScope`, `GetScienceChangedReasonKey`,
  `FormatScienceEventForReconcileDump`, `FormatFixed1`, and `FormatFixed3`
  are shared with the commit-window reconciliation path.
- `TryGetPersistedScienceActionWindow`, `GetCollapsedScienceWindowStart`, and
  `GetScienceReconcileCollapsedHalfWidth` are shared with commit-window
  science reconciliation and must not become owned by the post-walk class.
- `FindRecordingById` is called by `TryGetScienceReconcileWindow`; it is already
  `internal static`, so the extracted owner can call it without an access change.
- `emittedReconcileWarnKeys`, `emittedScienceReconcileDumpKeys`,
  `OneShotReconcileSkipLogIntervalSeconds`, and tracker override fields are
  owned by `LedgerOrchestrator` reset/test lifecycle.

The only production call site is inside `RecalculateAndPatchCore`:

```csharp
RecalculationEngine.Recalculate(actions, utCutoff);
ReconcilePostWalk(GameStateStore.Events, actions, utCutoff);
```

That call currently runs after derived fields are populated and before any KSP
patch/defer branch. That placement must remain unchanged.

## Proposed Owner

Add an internal static owner:

```csharp
namespace Parsek
{
    internal static class PostWalkActionReconciler
    {
        internal const double PostWalkReconcileEpsilonSeconds = 0.1;
        private const double PostWalkAggregateContributionEpsilon = 1e-6;

        internal struct PostWalkLeg { ... }
        internal struct PostWalkExpectation { ... }

        internal static PostWalkExpectation ClassifyPostWalk(GameAction action);

        internal static void ReconcilePostWalk(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff);
    }
}
```

Use `PostWalkActionReconciler`, not `PostWalkExpectationClassifier`, for the
first owner because the useful extraction boundary is the full log-only
reconciliation concern: classification, live coverage filtering, event matching,
coalesced aggregation, comparison, and post-walk diagnostics. A classifier-only
owner would move the smallest pure method, but it would leave the more complex
and more cohesive reconciliation policy in `LedgerOrchestrator`.

Move these symbols to `PostWalkActionReconciler` in one reviewed slice:

- `PostWalkReconcileEpsilonSeconds`
- `PostWalkAggregateContributionEpsilon`
- `PostWalkLeg`
- `PostWalkExpectation`
- `ClassifyPostWalk`
- `ReconcilePostWalk`
- all post-walk-specific helpers listed in the current surface section, except
  shared science-window helpers explicitly called out below

Keep the extracted class in namespace `Parsek`, matching the existing
`KscActionReconciler` and `KscActionExpectationClassifier` pattern.

The extracted reconciler must preserve:

- the original log tag string: use `private const string Tag =
  "LedgerOrchestrator"` inside the new class, or call a `LedgerOrchestrator`
  logging facade that uses the same tag;
- `fundsTol = 1.0`, `repTol = 0.1`, and `sciTol = 0.1`;
- `PostWalkReconcileEpsilonSeconds = 0.1`;
- `PostWalkAggregateContributionEpsilon = 1e-6`;
- every WARN/VERBOSE/INFO/ERROR message body and rate-limit key;
- all `CultureInfo.InvariantCulture` formatting.

## Shared Helper Strategy

Do not move the shared commit-window helpers as part of this extraction. They
are outside the post-walk owner boundary and moving them would broaden the
review surface.

For the first implementation slice, prefer narrow compatibility methods on
`LedgerOrchestrator` only where the new owner needs existing shared behavior:

- resource tracking availability;
- one-shot reconcile skip logging;
- WARN emission and warning de-duplication;
- science event scope matching;
- science reason-key selection;
- persisted science action-window lookup;
- collapsed science-window reconstruction;
- science dump line formatting;
- fixed-number formatting;
- access to the existing reconciliation warning de-duplication set;
- access to the existing science dump de-duplication set.

For this slice, flip access from `private` to `internal` for the listed shared
helpers when the existing signature is already the right shape; introduce a new
internal wrapper only when the existing helper or state access is too wide for
the new owner.

Those facades should be mechanical access changes only. They must not alter
conditions, log strings, keys, thresholds, or reset behavior. If a facade feels
too broad, keep that specific helper in `LedgerOrchestrator` and let the wrapper
method own the call until a later shared-reconcile utility is proposed.

In particular, keep `TryGetPersistedScienceActionWindow`,
`GetCollapsedScienceWindowStart`, and `GetScienceReconcileCollapsedHalfWidth`
in `LedgerOrchestrator` for the first slice. They are used by both
commit-window reconciliation and post-walk reconciliation; moving them under
`PostWalkActionReconciler` would either cross the explicit commit-window
boundary or force commit-window code to depend on the post-walk owner. The new
owner may call a narrow `LedgerOrchestrator` facade for persisted science
windows.

Do not expose the de-duplication sets themselves. If the extracted
`LogSciencePostWalkReconcileDumpOnce` needs the current
`emittedScienceReconcileDumpKeys.Add("postwalk-dump:" + ...)` behavior, add a
narrow helper such as `LedgerOrchestrator.TryRegisterPostWalkDumpKey(string
dumpKey)` that returns the `Add` result. Post-walk warning de-duplication should
continue to go through `LogReconcileWarnOnce`; grep should confirm the extracted
code does not touch `emittedReconcileWarnKeys` directly.

Do not introduce a general-purpose reconciliation utility in this slice. That
would mix commit-window and post-walk ownership and make a zero-logic review
harder than a direct move.

## Wrappers And Call-Site Strategy

Keep stable `LedgerOrchestrator` wrappers:

```csharp
// Const compatibility facade: downstream assemblies must be rebuilt if this
// value ever changes because C# const references are inlined.
internal const double PostWalkReconcileEpsilonSeconds =
    PostWalkActionReconciler.PostWalkReconcileEpsilonSeconds;

internal static PostWalkActionReconciler.PostWalkExpectation ClassifyPostWalk(
    GameAction action)
    => PostWalkActionReconciler.ClassifyPostWalk(action);

internal static void ReconcilePostWalk(
    IReadOnlyList<GameStateEvent> events,
    IReadOnlyList<GameAction> actions,
    double? utCutoff)
    => PostWalkActionReconciler.ReconcilePostWalk(events, actions, utCutoff);
```

Production should keep the existing source-level call in `RecalculateAndPatchCore`
as `ReconcilePostWalk(GameStateStore.Events, actions, utCutoff)` so the call
order diff stays minimal. The wrapper then delegates to the new owner.

This wrapper strategy intentionally does not preserve nested DTO type identity
for `LedgerOrchestrator.PostWalkExpectation` / `LedgerOrchestrator.PostWalkLeg`
if those types move to `PostWalkActionReconciler` in the first slice. Current
test search found no direct calls to `ClassifyPostWalk` or direct references to
those nested types. If implementation finds source or reflection consumers of
the nested type names, either keep the DTO structs nested in `LedgerOrchestrator`
for the first slice, or migrate those callers/tests in the same reviewed slice
and call out the compatibility break explicitly.

Tests that call `LedgerOrchestrator.ReconcilePostWalk` should continue to
compile unchanged. There are many direct post-walk tests in
`EarningsReconciliationTests` and integration wiring checks in
`PostWalkReconciliationIntegrationTests`; keeping the wrapper avoids turning a
mechanical extraction into a test migration.

Optional later cleanup, only after separate approval:

- migrate tests that directly target the classifier to
  `PostWalkActionReconciler.ClassifyPostWalk`;
- migrate tests that directly target the reconciler to
  `PostWalkActionReconciler.ReconcilePostWalk`;
- remove `LedgerOrchestrator` wrappers only after grep and reflection scans show
  no direct or string-based dependencies remain.

## Explicit Do-Not-Move Boundaries

Do not move or change:

- ledger mutation paths;
- KSP state patching;
- `RecalculateAndPatchCore` sequencing;
- the `RecalculationEngine.Recalculate(...)` call or its position;
- the `KspStatePatcher.PatchAll(...)` branch or deferral policy;
- legacy migration/load repair;
- KSC per-action reconciliation;
- `KscActionReconciler`;
- commit-window earnings reconciliation;
- resource/currency mutation order;
- `GameStateStore` mutation;
- action insertion, sequence assignment, or deduplication;
- `OnRecordingCommitted`, `OnKscSpending`, or `OnVesselRolloutSpending` write
  order;
- test reset ownership for tracker overrides and science dump de-duplication;
- logging text, log tag, log level, or rate-limit keys.

Also do not reinterpret post-walk data:

- do not change transformed reward fields used by `ClassifyPostWalk`;
- do not change science effective/cap semantics;
- do not change reputation curve effective-value semantics;
- do not change source-anchor or observed-event fallback behavior;
- do not change coalesced-window primary owner selection;
- do not change null/recording-scoped event matching.

## Test And Reflection Impact

Known direct test surface:

- `EarningsReconciliationTests` has the main `#440 post-walk tests` region and
  many direct calls to `LedgerOrchestrator.ReconcilePostWalk`.
- `PostWalkReconciliationIntegrationTests` verifies the hook is wired through
  recalculation and validates tracker-unavailable behavior.
- Related earnings tests assert that commit-window reconciliation and post-walk
  reconciliation read the same derived fields and avoid double WARNs.

The searched test tree did not show direct calls to `ClassifyPostWalk` or direct
references to `PostWalkExpectation` / `PostWalkLeg`, but the implementation PR
must still scan for:

- `LedgerOrchestrator.ReconcilePostWalk`;
- `LedgerOrchestrator.ClassifyPostWalk`;
- `LedgerOrchestrator.PostWalk*`;
- `typeof(LedgerOrchestrator).GetNestedType(...)`;
- `GetMethod("...PostWalk...", BindingFlags.NonPublic | ...)`;
- literal log text such as `Earnings reconciliation (post-walk,`,
  `Post-walk match:`, and `Post-walk reconcile: actions=`.

Keeping `LedgerOrchestrator.ReconcilePostWalk` and
`LedgerOrchestrator.ClassifyPostWalk` wrappers should avoid direct test source
changes for the first slice. Moving private helper names can still break
reflection-based tests if any exist, so the reflection scan is mandatory before
implementation.

## Validation Plan

For an approved implementation slice, first check log-call byte-stability shape
in the extracted file:

```powershell
Select-String -Path Source/Parsek/GameActions/PostWalkActionReconciler.cs -Pattern "ParsekLog\\."
```

Every direct `ParsekLog.*` site in the new file must use the local
`private const string Tag = "LedgerOrchestrator"` constant, not `nameof(...)`
and not a different literal.

Then run a build:

```powershell
dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo
```

Run the focused post-walk and adjacent reconciliation tests:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~ReconciliationBundleTests|FullyQualifiedName~MilestoneRewardCaptureTests"
```

Run broader ledger/career coverage because the hook sits in
`RecalculateAndPatchCore`:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests|FullyQualifiedName~FullCareerTimelineTests"
```

For strict zero-logic confidence, capture representative post-walk log output
before extraction and diff it after extraction. A lightweight gate is the first
five `PostWalk_*` tests in `EarningsReconciliationTests` with
`ParsekLog.TestSinkForTesting` output saved and compared byte-for-byte; this
catches tag, level, key, and formatting drift that broad `Assert.Contains`
checks can miss.

Run the ERS/ELS audit after extraction because the new file may read state that
the audit allowlist tracks by path:

```powershell
pwsh -File scripts/grep-audit-ers-els.ps1
```

Then run the full non-injection gate:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter FullyQualifiedName!~InjectAllRecordings
```

No in-game canary is required for the zero-logic extraction because the runtime
entry point and recalculation order remain unchanged. If the implementation
changes logging policy, matching policy, mutation order, or call order, this
proposal no longer applies and the validation plan must be updated.

## Rollback Plan

Rollback is source-only:

- delete `Source/Parsek/GameActions/PostWalkActionReconciler.cs`;
- restore the moved post-walk symbols inside `LedgerOrchestrator.cs`;
- restore `LedgerOrchestrator.PostWalkReconcileEpsilonSeconds` to its literal
  constant value;
- remove any temporary `LedgerOrchestrator` compatibility facades that existed
  only for the extracted owner;
- keep production call order as
  `RecalculationEngine.Recalculate(...)` followed by
  `ReconcilePostWalk(...)`;
- rerun the focused post-walk tests.

Because this extraction does not change schemas, persisted fields, ledger
contents, or KSP state contracts, no save migration, cleanup action, or runtime
repair is needed.

## What Remains In LedgerOrchestrator

After the proposed extraction, `LedgerOrchestrator` still owns:

- lifecycle and public-ish orchestration entry points;
- ledger action insertion, mutation, sequence assignment, and purge operations;
- `RecalculateAndPatchCore` and KSP patch/defer sequencing;
- KSC event entry points and KSC per-action reconciliation facades;
- commit-window earnings reconciliation;
- legacy migration/load repair;
- save/load/reset integration;
- resource module initialization and tracker override reset lifecycle;
- compatibility wrappers for `ClassifyPostWalk`, `ReconcilePostWalk`, and
  `PostWalkReconcileEpsilonSeconds`.

These helpers stay in `LedgerOrchestrator.cs` even though they currently live
inside or adjacent to the physical post-walk region:

- `GetResourceTrackingAvailability`;
- `LogReconcileSkippedOnce`;
- `LogReconcileWarnOnce`;
- `EventMatchesRecordingScope`;
- `DoesScienceEventMatchActionScope`;
- `GetScienceChangedReasonKey`;
- `FormatScienceEventForReconcileDump`;
- `TryGetPersistedScienceActionWindow`;
- `GetCollapsedScienceWindowStart`;
- `GetScienceReconcileCollapsedHalfWidth`;
- `FormatFixed1`;
- `FormatFixed3`;
- `FindRecordingById`;
- tracker override fields;
- `emittedReconcileWarnKeys`;
- `emittedScienceReconcileDumpKeys`;
- `OneShotReconcileSkipLogIntervalSeconds`.

`PostWalkActionReconciler` owns only the log-only post-walk reconciliation
decision cluster that runs after the recalculation walk has populated derived
action fields.
