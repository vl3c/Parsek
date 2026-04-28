# LedgerOrchestrator Recovery Funds and Rollout Adoption Refactor Proposal

Status: implemented in this branch; retained as the extraction/rollback plan and hard-boundary checklist.

Source baseline: `origin/main` at `e7e731e5` (after PR #631, which extracted post-walk reconciliation into `PostWalkActionReconciler`).

Target file: `Source/Parsek/GameActions/LedgerOrchestrator.cs`.

## Goal

Reduce the size and coupling of `LedgerOrchestrator` by extracting the private logic for:

- vessel recovery funds pairing and deferred recovery requests;
- vessel rollout cost recording and later adoption by a recording;
- the small helper value types/constants that exist only to support those paths.

This is intended as a zero-logic refactor. Public/internal behavior, logs, sequence assignment, ledger write order, event matching, reconciliation, and mutation order must remain byte-for-byte equivalent where practical.

## Hard Boundaries

Do not change these areas as part of the extraction:

- KSC event matching/reconciliation: `ReconcileKscAction`, `ClassifyAction`, `KscActionExpectationClassifier`, `PostWalkActionReconciler`, post-walk facades on `LedgerOrchestrator`, and all reconciliation log text.
- Ledger write order and sequence assignment. `kscSequenceCounter++` must occur at the same decision point before each rollout/recovery action is constructed.
- Resource mutation order: `Ledger.AddAction(...)`, `ReconcileKscAction(...)`, and `RecalculateAndPatch()` must remain in the same order for rollout; `Ledger.AddAction(...)` then log then `RecalculateAndPatch()` must remain in the same order for recovery.
- KSC reconciliation, earnings-window reconciliation, post-walk reconciliation, migration/load repair, and any `OnKspLoad` behavior except replacing method bodies with wrappers.
- Logging text/tag. The tag must remain `LedgerOrchestrator`; message strings should be moved unchanged or left in wrappers.
- Existing dedup key formats:
  - recovery: `BuildRecoveryEventDedupKey(...)`;
  - rollout: `rollout:<UT>|pid=<pid>|site=<escaped>|vessel=<escaped>`, plus legacy bare `rollout:<UT>` adoption.

## Current Surface

### Recovery Funds Pairing

Constants and state:

- `VesselRecoveryEventEpsilonSeconds` at `LedgerOrchestrator.cs:3410`.
- `LegacyRecoveryActionAmountTolerance` at `LedgerOrchestrator.cs:3411`.
- `VesselRecoveryReasonKey` at `LedgerOrchestrator.cs:3420`.
- `consumedRecoveryEventKeys` at `LedgerOrchestrator.cs:3431`.
- `PendingRecoveryFundsRequest` at `LedgerOrchestrator.cs:3433`.
- `pendingRecoveryFunds` at `LedgerOrchestrator.cs:3440`.
- `PendingRecoveryFundsStaleThreshold` at `LedgerOrchestrator.cs:3451`.

Recovery helpers:

- `BuildRecoveryEventDedupKey(GameStateEvent e)` at `LedgerOrchestrator.cs:3457`.
- `TryFindRecoveryFundsEvent(...)` at `LedgerOrchestrator.cs:3476`.
- `HasRecoveryActionForDedupKey(string dedupKey)` at `LedgerOrchestrator.cs:3510`.
- `AddPendingRecoveryFundsRequest(...)` at `LedgerOrchestrator.cs:3528`.
- `PendingRecoveryFundsCountForTesting` at `LedgerOrchestrator.cs:3557`.
- `OnRecoveryFundsEventRecorded(GameStateEvent evt)` at `LedgerOrchestrator.cs:3559`.
- `FindBestPairingIndex(double eventUt, string eventVesselName)` at `LedgerOrchestrator.cs:3614`.
- `WarnPairingCandidateTie(...)` at `LedgerOrchestrator.cs:3689`.
- `FlushStalePendingRecoveryFunds(string reason)` at `LedgerOrchestrator.cs:3727`.
- `RepairMissingRecoveryDedupKeys()` at `LedgerOrchestrator.cs:3754`.
- `TryFindLegacyRecoveryDedupKey(...)` at `LedgerOrchestrator.cs:3805`.
- `OnVesselRecoveryFunds(...)` at `LedgerOrchestrator.cs:3903`.
- `TryAddVesselRecoveryFundsAction(...)` at `LedgerOrchestrator.cs:3934`.
- `PickRecoveryRecordingId(string vesselName, double ut)` at `LedgerOrchestrator.cs:4295`.

Recovery call sites outside the helper block:

- `Initialize()` pins `VesselRecoveryReasonKey` against `TransactionReasons.VesselRecovery.ToString()` at `LedgerOrchestrator.cs:169`.
- `AddVesselRecoveryCostActions(...)` uses `TryFindRecoveryFundsEvent(..., skipConsumed: false, ...)` at `LedgerOrchestrator.cs:987`.
- `OnKspLoad(...)` clears `consumedRecoveryEventKeys` at `LedgerOrchestrator.cs:1789`, flushes pending requests at `LedgerOrchestrator.cs:1793`, and calls `RepairMissingRecoveryDedupKeys()` at `LedgerOrchestrator.cs:1803`.
- `ResetForTesting()` clears `consumedRecoveryEventKeys` and `pendingRecoveryFunds` at `LedgerOrchestrator.cs:4780`.
- `GetScienceChangedReasonKey(...)` returns `VesselRecoveryReasonKey` for recovered science at `LedgerOrchestrator.cs:4533`/`LedgerOrchestrator.cs:4536`.
- `GameStateStore.BlocksResourceCoalescing(...)` opens at `GameStateStore.cs:117` and compares event keys to `LedgerOrchestrator.VesselRecoveryReasonKey` at `GameStateStore.cs:122` so adjacent recovery funds events remain one event per KSP callback.
- `GameStateRecorder.OnFundsChanged` calls `OnRecoveryFundsEventRecorded(fundsEvt)` at `GameStateRecorder.cs:1030`.
- `ParsekScenario` calls `FlushStalePendingRecoveryFunds("rewind end")` at `ParsekScenario.cs:2097`.
- `ParsekScenario` calls `OnVesselRecoveryFunds(...)` at `ParsekScenario.cs:5258`.
- `ParsekScenario` calls `FlushStalePendingRecoveryFunds(...)` at `ParsekScenario.cs:5423`.

### Rollout Cost and Adoption

Constants and state:

- `RolloutDedupPrefix` at `LedgerOrchestrator.cs:113`; also consumed by `GameActionDisplay.IsUnclaimedRolloutAction`.
- `RolloutAdoptionContext` at `LedgerOrchestrator.cs:115`.
- `RolloutAdoptionWindowSeconds` at `LedgerOrchestrator.cs:4277`.

Rollout helpers:

- `AddVesselBuildCostActions(...)` at `LedgerOrchestrator.cs:916`. This is a mixed-domain orchestrator method: it calls rollout adoption first, then emits any residual `FundsSpendingSource.VesselBuild` action. Do not relocate the whole method into `LedgerRolloutAdoption`.
- `OnVesselRolloutSpending(double ut, double cost)` at `LedgerOrchestrator.cs:3339`.
- test overload `OnVesselRolloutSpending(double ut, double cost, uint vesselPersistentId, string vesselName, string launchSiteName)` at `LedgerOrchestrator.cs:3348`.
- private implementation `OnVesselRolloutSpending(double ut, double cost, RolloutAdoptionContext context)` at `LedgerOrchestrator.cs:3361`.
- `TryAdoptRolloutAction(string recordingId, double startUT)` at `LedgerOrchestrator.cs:4029`.
- `TryAdoptRolloutAction(string recordingId, double startUT, Recording rec)` at `LedgerOrchestrator.cs:4034`.
- `CanRecordingAdoptRolloutAction(Recording rec)` at `LedgerOrchestrator.cs:4097`.
- `ResolveCurrentRolloutAdoptionContext()` at `LedgerOrchestrator.cs:4106`.
- `TryResolveLaunchSiteNameFromFlightDriver()` at `LedgerOrchestrator.cs:4135`.
- `CreateRolloutAdoptionContext(Recording rec)` at `LedgerOrchestrator.cs:4148`.
- `CreateRolloutAdoptionContext(uint vesselPersistentId, string vesselName, string launchSiteName)` at `LedgerOrchestrator.cs:4159`.
- `NormalizeRolloutContextText(string value)` at `LedgerOrchestrator.cs:4172`.
- `CanMatchRolloutAdoptionContext(...)` at `LedgerOrchestrator.cs:4180`.
- `RolloutAdoptionContextsMatch(...)` at `LedgerOrchestrator.cs:4189`.
- `BuildRolloutDedupKey(...)` at `LedgerOrchestrator.cs:4212`.
- `ParseRolloutAdoptionContext(string dedupKey)` at `LedgerOrchestrator.cs:4221`.
- `FormatRolloutAdoptionContext(...)` at `LedgerOrchestrator.cs:4261`.

Rollout call sites outside the helper block:

- `CreateVesselCostActions(...)` calls `AddVesselBuildCostActions(...)` before recovery action creation at `LedgerOrchestrator.cs:910`.
- `AddVesselBuildCostActions(...)` calls `CanRecordingAdoptRolloutAction(...)` then `TryAdoptRolloutAction(...)` before emitting any residual build-cost action at `LedgerOrchestrator.cs:936`.
- `GameStateRecorder.OnFundsChanged` calls `OnVesselRolloutSpending(ut, -delta)` for `TransactionReasons.VesselRollout` at `GameStateRecorder.cs:1027`.
- `GameActionDisplay.IsUnclaimedRolloutAction` uses `LedgerOrchestrator.RolloutDedupPrefix` at `GameActionDisplay.cs:253`.
- `KscActionExpectationClassifier` classifies `FundsSpendingSource.VesselBuild` as `VesselRollout` at `KscActionExpectationClassifier.cs:75`; this must not move as part of this refactor.

## Extraction Candidate

Use two focused private/internal helper classes in `Source/Parsek/GameActions/`:

- `LedgerRecoveryFundsPairing`
- `LedgerRolloutAdoption`

Keep `LedgerOrchestrator` as the only public facade for existing internal entry points. This avoids touching external call sites in `GameStateRecorder`, `ParsekScenario`, `GameActionDisplay`, or tests during the first extraction.

### LedgerRecoveryFundsPairing

Candidate ownership:

- recovery constants except `VesselRecoveryReasonKey`, which should stay owned by `LedgerOrchestrator` because science reconciliation, event coalescing, and the KSP enum pin also read it;
- consumed recovery event key set;
- pending recovery queue;
- recovery event lookup and dedup key building;
- pending queue pairing and stale flush;
- legacy recovery dedup-key repair;

Deliberately keep `PickRecoveryRecordingId(...)` on `LedgerOrchestrator`. It reads `RecordingStore.CommittedRecordings`; moving that read into a new helper file would require adding the file to `scripts/ers-els-audit-allowlist.txt` with an `[ERS-exempt]` rationale. Avoid widening the ERS/ELS allowlist in a zero-logic extraction.

Required dependencies should be passed through a small context object or constructor parameters rather than hidden static calls where possible:

- `Func<IReadOnlyList<GameStateEvent>> getEvents` for `GameStateStore.Events`;
- `Func<IReadOnlyList<GameAction>> getActions` for `Ledger.Actions`;
- `Action<GameAction> addAction` for `Ledger.AddAction`;
- `Func<string, double, string> pickRecoveryRecordingId` that calls the orchestrator-owned `PickRecoveryRecordingId(...)`;
- `Action recalculateAndPatch`;
- `Func<int> nextKscSequence` or `Action<GameAction> assignNextKscSequence` to preserve sequence assignment timing;
- logging delegate that still routes to `ParsekLog.Info/Warn/Verbose(Tag, ...)` with `Tag = "LedgerOrchestrator"`.

Recommended first step: do not extract `RepairMissingRecoveryDedupKeys()` in the same commit if that would touch load repair heavily. Leave a wrapper that calls into a moved implementation only after characterization tests are green.

### LedgerRolloutAdoption

Candidate ownership:

- `RolloutAdoptionContext`;
- dedup key build/parse/format;
- context resolution/matching;
- adoption scan and mutation;
- rollout spending action construction.

Dependencies:

- current vessel/site resolver (`FlightGlobals.ActiveVessel`, `FlightRecorder.ResolveLaunchSiteName`, `FlightDriver.LaunchSiteName`) can remain in the helper because it is already KSP-bound;
- `Ledger.Actions` and `Ledger.AddAction`;
- `RolloutAdoptionContext` must remain internal to `LedgerRolloutAdoption`; do not expose it from any `LedgerOrchestrator` wrapper signature. Public wrappers continue to take either primitive context arguments or `Recording`.
- `AddVesselBuildCostActions(...)` stays on `LedgerOrchestrator`; the helper exposes adoption and rollout-action construction only. The orchestrator remains responsible for residual build-cost emission.
- `ReconcileKscAction(...)` should remain an orchestrator callback, not move;
- `RecalculateAndPatch()` should remain an orchestrator callback;
- sequence assignment must remain supplied by `LedgerOrchestrator` so rollout and recovery share the same `kscSequenceCounter`.

Keep shared constants owned by `LedgerOrchestrator` when any non-helper code reads them. Helpers should consume the orchestrator constant rather than become the source of truth for those cross-cutting contracts:

```csharp
// Stays on LedgerOrchestrator because GameActionDisplay reads it.
internal const string RolloutDedupPrefix = "rollout:";

// May stay on LedgerOrchestrator or be re-exported if moved, because tests read it.
internal const double RolloutAdoptionWindowSeconds = LedgerRolloutAdoption.RolloutAdoptionWindowSeconds;

// May stay on LedgerOrchestrator or be re-exported if moved, because tests read it.
internal const int PendingRecoveryFundsStaleThreshold =
    LedgerRecoveryFundsPairing.PendingRecoveryFundsStaleThreshold;
internal const double VesselRecoveryEventEpsilonSeconds =
    LedgerRecoveryFundsPairing.VesselRecoveryEventEpsilonSeconds;

// Stays on LedgerOrchestrator because Initialize, GameStateStore, and science
// reconciliation read it.
internal const string VesselRecoveryReasonKey = "VesselRecovery";
```

This preserves `GameActionDisplay.IsUnclaimedRolloutAction`, `GameStateStore.BlocksResourceCoalescing`, and existing tests without widening helper visibility.

## Wrapper Shape

Keep these `LedgerOrchestrator` wrappers with the current signatures:

- `OnVesselRolloutSpending(double ut, double cost)`
- `OnVesselRolloutSpending(double ut, double cost, uint vesselPersistentId, string vesselName, string launchSiteName)`
- `TryAdoptRolloutAction(string recordingId, double startUT)`
- `TryAdoptRolloutAction(string recordingId, double startUT, Recording rec)`
- `CanRecordingAdoptRolloutAction(Recording rec)`
- `OnVesselRecoveryFunds(double ut, string vesselName, bool fromTrackingStation, VesselType vesselType = VesselType.Unknown)`
- `OnRecoveryFundsEventRecorded(GameStateEvent evt)`
- `FlushStalePendingRecoveryFunds(string reason)`
- `PendingRecoveryFundsCountForTesting`
- `PendingRecoveryFundsStaleThreshold`
- `VesselRecoveryEventEpsilonSeconds`
- `TryFindRecoveryFundsEvent(...)` as an explicit orchestrator-private wrapper if the event finder moves before `AddVesselRecoveryCostActions(...)`. Do not widen helper visibility just for the commit-time `skipConsumed: false` call.
- `FindBestPairingIndex(double eventUt, string eventVesselName)`
- `PickRecoveryRecordingId(string vesselName, double ut)`
- `BuildRecoveryEventDedupKey(GameStateEvent e)`
- `RolloutAdoptionWindowSeconds`
- `RolloutDedupPrefix`
- `VesselRecoveryReasonKey`

Wrappers should do no new decision-making. They should call the helper and pass callbacks for:

- `Initialize()`;
- `RecalculateAndPatch()`;
- `ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, ut)`;
- `FindRecordingById(recordingId)`;
- shared `kscSequenceCounter` increment.

Wrappers must invoke `Initialize()` before delegating to the helper, matching the current call order in `OnVesselRolloutSpending` (`LedgerOrchestrator.cs:3363`), `OnRecoveryFundsEventRecorded` (`LedgerOrchestrator.cs:3568`), and `OnVesselRecoveryFunds` (`LedgerOrchestrator.cs:3909`). The `VesselRecoveryReasonKey` drift-pin WARN inside `Initialize()` must continue to land before any same-call-site recovery/rollout log line.

Suggested sequence callback:

```csharp
private static int AllocateKscSequence()
{
    kscSequenceCounter++;
    return kscSequenceCounter;
}
```

Use this only if every old `kscSequenceCounter++` site maps exactly to one call at the same point. The callback must fire after all guards pass and immediately before the `GameAction` is constructed, matching current `OnKscSpending` (`LedgerOrchestrator.cs:3303`), `OnVesselRolloutSpending` (`LedgerOrchestrator.cs:3375`), and `TryAddVesselRecoveryFundsAction` (`LedgerOrchestrator.cs:3986`). Do not introduce pre-allocation at method entry or before dedup/amount guards.

## Tests To Pin Before Moving Code

Run these focused tests before and after each extraction step:

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter Bug445RolloutCostLeakTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter Bug452RolloutLabelTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter GameStateRecorderLedgerTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter GameStateEventTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter VesselCostRecoveryRegressionTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter LedgerOrchestratorTests`
- full suite: `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`

Add characterization tests only if gaps appear during extraction. Likely useful pins:

- recovery sequence interleaves with `OnKscSpending` and rollout at the same UT. Add a characterization test that calls `OnKscSpending`, `OnVesselRolloutSpending`, and a deferred recovery pairing all at `UT=T`, then asserts the resulting `Sequence` values reflect the call order and each allocation occurred after the relevant guards.
- rollout wrapper still calls `ReconcileKscAction` before `RecalculateAndPatch`;
- `OnVesselRecoveryFunds` still adds to `consumedRecoveryEventKeys` before `Ledger.AddAction`;
- `CreateVesselCostActions` still calls build-cost adoption before recovery action creation;
- `OnKspLoad` preserves the four-step order around recovery state: clear consumed keys, flush pending requests with its WARN/log before dropping entries, run existing reconciliation/load repair flow, then `RepairMissingRecoveryDedupKeys()`. Pin this with log substrings before moving recovery helpers.
- exact log substrings for the primary rollout/recovery paths if any moved helper changes formatting risk.

Existing coverage already pins:

- rollout writing, non-positive skip, adoption, window boundary, context matching, LIFO, residual build-cost, cancelled rollout, save/load, and legacy bare key behavior in `Bug445RolloutCostLeakTests`.
- rollout label/display predicate behavior and cancelled-rollout description behavior in `Bug452RolloutLabelTests`.
- recovery immediate pairing, deferred pairing, debris handling, pending flush, same-UT/different-event pairing, reason-key filtering, dedup repair, ghost-only skip, bracketing-vs-latest recording selection, and pending candidate tie logs in `GameStateRecorderLedgerTests`.
- recovery resource-event coalescing exemptions and `VesselRecoveryReasonKey` event persistence behavior in `GameStateEventTests`.
- legacy terminal-state recovery fallback in `VesselCostRecoveryRegressionTests`.

## Rollout Plan

1. Add helper classes with copied logic and no call-site changes. Keep them internal and unused initially if desired.
2. Move pure rollout context helpers first: context struct, normalize, build/parse/format, match. Keep `RolloutDedupPrefix` owned by `LedgerOrchestrator`.
3. Move `TryAdoptRolloutAction(...)` behind wrappers. Keep `AddVesselBuildCostActions(...)` on `LedgerOrchestrator`; it calls the helper's adoption method and still emits residual build-cost actions itself. Verify `Bug445RolloutCostLeakTests`.
4. Move `OnVesselRolloutSpending(...)` private implementation behind wrappers, passing sequence allocation, reconciliation, and recalc callbacks. Verify rollout tests and KSC reconciliation logs.
5. Move recovery pure helpers: dedup key build, event find, pending candidate picker. Before moving `TryFindRecoveryFundsEvent(...)`, add an orchestrator-private wrapper for `AddVesselRecoveryCostActions(...)` to call with `skipConsumed: false`, or leave the event finder on `LedgerOrchestrator` until the commit path is ready to move. Keep queue state in orchestrator until tests pass.
6. Move immediate recovery action add before or in the same commit as deferred queue handling. `OnRecoveryFundsEventRecorded(...)` calls `TryAddVesselRecoveryFundsAction(...)`, so moving the queue first while the add method remains private on `LedgerOrchestrator` creates a non-compiling intermediate.
7. Move recovery queue state and deferred event handling together with the immediate add path, or immediately after the add path has an internal helper API. Verify `GameStateRecorderLedgerTests`.
8. Consider moving legacy repair only after recovery pairing is stable; it belongs near recovery but is load-repair behavior and has higher rollback cost.

## Rollback

Keep each stage as a small commit. Rollback should be mechanical:

- revert the stage commit;
- restore wrappers to direct local methods if needed;
- no schema/data migration is involved because dedup key formats and ledger actions do not change.

Do not rename serialized fields, enum values, `DedupKey` formats, reason keys, or log messages. If any of those change accidentally, rollback the whole extraction stage rather than patching forward.

## Risk Notes

- `TryFindRecoveryFundsEvent(..., skipConsumed: false, ...)` is used by commit-time recovery fallback while `skipConsumed: true` is used by live recovery pairing. Mixing these changes behavior.
- `consumedRecoveryEventKeys.Add(dedupKey)` currently happens before adding the recovery action in the normal positive-delta path. That ordering protects re-entrant reuse.
- Non-positive recovery deltas are marked consumed and return `true`. A helper extraction must preserve this because callers interpret `true` as "paired/handled".
- Pre-seeded recovery actions dedup by `DedupKey`, not just UT/amount.
- `PendingRecoveryFundsRequest` deliberately does not fuzzy-dedup. Bulk recovery can enqueue multiple same-name requests inside epsilon.
- Rollout adoption mutates an existing ledger action in place by setting `RecordingId` and clearing `DedupKey`. It does not return a new action and it does not call `Ledger.AddAction`.
- `AddVesselBuildCostActions(...)` is not just rollout adoption; it also emits residual vessel-build spending. Keep that residual emission in `LedgerOrchestrator` unless the helper is deliberately renamed and scoped as a broader build-cost producer.
- Rollout adoption scans newest-first and accepts a 0.5-second future slack. Both are behavior, not incidental implementation.
- Legacy bare rollout keys must remain adoptable.
- `RolloutDedupPrefix` is shared with display code; keep it owned by `LedgerOrchestrator`.
- `VesselRecoveryReasonKey` is shared with `GameStateStore.BlocksResourceCoalescing`; keep it owned by `LedgerOrchestrator` so adjacent recovery funds events cannot silently start coalescing again.
- `PickRecoveryRecordingId` skips ghost-only recordings and compares vessel names exactly. Do not introduce normalization in a zero-logic pass.
- `VesselRecoveryReasonKey` is also used for recovered science reason matching. Moving it solely into a recovery helper can accidentally broaden the refactor into science reconciliation.
- Moving `PickRecoveryRecordingId(...)` to a helper would require ERS/ELS allowlist work because it reads `RecordingStore.CommittedRecordings`. Keep it on `LedgerOrchestrator` for this proposal.
