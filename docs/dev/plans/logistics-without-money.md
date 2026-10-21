# Logistics without money (plan, 2026-10-21)

Owner rulings, 2026-10-21 (thread "Ledger rulings interview"):
- 13:32Z: "we delete money/ funds from the logistics system and go from there; logistics cycles
  should not touch the ledger, they only deliver resources to enable that gameplay". This replaces
  every route-money rule ruled earlier the same day (Model H, refund rows, gain cycles, the cost-only
  credit cap and credit at dispatch). Banked prepaid cycles were rejected as "too much technical debt".
- 13:39Z: Space Center origin routes stay, free, in every game mode, Career included. Accepted cost:
  a player can recover a vessel carrying delivered cargo for funds.

Why: route cycles are a real-time system. Every load back in time retires the route's cycles after its
cutoff and the live clock decides them again, so anything a cycle writes into the money ledger makes
the funds history depend on choices made after a rewind (pause, resume, moving an endpoint). With no
route money and no route rows, the ledger holds only committed history again and stays deterministic.
Cargo stays live: a rewind restores the vessels, and the cargo with them.

Analysis: Claude Doc "Supply routes and time travel: first principles"; research
`docs/dev/research/route-funds-vs-cargo-2026-10-21.md` section 7. Todo `LOGISTICS-WITHOUT-MONEY`.

## What stays

- Routes, cycles, dispatch cadence, delivery, pickup, origin debit for vessel origins, escrow, holds,
  pause / resume / Send Once, the Route History window, map route lines.
- The real-time rule: every load back in time retires the route events after its cutoff, restores the
  loop position from the loaded save, and the clock re-decides the cycles (design section 10.6).
- The Ghost Chain Rule endpoint hold and `RetiredRouteCargoStore` (a later committed recording's
  snapshot must not keep cargo from a retired cycle; design section 10.6).
- Space Center origin routes, now free in every mode.

## Phase 1: remove money (one PR)

Production (about 30 files, roughly -2,000 / +150 lines):
1. Dispatch: `EmitDispatchDebit` drops the funds half (`ComputeDispatchFundsCostForRoute`,
   `KscDispatchFundsCost`, `RouteKscFundsCost` on rows, the KSC clone of `CostManifest` onto the debit
   row). A Space Center cycle writes no `RouteCargoDebited` row; a vessel origin keeps its physical
   debit row until phase 2.
2. Delivery: delete step 7 of `ApplyDelivery` / `ApplyDeliveryFromPlan` (the live
   `Funding.Instance.AddFunds(-cost)` through `LiveDebitFunds`) and the money fields of
   `ApplyDeliveryContext`.
3. Recovery credit: delete `EmitPendingRecoveryCredit`, its every call site, the pending marker
   (`Route.PendingRecoveryCreditCycleId` / `DispatchUT`), `FlushDeferredSourceProblemCredits`, the
   `RouteStore` source-problem flush, `LiveCreditFunds` and the test seams.
4. Gate and status: remove `IRouteRuntimeEnvironment.IsCareer` / `KscFundsAvailable`, the gate step in
   `RouteDispatchEvaluator`, `RouteStatus.WaitingForFunds` paths, `RouteDispatchDecision.WaitFunds`, and
   the FundsShort hold arms in the Logistics window and hold clauses.
5. Calculators and UI: delete `RouteFundsCalculator.cs`, `RouteRunCostCalculator.cs`,
   `LogisticsCostPresentation.cs`, the Logistics window cost text (candidate cell, route detail
   "Cost/run", create dialog, `"KSC (funds)"` labels, funds tooltips), `RouteCreationFormatters` cost
   block and the ", cost N funds" suffix in `RouteHistoryBuilder`. Keep `Route.CreationTreeRecordingIds`
   (map route lines read it).
6. Recalculation: remove the route arms of `FundsModule` (dispatch, `ComputeTotalSpendings`, the two
   process methods, cashflow projection), `RecalculationEngine.IsEarningType` / `IsSpendingType`,
   `LedgerOrchestrator.ActionTouchesFundsBudget`, `SupersedeCommit` exclusions and `RouteModule`'s two
   money observers.

