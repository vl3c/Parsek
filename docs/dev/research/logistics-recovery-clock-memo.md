# Logistics recovery clock decision memo (M6 / recovery-credit OQ1)

Status: harness + memo only. No behavior change. The maintainer decides from
this memo whether the second recovery clock is built.

## The question

Should the per-cycle recovery credit stop being a constant amount deferred one
dispatch interval, and instead fire when the RECORDED recovery physically lands
in each run's replayed timeline (a second clock keyed on the recorded recovery
UT, with cycle-overlap bookkeeping)? This is OQ1 of
`docs/dev/done/plans/logistics-recovery-credit.md` and the "Precise per-run
recovery landing" bullet of design doc 19.4 M6.

## The current model (shipped)

- Cycle K's credit is flushed at the NEXT dock crossing, i.e. at
  dispatchUT(K) + one dispatch interval, keyed on the prior dispatched cycle:
  `RouteOrchestrator.EmitPendingRecoveryCredit` (RouteOrchestrator.cs:3708).
  The pending marker is armed in `EmitLoopCycle` (RouteOrchestrator.cs:1849)
  and the multi-stop path (:1106); it is flushed at the `EmitLoopCycle` top
  (:1783), the blocked branches (:786 single-stop, :1063 multi-stop), the
  multi-stop replay branch (:1131), and the stop-crossing tails: `TryPause`
  (:303), armed pause-after-cycle (:3426), EndpointLost-at-delivery (:3193),
  and `RouteStore.RevalidateSources` (RouteStore.cs:1013).
- The AMOUNT is recomputed fresh from ELS at every flush:
  `RouteRunCostCalculator.SumRecoveredCredits` (RouteRunCostCalculator.cs:99)
  sums `FundsAwarded` over the `FundsEarning(Recovery)` rows of the source
  tree, scoped to the creation-frozen membership via `ResolveTreeRecordingIds`
  (RouteRunCostCalculator.cs:175, M-MIS-9-R1). Idempotency backstop:
  `IsRecoveryCreditAlreadyInLedger` (RouteOrchestrator.cs:3620) keyed on
  (RouteId, RouteCycleId).
- The recovery row's `UT` (stamped at recovery time by
  `LedgerOrchestrator.OnVesselRecoveryFunds`) is captured but NEVER read by the
  credit timing. `SumRecoveredCredits` ignores it.

## The concrete case the harness pins

`Source/Parsek.Tests/Logistics/RouteRecoveryLandingHarnessTests.cs` (all
active asserts pass against today's orchestrator; the ideal-model assert is
deliberately skipped and fails RED with the delta if un-skipped).

Constructibility: `RouteBuilder` clamps `DispatchInterval >= dock - rootLaunch`
ONLY (RouteBuilder.cs:194-217, the outbound leg); the post-undock
fly-home-and-recover leg is outside the rendered `[launch .. dock]` loop span
(RouteBackingMission.cs header) and outside the clamp, so its recorded
duration is unbounded relative to the interval. Both lobes are constructible
in v0:

EARLY lobe (fly-home = 4 intervals; interval 300 s, dock 1150, recorded
recovery 2350, per-run recovery 7300 funds):

- Run 0 dispatches at UT 1150. Shipped credit lands at UT 1450 (next
  crossing). UT-mapped ideal lands at UT 2350 (dispatch + 1200 s fly-home).
  The shipped credit is 900 s = 3.0 dispatch intervals EARLY.
- By UT 2050 (4th crossing) the shipped model has paid 3 credits = 21900 funds
  while ZERO recorded recoveries have physically landed (first at 2350). While
  the route overlaps, the player's balance runs a permanent lead of about
  (flyHome/interval - 1) per-run recoveries over the recorded timeline.

LATE lobe (interval 900 s, fly-home 600 s): run 0 dispatches at 1150, its
recorded recovery lands at 1750, but the shipped credit waits for the next
crossing at 2050: 300 s late. In the realistic version (a monthly resupply
whose recorded round trip takes hours) the lag approaches a full dispatch
interval: gross fronted at dispatch, recovery back a month later.

