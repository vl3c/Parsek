# Committed-value rule (O6 / O7): implementation plan

Status: plan, 2026-10-22. Phase 1 (rule 1) built 2026-10-22. Phase 2 BUILT 2026-10-22 on branch
`o6-phase2-local`, shippable alone: the stock pool reference (2.4), stock's 0 clamp in the walk (the
no-cost-ahead half of 2.3) and open penalties out of the pool reserve (2.2) moved into it, so no
pool goes lower in normal play. Deviations from this text are marked "Built:" where they apply
(2.2, 2.3, 2.4). Phases 3 and 4 open. Authority for the rule: game-actions design section 3.13 (owner rulings
2026-10-21, record `decisions/open-decisions.md` L1.1-L1.6). Todo:
`LEDGER-RETIREMENT-PATHS-OUTSIDE-REFLY` (O6, O7) and the P15 half of
`GUI-P15-D7-TWO-SAFETY-GATES-THAT-EXIST-AND-ARE-NEVER-ARMED`. O2 and O3 shipped separately (PR #2172).

The rule, applied to funds, science and reputation:

1. Committed outcomes do not move earlier: a new flight cannot fail a contract whose outcome is
   committed.
2. Known obligations are reserved up front: committed costs, and every open contract's fail penalty
   from accept to outcome.
3. A click that digs into a reservation is greyed with the reason (D1 stock-control pattern plus a
   Harmony backstop); the funds check is enforced, no longer advisory.
4. Any other loss applies only down to the reserved floor; the rest is a debt charged from the first
   room above the floor. Derived in the walk, no saved state.

Flights and launches are never blocked for their risk; a launch is refused only for its cost.

## 1. What the code does today (measured)

- **Funds and science pools ARE the reserved amount.** `KspStatePatcher.PatchFunds` /
  `PatchScience` write `GetAvailableFunds()` / `GetAvailableScience()` (the projected minimum,
  floored at 0), not the running balance. So stock's own affordability checks (facility menu, R&D,
  hire, part purchase, launch) already refuse a spend that digs into the reservation, as long as the
  pool equals the target. Reputation is patched to the running value (no reservation).
- **The projection runs only on a cutoff walk.** `RecalculationEngine.Recalculate` builds the
  full-timeline projection (`RunProjectionWalk` + `ProjectAvailability`) only when `utCutoff` has a
  value, which `RecalculateAndPatchForCurrentTimelineIfFutureActions` passes only when rows exist
  after now. In normal play (no future rows) availability is the legacy
  `initial + earnings - totalCommittedSpendings`.
- **Open-contract penalties are partly reserved already, by accident of the projection.** The
  projection walk judges deadlines at its horizon, which `ComputeProjectionHorizon` stretches to the
  latest contract deadline, so `ContractsModule.PrePass` injects a synthetic `ContractFail` at the
  deadline of every contract still active there, and its `-FundsPenalty` enters the projected
  minimum. Three gaps: only on cutoff walks; a contract with no deadline is not reserved; and the
  penalty sits at the deadline, not at accept, so earnings between a committed cost and the deadline
  can hide it (a stock fail can come any time before the deadline). Reputation has no projection.
- **The drawdown guard fights any reservation that releases.** `ApplyDrawdownGuard` keys on running
  vs live. Once the pool is patched to `available < running`, running sits above live by the
  reservation, so `IsGuardableUplift` is true on every later recalc and the target is capped at live.
  A reservation that RELEASES without a clock crossing (a reserved contract completes, a reserved
  committed spend leaves the timeline) can then never raise the pool: the released money is lost.
  The clock-crossing authorization (`IsLiveUnchangedSinceLastPatch`) covers only the clock reaching
  committed rows. Latent today (deadline reservations exist only after a rewind); rule 2 would make
  it fire on every contract completion in normal play. Worked numbers: running R, reserve S + P (open
  contract), live R - S - P; stock pays the completion W: live R - S - P + W; commit: running R + W,
  target R + W - S, running - live = S + P > eps, so the uplift guard caps at live and P never returns.
  The same holds for a fail (stock pays P on a pool already reduced by P; the recalc must give one P
  back and the guard refuses).
