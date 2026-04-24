# Fix: #438 Reconciliation Test Coverage Backlog (Phase E1 of Ledger / Lump-Sum Fix)

Status: plan v2, tests-only plus three surgical `ReconcileEarningsWindow` switch-case
additions (path b, committed after clean review).
Worktree: `Parsek-438-phase-e1-test-coverage`, branch `chore/438-phase-e1-test-coverage`
off `origin/main`.
Governing plan: `docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md` (Phase B and
Phase E1 sections).

## v2 revisions (applied from clean review)

- Path (b) adopted: production fix for three missing cases in
  `ReconcileEarningsWindow` (`ContractAccept`, `FacilityUpgrade`, `FacilityRepair`)
  lands in the same PR. Rationale: pinned-mismatch tests on known-bad switch cases
  leave latent false-positive WARNs in the reconciliation pipeline. Three cases,
  same shape as the existing `ContractComplete` / `ContractFail` cases. Gap #6
  covers BOTH facility cases (upgrade + repair) so the symmetry is pinned.
- Gap #5 redesigned: the original test duplicated gap #3 at the
  `ReconcileEarningsWindow` level (same action shape, `MilestoneId` is not read by
  the reconciler). Replaced with a `GameStateEventConverterTests`-style test
  verifying the `ProgressRewardPatch`-enriched detail string (e.g.
  `"progressType=RecordsSpeed;body=Kerbin;funds=4800;rep=2;science=0"`) converts to
  a `MilestoneAchievement` action with the expected `*Awarded` fields, and then
  a follow-up assertion that feeding that converted action plus the three store
  deltas into `ReconcileEarningsWindow` is silent. This pins the full
  enrichment-to-reconciliation pipeline without duplicating gap #3.
- Gap #7 moved to its own new file `EarningsReconciliationEndToEndTests.cs` with
  the full reset pattern (ctor resets `GameStateStore`/`Ledger`/`LedgerOrchestrator`,
  Dispose symmetric). Avoids the brittle try/finally approach in the existing class.
- `EarningsReconciliationEndToEndTests` and `LegacyTreeReconciliationRepro438Tests`
  both use `[Collection("Sequential")]` and apply the same ctor-reset pattern as
  `LegacyTreeMigrationTests`. The `*ResetForTesting` helpers are confirmed to exist
  on `GameStateStore`, `Ledger`, `LedgerOrchestrator`, `RecordingStore`.
- Assertion-text tightening: pinned-mismatch assertions (if any remain) use
  `F1`-formatted signed deltas (e.g. `"2000.0"`, not `"2000"`) to pin the exact
  format string.
- Line-number re-verification: `ReconcileEarningsWindow` switch is at lines
  319-354 in current main. Re-verify at implementation time before editing.
- Sequencing note: the existing `ClassifyAction_StrategyActivate_...Transformed`
  test in `EarningsReconciliationTests.cs` will flip to `Untransformed` when #439
  ships Phase E1.5. This PR does NOT touch that test; #439's PR updates it.
- `MilestoneRewardCaptureTests.cs` confirmed to exist -- gap #5 can safely
  reference it for transitive Harmony-postfix coverage.

## Scope and non-goals