Bounded divergence, zero error in totals: every dispatched cycle receives
exactly one credit of the same constant amount under both models (the pause /
stop tails flush the owed credit), so cumulative funds converge; ONLY the
landing time differs. Steady-state per-cycle net is identical.

## What the second clock would cost (from the code as it stands)

- Persisted per-run queue. Today the deferral state is ONE pending marker (two
  sparse `Route` fields, RouteCodec.cs:168/333). With fly-home > interval,
  ceil(flyHome/interval) cycles are in flight simultaneously, each owing a
  credit at its own future UT; a single marker gets overwritten at the next
  dispatch and silently drops credits. Any landing-aligned timing therefore
  needs a QUEUE of (cycleId, landingUT) entries: a new ROUTE child node in
  `RouteCodec`, round-trip tests, and a bound on queue growth.
- Second idempotency surface. The current flush inherits the loop clock's
  once-per-crossing guarantee plus one ELS backstop. A UT-keyed clock fires
  BETWEEN crossings, so it needs its own firing condition in the ~1 Hz
  `RouteOrchestrator.Tick`, its own warp catch-up handling (several landings
  passed in one warp tick must each fire once), and the keyed ELS backstop per
  queue entry across save/reload.
- Rewind / tombstone reversibility. The amount must still be recomputed fresh
  from ELS at each landing flush (the T-CRASH-WINDOW-TOMBSTONE contract:
  a tombstone applied while a credit is owed must zero it). A queue multiplies
  the stale-entry surface: entries whose dispatch was rewound away must be
  swept, and a rewind landing between a dispatch and its queued landing leaves
  an owed credit whose fire UT is in the future relative to the rewound clock.
  Each rule needs its own test row in the section 9 style matrix.
- Per-row landing semantics. A run's recoveries are MULTIPLE
  `FundsEarning(Recovery)` rows at different UTs (boosters recovered during
  ascent, i.e. BEFORE the dock, the transport hours after). A faithful
  UT-mapped model splits the constant sum into per-row credits at row-mapped
  UTs, multiplying idempotency keys to (cycleId, rowId); booster rows map to
  landings BEFORE the dispatch crossing and need a clamp rule. The constant
  sum sidesteps all of this today.
- The pause tail erodes the precision claim. Today pause / stop / endpoint
  lost flushes the single owed credit immediately (plan section 5.4). With
  per-run landings, a paused route has up to N in-flight runs with FUTURE
  landing UTs: flushing them early re-introduces the constant-deferral
  approximation exactly where the clock was meant to remove it; not flushing
  them strands funds on a route that never crosses again. Either choice gives
  up part of the claimed precision.

## Recommendation: DO NOT BUILD the second clock now

- The divergence is a bounded cash-flow TIMING lead/lag with zero steady-state
  error and zero cumulative error. It is real and the harness pins it, but it
  is cosmetic relative to the economy invariants the ledger protects.
- OQ1's own bar ("revisit ONLY if a playtest shows the constant-deferred-credit
  timing reads wrong; build the concrete failing case first") is half met: the
  concrete case now exists (this harness), but no playtest report says the
  timing reads wrong.
- The cost is L (persisted queue, second idempotency surface, rewind/tombstone
  sweep rules, per-row landing semantics, codec) against that cosmetic gain,
  and the pause-tail dilemma means even the built clock cannot be exactly
  per-run without stranding funds.
- If a playtest ever does surface the timing as wrong, the cheaper first step
  is the LATE-lobe-only reduced variant: with interval > fly-home there is
  never more than one owed credit, so the EXISTING single pending marker
  suffices and only the flush condition moves from "next crossing" to
  "currentUT >= dispatchUT + flyHomeOffset" in Tick. That fixes the
  most player-visible lag (monthly resupply) without the queue. The EARLY lobe
  (overlap) inherently needs the full queue and should stay declined unless a
  playtest demonstrates it reads wrong.
- Keep `RouteRecoveryLandingHarnessTests` as the acceptance fixture either
  way: its skipped ideal-model assert is exactly what a built clock must turn
  green.