Old saves (the shape decisions; each needs a test):
- Enum ids 24 (`RouteCargoDebited`) and 28 (`RouteRecoveryCredited`) stay defined. Deleting them would
  read old rows as `Type = 0` (`ScienceEarning`).
- Old route money rows already in a ledger are past spending and earning: they stay as committed
  funds history and are no longer retired by route retirement, so loading or rewinding never moves
  past funds (ledger safety: never retroactively reduce earned value). FundsModule keeps reading the
  legacy rows for that; only new writes stop. VERIFY in review: whether keeping the legacy readers is
  smaller than a one-time load conversion of those rows into plain funds rows.
- A legacy owed credit (an old save written between a dispatch and its next crossing) is paid once at
  load as a plain committed funds row, then the marker key is ignored. VERIFY the amount source.
- `status = WaitingForFunds` reads as `Active`; `lastHoldKind = FundsShort` and old `RouteHeld` rows
  with that kind keep loading (keep the enum member or map it on read).
- Codec keys `kscDispatchFundsCost`, `pendingRecoveryCreditCycleId`, `pendingRecoveryCreditDispatchUT`,
  `routeKscFundsCost`: stop writing; read only where the legacy rules above need them.

Tests: delete the money-only files (RouteRecoveryCreditTests, RouteRunCostCalculatorTests,
RouteFundsCalculatorTests, RouteRecoveryLandingHarnessTests, LogisticsCostPresentationTests,
RouteDispatchReservationGateTests); drop `IsCareer` / `KscFundsAvailable` from the environment fakes
(about 25 files); add the old-save cells above and a Space Center route dispatching free in Career.
In-game tests: edit money assertions only; adding or removing an `[InGameTest]` moves the per-category
counts harness specs pin, so prefer edits. Harness: retire RVR-4 (career cost), RVR-17 (funds short)
and GUI-23 (funds-short hold census), the oracle's route-funds carve-out and IR-5's `careerKsc` token.

Docs in the same PR: logistics design (section 2.2 item 5 and the money sections listed in the
inventory), game-actions design 1.5 table row, 3.11, 3.13, user guide line on Career route charges,
CHANGELOG (0.10.6 Changed: Space Center routes are free; routes never charge or credit funds).

## Phase 2: route events leave the ledger (one PR)

Move the remaining route rows (`RouteDispatched`, `RouteCargoDebited` physical, `RouteCargoPickedUp`,
`RouteCargoDelivered`, `RoutePaused`, `RouteResumed`, `RouteHeld`, `RouteEndpointLost`) into a
route-owned event log persisted in the PARSEK scenario node with the route store, so a cold load or
quickload reads the save's own log.
- The log must serve today's readers: dispatch / pickup / delivery dedup by (route, cycle, stop); the
  hold-episode walk; catch-up resume; `DeriveTimelineStatus` and `ReconstructCycleCounters` at a rewind;
  retire-after-cutoff with kept and retired lists (go-back rewind, in-session load, Re-Fly bundle
  restore, stock revert keep-through-floor); `ChainTipRouteCargo` / `RetiredRouteCargo`; the discard
  leak report; Route History; delivery summary and flow rows.
- `RetiredRouteCargoStore` persistence moves from the ledger file to the route store.
- Delete `RouteModule`, the route fields and codec of `GameAction` (ids stay reserved), the route arms
  of TimelineBuilder, `ParsekScenario.Trim`, `SupersedeCommit`, `LedgerLoadMigration`, and the
  `RouteOrchestrator` entry in the ERS/ELS allowlist.
- Old saves: a one-time load step moves existing ledger route rows into the route logs.
Rough size: +1,000 / -1,500 production lines, about 3,000 test lines touched, about 75 files.

## Settled by the rulings

- Route cargo a committed flight later used (the open L3.8 case): with Space Center routes free, the
  money half is moot; a depot route's duplicated cargo after such a rewind is free cargo of the same
  kind a Space Center route already gives. No rule.
- Reservation-aware dispatch (PR #2159) goes with the gate in phase 1.

## Order of work

1. This plan and the rulings in the design docs (docs PR), then one clean-context review.
2. Phase 1 PR, review, merge; harness lanes that fly Logistics categories re-flown on V's PC.
3. Phase 2 PR, review, merge; same lanes re-flown.
