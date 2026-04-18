# Fix: Ledger / Lump-Sum Resource Reconciliation

Status: plan v3, awaiting review against current main (1ce5cdc5).
Reproducer log: `logs/2026-04-17_2158_revert-stress-test/KSP.log` (line 9863, "PatchFunds: suspicious drawdown delta=-30395.0").

## Diagnosis

`KspStatePatcher.PatchFunds` fires a "suspicious drawdown" WARN every revert/rewind cycle. KSP funds drop ~30k after vessel destruction following a tree-resource lump-sum application.

Log evidence:
- Ledger walk consistently produces `runningBalance=57795` (`FundsInitial=56995` + `FirstLaunch +800`).
- Committed tree `r0` from a prior session has `tree.DeltaFunds=+34400, ResourcesApplied=false` persisted in `.sfs`.
- On FLIGHT entry, `ApplyTreeLumpSum` adds +34400. Vessel rolls out (-4005). Vessel destroyed → SPACECENTER → PatchFunds drops 30395 to reach target 57795.
- The +34400 is NOT represented in the ledger walk. Each rewind re-arms `ResourcesApplied=false` and the lump sum re-fires.

Root cause: `ParsekFlight.ComputeTreeDeltaFunds` (ParsekFlight.cs:7104-7108) computes `Funding.Instance.Funds - tree.PreTreeFunds` — a KSP-funds snapshot. The ledger only sums explicit channels (`MilestoneAchievement`, `ContractComplete`, synthesized recovery). When real income flows through any other channel, `tree.DeltaFunds` carries it but the ledger does not. `ApplyTreeLumpSum` and `PatchFunds` then disagree by exactly that gap, every cycle.

Compounding bugs:
- `MergeDialog.cs:71-95` does not set `tree.ResourcesApplied=true` after merge commit. `ParsekFlight.CommitTreeFlight:5701` does. This is why the stress-test save loaded with `resourcesApplied=false`.
- `RecordingStore.ResetAllPlaybackState:2519-2524` zeroes `tree.ResourcesApplied` on rewind, so the lump sum re-fires every session after rewind.
- Post-rewind T0 has wrong funds/science because `MilestonesModule.ProcessAction:46-72` walks every `MilestoneAchievement` action without UT filtering — `FirstLaunch +800` gets re-credited even at a UT before its commit.
- `TryRecoverBrokenLedgerOnLoad:1116-1210` is gated by `MilestoneStore.CurrentEpoch` (line 1132). Events from prior epochs are skipped — that is *why* the tree's earnings never make it into the ledger after a revert/rewind.

## Goal

The ledger is the single source of truth for funds/science/reputation. After this work:
- `tree.DeltaFunds`, `tree.PreTreeFunds`, `tree.ResourcesApplied` are removed from `RecordingTree`.
- `ApplyTreeResourceDeltas`, `ApplyTreeLumpSum`, `ComputeTreeDelta*` are removed from `ParsekFlight`.
- `TickStandaloneResourceDeltas` and `Recording.ManagesOwnResources` are removed.
- `TreeCommittedFundsCost` family removed from `ResourceBudget`.
- `RecalculateAndPatch` alone drives `Funding.Instance.Funds` to the right target.
- The "suspicious drawdown" WARN never fires on a healthy save.

## Test invariant

Per-tree was wrong because legitimate KSC income (contract advance, part purchases, milestone-at-recovery) has `RecordingId=null` (per GameAction.cs:111-112 comment). Use **session-level**:

> At every reconciliation point R,
> `Funding.Instance.Funds == initialFunds + Σ(effective FundsEarning, MilestoneAchievement, ContractComplete, ContractAccept advances) − Σ(FundsSpending, ContractFail/Cancel penalties, FacilityCost, HireCost, SetupCost)`
> for all actions with `UT ≤ R.cutoffUT`.

A per-tree corollary holds *only* when every income flow during the tree's UT window has a tagged recording — soft check, not a hard test.

## Phasing

