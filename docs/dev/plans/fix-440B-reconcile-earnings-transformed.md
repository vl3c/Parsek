# Fix #440B: Switch `ReconcileEarningsWindow` from raw to `Transformed*` rewards

Status: plan v1. Branch: `chore/440B-reconcile-earnings-transformed` (sibling worktree at `Parsek-440B-reconcile-earnings-transformed/`, off `origin/main` post-#376/#378/#385/#440 merge).
Target: v0.8.2 if the timing resolution lands comfortably; otherwise slip to v0.8.3 (see section 10).
Depends on: #436, #437, #438 (PR #376), #439 Phase A (PR #378), #440 (PR #385) all shipped.
Scope: narrow follow-up to #440. Log-only. No serialized fields change.

## 1. Bug restatement

`LedgerOrchestrator.ReconcileEarningsWindow` (commit-path reconciliation called from `OnRecordingCommitted`) sums raw `FundsReward`, `RepReward`, `ScienceReward`, `MilestoneFundsAwarded`, `MilestoneRepAwarded`, `MilestoneScienceAwarded`, `NominalRep`, `NominalPenalty`, `FundsPenalty`, `RepPenalty`, `FundsAwarded`, `ScienceAwarded`, `AdvanceFunds`, `FacilityCost`, `Cost`, `FundsSpent` into `emittedFundsDelta` / `emittedRepDelta` / `emittedSciDelta`.

`ReconcilePostWalk` (added by #440 in the same file) reads the derived counterparts (`TransformedFundsReward`, `TransformedScienceReward`, `EffectiveRep`, `EffectiveScience`, `MilestoneFundsAwarded`, `MilestoneScienceAwarded`) for the eight "Transformed" action types.

Today `StrategiesModule.TransformContractReward` is an identity no-op (`LedgerOrchestrator.cs` / `StrategiesModule.cs:170-176`), so raw and `Transformed*` agree for `FundsReward` / `ScienceReward`. `EffectiveRep` today already differs from `RepReward` / `NominalRep` when the reputation curve is active, but that difference is hidden because the `RepReward` arrives from KSP's `OnCurrencyModifierQuery` already curve-applied, i.e. the "raw" `RepReward` equals the curve output in practice. See `#440` plan section 3.1.

The latent double-WARN risk: the day any module makes `TransformedFundsReward != FundsReward` or `TransformedScienceReward != ScienceReward` (e.g. a mod adding a non-identity contract transform, or a future stock strategy that diverts contract rewards), `ReconcileEarningsWindow` will sum the untransformed magnitude on the emitted side while `ReconcilePostWalk` will sum the transformed magnitude, and both will WARN on the same action with equal-and-opposite mismatch shapes.

## 2. Critical finding -- timing problem blocks the naive swap

`OnRecordingCommitted` (`LedgerOrchestrator.cs:201-265`) runs in this order:

1. Convert events to actions (line 212): raw fields populated, `Transformed*` / `EffectiveRep` / `EffectiveScience` default to zero (`float` default; only `Effective` defaults to `true` per `GameAction.cs:132`).
2. Merge cost / kerbal actions, dedup (lines 227-244).
3. `Ledger.AddActions(actions)` (line 247).
4. **`ReconcileEarningsWindow(events, actions, startUT, endUT)` (line 261).** At this moment `actions` contains freshly-converted actions whose derived fields are zero. They have never been through a walk.
5. `RecalculateAndPatch()` (line 264). This calls `RecalculationEngine.Recalculate` which, at lines 192-201 of `RecalculationEngine.cs`, resets `Effective=true`, `EffectiveScience=0`, `Affordable=false`, `EffectiveRep=0`, and seeds `TransformedFundsReward = FundsReward`, `TransformedScienceReward = ScienceReward`, `TransformedRepReward = RepReward` before dispatch. The walk then produces the real derived values. `ReconcilePostWalk` at `LedgerOrchestrator.cs:946` reads them.

**Naive swap breaks.** If line 261 swaps `a.FundsReward` to `a.TransformedFundsReward`, it reads zero for every `ContractComplete` in the fresh commit window. Every contract will WARN as "store delta=5000 vs ledger emitted delta=0". The identity invariant only helps after the walk seeds the fields. The commit-path reconciliation runs before the walk.

This is the scope change the #440B TODO did not foresee. Three options, discussed below.

## 3. Options analysis

### Option A -- reorder the commit path

Move `ReconcileEarningsWindow(events, actions, startUT, endUT)` to run AFTER `RecalculateAndPatch()` instead of before it. Then `Transformed*` / `EffectiveRep` / `EffectiveScience` are fresh.

Pros: one-line reorder in `OnRecordingCommitted`. Makes commit-path and rewind-path reconciliations symmetric (both read post-walk values). Closes the double-WARN risk cleanly because both hooks read the same derived value.

Cons:

- `ReconcileEarningsWindow`'s current contract is "reconcile the dropped events in [startUT, endUT] against what we just committed". Running it after `RecalculateAndPatch` is still the same arithmetic; `RecalculateAndPatch` itself is log-only plus the KSP patch. No semantic drift.
- Tests: all 22 existing `ReconcileEarningsWindow(...)` test call sites supply raw fields and expect the function to work without a walk. Most tests would break because the new contract requires `Transformed*` / `EffectiveRep` / `EffectiveScience` to be pre-populated. Mitigation: tests that want to exercise the new semantic must either (a) call `RecalculateAndPatch` first, or (b) pre-set `Transformed*` / `EffectiveRep` / `EffectiveScience` directly on the seeded `GameAction`s. Option (b) is cheaper and keeps the unit under test pure.
- Risk: some test assertions rely on `ReconcileEarningsWindow` being reachable without registering modules. Seeding derived fields manually bypasses that requirement and is test-only discipline.

### Option B -- read raw where it is the only populated source, `Transformed*` where it exists and is fresh

Keep call order unchanged; in the switch, conditionally read `Transformed*` only for actions where the walk has not yet fired. Since at commit time NO action has been walked, this collapses to "always read raw" -- i.e. no change. Rejected.

### Option C -- keep the commit-path raw, rely on post-walk for the double-WARN guard

`ReconcilePostWalk` (which already reads `Transformed*`) fires on every `RecalculateAndPatch` including the one at line 264. Argument: the commit-path `ReconcileEarningsWindow` serves a different purpose (pair dropped events against committed actions in a narrow UT window) and the double-WARN risk is tolerable because the two WARN lines carry different tags (`(funds)` vs `(post-walk, funds)`), so log readers can distinguish.

Pros: zero production code change. The TODO becomes a doc-only edit.

Cons: fails the #440B success criterion "both hooks read the same derived value so they can never disagree with each other". A mod author looking at the log sees two WARNs for one contract and cannot tell which reflects reality without reading the source. #440 section 11.1's wording ("follow-up: switch `ReconcileEarningsWindow` switch cases to `Transformed*Reward`") explicitly rules this out.

### Recommendation: Option A (reorder) plus the raw->Transformed swap

Order the commit path:

```
OnRecordingCommitted(...):
  ... convert, dedup, add ...
  RecalculateAndPatch();             // NEW position
  ReconcileEarningsWindow(events, actions, startUT, endUT);  // was at line 261
```

And swap the switch in `ReconcileEarningsWindow` to read `Transformed*` / `EffectiveRep` / `EffectiveScience` for the action types that have them.

Rationale:

- Single source of truth for derived values.
- Both hooks (commit-path and rewind-path `ReconcilePostWalk`) observe identical semantics.
- The reorder does not change `RecalculateAndPatch`'s output -- it already patches KSP state and fires `OnTimelineDataChanged`. The commit-path reconciliation was never gating the patch; it is log-only.
- Matches the #440 invariant: "log-only; does not affect patching".

Open risk R1 below: one production ordering change. Must walk every test call site. Estimated ~22 tests need `Transformed*` / `EffectiveRep` / `EffectiveScience` seeded manually (see section 6.1).

## 4. Audit table -- every switch case in `ReconcileEarningsWindow`

Source: `LedgerOrchestrator.cs:322-381`. Table covers every case that reads a reward field.

| # | Action type | Line(s) | Current raw read | Available `Transformed*` / `Effective*` | Recommendation | Rationale |
|---|---|---|---|---|---|---|
| 1 | `FundsEarning` | 327-329 | `a.FundsAwarded` | none (no `TransformedFundsAwarded`; `Effective` gate only) | Keep raw; add `if (!a.Effective) continue;` guard | KSC-path funds earnings have no transform today; `FundsModule` skips when `!Effective` (see #440 sec 3.7). Today `Effective` is always true for these types -- guard is preemptive parity with `FundsModule`. |
| 2 | `FundsSpending` | 330-332 | `a.FundsSpent` | none | Keep raw | Spending has no transform. `Affordable` is set by the walk but does not suppress the debit in `FundsModule` (it only blocks under-balance). |
| 3 | `ReputationEarning` | 333-335 | `a.NominalRep` | `a.EffectiveRep` (ReputationModule curve-applied) | **Swap to `a.EffectiveRep`** | `ReputationModule.ProcessOther*Earning` writes `EffectiveRep` as curve output. `NominalRep` is pre-curve. Today they match for `RepSource.ContractComplete`/`Milestone` because KSP pre-applies the curve on `OnCurrencyModifierQuery`, but the walk's `ApplyReputationCurve` is authoritative. `ReconcilePostWalk` reads `EffectiveRep`. |
| 4 | `ReputationPenalty` | 336-338 | `-a.NominalPenalty` | `a.EffectiveRep` (negative, curve-applied) | **Swap to `a.EffectiveRep`** (drop the unary minus; `EffectiveRep` is already signed) | Same reasoning as #3. `ReputationModule.ProcessContractPenaltyRep` stores negative `EffectiveRep`. `ReconcilePostWalk` reads `EffectiveRep`. |
| 5 | `ContractComplete` | 339-343 | `FundsReward`, `RepReward`, `ScienceReward` | `TransformedFundsReward`, `EffectiveRep`, `TransformedScienceReward` | **Swap all three.** Gate on `a.Effective` (duplicate completions skip). | Today identity for funds/sci because `TransformContractReward` is a no-op. Rep: `EffectiveRep` is curve-applied, today matches the KSP-captured `RepReward` because KSP applies the curve before the query, but the walk is the source of truth. `ReconcilePostWalk` gates on `Effective` at `LedgerOrchestrator.cs:3612`; mirror that guard. Primary target of this bug. |
| 6 | `ContractFail`, `ContractCancel` | 344-348 | `-a.FundsPenalty`, `-a.RepPenalty` | funds: none (no `TransformedFundsPenalty`); rep: `a.EffectiveRep` (negative, curve-applied) | Keep `-a.FundsPenalty`; **swap `-a.RepPenalty` to `a.EffectiveRep`** (already signed negative). No `Effective` gate -- penalties always apply. | `ReputationModule.ProcessContractPenaltyRep` writes negative `EffectiveRep`. `FundsModule.ProcessContractPenalty` deducts `FundsPenalty` directly. `ReconcilePostWalk` at 3642-3657 keeps `-FundsPenalty` and uses `EffectiveRep` for rep. Mirror exactly. |
| 7 | `ContractAccept` | 349-356 | `a.AdvanceFunds` (+=) | none | Keep raw | Advance is direct KSP credit, no transform. `ContractsModule` stores `AdvanceFunds` immutably. Added by #438. |
| 8 | `FacilityUpgrade` | 357-364 | `-a.FacilityCost` | none | Keep raw | No transform. Added by #438. |
| 9 | `FacilityRepair` | 365-368 | `-a.FacilityCost` | none | Keep raw | No transform. Added by #438. |
| 10 | `MilestoneAchievement` | 369-373 | `MilestoneFundsAwarded`, `MilestoneRepAwarded`, `MilestoneScienceAwarded` | funds: none; rep: `a.EffectiveRep` (curve-applied); sci: none | **Swap `MilestoneRepAwarded` -> `a.EffectiveRep`.** Keep `MilestoneFundsAwarded` / `MilestoneScienceAwarded`. Gate on `a.Effective` (duplicate milestones skip). | `ReputationModule.ProcessMilestoneRep` writes `EffectiveRep` (curve). `FundsModule` / `ScienceModule` credit milestone funds/sci directly, no transform. Mirror `ReconcilePostWalk` (3662-3683). |
| 11 | `ScienceEarning` | 374-376 | `a.ScienceAwarded` | `a.EffectiveScience` (post-subject-cap) | **Swap to `a.EffectiveScience`.** Gate on `a.Effective`. | `ScienceModule.ProcessScienceEarning` writes `EffectiveScience` post cap (line 207). `ReconcilePostWalk` reads `EffectiveScience` (3771). Divergence: if a subject is at cap, `EffectiveScience=0` but `ScienceAwarded=16.44`, and the emitted-side currently over-reports by the subject-cap residual. This is a behaviour change on main today, not a safety net -- flagged in risk R4. |
| 12 | `ScienceSpending` | 377-379 | `-a.Cost` | none | Keep raw | No transform. Tech unlock cost is direct. |

Summary of swaps: six switch branches change (`ReputationEarning`, `ReputationPenalty`, `ContractComplete` three legs, `ContractFail`/`ContractCancel` rep leg, `MilestoneAchievement` rep leg, `ScienceEarning`). Three branches gain an `if (!a.Effective) continue;` guard (`ContractComplete`, `MilestoneAchievement`, `ScienceEarning`). The initial TODO assumption that only `ContractComplete` needed a swap was too narrow.

## 5. Timing resolution

Confirm the reorder. Specifically:

- In `OnRecordingCommitted` (`LedgerOrchestrator.cs:201-265`), move line 261 to after line 264.
- Updated doc-comment on `ReconcileEarningsWindow` (line 267-277) now reads "after RecalculateAndPatch runs and populates derived fields".
- No change to `RecalculateAndPatch`, `ReconcilePostWalk`, or any rewind-path call.

No other production call site invokes `ReconcileEarningsWindow` -- only `OnRecordingCommitted:261` in production (confirmed via grep across `Source/Parsek/`). Tests call it directly; section 6.1 covers those.

Alternative considered (do not adopt): run `ReconcileEarningsWindow` between `RecalculationEngine.Recalculate` and `KspStatePatcher.PatchAll` inside `RecalculateAndPatch`. Rejected because `RecalculateAndPatch` runs on every trigger (commit, rewind, load, scenario) -- the `startUT` / `endUT` window arguments are only meaningful on commit. Keeping the call at the commit-path boundary preserves the window contract.

## 6. File-touch list

### 6.1 Production

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
  - Reorder lines 261 and 264 in `OnRecordingCommitted`.
  - Update the doc-comment on `ReconcileEarningsWindow` (lines 267-277) to reflect the post-walk timing and derived-field read.
  - In the switch at 322-381:
    - `ReputationEarning` (333-335): `a.NominalRep` -> `a.EffectiveRep`.
    - `ReputationPenalty` (336-338): `-a.NominalPenalty` -> `a.EffectiveRep`.
    - `ContractComplete` (339-343): add `if (!a.Effective) break;` then `FundsReward` -> `TransformedFundsReward`, `RepReward` -> `EffectiveRep`, `ScienceReward` -> `TransformedScienceReward`.
    - `ContractFail`/`ContractCancel` (344-348): `-a.RepPenalty` -> `a.EffectiveRep`.
    - `MilestoneAchievement` (369-373): add `if (!a.Effective) break;` then `MilestoneRepAwarded` -> `a.EffectiveRep`. Keep funds/sci milestone reads.
    - `ScienceEarning` (374-376): add `if (!a.Effective) break;` then `a.ScienceAwarded` -> `a.EffectiveScience`.
    - `FundsEarning` (327-329): add `if (!a.Effective) break;` as preemptive parity.
  - No changes to `ReconcilePostWalk`, `ClassifyPostWalk`, or any `PostWalk*` struct. No new constants.
  - Estimate: approximately 20-30 lines changed.

No other production files. `GameStateEventConverter.cs:140` has a doc comment referencing `ReconcileEarningsWindow` that may benefit from a one-line update noting the post-walk read. Optional.

### 6.2 Tests

- `Source/Parsek.Tests/EarningsReconciliationTests.cs`
  - 22 existing `ReconcileEarningsWindow(...)` call sites. Every test whose action list includes `ContractComplete` / `ContractFail` / `ContractCancel` / `ReputationEarning` / `ReputationPenalty` / `MilestoneAchievement` / `ScienceEarning` needs the derived fields seeded. Cheapest approach: set the derived fields alongside raw fields in the test's `new GameAction { ... }` initializer. Example for `Reconcile_ContractCompleteMatchesStore_Silent` at lines 157-187:
    - Add `TransformedFundsReward = 5000f, EffectiveRep = 10f, TransformedScienceReward = 0f, Effective = true` to the `GameAction` initializer.
  - Some tests treat "no action, store delta only" as the mismatch case -- those need no change (zero emitted expected).
  - New tests in section 6.3.
- `Source/Parsek.Tests/LegacyTreeReconciliationRepro438Tests.cs` (line 167): one call site. Audit the action types seeded and add derived fields as needed.
- `Source/Parsek.Tests/MilestoneRewardCaptureTests.cs` (line 762): `ReconcileEarningsWindow_StandaloneMilestonePairsCleanly_NoMismatchWarn` at 710. The seeded milestone needs `Effective=true` (default) and `EffectiveRep` seeded equal to `MilestoneRepAwarded` to keep the test passing.
- Any integration test that calls `OnRecordingCommitted` indirectly (`PostWalkReconciliationIntegrationTests.cs`, commit-path integration tests) automatically gets the reorder -- no test change required there except re-running them for regression signal.

### 6.3 New tests (in `EarningsReconciliationTests.cs`)

Five new `Fact` methods, shaped as section 8 below specifies.

### 6.4 Docs

- `docs/dev/todo-and-known-bugs.md`: strike through `## 440B.` entry with a "Fixed in PR #xxx" status line, matching the #440 entry format.
- `CHANGELOG.md`: under v0.8.2 "Fixed" (or v0.8.3 if the PR slips). 1-2 sentences: "Commit-path reconciliation (#440B) now reads post-walk Transformed* / EffectiveRep / EffectiveScience fields instead of raw reward fields, matching the rewind-path ReconcilePostWalk semantics. Closes the latent double-WARN on future non-identity reward transforms."
- `docs/dev/plans/fix-ledger-lump-sum-reconciliation.md`: no update needed; #440B is a #440 follow-up, not a new phase.

## 7. Invariants (enforced by tests)

- I1. Both hooks (commit-path `ReconcileEarningsWindow` and rewind-path `ReconcilePostWalk`) read the same derived value for a given action type + leg. No divergence by construction.
- I2. `ReconcileEarningsWindow` runs strictly AFTER `RecalculateAndPatch` in `OnRecordingCommitted`. Breaking this order regresses all swapped cases to zero-read. Guarded by a unit test that calls the function on actions with `Effective=true` but `TransformedFundsReward=0`, asserts a WARN, and documents the contract.
- I3. `Effective=false` actions are skipped by the switch for types that have an `Effective` gate in their module (`ContractComplete`, `MilestoneAchievement`, `ScienceEarning`, `FundsEarning`). Mirrors `ReconcilePostWalk:3612` / `3661` / `3766` / `3746`.
- I4. Tolerances unchanged: `fundsTol=1.0`, `repTol=0.1`, `sciTol=0.1` (lines 395-397).
- I5. WARN tag unchanged: `Earnings reconciliation (funds)` / `(rep)` / `(sci)`. Log scanners still match.

## 8. Test plan

### 8.1 Positive (Transformed swap is visible)

`Reconcile_ContractComplete_TransformedFundsReward_Silent`:

- Action: `ContractComplete` with `FundsReward=1000`, `TransformedFundsReward=800`, `RepReward=0`, `EffectiveRep=0`, `ScienceReward=0`, `TransformedScienceReward=0`, `Effective=true`, `ContractId="c1"`.
- Store: `FundsChanged(0 -> 800)` in window.
- Expect: no WARN. Proves the switch reads `TransformedFundsReward` not `FundsReward`.

`Reconcile_ContractComplete_TransformedFundsReward_RawDelta_Warns`:

- Action: same as above (raw 1000, transformed 800).
- Store: `FundsChanged(0 -> 1000)` (raw-aligned).
- Expect: WARN funds mismatch 800 vs 1000. Inverse pin.

### 8.2 Regression (today's identity path still passes)

`Reconcile_ContractComplete_IdentityTransform_Silent`:

- Action: `FundsReward=TransformedFundsReward=5000`, `RepReward=EffectiveRep=10`, `ScienceReward=TransformedScienceReward=0`, `Effective=true`.
- Store: `FundsChanged(0 -> 5000)`, `ReputationChanged(0 -> 10)`.
- Expect: no WARN. Existing `Reconcile_ContractCompleteMatchesStore_Silent` updated with derived seeds to serve this role.

### 8.3 Edge -- non-effective gate

`Reconcile_ContractComplete_NotEffective_SkipsEmitted_Silent`:

- Action: `Effective=false`, `TransformedFundsReward=0`, `FundsReward=5000` (duplicate completion scenario: raw is whatever the converter wrote, but the walk zeroed the transformed value for the duplicate).
- Store: no `FundsChanged` in window.
- Expect: no WARN. Without the `Effective` gate, the switch would read raw `FundsReward=5000` and WARN.

`Reconcile_MilestoneAchievement_NotEffective_SkipsEmitted_Silent`:

- Analogous for milestone duplicates.

### 8.4 Edge -- ScienceEarning subject cap

`Reconcile_ScienceEarning_AtSubjectCap_EffectiveScienceZero_Silent`:

- Action: `ScienceAwarded=16.44`, `EffectiveScience=0` (subject already capped), `Effective=true`.
- Store: no `ScienceChanged` event (or zero delta).
- Expect: no WARN. Documents the on-main behaviour change: under the raw read, this case was warning spuriously. The swap silences the false positive -- this is intended, see risk R4.

### 8.5 Double-WARN closure (the headline test)

`Reconcile_DoubleWarn_BothHooksReadSameDerivedValue_SingleWarn`:

- Seed a `ContractComplete` with `TransformedFundsReward=800`, `FundsReward=1000` (simulates a future non-identity transform), `RepReward=EffectiveRep=0`, `Effective=true`.
- Store: `FundsChanged(0 -> 800)` (KSP actually credited the transformed amount).
- Run `ReconcileEarningsWindow(events, actions, startUT, endUT)` and assert zero funds WARNs (transformed matches store).
- Run `ReconcilePostWalk(events, actions, null)` and assert zero funds WARNs (same read, same result).
- Invert: force the observed to 1000 and verify exactly ONE funds WARN fires per hook (commit-path sees 800 vs 1000 -> WARN; post-walk sees 800 vs 1000 -> WARN) -- two WARN lines total but with identical sign and magnitude, not opposing. This is the pinned behaviour: both hooks agree on expected=800 always, so they never produce opposing-shape WARNs again.
- Third case: set store to 800 and `TransformedFundsReward=800`, keep `FundsReward=1000`. Both hooks must be silent. Pre-fix, `ReconcileEarningsWindow` would WARN (1000 vs 800) while `ReconcilePostWalk` would be silent. This is the exact double-WARN scenario the TODO flagged.

### 8.6 Timing regression guard

`Reconcile_CalledBeforeRecalculate_ReadsZeroDerived_WarnsVisibly`:

- Action: `ContractComplete` with `FundsReward=5000`, `TransformedFundsReward=0` (not yet walked), `Effective=true`.
- Store: `FundsChanged(0 -> 5000)`.
- Expect: WARN funds mismatch 0 vs 5000.

This is a documentation test; the PR is allowed to keep it gated behind a comment pinning the "always call AFTER RecalculateAndPatch" invariant. If someone later reorders the commit path, this test fires and points at the reason.

### 8.7 Updates to existing tests

For each existing test whose action list includes one of the seven swapped types, add the matching derived-field seed. Mechanical rewrite. Catalog the 22 call sites during implementation; expect roughly 12-15 to need edits (funds-only / facility-only tests are unaffected).

## 9. Interaction with #440's `ReconcilePostWalk`

After this PR:

- `ReconcileEarningsWindow` (commit-path, narrow UT window, aggregate compare) reads `TransformedFundsReward`, `EffectiveRep`, `TransformedScienceReward`, `EffectiveScience`, `MilestoneFundsAwarded`, `MilestoneScienceAwarded`, `-FundsPenalty`, `AdvanceFunds`, `-FacilityCost`, `-Cost`, `-FundsSpent`, `FundsAwarded`.
- `ReconcilePostWalk` (every `RecalculateAndPatch`, per-action leg compare) reads the same set for the Transformed-bucket types.

Both hooks now observe the same derived values. For `ContractComplete` on commit:

- `ReconcileEarningsWindow` sums `TransformedFundsReward + EffectiveRep + TransformedScienceReward` across the window on the emitted side and matches the dropped event sums.
- `ReconcilePostWalk` compares per-leg, per-action `TransformedFundsReward` / `EffectiveRep` / `TransformedScienceReward` against keyed events within 0.1s.

If the walk produces a transform that diverges from KSP, BOTH hooks WARN, but with the same expected magnitude and sign. The aggregate WARN and the per-action WARN are complementary signals, not contradictory.

If a mod adds a non-identity transform that KSP does not see (e.g. a strategy that diverts contract funds out of the query), `TransformedFundsReward != observed`. Both hooks WARN with the transformed expected and the KSP observed -- same mismatch direction. The user knows immediately which side is off.

## 10. Conflict vectors

Branch sits on `origin/main` post-#385 merge. Open / pending PRs that touch `LedgerOrchestrator.cs`:

- **#390 (#439B)**: "Strategy setup cost reconciliation for Science and Reputation legs". Touches `ClassifyAction` / `ReconcileKscAction` / `KscActionExpectation` on the KSC per-action path. Does NOT touch `ReconcileEarningsWindow`. No textual conflict with this PR's switch edits. Likely conflict zones: `ClassifyAction`-neighbouring comment blocks. Mechanical rebase.
- **#391 (#436)**: "Science / Funds legacy migration follow-up". Touches `MigrateLegacyTreeResources` (far from `ReconcileEarningsWindow`). No conflict.
- **PR #376 / #378 / #385**: already merged per `git log --oneline`.
- **CHANGELOG.md, todo-and-known-bugs.md**: always textual. Mechanical.

Sequencing: this PR rebases trivially onto current `origin/main`. If #390 merges first, re-verify the switch statement indentation near `ClassifyAction`.

## 11. Risks and open questions

R1. **Test surface is larger than expected.** 22 call sites to `ReconcileEarningsWindow` in tests. Every test whose action list seeds one of the seven swapped types must also seed the derived fields. Mitigation: the swap is mechanical; each test needs a one-line addition to the `new GameAction { ... }` initializer. Budget 1-2 hours to walk and seed.

R2. **`ScienceEarning` subject cap is an on-main behaviour change.** Before the swap, a capped subject would WARN on commit because `emittedSciDelta += a.ScienceAwarded` but `droppedSciDelta += 0` (no KSP event fires for a zero delta). After the swap, `emittedSciDelta += a.EffectiveScience = 0` and the WARN disappears. This is the correct behaviour, and `ReconcilePostWalk` already does it this way. Verify whether any existing test pins the pre-swap WARN. If so, retire it with a comment that the post-swap silence is correct.

R3. **Rep curve parity.** `EffectiveRep` is `ApplyReputationCurve(RepReward, runningRep)`, which was pinned against KSP's curve in #440's `PostWalk_ReputationCurveMatchesKsp` gate-zero test. So `EffectiveRep` matches what KSP credits. Swap is safe. If the gate-zero test starts failing in the future, both hooks will fire -- and the fidelity bug is upstream, not #440B's.

R4. **`EffectiveRep` for penalty types is already signed negative.** Existing switch does `emittedRepDelta -= a.NominalPenalty`. Post-swap: `emittedRepDelta += a.EffectiveRep` (no minus; `EffectiveRep` is negative). Easy off-by-sign if the reviewer misreads; the test `Reconcile_DoubleWarn_BothHooksReadSameDerivedValue_SingleWarn` pins the sign.

R5. **`OnRecordingCommitted` reorder changes when `OnTimelineDataChanged` fires relative to the WARN.** Before the reorder, the WARN fires before the timeline event. After, the WARN fires after. UI consumers of `OnTimelineDataChanged` that inspect the log (none known today) would see different ordering. No functional impact.

R6. **Is there an action type where raw and transformed INTENTIONALLY differ today, such that swapping changes behaviour on main right now, not just as a safety net?** Per the audit:

   - `EffectiveScience` differs from `ScienceAwarded` at subject cap. Swap changes behaviour (correctly; see R2).
   - `EffectiveRep` differs from `NominalRep`/`RepReward`/`RepPenalty` when the curve is active. In practice `RepReward` is pre-curve-applied by KSP, so the values match when the gate-zero test passes. Neutral on main today.
   - `TransformedFundsReward` / `TransformedScienceReward`: identity today. Safety net only.

   One subtle divergence exists on main today (`EffectiveScience` at cap). This PR makes it silent. Flagged explicitly above.

R7. **Gloops / ghost-only recordings.** `CreateVesselCostActions` and `CreateKerbalAssignmentActions` skip ghost-only recordings (lines 518, 675). Reorder does not change ghost-only behaviour. No new risk.

R8. **Fault injector (`OnRecordingCommittedFaultInjector`, line 206).** Test-only. Unaffected.

## 12. Acceptance checklist (ship v0.8.2 or slip to v0.8.3)

- [ ] `LedgerOrchestrator.OnRecordingCommitted` reordered: `RecalculateAndPatch` BEFORE `ReconcileEarningsWindow`.
- [ ] Switch cases in `ReconcileEarningsWindow` updated per section 4 table.
- [ ] `Effective` gates added for `ContractComplete`, `MilestoneAchievement`, `ScienceEarning`, `FundsEarning`.
- [ ] Doc-comment on `ReconcileEarningsWindow` updated to describe post-walk read.
- [ ] 22 existing tests updated with derived-field seeds.
- [ ] 5 new tests per section 8 added and passing.
- [ ] `Reconcile_DoubleWarn_BothHooksReadSameDerivedValue_SingleWarn` passes (pins the close of the double-WARN risk).
- [ ] `Reconcile_ContractComplete_TransformedFundsReward_Silent` passes (proves the swap actually reads transformed).
- [ ] `dotnet test` green in `Source/Parsek.Tests`.
- [ ] `docs/dev/todo-and-known-bugs.md` entry #440B struck through with PR link.
- [ ] `CHANGELOG.md` v0.8.2 (or v0.8.3) "Fixed" entry added.
- [ ] Manual check (optional but high-signal): stock career, complete one contract, revert/rewind, reload; grep `KSP.log` for `Earnings reconciliation` -> zero matches other than intentional divergence tests.
- [ ] Rebased onto `origin/main` before merge; re-verify no conflict with #390/#391.

Ship plan: this PR is small and purely additive in behaviour (the reorder is the only non-switch change). Safe for v0.8.2. If the test reshuffle reveals unexpected regressions, slip to v0.8.3 and flag on the TODO entry.

## 13. Out of scope

- New `Transformed*` fields (e.g. `TransformedFundsPenalty`, `TransformedAdvanceFunds`). No module writes them today and none of the audited types needs them.
- Changes to `ReconcilePostWalk` or `ClassifyPostWalk`. Already correct.
- Rewind-path reconciliation changes (#440 already covers). This PR only touches the commit-path.
- Extending the switch to types not already present (e.g. `StrategyActivate`, `KerbalHire`, `KerbalRescue`). Those are `KscReconcileClass.Untransformed` or not currently swept by this window.
- Multi-resource `KscActionExpectation` (tracked as #439B / PR #390).
