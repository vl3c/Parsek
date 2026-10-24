# Supply routes across a rewind: funds vs cargo (2026-10-21)

Status: superseded by the owner ruling of 2026-10-21 13:32Z (routes carry no money; section 7,
logistics design section 10.6.1, plan `docs/dev/done/plans/logistics-without-money.md`). Sections 1-6
are the analysis that led there, kept as history.

Read-only analysis for V's question on O4 (thread "Ledger rulings interview"): separate what a
supply run does to GLOBAL resources (the funds a run costs or earns) from what it does to LOCAL
resources (cargo in vessel tanks and inventories), and decide whether deliveries are recorded or
calculated at runtime. Read from PR #2155's head (c3b28d6) and the logistics design
(`docs/parsek-logistics-supply-routes-design.md`). CONFIRMED = read in code or design text;
INFERRED = follows from them, not traced end to end.

Standing rules it is checked against: the ledger never retroactively reduces earned value; no
time-travel paradoxes; rewinds keep D2 (committed history stays, a rewind frees nothing); a
quickload or load restores the game exactly as saved; and the general ledger rule adopted today
(committed outcomes cannot change earlier, known obligations are reserved, clicks that dig into a
reservation are greyed, any other loss stops at the reserved floor with the rest carried as a debt).

## 1. How a route works today

### 1.1 Global side (funds)

- A KSC-origin cycle in Career charges the gross dispatch cost (vessel parts plus the resources
  used or delivered) as a `RouteCargoDebited` row, and one dispatch interval later pays a recovery
  credit as a `RouteRecoveryCredited` row (`FundsModule.cs` 648 / 676). CONFIRMED.
- These rows are free-standing (`RouteId`, no `RecordingId`), written as each cycle fires. Whether a
  cycle fires depends on local state: destination full, origin depot short, endpoint lost (design
  section 8 step 4, 10.4). So the charges cannot be worked out from the schedule alone. CONFIRMED.
- The dispatch funds gate reads the raw stock counter (`LiveRouteRuntimeEnvironment.KscFundsAvailable`,
  `Funding.Instance.Funds >= cost`), although the design says "affordable under the existing ledger
  reservation rules" (section 8 step 4). Design-code drift. CONFIRMED.
- Committed route debits count as committed spendings in the reservation projection
  (`FundsModule.cs` ~269). CONFIRMED.

### 1.2 Local side (cargo)

- Cargo moves are physical writes into stock vessels at the dock crossing: origin depot debit
  (non-KSC origins), pickup, delivery, into the loaded vessel or its proto snapshot
  (`LiveDeliveryWriters`). `RouteModule` is observe-only: the cargo rows (`RouteCargoDelivered`,
  `RouteCargoPickedUp`) are records used for dedup, the timeline and snapshot bookkeeping; nothing
  computes tank contents from them. CONFIRMED.
- Tank contents cannot be derived from the schedule anyway: the player, ISRU, engines, other routes
  and stock processes all change them. A delivery is always "computed at runtime" in the sense that
  what fits is decided by the live tank at the crossing (10.5: partly full destination, the origin
  still pays). CONFIRMED.
- Where a route is in its schedule (loop cursor, per-stop cursors, window anchor, owed credit) lives
  in the `ROUTES` node of the save. CONFIRMED.

### 1.3 What a rewind does today (design 10.6)

- The world reverts through the save restore, so every physical cargo effect after the rewind point
  is undone with the vessels. CONFIRMED.
- Every route row after the cutoff is deleted (`Ledger.RetireFutureRouteActionsAtRewind`, Re-Fly via
  `ReconciliationBundle.Restore`), the route store drops later cycle state, cursors come back from
  the loaded save's route copy, and each cycle fires again live when the clock passes. This is the
  one place where a rewind deletes committed rows, an exception to D2. CONFIRMED.
- Committed recordings whose snapshots already hold delivered cargo (a station held back by the
  ghost chain and spawned from its tip, an end-of-recording spawn of a route-fed vessel) need a
  separate keep-and-drop store (`RetiredRouteCargoStore`) so the spawn copy drops cargo the rewind
  just un-paid, plus a "route state floor" for saves written inside the lead-time window. CONFIRMED.
- After a warp, a loop route fires ONCE for the highest owed cycle and snaps forward (10.7 as built),
  so delivered totals depend on how far the player warped. CONFIRMED.
- A discard without a load keeps both funds and cargo (Rec-3 residual, owner option C 2026-07-06).
  CONFIRMED.