Sequence: **A → B → C → D → E1 → E1.5 → E2 → F**. Each on its own worktree+branch. Phases A through E ride one release; **Phase F ships in the next release** so legacy saves auto-migrate before the deleted fields disappear.

---

### Phase A — Legacy save migration

**Branch:** `fix/legacy-tree-resource-residual-migration`

This is what fixes the user's repro. Without it, the stress-test save still has `resourcesApplied=false` persisted and the WARN re-fires.

**Why this can't reuse `TryRecoverBrokenLedgerOnLoad`:** that recovery is gated by `MilestoneStore.CurrentEpoch` (LedgerOrchestrator.cs:1132), so it skips prior-epoch events — exactly the events that make up r0's missing income. Phase A reads the persisted `tree.DeltaFunds` field, which is epoch-agnostic.

**Changes:**
- `Source/Parsek/GameActions/GameAction.cs` — add `FundsEarningSource.LegacyMigration`. Reuse `Other` for `ReputationEarning`. `ScienceEarning` has no source taxonomy; encode the legacy origin in `SubjectId="LegacyMigration:<treeId>"`.
- `Source/Parsek/GameActions/LedgerOrchestrator.cs` — new `MigrateLegacyTreeResources()`, called from `OnKspLoad:703` after `Reconcile` and before `RecalculateAndPatch`. For each tree with `ResourcesApplied=false` and any non-zero delta:
  - Compute three residuals: `tree.DeltaFunds − Σ(funds-effective ledger Δ for actions with RecordingId ∈ tree.Recordings, UT in window)`. Same for science & rep.
  - **Tag synthetics with `tree.RootRecordingId`, NOT `ActiveRecordingId`.** `ActiveRecordingId` is nullable per RecordingTree.cs:14, and `Ledger.Reconcile:281` keeps null-recordingId earnings forever, defeating the per-tree invariant. If `RootRecordingId` is empty, skip the tree and log WARN.
  - Inject up to three actions: `FundsEarning(LegacyMigration)`, `ScienceEarning(SubjectId="LegacyMigration:<treeId>")`, `ReputationEarning(Other)`. Only when residual exceeds tolerance (1.0 funds, 0.1 science/rep).
  - UT for synthetics: `tree.ComputeEndUT()`.
  - Set `tree.ResourcesApplied=true` and mark each recording's `LastAppliedResourceIndex = Points.Count - 1`.
  - Log INFO per tree with all three residuals.

**Tests:** `Source/Parsek.Tests/LegacyTreeMigrationTests.cs`
- 3 resources × 3 coverage cases (full residual / partial coverage / zero residual) = 9 cases.
- Edge case: `ActiveRecordingId=null` tree — assert synthetic uses `RootRecordingId`.
- Edge case: empty `RootRecordingId` — assert WARN and no synthetic.
- Purge test: delete the tree's recordings, run `Ledger.Reconcile` with empty `validRecordingIds` — assert the synthetic is purged with the tree.

**Doc updates (in same commit):** CHANGELOG; strike entry in `docs/dev/todo-and-known-bugs.md` for the lump-sum drawdown WARN.

---

### Phase B — Reconciliation as a hard test

**Branch:** `chore/earnings-reconciliation-test-coverage`

Main already has `Source/Parsek.Tests/EarningsReconciliationTests.cs`. **Audit before writing.** New work is only the gaps:
- KSC-window-only income hook (see Open Questions resolution below).
- Tree-scoped reproducer for the +34400 case using a legacy-format synthesized fixture.

