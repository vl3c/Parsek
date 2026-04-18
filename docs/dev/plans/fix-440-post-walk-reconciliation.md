# Fix #440: Post-walk reconciliation for strategy-transformed and curve-applied reward types

Status: plan v1 (Phase E2 of ledger/lump-sum fix). Branch: feat/440-post-walk-reconciliation.
Target: v0.8.2 release (sits on top of #439; rebase onto origin/main when #439 merges).
Governs: Phase E2 of docs/dev/plans/fix-ledger-lump-sum-reconciliation.md.
Depends on: #436 Phase A shipped, #437 Phase B shipped, #343 Phase D shipped, #439 Phase A shipped.
Scope note: "do it all" per user direction. Full action-type coverage across funds, science, reputation.

## 1. Bug restatement and success criteria

Per docs/dev/todo-and-known-bugs.md entry #440 and the governing plan Phase E2:

LedgerOrchestrator.ReconcileKscAction.ClassifyAction routes eight action types into the "Transformed" bucket and VERBOSE-skips them:

- ContractComplete (funds/rep/sci, all strategy-transformable; rep via ApplyReputationCurve)
- ContractFail (funds/rep; rep via curve)
- ContractCancel (funds/rep; rep via curve)
- MilestoneAchievement (funds/rep/sci; rep via curve; gated on Effective)
- ReputationEarning (rep via curve)
- ReputationPenalty (rep via curve)
- FundsEarning on KSC path (safety route; no transform today)
- ScienceEarning on KSC path (safety route; no transform today)

Comparing their raw fields (FundsReward, NominalRep, MilestoneRepAwarded, etc.) against live KSP deltas would false-positive on every legitimate contract completion once strategies are active and on every rep change that crosses the curve. So today these channels pass through silently.

After #439, strategy-transformed rewards are first-class and the VERBOSE skip is no longer defensible. Phase D already threads utCutoff through RecalculateAndPatch, and after the walk the derived fields TransformedFundsReward, TransformedScienceReward, TransformedRepReward, EffectiveRep, and Effective are populated on each action.

Success criteria (all measurable):

- On a stock career after revert/rewind with active strategies, no post-walk reconciliation WARN is produced solely by ContractComplete, ContractFail, ContractCancel, MilestoneAchievement, ReputationEarning, ReputationPenalty, KSC-path FundsEarning/ScienceEarning income when ReputationCurve matches KSP.
- Controlled-divergence unit tests: seed a ContractComplete whose post-walk TransformedFundsReward does not match the store delta -> exactly one "Earnings reconciliation (post-walk, funds)" WARN fires.
- Controlled-divergence rep curve test: seed a ReputationEarning where the walk's curve output diverges from a pre-captured store delta -> WARN fires on the rep leg.
- Milestone duplicate test: two MilestoneAchievement actions with the same key, first Effective=true, second Effective=false -> only the first reconciles. No WARN for the duplicate.
- utCutoff test: a ContractComplete past the cutoff is not reconciled (rewind does not produce false WARNs for filtered actions).
- Double-WARN regression: the existing ReconcileKscAction pass must not also fire for the same action (these action types stay routed to the Transformed bucket with a SkipReason; the new post-walk hook is the only site that fires a post-walk WARN).

## 2. Invariants (authoritative list, enforced by tests)

I1. ClassifyAction stays authoritative for the per-action ReconcileKscAction split. "Untransformed" and "NoResourceImpact" buckets do not change.
I2. The new post-walk reconciliation is a separate iteration at the top of RecalculateAndPatch after RecalculationEngine.Recalculate returns. It does NOT interleave with ReconcileKscAction. "Transformed" class still skips with VERBOSE in ReconcileKscAction (per-action path); the post-walk hook picks them up at the end of the walk exactly once.
I3. utCutoff applies identically to the post-walk hook as to the walk. Actions with a.UT > utCutoff (and not seeds) are filtered out, never reconciled.
I4. Non-effective MilestoneAchievement duplicates skip reconciliation. EffectiveRep is set to 0f by ReputationModule and funds/science are skipped in their modules when Effective=false (see FundsModule:314, ScienceModule:332), so the post-walk hook must gate on action.Effective for MilestoneAchievement. Applies to ContractComplete identically.
I5. Multi-leg actions emit at most one WARN per resource leg per action. A ContractComplete may emit funds+rep+sci warns independently if all three diverge; a ContractFail may emit funds+rep independently.
I6. Per-walk, the post-walk hook runs ONCE, not per-action. Same call pattern as ReconcileEarningsWindow (pure reconciliation, log-only).
I7. Tolerances: funds=1.0, rep=0.1, sci=0.1 (match the existing LegacyMigrationFundsTolerance family and ReconcileKscAction's fundsTol/repTol/sciTol constants).

## 3. Audit findings per action type

For each type: raw field(s), post-walk derived field(s), store event(s) and TransactionReasons key, and whether a transform applies today.

### 3.1 ContractComplete

- Raw fields: action.FundsReward, action.RepReward, action.ScienceReward.
- Post-walk fields:
  - Funds: action.TransformedFundsReward (FundsModule reads this into runningBalance when Effective). Per #439 section 3.5, TransformContractReward is an identity no-op today, so TransformedFundsReward == FundsReward. Kept under the post-walk path for regression safety if a future mod re-enables a legitimate transform.
  - Reputation: action.EffectiveRep (set by ReputationModule.ProcessContractCompleteRep, applies ApplyReputationCurve to TransformedRepReward when Effective).
  - Science: action.TransformedScienceReward (ScienceModule reads this when Effective). Same identity invariant as funds today.
- Store events (tag GameStateStore.Events):
  - FundsChanged key=ContractReward (TransactionReasons.ContractReward). Recorder writes it via OnFundsChanged. Confirmed by existing test `ReconcileKsc_ContractComplete_SkipsWithVerbose` and `GameStateEventTests.cs`.
  - ReputationChanged key=ContractReward.
  - ScienceChanged key=ContractReward.
- Transform applies today? Rep does (curve). Funds/Sci are identity post-#439 Phase A; include them so the structural match protects against regressions.
- Effective gate: yes. Duplicates flagged by ContractsModule skip.

### 3.2 ContractFail

- Raw fields: action.FundsPenalty (positive magnitude; applied as -penalty by FundsModule), action.RepPenalty (positive magnitude).
- Post-walk fields:
  - Funds: no TransformedFundsPenalty field; FundsModule deducts FundsPenalty directly. Post-walk compare uses -FundsPenalty as the "expected delta" (structural no-op today; future parity).
  - Reputation: action.EffectiveRep (negative). Computed by ReputationModule.ProcessContractPenaltyRep with nominal = -RepPenalty through the curve. NOT gated on Effective (penalties always apply).
- Store events:
  - FundsChanged key=ContractPenalty (TransactionReasons.ContractPenalty).
  - ReputationChanged key=ContractPenalty.
- Transform applies today? Rep does (curve). Funds is identity.
- Effective gate: no.

### 3.3 ContractCancel

Same as ContractFail (shares ProcessContractPenalty and ProcessContractPenaltyRep).

- Store event key: TransactionReasons.ContractPenalty. Both Contract.Fail and Contract.Cancel emit via ContractPenalty (confirmed by existing test `ReconcileKsc_ContractCancel_SkipsWithVerbose`). `TransactionReasons.ContractDecline` is a separate reason fired by `onDeclined` on offered-but-not-accepted contracts, not Cancel.

### 3.4 MilestoneAchievement

- Raw fields: action.MilestoneFundsAwarded, action.MilestoneRepAwarded, action.MilestoneScienceAwarded. All gated on action.Effective.
- Post-walk fields:
  - Funds: no Transformed* variant. FundsModule.ProcessMilestoneEarning credits MilestoneFundsAwarded when Effective.
  - Rep: action.EffectiveRep (ReputationModule.ProcessMilestoneRep curve-transforms MilestoneRepAwarded when Effective).
  - Sci: no Transformed* variant. ScienceModule credits MilestoneScienceAwarded when Effective.
- Store events (confirmed via existing test `ReconcileKsc_MilestoneAchievement_SkipsWithVerbose`):
  - FundsChanged key=Progression (TransactionReasons.Progression).
  - ReputationChanged key=Progression.
  - ScienceChanged key=Progression.
  - Note: KSP's `ProgressNode.AwardProgress` calls `Funding.Add`/`ResearchAndDevelopment.Add`/`Reputation.AddReputation` which emit the three standard resource events with reason `Progression`. The MilestoneAchieved event itself carries the reward values in its detail string for display / ledger reconciliation via `ReconcileEarningsWindow`, but post-walk hook pairs against the generic resource events — the MilestoneAchieved event has no `valueBefore`/`valueAfter` to reconcile against.
- Transform applies today? Rep does (curve). Funds/Sci are identity.
- Effective gate: yes. Per invariant I4, non-effective duplicates skip.

### 3.5 ReputationEarning

- Raw field: action.NominalRep.
- Post-walk field: action.EffectiveRep (via ApplyReputationCurve with runningRep-at-walk-time; NOT gated on Effective).
- Store event: ReputationChanged, keyed by the emitter's `TransactionReasons`. The source taxonomy is dictated by the action's `RepSource` field:
  - `RepSource.VesselRecovery` -> key=`VesselRecovery`.
  - `RepSource.ContractReward` -> key=`ContractReward` (but these should arrive as ContractComplete actions, not ReputationEarning; audit at implementation time).
  - `RepSource.Progression` -> key=`Progression` (audit — may come in as MilestoneAchievement).
  - `RepSource.Other` / `RepSource.LegacyMigration` (#436 legacy path) -> untagged synthetics with no paired event; skip post-walk for those (ClassifyPostWalk returns Reconcile=false when RepSource is Other/LegacyMigration).
- Transform applies today? Yes (curve) for captured sources; not for synthetics.
- Effective gate: no (ReputationEarning is always effective).

### 3.6 ReputationPenalty

- Raw field: action.NominalPenalty (positive magnitude).
- Post-walk field: action.EffectiveRep (negative; curve applied).
- Store event: ReputationChanged. Source taxonomy by `RepPenaltySource`:
  - `RepPenaltySource.ContractPenalty` -> key=`ContractPenalty` (typically arrives as ContractFail/ContractCancel; audit).
  - `RepPenaltySource.Strategy` -> strategy-driven penalties (none emitted today from stock).
  - `RepPenaltySource.Other` / `LegacyMigration` -> skip.
- Transform applies today? Yes (curve) for captured sources.
- Effective gate: no.

### 3.7 FundsEarning (KSC path, source != Recovery/ContractReward/Milestone)

- Raw field: action.FundsAwarded. Gated on action.Effective.
- Post-walk field: none (no transform today; use FundsAwarded directly if Effective else 0.0).
- Store event: FundsChanged with a source-specific key.
- Transform applies today? No. This is the "safety" route: include it so that a future strategy payout or mod introduces a transform that gets caught immediately. For v0.8.2 this is structural.
- Effective gate: yes.

### 3.8 ScienceEarning (KSC path, SubjectId-driven)

- Raw field: action.ScienceAwarded. Post-walk field: action.EffectiveScience (after subject-cap headroom). This IS a transform (subject cap), not strategy-driven but still a walk-level transformation.
- Store event: ScienceChanged. Key depends on emitter.
- Transform applies today? Yes (subject cap).
- Effective gate: yes.

### 3.9 Not in scope -- FundsSpending(source=Strategy)

After #439, StrategyActivate is Untransformed and its SetupCost pairs with FundsChanged(StrategySetup) via ReconcileKscAction. Post-walk does NOT re-reconcile StrategyActivate. FundsSpending with source=Strategy is today flagged as Transformed in ClassifyAction (SkipReason "strategy spending not yet KSC-captured"). No strategy-driven FundsSpending action is emitted today from the KSC path (#439 captures activate/deactivate only; no ongoing-payout channel exists in stock), so this route stays dormant. If a future mod introduces a strategy FundsSpending, the post-walk hook will inherit it via the "Transformed" classification without further work. Flag in section 13 as carve-out for #439B follow-up.

## 4. Post-walk hook design

### 4.1 Placement

Inside LedgerOrchestrator.RecalculateAndPatch after RecalculationEngine.Recalculate returns and BEFORE KspStatePatcher.PatchAll. Rationale: the walk has populated all derived fields and the reconciliation is log-only (does not affect patching). Runs once per RecalculateAndPatch.

### 4.2 Method signature

```csharp
internal static void ReconcilePostWalk(
    IReadOnlyList<GameStateEvent> events,
    IReadOnlyList<GameAction> actions,
    double? utCutoff)
```

- events = GameStateStore.Events (parameterized for testability).
- actions = the walk copy of Ledger.Actions.
- utCutoff = same cutoff the walk used; null means no cutoff.
- Internal static, no KSP singleton access. Tests call it directly.

### 4.3 Iteration order

Single pass over `actions` in stored order. For each action:

1. Filter by utCutoff per RecalculationEngine.IsSeedType rule.
2. Gate by action type via a new classifier helper (see 4.4).
3. For each applicable resource leg (funds, rep, sci), compute:
   - expected = post-walk derived value.
   - observed = sum of store events with matching type + matching TransactionReasons key within PostWalkReconcileEpsilonSeconds of action.UT.
4. If |expected - observed| > tolerance, emit ONE WARN per (action, leg) with tag "Earnings reconciliation (post-walk, {leg})".
5. Otherwise emit VERBOSE.

### 4.4 New classifier helper

`internal static PostWalkExpectation ClassifyPostWalk(GameAction action)` returning a small struct with up to three legs populated:

```csharp
internal struct PostWalkExpectation
{
    public bool Reconcile;
    public PostWalkLeg Funds;
    public PostWalkLeg Rep;
    public PostWalkLeg Sci;
}

internal struct PostWalkLeg
{
    public bool Applies;
    public double Expected;
    public string ReasonKey;
}
```

Maps each action type per 3.1-3.8 above. Non-transformed / out-of-scope types return Reconcile=false.

Rationale for a separate classifier: keeps ClassifyAction (and its KscReconcileClass enum) untouched per invariant I1.

### 4.5 Per-leg comparison

Shared helper:

```csharp
private static void CompareLeg(
    GameAction action,
    string legTag,
    GameStateEventType evtType,
    string reasonKey,
    double expected,
    double tolerance,
    IReadOnlyList<GameStateEvent> events)
```

- Walks events with e.eventType == evtType and e.key == reasonKey within PostWalkReconcileEpsilonSeconds of action.UT. Sums valueAfter-valueBefore.
- If |expected - observed| > tolerance: WARN.
- Else: VERBOSE (rate-limited per (Type, legTag) pair).

Window and matching logic reuse the same pattern as ReconcileKscAction so the two functions share a predictable contract.

### 4.6 Summary log

After the iteration, emit one INFO summary with counts:

```
Post-walk reconcile: actions=X, matches=Y, mismatches(funds/rep/sci)=A/B/C, cutoffUT=Z
```

### 4.7 WARN format

Match existing "Earnings reconciliation" tagging so log readers already filtering for that substring catch new lines:

```
Earnings reconciliation (post-walk, funds): {Type} id={id} expected={expected:F1},
observed={observed:F1} across {n} event(s) keyed '{reasonKey}' at ut={ut:F1}
- post-walk delta mismatch
```

Where `id` is ContractId / MilestoneId / SubjectId depending on type.

## 5. Module surface audit

Current exposure on GameAction: Effective (bool), EffectiveRep (float), EffectiveScience (float), TransformedFundsReward (float), TransformedRepReward (float), TransformedScienceReward (float). All public fields. No module-level access required.

Gap: no "EffectiveFunds" per-action field. But none of the eight action types has a funds-specific transform separate from TransformedFundsReward (which is today identity). So funds needs NO new field.

Decision: NO production module surface changes required for #440 scope.

## 6. Windowing and event matching

Reuse ReconcileKscAction's UT-window + key-matching pattern. Edge cases:

- Event outside window: observedCount=0 -> WARN "no matching event within {epsilon}s of ut={ut} - missing earning channel or stale event?"
- Duplicate / coalesced events: summing valueAfter-valueBefore across the window handles coalescing by construction (ResourceCoalesceEpsilon).
- MilestoneAchievement duplicate (Effective=false second occurrence): skip in the iteration (I4).

Window constant: introduce PostWalkReconcileEpsilonSeconds = 0.1, same rationale as KscReconcileEpsilonSeconds. Keep independent so a future tune does not inadvertently couple them.

## 7. Tolerance

Per invariant I7, match the existing family:

- funds: 1.0
- rep: 0.1
- sci: 0.1

Promote to three `private const double` fields on LedgerOrchestrator alongside the existing KSC constants (or reuse the KSC trio; a single shared set is cleanest).

## 8. Logging and observability

- WARN tag: "Earnings reconciliation (post-walk, {leg})". Leg in {"funds","rep","sci"}. Prefix kept identical to ReconcileEarningsWindow so existing log scanners catch it.
- VERBOSE per-match (rate-limited) via ParsekLog.VerboseRateLimited with key "post-walk-match:{Type}:{leg}".
- INFO summary per walk: always emitted.
- SkipReason updates in ClassifyAction: each Transformed-class branch gets its SkipReason tweaked to reference "post-walk hook reconciles (#440)" instead of "not yet post-walk-aware". Cosmetic.

## 9. Tests

New tests in Source/Parsek.Tests/EarningsReconciliationTests.cs (shared test class, existing [Collection("Sequential")] and Dispose teardown cover the same static state).

### 9.1 Unit tests (pure post-walk comparison)

Approximately 14 cases:

- PostWalk_ContractComplete_AllLegsMatch_NoWarn
- PostWalk_ContractComplete_FundsMismatch_OnlyFundsWarn
- PostWalk_ContractComplete_RepMismatch_OnlyRepWarn
- PostWalk_ContractComplete_SciMismatch_OnlySciWarn
- PostWalk_ContractFail_RepCurve_NoWarn
- PostWalk_ContractCancel_FundsAndRep_Match_NoWarn
- PostWalk_MilestoneAchievement_EffectiveTrue_AllLegsMatch_NoWarn
- PostWalk_MilestoneAchievement_EffectiveFalseDuplicate_Skipped_NoWarn
- PostWalk_ReputationEarning_CurveMatch_NoWarn
- PostWalk_ReputationEarning_CurveDiverges_Warn
- PostWalk_ReputationPenalty_CurveMatch_NoWarn
- PostWalk_FundsEarning_KscPath_Match_NoWarn
- PostWalk_ScienceEarning_SubjectCapApplied_Match_NoWarn
- PostWalk_UtCutoff_FiltersFutureActions_NoWarn
- PostWalk_NoMatchingEvent_WarnsMissingChannel

### 9.2 Integration tests

New file Source/Parsek.Tests/PostWalkReconciliationIntegrationTests.cs:

- Integration_StrategyActive_ContractComplete_NoPostWalkWarn (commit path — drives OnRecordingCommitted end-to-end with seeded events; baseline ship criterion for v0.8.2)
- Integration_StrategyActive_ContractComplete_TransformDiverges_Warn (rewind path)
- Integration_RewindCutoffsFiltersPostWalk

### 9.3 ClassifyAction SkipReason updates

Update any test that asserts the current SkipReason string to match the new language. Grep targets include ReconcileKsc_ContractComplete_SkipsWithVerbose etc.

### 9.4 Regression tests

- PostWalk_DoubleWarnGuard_TransformedActionOnlyFiresOnce
- PostWalk_ReputationCurveMatchesKsp (guards against curve drift)

## 10. ClassifyAction changes

Leave ClassifyAction alone (user gut-read confirmed). Add a separate iteration at the top of RecalculateAndPatch.

- ClassifyAction continues to return KscReconcileClass.Transformed for the eight types. No new enum value.
- ReconcileKscAction continues to VERBOSE-skip "Transformed" per-action on the KSC path.
- The post-walk hook is an independent iteration at the top of RecalculateAndPatch. Consults the new ClassifyPostWalk helper, not ClassifyAction.

Only cosmetic change to ClassifyAction: update SkipReason strings to "post-walk hook reconciles (#440)" for the eight Transformed branches.

## 11. Interaction with #438, #439, and #439B

### 11.1 #438 follow-ups

#438 added ReconcileEarningsWindow switch cases for ContractAccept, FacilityUpgrade, FacilityRepair (funds-only, identity). Those are Untransformed-class and are reconciled per-action by ReconcileKscAction. Post-walk does NOT cover them (ClassifyPostWalk returns Reconcile=false).

Latent double-WARN risk (flagged, not blocking): `ReconcileEarningsWindow` (commit path) sums raw `FundsReward`/`RepReward`/`ScienceReward` into `emittedFundsDelta`/etc., not `Transformed*Reward`. Today (identity transforms) this is equivalent. If a future non-identity transform lands (e.g. mod-provided strategy effect), both `ReconcileEarningsWindow` on commit AND `ReconcilePostWalk` will WARN for the same action with different signs. Follow-up: switch `ReconcileEarningsWindow` switch cases to `Transformed*Reward` after #440 ships. Document as a post-#440 follow-up in todo-and-known-bugs.md.

### 11.2 #439 interaction

#439 Phase A flipped StrategyActivate to Untransformed with ExpectedReasonKey="StrategySetup". The post-walk hook MUST NOT re-reconcile StrategyActivate. Per invariant I1, ClassifyAction handles it.

Known invariant from #439 section 3.5: StrategiesModule.TransformContractReward is an identity no-op. So today TransformedFundsReward == FundsReward for every ContractComplete. The post-walk hook's funds-leg comparison is a structural no-op.

### 11.3 #439B follow-up

#439B defers multi-resource KscActionExpectation for Sci/Rep StrategyActivate setup costs. #440 does NOT cover #439B.

## 12. Conflict vectors with open PRs

- PR #376 (#438): adds switch cases in ReconcileEarningsWindow and test cases in EarningsReconciliationTests.cs. #440 also edits the same test file (adds approximately 15 new post-walk tests) and touches LedgerOrchestrator.cs. Likely textual conflict; git rebase handles mechanically.
- PR #378 (#439): this branch sits on top of #378. Will rebase onto origin/main after #378 merges. Possible follow-up rebase if #376 merges first.
- CHANGELOG.md, docs/dev/todo-and-known-bugs.md: mechanical conflicts expected.

Sequencing: #440 rebases onto origin/main after #439 (and #438) merge. When that happens, ClassifyAction StrategyActivate is already flipped.

## 13. File-touch list

Production:

- Source/Parsek/GameActions/LedgerOrchestrator.cs
  - Add ReconcilePostWalk method and ClassifyPostWalk / PostWalkExpectation / PostWalkLeg types.
  - Add PostWalkReconcileEpsilonSeconds constant.
  - Call site in RecalculateAndPatch after RecalculationEngine.Recalculate.
  - Update Transformed SkipReason strings (cosmetic).
  - Estimate: approximately 180-220 lines added.

No other production files.

Tests:

- Source/Parsek.Tests/EarningsReconciliationTests.cs
  - Approximately 14 new Fact methods per 9.1/9.3/9.4.
  - Update existing ReconcileKsc_*_SkipsWithVerbose cases to match new SkipReason strings.
- Source/Parsek.Tests/PostWalkReconciliationIntegrationTests.cs (new)
  - 3 integration tests per 9.2.

Docs:

- docs/dev/todo-and-known-bugs.md: strike through entry #440.
- CHANGELOG.md: under v0.8.2 "Fixed". Plain ASCII, 1-2 sentences.
- docs/dev/plans/fix-ledger-lump-sum-reconciliation.md: mark Phase E2 delivered.

## 14. Risks and open questions

R1. Does ReputationModule.ApplyReputationCurve exactly match KSP's addReputation_granular? If not, every ContractComplete rep leg would false-warn. **Mitigation: `PostWalk_ReputationCurveMatchesKsp` regression test is gate zero — write it FIRST, run it before any other implementation work. If it fails, #440 stops until the curve fidelity is fixed (separate bug, blocks this one).** Verify during implementation with a one-off ilspycmd extract of KSP's RepPenalty/RepGain paths alongside the curves stored in ReputationModule.cs.

R2. MilestoneAchievement.Effective=false means duplicates re-emitted but not credited. The live event only reflects the first credit. Post-walk hook gates on action.Effective for MilestoneAchievement and ContractComplete. Duplicate-skip tested in 9.1.

R3. Transformed fields reset between walks? YES. RecalculationEngine.Recalculate has a reset loop that zeroes EffectiveScience / EffectiveRep / Affordable and seeds TransformedFundsReward/ScienceReward/RepReward from the immutable raw fields. Each walk produces deterministic derived values.

R4. Double-WARN risk: Existing ReconcileKscAction VERBOSE-skips Transformed action types, so it does not emit WARN. The new post-walk hook is the only site. Regression tested in 9.4.

R5. [RESOLVED] MilestoneAchievement store-event source: KSP fires separate FundsChanged/ReputationChanged/ScienceChanged events with reason `Progression`. Confirmed by existing test fixture. Use generic key-matching path.

R6. Multi-leg coalescing: a ContractComplete fires FundsChanged + ReputationChanged + ScienceChanged in sequence within ResourceCoalesceEpsilon (0.1s). GameStateStore coalesces same-eventType/same-key within the window, but the three legs are different eventTypes, so they land in separate slots.

R7. KSC-path FundsEarning has no transform today; post-walk is a structural no-op. Safety net; keep included.

R8. FundsSpending(source=Strategy) scope carve-out: not emitted today. Low risk.

## 15. Acceptance checklist (v0.8.2 ship)

- [ ] **Gate zero**: `PostWalk_ReputationCurveMatchesKsp` written FIRST and passing — proves our ApplyReputationCurve output matches a KSP-captured before/after rep delta. If this fails, stop the PR and file a rep-curve fidelity bug.
- [ ] LedgerOrchestrator.ReconcilePostWalk + ClassifyPostWalk implemented.
- [ ] PostWalkReconcileEpsilonSeconds constant added.
- [ ] Call site in RecalculateAndPatch added after Recalculate returns.
- [ ] Cosmetic SkipReason updates in ClassifyAction branches.
- [ ] EarningsReconciliationTests.cs: approximately 14 new tests pass.
- [ ] PostWalkReconciliationIntegrationTests.cs: 3 integration tests pass.
- [ ] Existing ReconcileKsc_*_SkipsWithVerbose tests updated for new SkipReason strings, still pass.
- [ ] dotnet test green in Source/Parsek.Tests.
- [ ] docs/dev/todo-and-known-bugs.md entry #440 struck through.
- [ ] CHANGELOG.md v0.8.2 Fixed entry added.
- [ ] docs/dev/plans/fix-ledger-lump-sum-reconciliation.md Phase E2 marked delivered.
- [ ] Rebased onto origin/main after #439 (and #376 if earlier) merge.
- [ ] Manual in-game check (v0.8.2 user-visible ship criterion): stock career, activate one Admin-tier-1 strategy, complete one contract, save, reload. Grep KSP.log for `Earnings reconciliation (post-walk,` -> zero matches. Revert to launch, reload -> still zero matches.

## 16. Out of scope

- Multi-resource KscActionExpectation for StrategyActivate (tracked as #439B).
- A StrategyPayout capture path (#439 Phase C).
- Pre-transform raw ContractComplete reward capture (#439 Option C).
- Enriching MilestoneAchieved events with Source / TransactionReasons keys if R5 resolution requires detail parsing.
- Per-leg EffectiveFunds field on GameAction.
