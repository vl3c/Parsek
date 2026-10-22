# Committed-value rule (O6 / O7): implementation plan

Status: plan, 2026-10-22. Rule 1 built 2026-10-22. Phase 2 (section 2.4, the stock pool
reference) BUILT 2026-10-22 on branch `o6-phase2-local`, with three deviations recorded in the todo
entry: a removed row steps back only what was stepped, not `-counted` (otherwise a released
reserved cost never comes back); load-migration and broken-ledger-recovery rows are classified
stock-applied, not Parsek-authored (they convert stock events the pool already holds, so a
Parsek-authored stamp would pay them twice once a kept leak holds them); the bundle restore keeps
each row's own flag. Found while building: phase 2 alone takes back a gain after a stock-clamped
fail (B2), so it ships only together with phase 3; and a save with no saved reference baselines
from the live pool, which writes an older save's running-vs-live gap once instead of keeping it.
Phases 3 and 4 open. Authority for the rule: game-actions design section 3.13 (owner rulings
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

## 2. Design (revision 5, after four clean reviews)

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
settles a cost row (2.3).

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
stays exactly stock (V's answer to the open-penalty card, 2.2). Reputation works in post-curve
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
breaks that (B1-B5 in the review record below). Revision 3 answers it incrementally:

- `lastWritten`: the pool value Parsek left at its last patch of that pool (a no-op patch counts,
  it re-reads the live value). Saved per pool in the PARSEK node with `cumLeak`, never re-read from
  the live pool at a load: a save made mid-flight holds a pool that already includes awards whose
  rows do not exist yet (#466 deferral), and re-reading it would count them twice.
- `counted`: per row id and pool, the amount the reference has already stepped for that row, saved
  in the PARSEK node next to `lastWritten` (only nonzero entries), not on the ledger row: the
  ledger file is one per save folder and outlives any one `.sfs`, while the pool, `lastWritten` and
  `counted` must come from the same `.sfs` (an F5 taken before a merge would otherwise read a row
  counted by a later patch as already in its pool). A row is stepped by `delta - counted`, not by
  "id not seen before": `EnrichPendingMilestoneRewards` puts a reward onto a milestone row that is
  already in the ledger, and stepping by the difference counts it once. A row in `counted` that is
  no longer in the ledger is stepped by `-counted` and dropped (`RepairDuplicateRolloutActions`,
  `DeduplicateAgainstLedger` and the revert strategy-row prune remove rows between patches),
  except rows a load removed because stock itself went back past them (a revert, a rewind), which
  the load-kind cell decides.
- `expected` = `lastWritten`, then for every ledger row with UT at or before now that stock applied
  (below) and whose `counted` differs from its delta, in ledger order, stock's own step: funds and
  science `expected = max(0, expected + (delta - counted))` when `AllowNegativeCurrency` is off
  (`Funding.AddFunds`, `ResearchAndDevelopment.AddScience`); reputation curves a nominal amount at
  `expected` (`Reputation.AddReputation` applies the curve at the live value, so the reference does
  too), and takes the stored value as is for the sources `ReputationModule` already stores
  post-curve (`KerbalDeath`, bailout, decline, strategy), which stock does not curve again.
  Science adds the change in the three pending adjusters since `lastWritten`
  (`ComputePendingAdjustedRunningScience`): a stock tech unlock the ledger does not hold yet is
  something stock applied.
- `leak = live - expected`; `cumLeak += leak` when it is beyond epsilon.
- Write `max(0, unflooredTarget + cumLeak)` for funds and science (the target before today's 0
  floor in `GetAvailableFunds` / `ResolveNegativeCurrencyPatchTarget`: flooring first and adding the
  leak after creates the difference on every purchase that spends a kept leak), reputation
  `target + cumLeak`; then `lastWritten` = the written value and every row's `counted` = its delta.

Why this holds: a committed row crossing the clock (B1) is not new, so `expected` does not move,
and the target moves by exactly what the reservation released and the row charged (0 for a
reserved cost). Stock's 0 clamp (B2) is applied row by row where stock applied it, so a fail
forgiven by the clamp is forgiven in the reference too (the walk's floor and debt decide the target;
the guard only keeps what the ledger does not model). The reputation curve (B4) is evaluated where
stock evaluated it. A pending tech unlock (B5) is in `expected`. A kept leak (`cumLeak`) is part of
every later write, so spending it is an ordinary new row and the reference stays exact (B3).

Which rows stock applied: every row converted from a stock game-state event (the flight's and the
KSC's), the rollout row, recovery rows, and the rows an ordinary Discard keeps
(`PreserveIrreversibleLiveGameplayOnDiscard` turns into rows awards stock already paid during the
discarded flight; that Discard is not authoritative). Rows Parsek authors without a stock
application (a load migration's rows, the reconciliation bundle's rows) are not in `expected`; they
arrive through a load or a recalculation that is authoritative today (a Re-Fly merge or discard),
and an authoritative patch writes the target plus `cumLeak` and marks every row counted. Phase 2 audits the 35
`Ledger.AddAction` / `AddActions` call sites (35 at plan time, in-game tests included) and pins the
classification in a source gate, so a new writer has to choose.

`cumLeak`, `lastWritten` and `counted` are saved in the PARSEK scenario node and always come from
the same `.sfs` as the live pool. For an in-session load,
where the ledger stays in memory, the rows the save does not hold are pruned or kept by the load
kind, so the reference is a new state category in `LoadReconcilePolicy` with a decided cell per load
kind (cold, plain rewind, Re-Fly start, Discard Re-fly, stock revert, quickload) and a test per
cell. A save without the values starts from `lastWritten` = the live pool and every row counted,
which is today's behaviour for that save. With no reservation and no leak this is exactly
today's guard, so every existing `DrawdownGuardTests` cell stands; `ComputeEffectiveAffordable`
(BUG-G) adds `cumLeak` the same way. It also fixes the latent release bug of section 1: a
reservation that releases moves the target, and nothing in `expected` fights it.

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
   saved `cumLeak`. Fixes the latent release bug.
3. **Floor and debt** (2.3): `ReservedFloorPlan`, the settle step, the single pipeline, the Debt
   tooltip line.
4. **Clicks and open reservations** (2.5): predicates, the launch mark and backstop, the Mission
   Control kind, open deadline penalties leaving the pool reserve, Reserved including `openRes`.

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
cell, a reserve release, load with and without saved `cumLeak`, and the row-writer classification
gate; the predicates
and each patch decision; the single pipeline (`ProbeModuleAtCutoff`, `WalkBalancesOnClones` and
`Recalculate` agree).

Harness: grep the committed specs for pinned funds / science / reputation values that a phase can
move (phase 3 only after a rewind with a cost ahead; phase 4 moves post-rewind pools with open
deadline contracts) and the ledger ground-truth lanes; re-pin only for a ruled move. New career
lanes on the H73-H79 pattern: after a rewind, a flight's crew death before a committed strategy holds
reputation at the floor (phase 3); a purchase and a Mission Control Accept greyed while a committed
cost needs the money (phase 4); a reserve released by a contract completion comes back (phase 2).
Flown on V's PC.

## 5. Risks

- Phase 2 touches the guard every patch passes through; the no-reservation identity keeps today's
  behaviour wherever no reservation exists, and the saved `cumLeak` is a new PARSEK key (check the
  load-reconcile gates when adding it). The row-writer classification is the part most likely to
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