**KSC reconciliation hook (new):**
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:OnKscSpending` (line 1333+) currently bypasses `ReconcileEarningsWindow`. Add `ReconcileKscWindow(eventList, action, ut)` that compares the synthesized action's amount against the most-recent `FundsChanged` event in `GameStateStore` since the previous KSC reconciliation point. WARN on mismatch.
- Add KSC test cases to `EarningsReconciliationTests.cs`.

**Reproducer test:**
- Synthesize a save with a tree where `DeltaFunds=+34400`, `ResourcesApplied=false`, zero ledger actions tagged to its recordings. Mark `[Fact(Skip="phase A")]` until Phase A enables it (then Phase A's commit flips the skip).

---

### Phase C — Merge dialog: tree-scoped helper (NOT `MarkAllFullyApplied`)

**Branch:** `fix/merge-marks-tree-resources-applied`

`RecordingStore.MarkAllFullyApplied:2685-2724` is global — it walks every committed recording, every committed tree, *and bumps every `Milestone.LastReplayedEventIndex`*. `CommitTreeFlight:5691-5723` is tree-scoped: it sets `activeTree.ResourcesApplied=true` and walks `activeTree.Recordings.Values` setting `LastAppliedResourceIndex`. Calling `MarkAllFullyApplied` from the merge dialog would silently mark unrelated milestones as fully replayed.

**Changes:**
- `Source/Parsek/RecordingStore.cs` — add new `MarkTreeAsApplied(RecordingTree tree)` that sets `tree.ResourcesApplied=true` and walks `tree.Recordings.Values` setting `LastAppliedResourceIndex = Points.Count - 1` for recordings with non-empty Points. **Does not touch `MilestoneStore.Milestones`.**
- `Source/Parsek/MergeDialog.cs:71-95` — call `MarkTreeAsApplied(tree)` after `RecordingStore.CommitPendingTree()`.
- Audit other commit branches in `MergeDialog.cs` (Discard, etc.). Discard should NOT mark applied — confirm before changing.
- `Source/Parsek/ParsekFlight.cs:5701-5714` — refactor `CommitTreeFlight` to call `MarkTreeAsApplied` for symmetry (replaces the inline equivalent).

**Tests:** `Source/Parsek.Tests/MergeDialogResourcesAppliedTests.cs`
- Extract `MergeCommit(RecordingTree, decisions)` from the lambda for testability.
- Assert `tree.ResourcesApplied == true` after merge.
- **Assert no `MilestoneStore.Milestones[i].LastReplayedEventIndex` changed across the call** (snapshot before, compare after).
- Assert each tree recording's `LastAppliedResourceIndex` advanced.

---

### Phase D — Rewind UT cutoff (explicit parameter, not global)

**Branch:** `fix/rewind-ut-cutoff-explicit-param`

`HandleRewindOnLoad` calls `RecalculateAndPatch()` synchronously at ParsekScenario.cs:1486, then `RewindContext.EndRewind()` at line 1492 (clears `RewindAdjustedUT`). The deferred coroutine `ApplyRewindResourceAdjustment:2461` calls `RecalculateAndPatch()` again at line 2502. v2's "read `RewindContext.RewindAdjustedUT` directly" approach gives 0 to the second call.

The real milestone re-credit happens because `MilestonesModule.ProcessAction:46-72` walks every `MilestoneAchievement` action in dispatch order without UT filtering — `MilestonesModule` itself needs no changes; the fix belongs at the engine boundary.

**Changes:**
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:RecalculateAndPatch` — accept explicit `RecalculateAndPatch(double? utCutoff = null)`. Pass through to `RecalculationEngine.Recalculate`.
- `Source/Parsek/GameActions/RecalculationEngine.cs` — accept `double? utCutoff`. In dispatch loop, skip actions where `action.UT > utCutoff`. Pre-pass (`ComputeTotalSpendings` etc.) takes the same filter or the reservation system over-counts future spendings.
- `Source/Parsek/ParsekScenario.cs:1486` (`HandleRewindOnLoad` synchronous call) — pass `RewindContext.RewindAdjustedUT` (still set at this point).
- `Source/Parsek/ParsekScenario.cs:2502` (`ApplyRewindResourceAdjustment` deferred call) — pass the local `adjustedUT` variable (already captured at line 2467, before yield).
- `RewindContext.EndRewind()` stays at line 1492. **No globals consulted from inside `RecalculateAndPatch`.**
- **Cutoff sentinel:** `null` (no cutoff) vs `0.0` (cutoff to start of session). UT=0 is a valid rewind target. Use `double?` not `double`.
- **Logging:** `Recalculate` always logs `(actionsTotal, actionsAfterCutoff, cutoffUT?)` so silent cutoff bugs are visible.
- **Do NOT add UT-cutoff pruning to `Ledger.Reconcile`.** That function (Ledger.cs:246-360) mutates persisted state on every load (line 352: `actions = surviving`). UT-pruning all action types there would delete future state on normal load. Cutoff applies *only* at the walk level. The persisted ledger keeps everything; the next non-rewind walk sees the full set again.