- **The guard is also O7's other hole.** The UP clamp returns `max(target, live)`, so after a leak the
  pool goes back to live, reservation included: stock then lets the player spend reserved money.
  `ComputeEffectiveAffordable` (BUG-G) adds `live - running` only when positive, which under-counts
  a leak by the reservation (conservative, not a hole).
- **Stock contract fail funnel** (decompiled, KSP 1.12.5 `Contracts.Contract`): every fail goes
  through the protected `SetState(State newState)`. `Fail()` calls `SetState(State.Failed)`; a
  failed non-optional parameter (`ParameterStateUpdate`) calls `SetState(State.Failed)` directly;
  `Update()` calls `SetState(State.DeadlineExpired)`. `SetState` runs `PenalizeFailure()` for
  `Failed` and `DeadlineExpired`. Stock's cancel penalty is `Lerp(FundsAdvance, FundsFailure, t)` and
  `Lerp(RepLossDeclined, ReputationFailure, t)`, never above the fail penalty, and stock sets
  `FundsFailure = max(FundsAdvance, ...)`. Stock contracts carry no science penalty, so rule 2 has
  nothing to reserve in science; science still gets rules 3 and 4 through committed costs.
- `CanAffordFundsSpending` has no production caller; `CanAffordScienceSpending` guards tech through
  `TechResearchPatch.IsScienceShort` and `CommittedActionDialog.ShowBlocked`.

## 2. Design (revision 6, after five clean reviews)

