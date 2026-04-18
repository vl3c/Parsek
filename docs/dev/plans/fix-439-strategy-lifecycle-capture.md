# Fix #439: Strategy lifecycle capture (Phase E1.5 of ledger/lump-sum fix)

Status: plan v2 (Phase A scope, user-confirmed 2026-04-18). Branch: feat/439-strategy-lifecycle-capture.
Target: v0.8.2 release.
Governs: Phase E1.5 of docs/dev/plans/fix-ledger-lump-sum-reconciliation.md.
Depends on: #436 Phase A shipped, #437 Phase B shipped, Phase C, Phase D (#343) shipped, #441 shipped.

## Phase A scope (supersedes detailed sections below where they conflict)

Ship to v0.8.2:

- New event types: `StrategyActivated`, `StrategyDeactivated`. **DROP** `StrategyPayout` entirely — no stock hook drives it. Enum is append-only; adding later is cost-free when a concrete need arises.
- Harmony postfix on `Strategy.Activate` / `Deactivate` (filter on `__result==true`, try/catch, respect `IsReplayingActions`).
- Recorder emitters + pure-static detail formatters.
- Converter routes to existing `StrategyActivate` / `StrategyDeactivate` `GameAction`s.
- `ClassifyAction`: `StrategyActivate` -> `Untransformed`, Funds-only `ExpectedDelta = -SetupCost` paired against `FundsChanged(StrategySetup)`. `StrategyDeactivate` stays `NoResourceImpact`.
- `StrategiesModule.TransformContractReward` -> documented no-op + VERBOSE log (required — populating `activeStrategies` activates the dormant double-count).
- `activeStrategies` populated (slot-accounting, reservation logic, Actions window display).
- Flip the existing `ClassifyAction_StrategyActivate_TransformedNotUntransformed` test.
- New `StrategyCaptureTests.cs`.
- Audit `StrategiesModuleTests.cs` for any test asserting `TransformedFundsReward != FundsReward` under active strategy — update to the identity invariant.
- CHANGELOG 1-liner; todo strike-through plus known-limitation note for Sci/Rep setup costs.

**Known limitation shipped with Phase A**: strategies with non-zero `InitialCostScience` or `InitialCostReputation` will still emit a false-positive KSC-reconciliation WARN on those resource legs. Stock Admin-tier-1 strategies are funds-only, so most new careers never trip it. Follow-up entry filed for Phase B.

**NOT in Phase A (deferred):**
- Phase B: multi-resource `KscActionExpectation` + Sci/Rep setup cost reconciliation. ~200 lines, fast-follow candidate.
- Phase C: `StrategyPayout` fallback emitter for mod-compat. Add when a specific mod demands it.
- Phase D: raw pre-transform reward capture (Funding.AddFunds prefix). Only if a UI feature needs to display diversion.

All sections below are the detailed engineering plan for Phase A. Sections that reference `StrategyPayout` are retained for context only; Phase A emits / converts / enum-adds nothing named `StrategyPayout`.

## 1. Bug restatement

Per docs/dev/todo-and-known-bugs.md entry #439:

- GameStateEvent.cs has no strategy event types.
- StrategiesModule (Source/Parsek/GameActions/StrategiesModule.cs) consumes StrategyActivate / StrategyDeactivate actions during the recalculation walk and reads action.StrategyId / SourceResource / TargetResource / Commitment / SetupCost.
- Nothing in the mod captures strategy lifecycle from KSP and nothing converts strategy events into those actions, so StrategiesModule never sees input.
- Any career that activates a stock strategy (Leadership Initiative, Unpaid Research Program, etc.) diverts contract rewards through a channel the ledger does not model; PatchFunds: suspicious drawdown WARN fires on every revert/rewind cycle for strategy-using careers (same shape as #436 but on new saves, where Phase A's legacy-migration safety net does not apply).

## 2. Success criteria

- On a strategy-using career, no PatchFunds: suspicious drawdown WARN during revert/rewind cycles solely attributable to strategy income or strategy setup cost.
- StrategiesModule.activeStrategies contains the player's active strategies after a recalculation walk.
- Contract rewards reconcile against actual KSP funds delta with strategies active (transformed rewards already match because KSP's modifier-query reduces them before the FundsChanged event fires; this plan documents that invariant rather than adding new transform code).
- Strategy activate SetupCost passes KSC reconciliation (Untransformed class with paired FundsChanged(StrategySetup)).
- Round-trip test through ParsekScenario save/load preserves the three new event types.

## 3. KSP API research findings (CRITICAL -- reshapes the scope)

Source: ilspycmd decompilation of Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll. This section supersedes the original todo entry's "Strategies.Strategy.Activate / Deactivate and the payout callback" framing; there is NO payout callback.

### 3.1 Patch targets

Concrete, public, instance-method, non-abstract:

- Strategies.Strategy.Activate() -- signature `public bool Activate()`, returns true on success, returns false if CanBeActivated fails. Sets isActive=true, calls Register() (which fires each effect's OnRegister()), sets dateActivated=Planetarium.fetch.time, then for non-zero InitialCostFunds / InitialCostReputation / InitialCostScience calls Funding/Reputation/ResearchAndDevelopment.Instance.Add*(-Mathf.Abs(...), TransactionReasons.StrategySetup). Harmony POSTFIX is correct: we only want to capture on success, and the cost deductions have already fired when the postfix runs, so the FundsChanged(StrategySetup) event has already landed in GameStateStore by the time we emit. Filter on the postfix result (`bool __result`) to skip the false/CanBeActivated-failed branch.
- Strategies.Strategy.Deactivate() -- signature `public bool Deactivate()`, returns true on success. Sets isActive=false, calls Unregister(). No resource cost. Harmony POSTFIX, filter on __result.

Both methods are concrete (not abstract / not virtual overrides that a subclass may elide), and both are reachable from StrategyListItem / ActiveStrategyListItem UI paths. The Harmony targets are stable.

### 3.2 There is NO strategy-payout API call in stock KSP

Stock strategies with ongoing effects work via CurrencyModifierQuery interception, not discrete payout calls. Evidence:

- Strategies.StrategyEffect has virtual OnRegister / OnUnregister / OnUpdate. OnUpdate is called by Strategies.Strategy.Update() per tick but stock effects use it only for cache refresh, not payouts.
- Strategies.Effects.CurrencyConverter.OnRegister subscribes GameEvents.Modifiers.OnCurrencyModifierQuery. Its handler reads `qry.GetInput(input)`, multiplies by share, calls `qry.AddDelta(input, -num)` and `qry.AddDelta(output, rate*num)`. The CurrencyModifierQuery is consulted by KSP's Funding/Reputation/RnD code BEFORE the final AddFunds / AddReputation / AddScience call -- so the diverted amount never enters the funds delta. The player never sees the "pre-transform" reward and neither does GameStateRecorder.OnFundsChanged -- by the time the FundsChanged event fires, the reward has already been reduced.
- Strategies.Effects.CurrencyOperation follows the same OnCurrencyModifierQuery subscription pattern.
- Strategies.Effects.ValueModifier modifies game values (e.g. kerbal training cost) via ad-hoc GameVariables hooks -- no currency flow at all.

**Implication**: the "StrategyPayout" event originally planned as a captured-from-KSP event has no hook to capture. Strategy income on contract completion is ALREADY in the ContractComplete FundsReward value we read -- KSP has pre-transformed it through the modifier query before firing onContractCompleted. Our existing ContractComplete capture is therefore correct even with active strategies, contrary to the plan's premise. The StrategiesModule.TransformContractReward step that applies Commitment as a diversion fraction then DOUBLE-applies the transform. See section 3.5.

### 3.3 There is NO `GameEvents.onStrategyToggled`

`ilspycmd -t GameEvents` shows only strategy-UI-adjacent events (`onGUIAdministrationFacilitySpawn` / `Despawn`), no strategy state toggle. Harmony on Strategy.Activate / Deactivate is the only path.

### 3.4 One-shot activation rewards (non-transform strategies)

`Strategy.Activate()` pays setup cost only (InitialCostFunds / Science / Reputation, debits via TransactionReasons.StrategySetup). Stock does not grant one-shot funds/rep/science on activate; the initial-cost fields are always costs (Activate subtracts Mathf.Abs(cost)), never payouts. If a mod introduces a strategy that grants at activation, the generic FundsChanged(StrategySetup) / ScienceChanged / ReputationChanged stream with positive delta will capture it, and the StrategyActivated event's paired cost metrics let ClassifyAction do the reconciliation without a new "StrategyPayout" type.

Leadership Initiative (cited in the todo entry as a one-shot payout milestone example) is actually stock-implemented as a CurrencyConverter -- it converts a fraction of incoming Reputation gains into Funds, not a milestone-style one-shot.

### 3.5 Correctness review of StrategiesModule.TransformContractReward (existing code)

StrategiesModule.cs:183-228 reads action.TransformedFundsReward, multiplies by Commitment, and re-credits the diverted amount to the target resource. BUT the ContractComplete action's FundsReward / RepReward / ScienceReward values that feed TransformedFundsReward / etc. are READ FROM the FundsChanged / ReputationChanged / ScienceChanged event deltas that KSP wrote AFTER the modifier-query already transformed them. Applying Commitment a second time over-diverts. This is a latent bug that only surfaces once #439 starts feeding StrategyActivate actions into the walk.

**Resolution** (option B picked):

- Option A: keep TransformContractReward, subtract a second diversion, and bank on ConvertContractCompleted reading pre-transform values from some KSP-side snapshot. No such snapshot exists -- onContractCompleted fires with `contract.FundsCompletion` which is the raw config value; the reward KSP actually pays (and we see via FundsChanged) is the transformed one. Mixed semantics, bug-prone.
- Option B (picked): treat ConvertContractCompleted's FundsReward / RepReward / ScienceReward as the effective, already-transformed amount. Make StrategiesModule.TransformContractReward a no-op when the contract is post-activation (document the invariant). Keep the activeStrategies tracking for the reservation-slot accounting use case (GetActiveStrategyCount / GetAvailableSlots) and for detail rendering in the Actions window.
- Option C: switch ConvertContractCompleted to read raw contract rewards (contract.FundsCompletion as it would pay without strategies), then let TransformContractReward re-apply diversion. Requires patching ConvertContractCompleted AND capturing raw pre-transform values at onContractCompleted time AND a separate reconciliation step for the difference. Large, out-of-scope for #439.

Option B is the minimum viable change. Option C is deferred and tracked as a follow-up TODO (see section 12.2).

## 4. Event-type design

Match shape of existing events in `Source/Parsek/GameStateEvent.cs` (see ContractAccepted for the "semicolon-separated key=value detail" convention).

### 4.1 StrategyActivated

- key = strategy Config.Name (stable config identifier, not localized Title). Example: `"UnpaidResearchProgram"`.
- detail format: `title=<Title>;dept=<DepartmentName>;factor=<Factor>;setupFunds=<InitialCostFunds>;setupSci=<InitialCostScience>;setupRep=<InitialCostReputation>`. All numerics with InvariantCulture "R" format.
- valueBefore / valueAfter: unused, leave zero.
- ut = Planetarium.GetUniversalTime() at the postfix.
- epoch / recordingId: stamped by Emit via ref parameter per #454.

### 4.2 StrategyDeactivated

- key = Config.Name.
- detail format: `title=<Title>;dept=<DepartmentName>;factor=<Factor>;activeDurationSec=<now-DateActivated>`.
- valueBefore / valueAfter: unused.
- ut = Planetarium.GetUniversalTime() at the postfix.

### 4.3 StrategyPayout

Retained in the enum but only emitted from a fallback path. Payload:

- key = Config.Name.
- detail format: `title=<Title>;fundsDelta=<f>;sciDelta=<s>;repDelta=<r>;reason=<TransactionReasons.ToString()>`.
- valueBefore / valueAfter: running post-payout balance of the dominant non-zero currency.

StrategyPayout exists as an enum value and a converter branch so a future mod-compat improvement (or a stock change) can populate it without schema migration.

### 4.4 Enum ids

Append to the enum (existing ids 0-19):

```csharp
public enum GameStateEventType
{
    // ... existing 0..19
    StrategyActivated    = 20,
    StrategyDeactivated  = 21,
    StrategyPayout       = 22
}
```

Append-only -- existing serialized saves continue to load.

## 5. Harmony patch design

### 5.1 StrategyLifecyclePatch.cs (new file)

```
Source/Parsek/Patches/StrategyLifecyclePatch.cs
```

Two Harmony postfixes, one per method. Both filter on `bool __result`.

```csharp
[HarmonyPatch(typeof(Strategies.Strategy), nameof(Strategies.Strategy.Activate))]
internal static class StrategyActivatePatch
{
    internal static void Postfix(Strategies.Strategy __instance, bool __result) { ... }
}

[HarmonyPatch(typeof(Strategies.Strategy), nameof(Strategies.Strategy.Deactivate))]
internal static class StrategyDeactivatePatch
{
    internal static void Postfix(Strategies.Strategy __instance, bool __result) { ... }
}
```

**Replay guard**: mirror ProgressRewardPatch's `GameStateRecorder.IsReplayingActions` check so capture stays off during recalculation replay (prevents duplicate events during simulated walks).

**Defensive wrapping**: try/catch around the whole postfix body, log WARN on exception, never rethrow into KSP's strategy pipeline.

### 5.2 Fallback plan

If `Strategies.Strategy.Activate` proves un-patchable in some KSP build variant (unlikely), fall back to Harmony on `Strategies.StrategySystem.OnSave` / `OnLoad` to diff the `strategies[i].IsActive` snapshot against the previous recorded snapshot. Coarse but self-healing. Not implemented unless primary target breaks.

### 5.3 StrategyPayout emitter (fallback only)

The recorder's existing `OnFundsChanged(double)` already fires `FundsChanged` events for every `TransactionReasons.StrategySetup` / any other strategy-attributed debit. The StrategyPayout event type is emitted ONLY when a future KSP update or mod introduces a non-query payout path whose reason enum lands outside `TransactionReasons.StrategySetup`. For v0.8.3 the code path is present but never reached on stock; covered by a unit test that pokes the helper directly.

## 6. Recorder emission

File: `Source/Parsek/GameStateRecorder.cs`.

Add three methods plus two pure static helpers to keep logic testable without Harmony:

- `internal void OnStrategyActivated(Strategies.Strategy strategy)` -- called from StrategyActivatePatch.
- `internal void OnStrategyDeactivated(Strategies.Strategy strategy)` -- symmetric.
- `internal void RecordStrategyPayout(string strategyConfigName, double fundsDelta, float sciDelta, float repDelta, string transactionReason)` -- fallback-only writer, unit-testable.
- `internal static string BuildStrategyActivateDetail(string title, string dept, float factor, float setupFunds, float setupSci, float setupRep)` -- pure static, testable.
- `internal static string BuildStrategyDeactivateDetail(string title, string dept, float factor, double activeDurationSec)` -- pure static, testable.

Patterns copied from `OnContractAccepted` (for detail formatting via InvariantCulture "R") and `OnProgressComplete` / `EnrichPendingMilestoneRewards` (for the split-emit pattern).

Subscribe site: the Harmony patches invoke the GameStateRecorder singleton directly. No GameEvents subscription to add / remove.

**Flight vs KSC scope tagging**: ResolveCurrentRecordingTag already handles this.

## 7. GameAction plumbing

File: `Source/Parsek/GameActions/GameAction.cs`.

### 7.1 Enum additions

`FundsEarningSource` gets `Strategy = 6` appended (for strategy-derived funds gains -- fallback path).

`ReputationSource` gets `Strategy = 3` appended.

`ReputationPenaltySource` already has `Strategy = 3` (existing -- reuse).

`FundsSpendingSource` already has `Strategy = 5` (existing -- consumed by classifier comment in LedgerOrchestrator.cs:3122).

### 7.2 GameActionType

Decision: DO NOT add `GameActionType.StrategyPayout`. Rationale:

- Existing StrategyActivate / StrategyDeactivate carry all state we need.
- A StrategyPayout event converts to an existing FundsEarning / ReputationEarning / ScienceEarning with the new Strategy source enum -- same plumbing as Milestone/Recovery.
- Adding a distinct action type duplicates the entire serialize / deserialize ladder for zero schema value.

This diverges from the original todo entry's Option 1 ("Add GameActionType.StrategyPayout") and matches Option 2 ("reuse existing earning types with a strategy source"). Flagged in Risks.

### 7.3 No field changes to GameAction

All existing strategy fields (StrategyId / SourceResource / TargetResource / Commitment / SetupCost) are already declared.

## 8. Converter routing

File: `Source/Parsek/GameActions/GameStateEventConverter.cs`.

### 8.1 StrategyActivated -> GameAction

Add a case branch in ConvertEvent:

```csharp
case GameStateEventType.StrategyActivated:
    return ConvertStrategyActivated(evt, recordingId);
```

ConvertStrategyActivated:

- Type = GameActionType.StrategyActivate.
- StrategyId = evt.key.
- SourceResource / TargetResource: default (StrategyResource.Funds). Acceptable gap: TransformContractReward is being made a no-op per section 3.5 -- SourceResource / TargetResource are consumed only for detail rendering.
- Commitment = parse "factor" from detail.
- SetupCost = parse "setupFunds" (converter side uses SetupCost for the funds leg; sci / rep setup costs are deferred -- section 12.2).

### 8.2 StrategyDeactivated -> GameAction

```csharp
case GameStateEventType.StrategyDeactivated:
    return ConvertStrategyDeactivated(evt, recordingId);
```

Trivially: Type = StrategyDeactivate, StrategyId = evt.key.

### 8.3 StrategyPayout -> GameAction (fallback path)

Prefer one StrategyPayout event per resource leg, so each converts to a single GameAction (FundsEarning + FundsSource=Strategy, or ReputationEarning + RepSource=Strategy, or ScienceEarning with SubjectId="Strategy:<name>"). Keeps the converter's single-return contract intact.

## 9. LedgerOrchestrator wiring

File: `Source/Parsek/GameActions/LedgerOrchestrator.cs`.

### 9.1 ClassifyAction changes

Flip StrategyActivate from its current Phase E1.5 skip stub to Untransformed:

```csharp
case GameActionType.StrategyActivate:
    // Phase E1.5 delivered: StrategyActivate's SetupCost pairs with a
    // FundsChanged(StrategySetup) event written by GameStateRecorder.OnFundsChanged.
    return new KscActionExpectation
    {
        Class = KscReconcileClass.Untransformed,
        EventType = GameStateEventType.FundsChanged,
        ExpectedReasonKey = "StrategySetup",
        ExpectedDelta = -action.SetupCost
    };
```

Side-effects: ReconcileKsc will now WARN on strategies that activate with a non-zero InitialCostFunds but no paired FundsChanged(StrategySetup) event -- desired. The paired ScienceChanged(StrategySetup) / ReputationChanged(StrategySetup) pairings for InitialCostScience / InitialCostReputation are noted as gaps and deferred (section 12.2).

StrategyDeactivate stays NoResourceImpact (no resource flow on deactivate in stock).

### 9.2 Test flip

`Source/Parsek.Tests/EarningsReconciliationTests.cs::ClassifyAction_StrategyActivate_TransformedNotUntransformed` (lines 932-941) becomes `ClassifyAction_StrategyActivate_Untransformed`:

```csharp
var a = new GameAction { Type = GameActionType.StrategyActivate, SetupCost = 100000f };
var exp = LedgerOrchestrator.ClassifyAction(a);
Assert.Equal(LedgerOrchestrator.KscReconcileClass.Untransformed, exp.Class);
Assert.Equal(GameStateEventType.FundsChanged, exp.EventType);
Assert.Equal("StrategySetup", exp.ExpectedReasonKey);
Assert.Equal(-100000, exp.ExpectedDelta);
```

Plus a zero-cost case (free strategy) asserts the zero-delta early return.

### 9.3 Post-walk hook

Per #440's scope -- #440 owns the post-walk reconciliation. #439 does NOT change post-walk behavior.

### 9.4 StrategiesModule behavioral change (from 3.5)

File: `Source/Parsek/GameActions/StrategiesModule.cs`.

TransformContractReward (lines 163-228) becomes a no-op documented as "KSP's CurrencyModifierQuery already transformed the reward pre-event; applying Commitment here would double-count". Keep the activeStrategies dict population (ProcessActivate / ProcessDeactivate) -- the dict is still used by GetActiveStrategyCount / GetAvailableSlots (reservation accounting) and by the Actions window for display.

Prefer "documented identity function that logs VERBOSE on each call" over outright deletion -- clearer diff, easier to revert if KSP behavior changes.

## 10. Save/load schema

File: `Source/Parsek/ParsekScenario.cs` (no change required).

File: `Source/Parsek/GameStateStore.cs` (no change required).

Evidence: GameStateStore writes every event via a generic `e.SerializeInto(eventNode)`; reads via `GameStateEvent.DeserializeFrom(en)`. The serialize/deserialize pair in `GameStateEvent.cs` is generic over the enum value -- new ids round-trip automatically. Deserialize already logs WARN on unknown enum ids, so older builds loading new events degrade to a warning, not a crash.

GameAction uses the same generic pattern -- any GameAction emitted by the converter round-trips.

Round-trip test (section 11) explicitly covers save + reload + re-walk to verify these invariants.

## 11. Test plan

New file: `Source/Parsek.Tests/StrategyCaptureTests.cs`, pattern from `MilestoneRewardCaptureTests.cs`.

Scaffolding (mirrors MilestoneRewardCaptureTests.ctor):

- `[Collection("Sequential")]` -- touches GameStateStore / LedgerOrchestrator / ParsekLog static state.
- Constructor clears RecalculationEngine modules, resets ParsekLog test sink, suppresses KspStatePatcher Unity calls, resets GameStateStore and LedgerOrchestrator.

Test categories:

### 11.1 Capture symmetry (recorder helpers, no Harmony)

- `BuildStrategyActivateDetail_FormatsAsExpected` -- asserts semicolon-joined, InvariantCulture "R" formatting, all six fields present.
- `BuildStrategyDeactivateDetail_FormatsAsExpected` -- symmetric.
- `OnStrategyActivated_EmitsEventWithCorrectKeyAndDetail` -- calls recorder helper with a fake strategy mock, asserts GameStateStore.Events contains a StrategyActivated with the expected key / detail / ut / recordingId-empty (KSC scope).
- `OnStrategyDeactivated_EmitsEvent` -- symmetric.
- `OnStrategyActivated_DuringFlight_TagsRecordingId` -- TagResolverForTesting stubbed to return "rec-123", assert recordingId tag.
- `OnStrategyActivated_DuringReplay_Suppresses` -- sets GameStateRecorder.IsReplayingActions = true, asserts no event added.

### 11.2 Converter correctness

- `ConvertStrategyActivated_ReturnsStrategyActivateAction` -- asserts Type, StrategyId, Commitment, SetupCost parsed from detail.
- `ConvertStrategyActivated_MissingDetailField_DefaultsZero` -- resilience.
- `ConvertStrategyDeactivated_ReturnsStrategyDeactivateAction` -- asserts Type, StrategyId.
- `ConvertStrategyPayout_EmitsFundsEarningWithStrategySource` -- fallback path coverage.
- `ConvertStrategyPayout_ZeroDelta_ReturnsNull` -- no-op shape.

### 11.3 Round-trip

- `RoundTrip_StrategyActivated_SurvivesSaveLoad` -- builds a minimal GameStateStore with one StrategyActivated event, calls GameStateStore.SaveToNode then LoadFromNode, asserts event survives byte-exact on key / detail / ut.
- `RoundTrip_StrategyActivateAction_SurvivesGameActionSerialize` -- parallel for GameAction.
- `EndToEnd_StrategyActivateConvertsReplaysPersists` -- emit event -> ConvertEvents -> register action in Ledger -> RecalculateAndPatch -> Save -> Load -> RecalculateAndPatch -> assert StrategiesModule.activeStrategies contains the strategy, assert running balance includes SetupCost deduction.

### 11.4 Classifier

- `ClassifyAction_StrategyActivate_Untransformed_PairsWithStrategySetup` -- replaces the existing "TransformedNotUntransformed" test.
- `ClassifyAction_StrategyActivate_ZeroSetupCost_ShortCircuits` -- asserts Untransformed class but zero ExpectedDelta so ReconcileKscAction's early return kicks in.
- `ClassifyAction_StrategyDeactivate_NoResourceImpact` (additive coverage).

### 11.5 Log-capture

Each test verifies logLines contains the expected `[Parsek][INFO][StrategyLifecyclePatch]` / `[Parsek][INFO][GameStateRecorder] Game state: StrategyActivated ...` line. Match the existing pattern from `RewindLoggingTests.cs`.

### 11.6 StrategiesModule no-op transform test

- `TransformContractReward_IsNoOp_AfterPhaseE1_5` -- activate a strategy, process a ContractComplete, assert TransformedFundsReward equals FundsReward (no diversion applied). Pins section 3.5 option B behavior.

## 12. Documentation

### 12.1 CHANGELOG.md

Under the current version's "Fixed" section (ASCII only, 1-2 sentences):

```
- Fix #439: capture strategy activate / deactivate lifecycle so StrategiesModule sees input on strategy-using careers; removes the spurious PatchFunds suspicious-drawdown warning on revert / rewind cycles after a strategy is active.
```

### 12.2 docs/dev/todo-and-known-bugs.md

- Strike through the #439 entry and append `Status: done in <branch / commit>`.
- Update the "Fix:" bullets for #440 to note the StrategyPayout carve-out: stock strategies use CurrencyModifierQuery, so #440's post-walk reconciliation need not sum StrategyPayout deltas; it only needs to reconcile the ContractAccept advance / ContractFail penalty reconciliation paths that already exist in main, plus the strategy activate setup-cost sci/rep follow-ups.
- Add a new follow-up entry: "Strategy contract-reward double-transform audit" -- if a future KSP update or mod begins emitting onContractCompleted with pre-transform values, StrategiesModule.TransformContractReward needs re-enabling. Low priority.
- Add another follow-up: "Strategy setup cost for Science and Reputation" -- ClassifyAction currently returns a single ExpectedDelta / EventType; a multi-resource activate (InitialCostScience != 0 OR InitialCostReputation != 0) is unreconciled. Small follow-up once the classifier can return a tuple.

## 13. Risks and open questions

- **R1**: Harmony patch on Strategies.Strategy.Activate might collide with stock strategy mods (Strategia, Contract Configurator+Strategy). Mitigate by using postfix (additive, no prefix short-circuit) and filtering on __result=true. Document in mod-compatibility-notes.md.
- **R2**: Decision to NOT add GameActionType.StrategyPayout diverges from the original todo text. If a future test or mod needs a distinct action type, adding it later is schema-compatible (append-only enum).
- **R3**: Making TransformContractReward a no-op may regress a test that asserted diversion. Grep before commit: `Source/Parsek.Tests/StrategiesModuleTests.cs` -- audit for any test that expects TransformedFundsReward != FundsReward under an active strategy. Update those tests to match the new invariant (KSP pre-transforms -- our transform is an identity).
- **R4**: ClassifyAction for StrategyActivate assumes setupCost = InitialCostFunds only. Strategies with non-zero InitialCostScience or InitialCostReputation will have unreconciled resource events. Follow-up filed in section 12.2. Not release-blocking because stock Administration-level-1-accessible strategies have funds-only or zero costs.
- **R5**: Strategy.Activate() does not fire GameEvents for resource changes in a specific order; the FundsChanged(StrategySetup) may land on a different frame than the Strategy.Activate postfix runs. Test coverage section 11 pins the event order via GameStateStore.AddEvent call sequence in a unit test harness; real KSP frame ordering is covered by an InGameTests addition (deferred).

## 14. Sequencing note

### Conflict vectors with open PR #376 (#438 -- Phase E1 test coverage)

PR #376 touches the following files that #439 also edits:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs` -- #376 adds switch cases in `ReconcileEarningsWindow`; #439 edits ClassifyAction's StrategyActivate branch. Likely textual conflict around different methods but the same file; `git rebase` should handle it cleanly.
- `Source/Parsek.Tests/EarningsReconciliationTests.cs` -- #376 adds new cases; #439 flips the `ClassifyAction_StrategyActivate_TransformedNotUntransformed` test.
- `docs/dev/todo-and-known-bugs.md` -- both edit status.
- `CHANGELOG.md` -- both append.

**Sequencing**: land #376 first, then rebase #439 onto `origin/main`. All four conflicts are mechanical.

### Downstream dependent

#440 (post-walk reconciliation) depends on #439 and must rebase after #439 merges. #440's scope shrinks substantially given the section 3.5 finding that strategy income is pre-transformed -- #440 now primarily covers the ContractComplete / Fail / Cancel / MilestoneAchievement / ReputationEarning / direct-KSC FundsEarning / ScienceEarning post-walk path.

## 15. Files touched

- `Source/Parsek/GameStateEvent.cs` -- enum values 20/21/22.
- `Source/Parsek/Patches/StrategyLifecyclePatch.cs` -- new file.
- `Source/Parsek/GameStateRecorder.cs` -- helpers + emitters.
- `Source/Parsek/GameActions/GameAction.cs` -- FundsEarningSource.Strategy, ReputationSource.Strategy.
- `Source/Parsek/GameActions/GameStateEventConverter.cs` -- three case branches + helpers.
- `Source/Parsek/GameActions/StrategiesModule.cs` -- make TransformContractReward a documented no-op.
- `Source/Parsek/GameActions/LedgerOrchestrator.cs` -- ClassifyAction branch flip.
- `Source/Parsek.Tests/StrategyCaptureTests.cs` -- new file.
- `Source/Parsek.Tests/EarningsReconciliationTests.cs` -- flip one test, add classifier coverage.
- `Source/Parsek.Tests/StrategiesModuleTests.cs` -- audit and update any tests that asserted active-strategy contract-reward diversion.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, `docs/dev/plans/fix-ledger-lump-sum-reconciliation.md` (mark Phase E1.5 delivered).

## 16. Out of scope

- Full pre-transform capture path (Option C in section 3.5).
- Science / Reputation legs of strategy setup cost reconciliation -- deferred follow-up in section 12.2.
- Modded strategy interactions (Strategia etc.) -- covered only insofar as Harmony postfix on Strategy.Activate is additive.
- KSP version drift -- ilspycmd verified against the 1.12.x Assembly-CSharp.dll in this repo's KSP install.

## 17. Acknowledgements (from research)

- Plan v1 (this doc) supersedes the original todo entry's "Harmony patch on Strategies.Strategy.Activate / Deactivate and the payout callback" -- there is no payout callback in stock KSP; strategies use OnCurrencyModifierQuery interception.
- The "StrategyPayout event type is redundant with TransformedFundsReward on ContractComplete" hypothesis from the user prompt is confirmed by decompile. The plan retains the enum value for mod-compat and degrades emission to a fallback-only path.
- StrategiesModule.TransformContractReward is a pre-existing over-application bug (double-transform) that only surfaces once strategy capture starts; #439 neutralizes it as a corollary, not a separate bug.