Close the 8 reconciliation-coverage gaps enumerated in `todo-and-known-bugs.md:381-402`.
Tests only, with one caveat: the audit has surfaced two latent production gaps in
`ReconcileEarningsWindow` (gaps #1 and #6 below). Decision defers to the clean-review
step; the current default is path (a) from the Risks section (pin as
mismatch-today, file follow-up) but path (b) -- fix in the same PR -- is acceptable
if the reviewer prefers it and keeps the PR scope coherent.

## Audit of EarningsReconciliationTests.cs (existing coverage baseline)

File: `Source/Parsek.Tests/EarningsReconciliationTests.cs` (1113 lines, 34 Facts/Theories
at time of audit; exact counts must be re-read at implementation time since review
agents may have moved the ground).
Collection: `[Collection("Sequential")]` (touches static `ParsekLog` sink + suppresses
`GameStateStore` logging).
Ctor/Dispose: resets `ParsekLog` sink, suppresses `GameStateStore` logging. Does NOT
reset `RecordingStore` / `Ledger` / `LedgerOrchestrator` state -- the tests operate
purely on the two static methods `LedgerOrchestrator.ReconcileEarningsWindow` and
`LedgerOrchestrator.ReconcileKscAction` and pass in their own event and action lists.

Helpers available for reuse:
- `MakeFundsChanged(ut, before, after)` -- untagged `FundsChanged` event.
- `MakeKeyedFundsChanged(ut, before, after, reason)` -- keyed funds event with
  `TransactionReasons` key.
- `MakeKeyedScienceChanged(ut, before, after, reason)` -- keyed science event.
- `ReconcileKsc(events, ledger, action, ut)` -- thin wrapper over
  `LedgerOrchestrator.ReconcileKscAction`.

Two method targets in the current file:
A. `ReconcileEarningsWindow(events, actions, startUT, endUT)` -- commit-time bulk
   reconciliation. Accepts transformed types too (`ContractComplete`,
   `MilestoneAchievement`, `ContractFail`, `ContractCancel`, etc.) and sums them
   directly against store deltas. Three WARN tags: `Earnings reconciliation (funds|rep|sci)`.
B. `ReconcileKscAction(events, ledger, action, ut)` -- per-action KSC reconciliation.
   Branches on `ClassifyAction`:
   - `NoResourceImpact`: silent.
   - `Transformed`: `VerboseRateLimited` "KSC reconciliation: <T> skipped" line only.
   - `Untransformed`: pair event + WARN on delta mismatch / missing match.
   Three WARN tags: `KSC reconciliation (funds|rep|sci)`.

Pinned today (do NOT duplicate):
- A-path: `PerfectMatch` no-warn, `MissingFundsEarning` warn, Reputation mismatch warn,
  Science mismatch warn, `OutsideWindow` ignored, `ContractComplete` matches,
  Milestone matches (funds+rep only), `FundsSpending` matches dropped negative.
- B-path positive (no-warn): part purchase, facility upgrade, facility repair, tech
  unlock, kerbal hire, contract advance.
- B-path negative (warn): key mismatch, delta mismatch, event outside epsilon, event
  just inside boundary, event just outside boundary.
- B-path transformed-skip (no warn): `ContractComplete`, `MilestoneAchievement`,
  `ReputationEarning`, `ContractCancel`.
- B-path coalescing: two part purchases coalesced no false warn, both perspectives.
- B-path windowing: 0.3 s-apart separate events, opposing errors both warn.
- Defensive: `KerbalAssignment` no-op, null events, null ledger, zero-delta bypass.
- `ClassifyAction` direct: `PartPurchase` untransformed, `ContractComplete` transformed,
  `KerbalAssignment` no-impact, `StrategyActivate` transformed (Phase E1.5 reason),
  `PartPurchase` bypass-off still untransformed.
- #448/#451 bypass: zero-cost silent, bypass-off missing warns, entry cost matched,
  legacy cost mismatch warns.

Gaps NOT covered today (this plan's targets): see section "Coverage gaps" below.

## Audit of ReconcileKscAction.ClassifyAction (which action types reach which bucket)

Untransformed (per-action KSC reconcile, warns on mismatch):
- `FundsSpending (source=Other)`      -> `RnDPartPurchase`,       expected -FundsSpent
- `FundsSpending (source=VesselBuild)` -> `VesselRollout`,          expected -FundsSpent
- `ScienceSpending`                    -> `RnDTechResearch`,        expected -Cost
- `FacilityUpgrade`                    -> `StructureConstruction`,  expected -FacilityCost
- `FacilityRepair`                     -> `StructureRepair`,        expected -FacilityCost   (GAP #6 targets the commit-window side)
- `KerbalHire`                         -> `CrewRecruited`,          expected -HireCost       (GAP #7 targets the end-to-end flow)
- `ContractAccept`                     -> `ContractAdvance`,        expected +AdvanceFunds   (GAP #1 targets the commit-window side)

Transformed (VERBOSE skip, NO warn even on mismatch):
- `FundsSpending (source=Strategy)`  (Phase E1.5)
- `StrategyActivate`                 (Phase E1.5)
- `ContractComplete`                 (strategy + rep curve)
- `ContractFail`, `ContractCancel`   (rep curve -- GAP #2 must target ReconcileEarningsWindow)
- `MilestoneAchievement`             (mod-transform safety -- GAPs #3, #5 must target ReconcileEarningsWindow)
- `ReputationEarning`, `ReputationPenalty`  (rep curve)
- `FundsEarning`, `ScienceEarning`   (safety; GAP #4 must target ReconcileEarningsWindow)

NoResourceImpact (silent short-circuit):
- `KerbalAssignment`, `KerbalRescue`, `KerbalStandIn`, `FacilityDestruction`,
  `StrategyDeactivate`, `FundsInitial`, `ScienceInitial`, `ReputationInitial`, default.

Bucket implications for the 8 gaps:
- #1 Contract advance at accept: `Untransformed` path is already covered at
  `ReconcileKsc_ContractAdvance_...`. This bug entry is about the commit-window side --
  `ReconcileEarningsWindow` spanning the `ContractAccept` UT. Add that test. AUDIT NOTE:
  the `ReconcileEarningsWindow` switch does NOT sum `ContractAccept.AdvanceFunds`, so
  this test would see a false-positive funds-missing WARN today. See Risks and
  "Production gaps surfaced".
- #2 Contract fail / cancel penalties: `Transformed` at KSC. Only `ReconcileEarningsWindow`
  is viable. The switch already handles these in-window.
- #3 Milestone with science reward: `Transformed` at KSC. Only `ReconcileEarningsWindow`.
  Switch already handles `MilestoneScienceAwarded`.
- #4 Standalone `ScienceEarning` happy-path: `Transformed` at KSC. Only
  `ReconcileEarningsWindow`. Only negative case is pinned today -- add the positive.
- #5 World-first / progress reward: enriched `MilestoneAchievement` flows, same
  `Transformed` bucket at KSC, use `ReconcileEarningsWindow`.
- #6 Facility repair: `Untransformed`. KSC positive match already pinned; commit-window
  side is unpinned. AUDIT NOTE: the switch does NOT sum `FacilityRepair.FacilityCost`
  (nor `FacilityUpgrade.FacilityCost`). See Risks and "Production gaps surfaced".
- #7 Kerbal hire: `Untransformed`. Both unit sides pinned; the bridge test
  (`OnKscSpending` + paired `FundsChanged(CrewRecruited)` event end-to-end) is
  missing. Add it.
- #8 +34400 legacy reproducer: end-to-end. Fixture + `OnKspLoad` + assert
  `MigrateLegacyTreeResources` injects + `ReconcileEarningsWindow` over the tree's
  UT window sees zero mismatch.

## Coverage gaps -- per-gap test design

All tests go in `Source/Parsek.Tests/EarningsReconciliationTests.cs` unless otherwise
noted. Tests live in the existing `[Collection("Sequential")]` class. Naming convention
matches existing tests: `Reconcile_<Scenario>_<Expected>` for `ReconcileEarningsWindow`
targets, `ReconcileKsc_<Scenario>_<Expected>` for `ReconcileKscAction` targets.

### Gap 1 -- Contract advance at accept (commit-window positive match)

Purpose: pin that a `ContractAccept` action with `AdvanceFunds` emits a
`FundsChanged(ContractAdvance)` delta that `ReconcileEarningsWindow` treats as a match.

New test method: `Reconcile_ContractAcceptAdvance_MatchesStore_Silent`.

Fixture: reuse `MakeFundsChanged` (untagged) -- `ReconcileEarningsWindow` ignores event
keys and only sums deltas by event type.

Events: `FundsChanged +2000` at `UT=150`.
Ledger action: `GameAction { UT=150, Type=ContractAccept, AdvanceFunds=2000,
ContractId="c-adv" }`.

Production audit: the `ReconcileEarningsWindow` switch in
`LedgerOrchestrator.cs:319-355` (line numbers per plan-time snapshot -- re-verify)
does NOT sum `ContractAccept.AdvanceFunds` into `emittedFundsDelta`. Two paths:
- Path (a) -- scope-tight: write the test as a pinned-mismatch today (store=+2000
  vs emitted=0, assert `"Earnings reconciliation (funds)"` fires) with a clear
  TODO comment and file a follow-up entry in `todo-and-known-bugs.md`. Flip to
  silent once production catches up.
- Path (b) -- holistic: add `case GameActionType.ContractAccept: emittedFundsDelta +=
  a.AdvanceFunds;` in the same PR, and the test asserts silent. Small production
  change (~3 lines), naturally surfaced by the audit.

Recommendation: path (b). Argument: the bug's stated goal is "a broader coverage
matrix" that makes downstream Phase E1.5/E2 review easier. Leaving two known-bad
switch cases as pinned-mismatch tests defeats the point. The production fix is
mechanical; it matches the shape of the existing `ContractComplete` case already
in the switch. Re-discussed at clean-review time.

Assertion (path b): `Assert.DoesNotContain(logLines, l => l.Contains("Earnings
reconciliation"));`.
Assertion (path a): `Assert.Contains(logLines, l => l.Contains("Earnings
reconciliation (funds)") && l.Contains("2000"));`.

### Gap 2 -- Contract fail / cancel penalties (commit-window positive match)

Purpose: pin that `ContractFail` and `ContractCancel` penalties reconcile in the
`ReconcileEarningsWindow` path.

New test methods:
- `Reconcile_ContractFailPenalty_MatchesStore_Silent`
- `Reconcile_ContractCancelPenalty_MatchesStore_Silent`

Fixture: two events per test (a `FundsChanged` delta and a `ReputationChanged` delta,
both signed negative) plus the matching `ContractFail`/`ContractCancel` action with
`FundsPenalty` and `RepPenalty`. The switch already subtracts these -- no production
change.

Events:
- `FundsChanged -1000` at `UT=150` (`before=20000, after=19000`).
- `ReputationChanged -5` at `UT=150` (`before=10, after=5`).
Ledger action: `GameAction { UT=150, Type=ContractFail (or Cancel), ContractId="cf1",
FundsPenalty=1000, RepPenalty=5 }`.
Assertion: `Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));`.

### Gap 3 -- Milestone with science reward (commit-window positive match)

Purpose: existing `Reconcile_MilestoneAchievementMatchesStore_Silent` pins funds+rep
only. Extend with a science-bearing milestone.

New test method: `Reconcile_MilestoneAchievementWithScience_MatchesStore_Silent`.

Fixture: three events (Funds + Rep + Science) and one `MilestoneAchievement` action
with all three `*Awarded` fields. Switch already handles `MilestoneScienceAwarded`.

Events:
- `FundsChanged +3000` at `UT=150`.
- `ReputationChanged +5` at `UT=150`.
- `ScienceChanged +20` at `UT=150`.
Ledger action: `GameAction { UT=150, Type=MilestoneAchievement, MilestoneId="Mun/Landing",
MilestoneFundsAwarded=3000, MilestoneRepAwarded=5, MilestoneScienceAwarded=20 }`.
Assertion: `Assert.DoesNotContain ... "Earnings reconciliation"`.

### Gap 4 -- Standalone ScienceEarning happy path

Purpose: only the mismatch case is pinned today (event with no action). Add the
symmetric positive case.

New test method: `Reconcile_ScienceEarningMatchesStore_Silent`.

Event: `ScienceChanged +16.44` at `UT=150` (before=0, after=16.44).
Ledger action: `GameAction { UT=150, Type=ScienceEarning, SubjectId="surfaceSample@Mun",
ScienceAwarded=16.44 }`.
Assertion: `Assert.DoesNotContain ... "Earnings reconciliation"`.

### Gap 5 -- World-first / progress reward enriched detail

Purpose: `ProgressRewardPatch` enriches a pending `MilestoneAchievement` with
funds/science/reputation via the shared `EnrichPendingMilestoneRewards` OR emits a
standalone `MilestoneAchievement` via `EmitStandaloneProgressReward` (world-first
path where `OnProgressComplete` was bypassed, e.g. `RecordsSpeed`). The ledger
action must reconcile cleanly against the three store deltas on the same UT.

New test method: `Reconcile_WorldFirstProgressReward_MatchesStore_Silent`.

Approach: hand-rolled `MilestoneAchievement` action mirroring the shape
`EmitStandaloneProgressReward` + `GameStateEventConverter` produce. The
`MilestoneRewardCaptureTests` suite already pins the Harmony postfix wiring end-to-end;
these reconcile tests stay at the `ReconcileEarningsWindow` level to avoid duplicating
that coverage.

Events: `FundsChanged +4800`, `ScienceChanged +0.0` (omit the event since delta is 0),
`ReputationChanged +2` at `UT=100`.
Ledger action: `GameAction { UT=100, Type=MilestoneAchievement,
MilestoneId="RecordsSpeed/Kerbin", MilestoneFundsAwarded=4800, MilestoneRepAwarded=2,
MilestoneScienceAwarded=0 }`.
Assertion: `Assert.DoesNotContain ... "Earnings reconciliation"`.

(Optional second integration test `Reconcile_WorldFirstProgressReward_StandaloneEmit_...`
 is explicitly dropped -- the Harmony path requires live runtime and is covered
 transitively by `MilestoneRewardCaptureTests` + this test.)

### Gap 6 -- Facility repair commit-window match

Purpose: KSC positive test exists. Add the `ReconcileEarningsWindow` positive test
for a repair action whose event lands in the same recording's UT window.

New test method: `Reconcile_FacilityRepair_MatchesStore_Silent`.

Production audit: `ReconcileEarningsWindow` switch does NOT sum
`FacilityRepair.FacilityCost` nor `FacilityUpgrade.FacilityCost` into `emittedFundsDelta`.
Same decision as gap #1 -- recommendation path (b): add two switch cases in the
same PR (`case GameActionType.FacilityRepair: emittedFundsDelta -= a.FacilityCost;`
and the symmetric `FacilityUpgrade`) and pin the test as silent.

If path (a) is taken, both gap #1 and gap #6 tests ship as pinned-mismatch with
coordinated follow-up entries.

### Gap 7 -- Kerbal hire cost match via OnKscSpending end-to-end

Purpose: `GameStateRecorderLedgerTests` pins that `OnKscSpending` writes a
`KerbalHire` action with correct cost. `EarningsReconciliationTests` pins that
`ReconcileKscAction` stays silent for a well-paired hire. No test combines the two:
feeding a `FundsChanged(CrewRecruited)` event into `GameStateStore`, calling
`OnKscSpending` with the `CrewHired` event, and asserting end-to-end (ledger write
+ reconcile) produces no WARN.

New test method: `OnKscSpending_CrewHired_WithPairedFundsChangedEvent_ReconcilesSilently`.

Fixture pattern: borrow `GameStateStore` + `LedgerOrchestrator` reset pattern from
`LegacyTreeMigrationTests`. Two options:
- (a) add `ResetForTesting` calls inside a `try/finally` wrapping this one test
  (minimises disruption to existing 34 pinned tests).
- (b) bifurcate into a new `EarningsReconciliationEndToEndTests` class with the
  reset pattern in ctor/Dispose.

Recommendation: (a). Only one end-to-end test; the try/finally stays local.

Steps:
1. `GameStateStore.AddEvent` `FundsChanged` keyed `"CrewRecruited"` at `UT=400`,
   delta `-62113`.
2. `GameStateStore.AddEvent` `CrewHired` at `UT=400` with detail
   `"trait=Pilot;cost=62113"`.
3. `LedgerOrchestrator.OnKscSpending(the CrewHired event)`.
4. Assert `Ledger.Actions` has a `KerbalHire` with `HireCost=62113`.
5. Assert `logLines DOES NOT contain "KSC reconciliation (funds)"`.

Cleanup in `try/finally`: `GameStateStore.ResetForTesting`, `Ledger.ResetForTesting`,
`LedgerOrchestrator.ResetForTesting` (verify exact helpers exist; fall back to
(b) if the state surface forces it).

### Gap 8 -- Tree-scoped +34400 legacy reproducer

Purpose: bridge Phase A's unit tests with Phase B's reconciliation invariant.
Synthesize a legacy-format save with `tree.DeltaFunds=+34400`,
`ResourcesApplied=false`, zero ledger coverage. Call `OnKspLoad`. Assert
`MigrateLegacyTreeResources` injected the synthetic. Then run
`ReconcileEarningsWindow` over the tree's UT window and assert zero mismatch.

New test methods (in a NEW file -- see below):
- `LegacyTree_34400_OnKspLoadInjectsSynthetic_ReconcileWindowMatches`
- `LegacyTree_34400_WithStoreEvent_ReconcileWindowMatchesSynthetic`

File: `Source/Parsek.Tests/LegacyTreeReconciliationRepro438Tests.cs` (NEW). Needs
the same reset pattern as `LegacyTreeMigrationTests` (ctor: reset `GameStateStore`,
`RecordingStore`, `LedgerOrchestrator`; Dispose: symmetric teardown). Put this in a
separate file rather than polluting the lightweight setup of
`EarningsReconciliationTests`.

Fixture pattern borrowed verbatim from `LegacyTreeMigrationTests.MakeTree` +
`RegisterTree` helpers. DO NOT extract into a shared fixture file in this PR --
copy the needed shape locally (approx 20 lines). Rationale: one reuse; the
indirection is not warranted until there's a second caller.

Steps (test 1):
1. Build a tree: `deltaFunds=+34400`, `startUT=100`, `endUT=200`,
   `rootRecordingId="rec-root"`.
2. `RecordingStore.AddCommittedTreeForTesting(tree)`.
3. `LedgerOrchestrator.OnKspLoad(validRecordingIds: {rec-root}, maxUT: 1000)`.
4. Assert a synthesized `FundsEarning(LegacyMigration)` exists with
   `FundsAwarded=34400`, `RecordingId=rec-root`, `UT=200`.
5. Assert `tree.ResourcesApplied == true`.
6. Run `LedgerOrchestrator.ReconcileEarningsWindow(GameStateStore.Events, <the
   single synthetic>, startUT=100, endUT=200)`. With empty store events, both sides
   see zero delta -- assert no `"Earnings reconciliation (funds)"` line.

Steps (test 2): same fixture, but pre-populate `GameStateStore` with a
`FundsChanged +34400` event inside `[100, 200]` (simulating what the legacy save
originally captured). Both sides sum to +34400 -- match, no WARN.

## Test-ordering and fixture-dependency map

| Test | File | Resets GameStateStore | Resets Ledger | Independent |
|---|---|---|---|---|
| Reconcile_ContractAcceptAdvance_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_ContractFailPenalty_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_ContractCancelPenalty_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_MilestoneAchievementWithScience_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_ScienceEarningMatchesStore_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_WorldFirstProgressReward_... | EarningsReconciliationTests.cs | no | no | yes |
| Reconcile_FacilityRepair_... | EarningsReconciliationTests.cs | no | no | yes |
| OnKscSpending_CrewHired_WithPaired... | EarningsReconciliationTests.cs | yes (try/finally) | yes | within-file isolated |
| LegacyTree_34400_... (both) | LegacyTreeReconciliationRepro438Tests.cs (new) | yes | yes | new class |

## File-touch list

Tests:
- `Source/Parsek.Tests/EarningsReconciliationTests.cs` (append approx 8 tests: gaps 1-7).
- `Source/Parsek.Tests/LegacyTreeReconciliationRepro438Tests.cs` (NEW, approx 100 lines,
  gap 8 two tests).

Production (conditional on path-b decision for gaps #1 and #6):
- `Source/Parsek/GameActions/LedgerOrchestrator.cs` -- add three switch cases in
  `ReconcileEarningsWindow`:
  - `case GameActionType.ContractAccept: emittedFundsDelta += a.AdvanceFunds;`
  - `case GameActionType.FacilityUpgrade: emittedFundsDelta -= a.FacilityCost;`
  - `case GameActionType.FacilityRepair: emittedFundsDelta -= a.FacilityCost;`
  (Exact field names to be verified at implementation time; pattern mirrors the
  existing `ContractComplete` / `ContractFail` / `MilestoneAchievement` cases.)

Docs (same commit):
- `CHANGELOG.md` -- 1 line per user-facing change. Tests-only work is not
  user-visible; skip unless path (b) is taken, in which case add a 1-liner for
  the false-negative reconciliation fix.
- `docs/dev/todo-and-known-bugs.md`:
  - Strike-through entry #438 when tests merge.
  - If path (a): file two new entries "ReconcileEarningsWindow does not sum
    ContractAccept.AdvanceFunds" and "... FacilityUpgrade/Repair.FacilityCost"
    with "discovered during #438" tag.
- `docs/dev/done/plans/fix-438-phase-e1-test-coverage.md` -- this file; update to
  reflect review feedback.

## Logging-capture pattern

All tests reuse the existing `ParsekLog.TestSinkForTesting` pattern already wired in
the `EarningsReconciliationTests` ctor/Dispose -- no changes to the pattern, no new
sink hooks. The new standalone file (gap #8) copies the sink wiring verbatim from
`LegacyTreeMigrationTests`. Assertion style matches existing tests:

```
Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]") && l.Contains(expected));
Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
```

Per the `feedback_logging_testing` memory: the log is the primary observability surface.
Every new test MUST include at least one log assertion (positive OR negative). Silent
expected-no-warn tests assert the absence of the `Earnings reconciliation` /
`KSC reconciliation` tags; pinned-mismatch tests assert the presence of the specific
warn text including the signed delta values.

## Risks and open questions

1. Gaps #1 and #6 surface production gaps in `ReconcileEarningsWindow`. Two options:
   - (a) file follow-up tickets and pin the tests as mismatches today. Scope-tight,
     but leaves known-bad behavior pinned as "expected".
   - (b) fix production in the same PR (three switch-case additions). Slight scope
     expansion but discovered-and-fixed is a natural unit.
   Recommendation: (b). Revisit at clean review.
2. Gap #5 could also exercise the Harmony postfix (`ProgressRewardPatch.RoutePostfix`)
   via `MilestoneRewardCaptureTests` shape, but reconcile tests should stay at the
   `ReconcileEarningsWindow` level to avoid duplicating that coverage.
3. Gap #7 is the only end-to-end test. If xUnit ordering causes flake, escalate to
   separate file (gap #8 pattern).
4. Sequencing per `todo-and-known-bugs.md:400`: #438 should land before #439/#440.
   If #439 lands first, `ClassifyAction`'s transformed/untransformed split may shift
   (strategy activation moves to `Untransformed` via Phase E1.5). Re-audit at merge
   time.
5. `LegacyTreeReconciliationRepro438Tests` fixture duplicates `MakeTree` from
   `LegacyTreeMigrationTests`. If a second caller appears in #439/#440, extract to
   `Source/Parsek.Tests/Fixtures/LegacyTreeBuilder.cs` as a follow-up.

## Acceptance checklist

- [ ] 7 test methods added to `EarningsReconciliationTests.cs` covering gaps 1-7.
- [ ] 2 test methods added to `LegacyTreeReconciliationRepro438Tests.cs` for gap 8.
- [ ] Decision on path (a) vs (b) for gaps #1/#6 recorded in CHANGELOG and
      `todo-and-known-bugs.md`.
- [ ] All tests pass:
      ```
      cd Source/Parsek.Tests && dotnet test --filter FullyQualifiedName~EarningsReconciliation
      cd Source/Parsek.Tests && dotnet test --filter FullyQualifiedName~LegacyTreeReconciliationRepro438
      ```
- [ ] Full suite pass: `dotnet test` from `Source/Parsek.Tests`.
- [ ] `docs/dev/todo-and-known-bugs.md` #438 strike-through after PR merge.
- [ ] `CHANGELOG.md` entry if path (b) is taken.

## Acknowledgements

Audit by Plan subagent on 2026-04-18. Clean-review pass pending.