The first draft held every reservation in the stock pool and let the drawdown guard read a
remembered reserve. Its review found the flaw that shaped this revision: stock pays a fail penalty
out of the pool and clamps the pool at 0 (`Funding.AddFunds` with `AllowNegativeCurrency` off), so a
pool held back by an open contract's penalty loses the information the guard needs, and the guard
then creates or destroys money; the guard also undid the floor and the debt, because stock applies
every loss in full. Revision 2 keeps in the stock pool only what Parsek already holds there today
(committed costs), keeps open-contract penalties out of it, and gives the guard a stock-naive
reference. Its review showed that a reference rebuilt from the whole timeline still misreads
ordinary play (a committed row crossing the clock outside the KSC cursor, stock's 0 clamp after
any other move, the reputation curve at a different value, science's pending adjusters) and that
settling a cost row lets a kept leak pay for purchases without limit. Revision 3 makes the
reference incremental (2.4: the last written pool plus what stock itself applied since) and never
settles a cost row (2.3). Revision 6 (after the clean review of the shippable phase 2) turns a
measured leak into a ledger row, so the walk's running value is the pool's value, steps each row by
its effect in the visible walk instead of re-simulating stock, and keeps a row the walk injected out
of every pool (2.4).

### 2.1 Rule 1: refuse an early end of a contract with a committed outcome (phase 1, built)

`Patches/ContractEarlyFailPatch.cs`, a prefix on the protected `Contracts.Contract.SetState(State)`.
For an Active contract and `newState` `Failed` (charges the penalty; reached by `Fail()` and a failed
non-optional parameter) or `OfferExpired` (stock's `Update()` on an Active contract whose
requirements no longer hold), it refuses while
`StockUiReservationPredicates.CommittedContractResolutionAfter` (the D7 Cancel predicate) holds a
committed complete / fail / cancel after now. `DeadlineExpired` is not intercepted (the committed
timeline holds the same expiry). Parsek never calls `SetState` (`PatchContracts` goes through
`Contract.Load`). The refusal log is rate-limited per contract (an `Update()` refusal repeats every
frame). `dateFinished`, set by stock before `SetState`, stays set on a refused contract; nothing
reads it while it is Active. Completing earlier stays allowed. Session-suppressed Re-Fly rows are not
in the index, so section 2.12 governs a Re-Fly attempt.

### 2.2 Where each reservation lives

| Reservation | Held in the stock pool | Enforced by |
|---|---|---|
| Committed costs (funds, science) | yes, as today: pool = running - reserve | stock's own affordability checks, plus Parsek backstops |
| Committed costs (reputation) | no: the pool shows the running value | rule 3 predicates (strategy, Mission Control) |
| Open-contract fail penalties (rule 2) | no, in every pool | rule 3 predicates on every click that spends |
| Floor and debt (rule 4) | yes: the walk's running value itself | the walk |

Open penalties stay out of the pool because stock pays them out of it: a pool already lowered by the
penalty hits stock's 0 clamp when the contract fails, and the gap between what stock took and what
the ledger charged is indistinguishable from a leak. Today the projection does put an open
deadline-contract's penalty into the pool reserve on cutoff walks (the synthetic deadline fail);
phase 4 takes it out, in the same PR that adds the click enforcement, so protection never lapses.

**Built in phase 2 instead (2026-10-22, coordinator's call on V's rulings: no change may lower a pool
in normal play):** `RecalculationEngine.ProjectAvailability` leaves every projected row that is not in
the walk's input list out of the reserve (`CountsInReserve`, membership by reference; today only
`ContractsModule`'s synthetic deadline fail), in `Recalculate` and `ProbeModuleAtCutoff` alike, and
logs `Projection reserve for <module>: ... excludedSyntheticRows=N excludedSyntheticDelta=D`.
`ContractsModule` is unchanged and still injects the row into the projection walk. A committed
ledger `ContractFail` stays reserved. A synthetic fail whose deadline is already PAST is a visible
walk row, and since revision 6 it moves contract state only, never a pool (2.4, L5's shape): stock
files its own fail row when it charges one. Until phase 4 adds the click predicates, an open penalty
is protected by nothing: a player may spend the money a later fail needs, and stock's 0 clamp then
forgives the rest of that fail (below).

**Ruled by V (decision card, 2026-10-22):** open penalties are reserved only while a committed
purchase or strategy cost lies ahead (`openRes` counts only when `costAhead`). Normal play, where no
committed cost lies ahead, stays exactly stock; the same switch keeps 2.3's forgiveness at 0 when no
cost lies ahead.

### 2.3 Rule 4: the floor and the debt, in the walk

A pure `GameActions/ReservedFloorPlan.cs`, built per pool from a debt-free walk of the full timeline
on module clones (the existing projection walk, now run on every recalculation), with each row's
delta captured before the visible walk resets the derived fields: funds and science through
`TryGetProjectionDelta`, reputation through the walk's own post-curve `EffectiveRep`. Synthetic
deadline fails are told apart from ledger rows by membership in the input list, so
`ContractsModule.PrePass` / `CheckDeadlines` are untouched.

- **Cost rows** (what the floor protects; ruling L1.3's "committed purchase or strategy cost"):
  funds `FundsSpending`, `FacilityUpgrade`, `FacilityRepair`, `KerbalHire`, `StrategyActivate`
  setup funds; science `ScienceSpending`, `StrategyActivate` setup science; reputation
  `StrategyActivate` setup reputation. Repairs are cost rows like any purchase: a repair a new
  destruction made chargeable is charged in the debt-free walk too, so the reservation grows the
  moment the destruction lands, and anything it cannot cover becomes debt (L1.5).
- **Floor-limited losses** (never reserved for): reputation penalties with source `KerbalDeath` or
  `Other`. Nothing in funds or science today.
- **Per position i**: `costAhead_i` (a cost row lies after i); `need_i = max(0, -min over k in
  (i, lastCost_i] of sum_{j=i+1..k} d'(j))`, where `d'` drops floor-limited losses and open-contract
  synthetic fails (one backward pass). The window ends at the last cost row: a dip after the last
  committed cost leaves nothing unpaid. `floor_i = need_i` when `costAhead_i`, else none.

**The visible walk** (live modules, cutoff or not) runs each module as today, then one settle step
per pool after every row that is not a cost row: if the row lowered the running value below
`floor_i`, the part of the row's own delta below the floor is taken back and added to the pool's
debt (`min(-d, floor_i - running)` when `d < 0` and running ends below the floor). An open
contract's fail, a death, a contract fail the committed timeline itself holds, all stop at the
floor (this also covers an old save whose open penalties already exceed the room). A cost row is
never settled: a purchase is paid in full or refused at its click (2.5), and settling one would
hand part of the player's own purchase back as debt. A timeline that is already overcommitted
(reachable only from an older save or from a kept leak that was spent, 2.4) therefore runs its
running value below 0 on its cost rows; the pool is written at `max(0, ...)` as stock holds it,
and the shortfall shows as Debt in the tooltip. After the settle, a debt
is charged from the room above `max(floor_i, 0)`, so it waits for later gains once the last cost is
paid (ruling: "charged from the first room above the floor ... or later gains"). With no cost ahead
a fresh loss applies as stock applies it: funds and science stop at 0 and the rest is forgiven
(`AllowNegativeCurrency` off; today the walk runs negative and only the guard keeps later earnings,
until the next authoritative write takes them), reputation may go negative. A career with no rewind
stays exactly stock (V's answer to the open-penalty card, 2.2).

**Built in phase 2 (2026-10-22), ahead of the floor and the debt:** the no-cost-ahead half only, on
every row and with no plan: in the visible walk (and every clone walk on the same module classes),
a funds or science row that is not a cost row and takes the running value below 0 stops at 0 and the
rest is forgiven (`StockCurrencyClamp.ForgivenBelowZero`; a row starting below 0 is forgiven in full,
never raised; off under `AllowNegativeCurrency`). The forgiven sum is added back to the
no-projection availability so it stays the running balance. Logged per row (`Zero clamp: ...
forgiven=`). Phase 3 replaces it with the floor and the debt where a cost lies ahead; future rows in
the projection are not clamped until then. One difference from the cost-row list above: science's
`StrategyScienceDebit` (the science half of a currency conversion) is a cost row, as its funds half
(a `FundsSpending` row) already is; that row can sort ahead of the earning that funded it, and
forgiving it would create science. Science has no other non-cost loss, so the science clamp has no
row to act on today. Reputation works in post-curve
units; the reservation for a strategy's reputation cost comes from the debt-free walk's curve, so
at high reputation it can be a few percent short of the visible walk's cost; the cost row then
charges the difference (it is the cost itself) and the result can dip that much below 0. Accepted
and documented; nothing is unpaid.

Worked rulings: L1.4 (reputation 60, committed 40-rep strategy day 50, deaths of 30 at day 20):
reputation 40 at day 20 with a debt of 10, 0 after the strategy, the debt paid by the next 10 of
gains. L1.2 with an old-save open contract (120k, Y with a 50k penalty, committed 100k upgrade
day 50, Y fails day 20): 100k after the fail, debt 30k, the upgrade paid, the debt from later gains.

`Recalculate`, `ProbeModuleAtCutoff` and `WalkBalancesOnClones` (Re-Fly merge dominance) go through
one planned walk. A new `IReservedFloorModule` (funds, science, reputation) receives the plan; the
engine keeps knowing no module type. The plan is derived from the rows on every walk; no debt is
saved.

### 2.4 The guard reads an incremental stock reference

The drawdown guard asks one question per pool: did the live pool move by anything other than what
stock applied for rows the ledger holds? Revision 2 answered it from the whole timeline (`raw`, the
debt-free running value at now, minus a saved adjust) and its review found five ways ordinary play
breaks that (B1-B5 in the review record below). Revision 3 answers it incrementally; revision 6
(below) changes where a measured leak lives and how a row is stepped.

- `lastWritten`: the pool value Parsek left at its last patch of that pool (a no-op patch counts,
  it re-reads the live value). Saved per pool in the PARSEK node, never re-read from the live pool
  at a load: a save made mid-flight holds a pool that already includes awards whose rows do not
  exist yet (#466 deferral), and re-reading it would count them twice.
- `counted`: per row id and pool, the row's effect in the walk the reference last committed
  (`Effect`, NaN for a row seen but not walked: a committed future row) and the part of it the
  reference stepped (`Stepped`), saved in the PARSEK node next to `lastWritten` as `id effect
  stepped` (a row whose effect and step are both 0 is not written), not on the ledger row: the
  ledger file is one per save folder and outlives any one `.sfs`, while the pool, `lastWritten` and
  `counted` must come from the same `.sfs` (an F5 taken before a merge would otherwise read a row
  counted by a later patch as already in its pool). A row in `counted` that is no longer in the
  ledger is stepped back by its `Stepped` part and dropped (`RepairDuplicateRolloutActions`,
  `DeduplicateAgainstLedger` and the revert strategy-row prune remove rows between patches); a row
  counted without a step (a committed future cost, reserved through the target) was never in
  `expected`, and stepping it back would hold a released reservation out of the pool for good.
- `expected` = `lastWritten`, then for every ledger row with UT at or before now that stock applied
  (below) and that the visible walk dispatched, its step: the row's EFFECT in that walk (the walk's
  running value after the row minus before it, as the walk applied it, the 0 clamp and the
  reputation curve included) minus `counted.Effect`. A row not counted steps by its whole effect; a
  row counted with no effect (NaN: a committed row crossing the clock) steps 0. Science adds the
  change in the three pending adjusters since `lastWritten` (`ComputePendingAdjustedRunningScience`):
  a stock tech unlock the ledger does not hold yet is something stock applied. There is no separate
  re-simulation of stock's step: the review of the shippable phase 2 showed one re-stepped through
  the clamp next to a pending change leaves a permanent leak (R4, R5).
- `leak = live - expected`. Beyond epsilon, the patch files it as a ledger row: an additive
  `GameActionType.StockPoolAdjustment` (id 39; `adjustPool` names the pool, `adjustAmount` the
  amount, untagged, at the patch's UT, reputation stored post-curve like `KerbalDeath`), written by
  `KspStatePatcher.FileStockPoolAdjustment` (a classified stock-applied writer: it records something
  stock applied that no other row holds). `Commit` counts it at creation (`Effect = Stepped =
  amount`), so a later walk's dispatch of it steps nothing. The walk carries it from then on:
  `FundsModule` / `ScienceModule` add it to the running value (clamp-exempt), `ReputationModule`
  adds it uncurved. So the walk's running value IS the pool's value, and the walk's own 0 clamp and
  reputation curve run where stock's do (R1-R3: a running value that differs from the pool by a
  kept leak clamps and curves at the wrong value).
- Write `max(0, unflooredTarget + adjustment)` for funds and science (this patch's adjustment only;
  earlier ones are in the target through the walk), reputation `target + adjustment`; then
  `lastWritten` = the written value and every row's `counted` = its walk effect.

Why this holds: a committed row crossing the clock (B1) is counted with no effect, so `expected`
does not move, and the target moves by exactly what the reservation released and the row charged
(0 for a reserved cost). Stock's 0 clamp (B2) and the reputation curve (B4) are the walk's own, at
the pool's value. A pending tech unlock (B5) is in `expected`. A kept leak is a row of the walk, so
spending it is an ordinary new row and the reference stays exact (B3).

A row the walk injected is not a row stock applied. `RecalculationEngine` records the walk's input
rows before the pre-pass and withholds every other row from the stock pool modules
(`IStockPoolModule`: funds, science, reputation), in their dispatch and their pre-pass alike, and
logs `Walk withheld N injected row(s) from the stock pool modules (contract state only, no pool
moves): [...]` once per walk. Today that is `ContractsModule`'s synthetic deadline fail: it retires
the contract (slot, terminal outcome) and moves no pool. Stock files its own fail row when it
charges one; a stock fail made before its row lands reads as a leak, files an adjustment, and the
row taken in later cancels it (R5). `ContractsModule.PrePass`, `CheckDeadlines` and
`ResolveDeadlineClockUT` are untouched. A contract stock ends Active -> OfferExpired files no
terminal row, so the walk's synthetic fail there now charges nothing, as stock did not
(STOCK-OFFER-EXPIRED-ACTIVE-CONTRACT-NO-TERMINAL-ROW).

Which rows stock applied: every row converted from a stock game-state event (the flight's and the
KSC's), the rollout row, recovery rows, the rows an ordinary Discard keeps
(`PreserveIrreversibleLiveGameplayOnDiscard` turns into rows awards stock already paid during the
discarded flight; that Discard is not authoritative), and the adjustment rows. Rows Parsek authors
without a stock application (a load migration's rows, the reconciliation bundle's rows) are not in
`expected`; they arrive through a load or a recalculation that is authoritative today (a Re-Fly
merge or discard). An authoritative patch writes the target, carries the stepped part of the rows
it removed as one adjustment row (`carried-removed`, so a leak kept before the patch stays kept),
and marks every row counted. Phase 2 audits the `Ledger.AddAction` / `AddActions` call sites and
pins the classification in a source gate, so a new writer has to choose. **Built:** 32 call sites,
every one `LedgerRowWriter.StockApplied` (`LedgerRowWriterClassificationTests`). The load migration
and the broken-ledger recovery are stock-applied, not Parsek-authored: their rows convert stock
events the pool already holds, so a Parsek-authored stamp next to a saved reference would pay them
twice. The reconciliation bundle does not add rows: it restores the bundle's own rows through
`Ledger.RestoreActions`, which keeps each row's flag. No production writer is Parsek-authored today.

The adjustment row is ledger state like any other: it serializes with the ledger, a cold
`Ledger.Reconcile` keeps it (it is an earning type), the Timeline hides it (it has no event to
show; `GameActionDisplay` still names it `Pool adjustment (<pool>)`), it is never tombstone-eligible
and never strict-blocks a supersede, and `LedgerOrchestrator`'s budget probes read it per pool.

`lastWritten` and `counted` are saved in the PARSEK scenario node and always come from the same
`.sfs` as the live pool. For an in-session load, where the ledger stays in memory, the rows the
save does not hold are pruned or kept by the load kind, so the reference is a state category in
`LoadReconcilePolicy` with a decided cell per load kind. A save without the values starts from
`lastWritten` = the live pool and every row counted. **Built:** "today's behaviour" read literally: a
measured first patch files the gap between live and its leak-free unfloored target (science: plus
its pending adjusters) as one adjustment row (`adopted-gap`) and writes the live pool, so an older
save keeps the pool its build left (whatever the old guard was keeping) and logs it at Info, never as
a GUARDED clamp; an authoritative first patch (a rewind, a Re-Fly merge) writes the target, as it
always has. With no reservation and no leak this is exactly today's guard, so every existing
`DrawdownGuardTests` cell stands; `ComputeEffectiveAffordable` (BUG-G) floors the probe's minimum,
which already carries the adjustment rows. It also fixes the latent release bug of section 1: a
reservation that releases moves the target, and nothing in `expected` fights it.

Known limits of revision 6: with a reservation the walk's running value is the pool plus the
reserve, so the walk's 0 clamp still runs above the pool by the reserve (phase 3's floor and debt
replace that clamp where a cost lies ahead). A transient leak (a stock change made before its row
lands) files a pair of opposite adjustment rows; a row back-dated before an earlier patch corrects
through a new adjustment row. After a rewind or a quickload to before its UT, an adjustment row is an
untagged row ahead of the clock, the same semantics as an untagged earning row; a Re-Fly attempt can
add untagged adjustment rows.

### 2.5 Rule 3: clicks

Per pool at now, from the plan: `spendable = target - openRes` (reputation: `running - need - openRes`),
with `openRes` the open-contract penalties counted per V's answer to 2.2.

- **Purchases** (funds: facility upgrade and repair, hire, part purchase, strategy setup, launch;
  science: tech, strategy setup; reputation: strategy setup): refused when `cost > spendable`.
  Stock already refuses a funds or science cost above the pool; Parsek adds the reservation that is
  not in the pool (`openRes`), and the whole of it for reputation. Each control reads one predicate
  for its greyed state and its backstop (pairing rule): the existing facility menu, R&D, Astronaut
  Complex and Administration marks, and every launch entry point: the editor Launch button, the
  Space Center launchpad and runway craft picker (it launches without the editor), and the editor's
  part-purchase prompt (new marks and backstops on stock's launch and purchase paths, located by
  decompile). A missed entry point would let a purchase spend money held for an open penalty
  (`openRes`), and that contract's fail would become debt, which breaks L1.2. The refusal text is one new `ReservationExplanation` sentence
  in house style. A launch is refused only for its cost.
- **Mission Control** (ruling L1.3, only when a committed purchase or strategy cost would go
  unpaid): refused when `costAhead` and `spendable + delta - addedReserve < min(0, spendable)` in any
  pool, so a Cancel or Decline that improves an already short pool is never refused. Accept:
  funds `+advance`, added reserve `FundsFailure`; reputation added reserve `ReputationFailure`.
  Decline: reputation `-RepLossDeclined`. Cancel: `-Lerp(advance, FundsFailure, t)` funds and
  `-Lerp(RepLossDeclined, ReputationFailure, t)` reputation, plus the release of the contract's own
  reserve; the reputation lerp can exceed `ReputationFailure`, so Cancel is checked like the others.
  A new `StockUiDecorationKind` (`ContractReserve`) through `MissionControlStockAnnotation.Decide`,
  backstops in `ContractAcceptPatch`, `ContractDeclinePatch`, `ContractCancelPatch`; the harness GUI
  mirror's kind lists (`test_gui_mirror`) learn the new kind.

### 2.6 Tooltips

The funds and science tooltips (`CurrencyReservationOverlay`) gain one `Debt:` line, shown only
while a debt is outstanding, and their Reserved amount includes `openRes` (budgeted by
`TooltipEchoBudgetTests`). Reputation gets no tooltip (none exists; the greyed controls carry the
reason).

## 3. Phases (one PR each, in this order)

1. **Contract early-end refusal** (2.1). Built on a local branch; lands after PR #2172.
2. **Guard reads an incremental stock reference** (2.4), the row-writer classification gate and the
   `StockPoolAdjustment` row. Fixes the latent release bug. Built, and widened so it ships alone:
   stock's 0 clamp in the walk (2.3, no cost ahead), open deadline penalties out of the pool reserve
   (2.2), an injected row kept out of every pool (2.4), and an older save's gap filed as one
   adjustment row (2.4).
3. **Floor and debt** (2.3): `ReservedFloorPlan`, the settle step, the single pipeline, the Debt
   tooltip line; replaces phase 2's plain 0 clamp where a cost lies ahead.
4. **Clicks and open reservations** (2.5): predicates, the launch mark and backstop, the Mission
   Control kind, Reserved including `openRes` (the open penalties left the pool reserve in phase 2).

Each phase updates CHANGELOG, the todo entry and design 3.9 / 3.13 / 14.4 in its own commits.

## 4. Tests

Unit (red-first where a defect exists): the L1.1, L1.2 and L1.4 worked rulings end to end through
`Recalculate`; the plan builder (need window, costAhead, floor none without a cost ahead, synthetic
fails by membership, repairs as costs); the settle step (every non-cost negative row kind, cost rows never
settled, an overcommitted timeline, debt charging above `max(floor, 0)`, negative reputation with no
cost ahead unchanged); the guard, red-first on each review case (a committed cost crossing the clock
in a plain recalculation; a fail clamped at 0 followed by a recovery in flight; a fail with no cost
ahead clamped at 0 followed by an earning; a kept leak then repeated purchases; a reputation gain far
from the walk's value; a pending tech unlock), plus the no-reservation identity over every existing
cell, a reserve release, load with and without a saved reference, the review's five sequences
against the `cumLeak` model (R1-R5), and the row-writer classification gate; the predicates
and each patch decision; the single pipeline (`ProbeModuleAtCutoff`, `WalkBalancesOnClones` and
`Recalculate` agree).

Harness: grep the committed specs for pinned funds / science / reputation values that a phase can
move (phase 3 only after a rewind with a cost ahead; phase 2 moves post-rewind pools with open
deadline contracts, a ledger-only past-deadline fail such as L5's, and any pool a stock-clamped fail
used to drag below 0) and the ledger ground-truth lanes; re-pin only for a ruled move. New career
lanes on the H73-H79 pattern: after a rewind, a flight's crew death before a committed strategy holds
reputation at the floor (phase 3); a purchase and a Mission Control Accept greyed while a committed
cost needs the money (phase 4); a reserve released by a contract completion comes back (phase 2).
Flown on V's PC.

## 5. Risks

- Phase 2's GUARDED WARN now means a measured leak, and every measured leak is a ledger row. A
  deadline fail stock applies in flight while a cutoff walk already injects its synthetic fail reads
  as a leak (the injected row moves no pool), files an adjustment, and the captured row cancels it
  when it lands: a pair of rows, no double charge (R5).
- Phase 2 touches the guard every patch passes through; the no-reservation identity keeps today's
  behaviour wherever no reservation exists. The reference is new PARSEK state and
  `StockPoolAdjustment` a new ledger row type (additive, no schema bump: an older build warns
  `Unknown action type id '39'` at load and reads the row as a payload-less default row that moves
  no pool, so its own guard holds the pool as before). The row-writer classification is the part most likely to
  rot; the source gate is what keeps it honest.
- Phase 3 runs the debt-free full walk on every recalculation (today only on cutoff walks): about
  twice the walk cost, well under a millisecond at 500 rows (design 15.3).
- Coordination: nothing here touches `ContractsModule.PrePass`, `CheckDeadlines` or
  `ResolveDeadlineClockUT` (the Re-Fly rule gaps thread's files).

## 6. Review record

- Revision 1 (first clean review, 10 findings): the clamp created money, the guard undid floor and
  debt, repairs were missing from the need, a load default leaked forever, L1.2 / L1.4 contradicted
  the algorithm, the reputation curve, an over-broad Mission Control predicate, rule-1 gaps
  (`OfferExpired`, log spam, `dateFinished`), the Cancel reputation lerp. All addressed in revision 2.
- Revision 2 (second clean review): B1 a committed row crossing the clock outside the KSC / TS
  cursor read as a leak and refunded the cost; B2 stock's 0 clamp misread once anything else moved
  the pool, turning a stock-forgiven loss into debt; B3 settling a cost row let a kept leak pay for
  purchases without limit; B4 the reputation curve evaluated at the walk's value instead of the live
  one; B5 science's pending adjusters missing from the reference; plus the Space Center launch
  picker and the part-purchase prompt, and Mission Control refusing an improving Cancel. Revision 3
  answers all of them (2.3 no settle on cost rows, 2.4 incremental reference, 2.5 entry points and
  `min(0, spendable)`).
- Revision 3 (third clean review): B3 needed the leak added before the 0 floor; B4 must not curve
  reputation rows stored post-curve; N1 an ordinary Discard's kept awards are stock-applied, not
  authoritative; N2 a milestone row enriched in place needs a per-row counted amount; N3
  `lastWritten` re-read from a mid-flight save counts deferred awards twice. Revision 4 answers them
  (2.4) and records V's open-penalty answer (2.2).
- Revision 4 (fourth clean review): M1 `counted` stored on the ledger rows is shared by every save
  of the folder, so a quicksave from before a merge read the merged row as already in its pool; M2
  rows removed between patches were never stepped back. Revision 5 saves `counted` in the PARSEK
  node with `lastWritten` and steps a removed row by `-counted`.
- Build of phase 2 (2026-10-22): stepping a removed row by `-counted` never releases a reserved
  future cost (fixed: step back the stepped part); the plan's Parsek-authored classification of the
  load migration and broken-ledger recovery would pay their rows twice (classified stock-applied);
  phase 2 alone took back a gain after a stock-clamped fail (B2) and wrote an older save's gap at its
  first load. The coordinator moved stock's 0 clamp (2.3) and the open-penalty exclusion (2.2) into
  phase 2 and ruled the older-save gap kept as `cumLeak`; the B2 cells now pin the kept gain.
- Revision 5 as built (fifth clean review, of a075d07): the walk clamped and curved at its running
  value while stock clamps and curves at the pool (running plus `cumLeak`): R1 a -500 leak, a 300
  fail stock clamped, then a 1000 earning wrote 800; R2 a +40k leak and a 30k fail the walk clamped
  wrote 40k (20k created); R3 a reputation gain curved at 0 instead of 500. `Measure` re-simulated
  stock's step apart from the walk: R4 a 25-cost tech pending at science 30 left a -20 leak for good.
  A synthetic deadline fail stock never charged reached the pool (L5), and R5 a stock fail plus the
  synthetic fail in one measured patch left -100 for good. Revision 6: a leak is a
  `StockPoolAdjustment` ledger row (no `cumLeak`), a row steps by its walk effect, and an injected
  row moves no pool. The R1-R5 cells read red against a075d07 (800, 40000, 550 for 535.6, 0 for 5,
  0 for 50) and green now.