- Co-op: routes are owner-local; foreign route rows import as inert history (coop design 7.11).
  CONFIRMED.

## 2. The key finding on the global side

A KSC route is normally a net COST per cycle (gross debit, smaller deferred credit). Deleting its
future rows at a rewind therefore RAISES funds, which is safe under never-reduce, with two exceptions:

1. **The straddling credit.** A cycle dispatched before the rewind keeps its debit; its credit (after
   the rewind) is deleted and only re-owed through the saved marker, paid at the next crossing, never
   if the route is paused or loses its endpoint. A net loss. CONFIRMED path.
2. **Profitable routes.** A pickup run that brings cargo home and recovers it at KSC is credited
   stock's recovery value, which includes the cargo (design table, case 5 "Sell-at-KSC"). When that
   credit exceeds the gross dispatch cost, each cycle is a net GAIN, and a rewind deletes those gains
   while committed purchases they paid for stay. They come back only if the route fires again at the
   same UTs. INFERRED (profit is possible by the costing formula; no measured case).

And one gap on the spending side: a re-fired cycle checks the raw counter, so it can spend money a
committed purchase needs (breaks today's general rule part 3).

## 3. The two models

### Model L (live): routes are standing orders, cycles are re-decided after a rewind

What a rewind does: as today. Fixes needed for the rules:
- L1. Keep the straddling credit at its committed UT when its dispatch row stays.
- L2. Dispatch gate reads reservations (the design already says so).
- L3. Profitable cycles: no clean fix. Keeping their credits while deleting their debits double-pays
  when the route fires again; keeping both is Model H. Options inside L: refuse to create a route
  whose cycle is net positive, or accept the gap.

### Model H (history): committed cycles are history, cargo is re-applied at runtime

- Funds: a rewind deletes no route row. Every cycle that fired before the rewind keeps its debit and
  credit at their UTs, like every other committed row (D2). Cycles after the committed present (the
  latest UT the timeline reached) are new and live, gated by reservations.
- Cargo: when the clock passes a committed cycle's crossing, its recorded cargo is re-applied
  physically, because the restore put the tanks back. No new ledger row is written. What fits is
  decided at runtime by the live tank (the 10.5 rule); an origin depot gives at most what it holds,
  so cargo is never created from nothing.
- The marker for "this vessel already holds cycle N" is the UT the vessel's state was taken at: a
  vessel loaded from a save holds every crossing up to the save's UT, a vessel spawned from a
  recording snapshot holds every crossing up to the snapshot's capture UT. Crossings after that UT
  are applied when the clock passes them, once. This likely needs no new storage, because the save
  UT and the snapshot capture UT already exist. VERIFY: the snapshot capture UT is what the
  optimizer tail-trim note already tracks (design 10.6 "end-of-recording snapshot"); confirm a spawn
  path can read it for every snapshot kind.
- What it removes: the rewind route-row delete (Rec-1 at rewinds), the keep-and-drop store, and
  probably the route state floor. What it keeps: the loop-position restore from the save, the
  quickload path (a load still restores exactly what the save holds, ledger rows included, per the
  quickload ruling), and the Rec-3 discard residual.
- Committed cycles occupy the schedule until the committed present. Pause, edit or Send Once in the
  rewound past take effect after it (the route row can say "committed until <date>" through the
  existing route UI text, no new surface).

## 4. Scenarios

| # | Scenario | Model L (today + L1, L2) | Model H |
|---|---|---|---|
| 1 | Rewind, play on, route unchanged | Cycles fire again at the same UTs (create-active routes are grid-deterministic, design Rec-5); funds dip and recover | Same cargo, funds never move |
| 2 | Rewind, route was re-activated at a different UT (pause-then-activate route) | Grid shifts, cycles land at other UTs; totals differ | Committed cycles keep their UTs; the shift applies only after the committed present |
| 3 | Rewind across a profitable route's cycles | Profits deleted; a committed purchase they paid for can go unpaid until the route fires again, or never | Profits stay; nothing unpaid |
| 4 | Rewind, then the new flight fills the destination | Cycle sees it full, does not fire, does not charge | Committed cycle is already paid, delivers what fits (10.5 rule) |
| 5 | Rewind, then the new flight drains the origin depot | Cycle waits for resources, does not charge | Committed cycle paid, the origin gives what it holds, delivery capped to that |
| 6 | Rewind, then the new flight destroys the endpoint | Route goes EndpointLost, stops charging | Committed cycles paid, nothing delivered after the loss; the player's own doing, like 4 and 5 |
| 7 | Station held back by the ghost chain, spawned from its tip snapshot | Keep-and-drop store strips cargo the rewind un-paid; replayed crossings are re-paid | Snapshot holds crossings up to its capture UT; later committed crossings apply when the clock passes; no store |
| 8 | Warp over several committed cycles after a rewind | Fires once for the highest owed cycle; total depends on warp length | Every committed cycle re-applies its recorded cargo; deterministic |
| 9 | Warp over several NEW cycles | Fires once (10.7) | Same as L for new cycles (a separate design choice) |
| 10 | Re-Fly start | Same as a rewind (bundle restore deletes route rows) | Same as a rewind: rows stay, cargo re-applies |
| 11 | F9 quickload / Load | Load restores the save's route state; rows after the save go (quickload ruling) | Unchanged: a load is not a rewind |
| 12 | Stock revert | Route rows after the launch are pruned with the revert's own rows | Under today's O2 ruling the revert removes only the reverted flight's rows; route rows written during that flight go with it, committed cycle rows stay |
| 13 | Discard without a load | Funds and cargo persist (Rec-3) | Same |
| 14 | Co-op import | Foreign route rows are inert history | Same, and local rows are history too: one model for both |

## 5. Assessment

- On the GLOBAL side Model H is what the rules ask for: committed route rows are committed history
  like everything else, so D2 holds without an exception, profitable routes cannot strand a committed
  purchase, and the straddling credit stops being a special case. Model L needs a carve-out for
  profitable routes that has no clean form.
- On the LOCAL side both models compute deliveries at runtime against the live tank; the difference
  is only whether a committed crossing is re-decided (L) or re-applied (H). H makes scenarios 4-6
  "paid, delivers what fits", which is the existing 10.5 rule and the player's own choice in the new
  timeline (V's debt ruling: the player decides the risks of rewinding and launching). L makes them
  "not paid, not delivered", friendlier in the moment but only because the history was re-decided.
- Cost of H: the route rewind reconcile is reworked (it was flown green); the snapshot capture UT
  must be readable on every spawn path; committed cycles lock the route's schedule until the
  committed present. It probably removes more code than it adds (the keep-and-drop store, the delete
  at rewinds, likely the state floor).

**Recommendation: Model H.** File it as a routes design task: a design section in the logistics
design (section 10.6 rewritten), the VERIFY on the snapshot capture UT, then a plan. Until it lands,
fix L2 (reservation-aware dispatch gate) on its own, since it is design-code drift and the general
rule needs it either way.

## 6. Questions for V

1. Model H or Model L.
2. In Model H, scenarios 4-6: a committed cycle that cannot deliver in full stays paid (recommended:
   yes, the 10.5 rule), or is refunded the undelivered share.
3. Whether L2 ships now, ahead of the routes design task (recommended: yes).

## 7. Outcome (owner rulings, 2026-10-21)

V first ruled Model H, then replaced it the same day. Under H a committed cycle stays charged even
when the new timeline cannot deliver it (the player moved or lost the destination), and holding the
destination as a ghost until the last committed cycle defeats the point of a supply route. The rule
adopted instead keeps every committed route row (no D2 exception) and adds, at the rewind, a
refund of each later committed cycle's net cost, once per cycle; the cycles are re-decided live as
in Model L. A cycle that made money stays history: not refunded, not re-run, its pickup re-taken
from the origin depot when the clock passes it (a depot emptied after the rewind is an open case).

Replaced again at 13:13Z. V: "I abhor the mix of time travel and global resources ... no paradoxes
allowed by design", and asked for a comparison of four systems: real-time or recorded cycles, each
with or without money. Recorded cycles break whenever the player moves the destination or empties
the depot after a rewind, with or without money; real-time cycles do not. Money then adds one
problem, a gain being retired, which a cost-only rule removes. V picked real-time cycles with money:
every load back in time retires the later route rows (as built), each cycle's recovery credit is
capped at its own cost and settles at dispatch. Refund rows, gain cycles staying history and the
emptied-depot shortfall were withdrawn.

Replaced a final time at 13:32Z. Even cost-only route money leaves the funds history depending on
what the player does after a rewind (pause, then spend, then rewind and resume). V rejected banked
prepaid cycles as too much technical debt and ruled: "we delete money/ funds from the logistics
system and go from there; logistics cycles should not touch the ledger, they only deliver resources
to enable that gameplay". At 13:39Z: Space Center routes stay, free in every mode. Todo
LOGISTICS-WITHOUT-MONEY, plan `docs/dev/done/plans/logistics-without-money.md`, logistics design 10.6.