**Tests:** `Source/Parsek.Tests/RewindUtCutoffTests.cs`
- Two milestones at UT=200 and UT=500, cutoff=300, assert only UT=200 contributes.
- Same for `ContractComplete`, `FundsEarning`, `FundsSpending`, `ScienceEarning`, `ReputationEarning`, `KerbalAssignment`.
- Edge: cutoff=`null`, assert no filtering.
- Edge: cutoff=0.0, assert all post-zero actions filtered.
- Edge: rewind-to-pre-tree-start (cutoff < earliest tree's startUT).
- Two-pass test: call `RecalculateAndPatch(cutoff=200)` then `RecalculateAndPatch(cutoff=200)` again, assert both honor identically (regression for the two-call rewind flow).

---

### Phase E1 — Pre-existing channel audit

**Branch:** `chore/audit-existing-earning-channels`

Main already has:
- `OnContractAccepted` advance capture (`Source/Parsek/GameStateRecorder.cs:253-318`).
- `ProgressRewardPatch` for milestone reward enrichment (`Source/Parsek/Patches/ProgressRewardPatch.cs:7-53`).

This phase is just verification — no production change. Run Phase B's reproducer with synthetic recordings exercising contract advance and milestone rewards in isolation. Confirm the gap is non-zero only for strategy and any other green-field channel.

If E1 surfaces no remaining gap, skip directly to F (with `LegacyMigration` as the safety net for any unknown-channel income).

---

### Phase E1.5 — Strategy lifecycle capture (prerequisite for E2)

**Branch:** `feat/strategy-lifecycle-capture`

`Source/Parsek/GameStateEvent.cs:6-27` has no strategy event type. `StrategiesModule` consumes `StrategyActivate`/`StrategyDeactivate` actions but nothing emits them. Strategy income is currently invisible to the ledger.

**Changes:**
- `Source/Parsek/GameStateEvent.cs` — add `GameStateEventType.StrategyActivated`, `StrategyDeactivated`, `StrategyPayout`.
- `Source/Parsek/Patches/StrategyLifecyclePatch.cs` — new Harmony patch on `Strategies.Strategy.Activate`/`Deactivate`/payout callbacks. Find via `ilspycmd` on `Assembly-CSharp.dll` per `.claude/CLAUDE.md` decompilation workflow.
- `Source/Parsek/GameStateRecorder.cs` — emit events; tag with current recording id when in flight.
- `Source/Parsek/GameActions/GameAction.cs` — add `FundsEarningSource.Strategy`. Add `GameActionType.StrategyPayout` if it doesn't exist already.
- `Source/Parsek/GameActions/GameStateEventConverter.cs` — convert new event types to actions.
- `Source/Parsek.Tests/StrategyCaptureTests.cs` — new file.

This phase blocks Phase E2 for strategy. Without it, strategy income permanently relies on Phase A's residual injection.

---

### Phase E2 — Plug remaining channels

**Status:** Delivered in v0.8.2 via #440 (`feat/440-post-walk-reconciliation`); see `docs/dev/plans/fix-440-post-walk-reconciliation.md` for the per-action-type audit and the reviewer-corrected event keys. Remaining reconciliation follow-ups (non-blocking) tracked as #440B (switch `ReconcileEarningsWindow` to Transformed* fields) and #439B (multi-resource `KscActionExpectation` for strategy setup costs).

**Branch family:** `fix/ledger-channel-<channel>`

Per E1 audit. World firsts (if not covered by `ProgressRewardPatch`), any other green-field channel surfaced by the reproducer test. One commit per channel.

Each channel commit also flips its corresponding `[Skip]` test in Phase B.

**Acceptance:** Phase B's reproducer mismatch reaches 0 (or has a documented carve-out).

---

### Phase F — Delete the dual source of truth

**Branch:** `chore/remove-tree-deltas-and-standalone-applier`

**Pre-flight:** Phase A must have shipped in a release. Phase F-only changes do NOT auto-fix legacy saves; that work is in A.

**Tree deletions:**
- `Source/Parsek/ParsekFlight.cs` — delete `ApplyTreeResourceDeltas` (8993-9035), `ApplyTreeLumpSum` (9037-9083), per-frame call site, `ComputeTreeDelta*` (7104-7123), PreTree* assignments at 5386-5390 and delta capture at 6523-6530.
- `Source/Parsek/RecordingTree.cs` — remove `PreTreeFunds`, `PreTreeScience`, `PreTreeReputation`, `DeltaFunds`, `DeltaScience`, `DeltaReputation`, `ResourcesApplied` (24-30) and Save handlers (92-98).
- `Source/Parsek/RecordingStore.cs` — drop `tree.ResourcesApplied` references in `MarkAllFullyApplied:2685` and `ResetAllPlaybackState:2519`. Phase C's `MarkTreeAsApplied` becomes a `LastAppliedResourceIndex`-only setter or is deleted if no other callers.

**Standalone deletions:**
- `Source/Parsek/ParsekFlight.cs` — delete `TickStandaloneResourceDeltas` and the `ApplyResourceDeltas` per-frame call for standalone recordings (the loop in lines ~8950-8990 area, verify exact range at implementation time).
- `Source/Parsek/Recording.cs` — `ManagesOwnResources` (line 484) becomes always false or is deleted; audit all callers.
- `Source/Parsek/ResourceBudget.cs` — delete `ComputeStandaloneDelta:247-268` and `ResourceDelta` struct:14-21 if no remaining callers.

**ResourceBudget rewrite:**
- `Source/Parsek/ResourceBudget.cs:194-238 ComputeTotal` and `:350-397 ComputeTotalFullCost` — **remove the `if (!recordings[i].ManagesOwnResources) continue;` skip at lines 209 and 364.** Iterate `tree.Recordings.Values` and reuse `CommittedFundsCost(rec)`. Delete `TreeCommittedFundsCost`/`Science`/`Reputation` (176-192) and `Full*` variants (290-306).

**Format-version gate (real, not hand-wave):**
- `Source/Parsek/RecordingTree.cs` — add `TreeFormatVersion` field (defaults to 0 on load when absent). Phase F bumps to 1 and writes on every Save.
- On Load, if `TreeFormatVersion < 1` AND any of the now-deleted fields (`deltaFunds`, `deltaScience`, `deltaRep`, `resourcesApplied`) is present in the ConfigNode AND non-zero AND the tree's `RootRecordingId` doesn't already have a `LegacyMigration`-tagged synthetic action in the ledger, log WARN: `"Tree '<id>' has pre-Phase-F legacy resource fields. Open this save in a Phase-A-shipped release first to auto-migrate."`. **Do not silently zero.**
- This makes the Phase-A-prerequisite enforced, not honor-system.

**Test fixture surgery:**
- `Source/Parsek.Tests/BackwardCompatTests.cs:135-161` — update assertions for new format version behavior. The test that pins "missing fields default to zero" needs to become "missing fields with version 0 trigger WARN, version 1 stays zero."
- `Source/Parsek.Tests/RecordingTreeTests.cs` (lines 158-188, 841-866, 1073, 1292-1293) — delete or update assertions on removed fields.
- `Source/Parsek.Tests/SyntheticRecordingTests.cs` (lines 6266, 6353, 6453) — delete or update.
- `Source/Parsek.Tests/Generators/RecordingBuilder.cs` (and tree-builder if separate) — drop any `WithDeltaFunds` / `WithPreTreeFunds` / `WithResourcesApplied` builder methods.

**New regression tests:**
- `Source/Parsek.Tests/NoLumpSumRegressionTests.cs` — synthesize stress-test scenario, run revert+rewind, assert no suspicious-drawdown WARN via `ParsekLog.TestSinkForTesting`.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — `[InGameTest(Category="ResourceReconciliation", Scene=GameScenes.SPACECENTER)]` reproducing the stress-test sequence end-to-end.

**Doc updates:** CHANGELOG; `.claude/CLAUDE.md` if file-layout claims about `RecordingTree` change; `docs/dev/todo-and-known-bugs.md` strike-throughs.

---

## Sequencing rules

- A → B → C → D → E1 → E1.5 → E2 → F.
- A through E may all ride one release. F ships in the next release; Phase F's format-version gate enforces the prerequisite.
- Each branch rebases on `origin/main` after the prior phase merges. **No stacked branches.**
- E2 channel commits can land in parallel as separate worktrees (they don't share files).

## Risks

- **Phase A masks unknown-channel gaps.** If E2 never finds the channel, the save is patched with a `LegacyMigration` synthetic that hides the symptom forever. Mitigation: B's tests catch new commits at commit time; A only runs against persisted `tree.DeltaFunds`, which is gone after F.
- **Phase D + UT=0 sentinel.** Get `null` vs `0.0` right or you'll silently wipe the entire ledger walk on first rewind. Logging at every `Recalculate` invocation makes this visible.
- **Phase E1.5 timing.** Strategy capture is from-scratch; budget conservatively. If it slips, A's residual injection covers strategy income on user saves until E2 catches up. Document the carve-out in CHANGELOG.
- **Phase F test-fixture surgery.** Tedious but mechanical. Run `dotnet test` after each fixture file edit.
- **Phase F shipped before A is widely upgraded.** The format-version WARN is the user-visible safety net. Without F's gate, silent data loss is possible — gate is mandatory.

## Files touched (summary)

| Phase | Files |
|---|---|
| A | `GameAction.cs`, `LedgerOrchestrator.cs`; new `LegacyTreeMigrationTests.cs` |
| B | `LedgerOrchestrator.cs` (KSC hook); update `EarningsReconciliationTests.cs` |
| C | `RecordingStore.cs`, `MergeDialog.cs`, `ParsekFlight.cs`; new `MergeDialogResourcesAppliedTests.cs` |
| D | `LedgerOrchestrator.cs`, `RecalculationEngine.cs`, `ParsekScenario.cs`; new `RewindUtCutoffTests.cs` |
| E1 | (audit-only, no production change) |
| E1.5 | `GameStateEvent.cs`, `Patches/StrategyLifecyclePatch.cs` (new), `GameStateRecorder.cs`, `GameAction.cs`, `GameStateEventConverter.cs`; new `StrategyCaptureTests.cs` |
| E2 | per-channel patches + tests |
| F | `ParsekFlight.cs`, `RecordingTree.cs`, `RecordingStore.cs`, `MergeDialog.cs`, `Recording.cs`, `ResourceBudget.cs`, `BackwardCompatTests.cs`, `RecordingTreeTests.cs`, `SyntheticRecordingTests.cs`, `Generators/`; new `NoLumpSumRegressionTests.cs`, new `InGameTests` entry |

## Acknowledgements (from review)

- Plan v1 used `MarkAllFullyApplied` from the merge dialog — primitive was wrong (touches milestones).
- Plan v2 read `RewindContext.RewindAdjustedUT` from inside `RecalculateAndPatch` — broke under the two-pass rewind flow because `EndRewind` clears it between calls.
- Plan v2 tagged Phase A synthetics with `ActiveRecordingId` — nullable, breaks per-tree invariant.
- Plan v2 added UT-cutoff pruning to `Ledger.Reconcile` — destructive on normal load, removed.
- Plan v2 hand-waved "bump format version" with no enforcement — replaced with explicit gate.
- Plan v2 over-budgeted Phase E1 — main already has contract-advance and milestone-reward channels.
- Doc path corrected: `docs/dev/todo-and-known-bugs.md`, not `docs/dev/known-bugs.md`.
