# Logistics Recovery-Credit Plan

Status: IMPLEMENTED (committed on `logistics-recovery-credit`; full xUnit suite green at 14317; design re-audited, double-count adversarially cleared, cross-references corrected). Revised after design review (timing pairing resolved to prior-cycle deferral, sections 5.1-5.6; all symbol / line claims verified against the worktree at commit `95e359e4`). Second design-review pass folded in the cutoff-walk reservation-floor reversibility proof (section 6.3 BRANCH (a)/(b)), the double-source over-credit invariant (section 3.3), the crash-window + tombstone fresh-recompute requirement (section 5.3), the OQ3 Option-A intentional-behavior-change resolution + mandatory regression test (section 6.3 / OQ3), and the missing matrix rows (T-REWIND-RESERVATION, T-RECONCILE-NOOP, T-LEGACY-DEBIT, T-SCENARIO-ROUNDTRIP, T-CYCLE0, T-ROUTEMODULE-OBSERVE, T-CRASH-WINDOW-TOMBSTONE, split T-PAUSE-FLUSH).
Branch base: `logistics-recovery-credit` (stacked on the run-cost DISPLAY feature, which already landed `RouteRunCostCalculator` + `SumRecoveredCredits` on this branch)
Scope: turn the run-cost feature's DISPLAYED net (launch minus recovered) into the ACTUAL net funds effect of a cycle, by adding a deferred per-cycle RECOVERY CREDIT to the route ledger. The gross launch cost is still charged at dispatch (unchanged); the recovery comes back ONE DISPATCH INTERVAL LATER, at the next dock crossing, keyed on the PRIOR dispatched cycle, as a new ledger row (section 5.1, so the credit lands on a strictly later tick than the debit it pays back). The per-cycle credit is a constant deferred amount: exact in steady state, an approximation of each run's physical recovery landing (section 5.5). Career + KSC-origin only. The whole point is ledger safety: the credit must participate in the route ledger's epoch isolation, recompute, and rollback / tombstone contract, so rewind / re-fly / supersede reverse it. If a path cannot be made reversible it stays disabled (design doc section 10.5).

This document is the implementation brief. A fresh agent should be able to execute it without the originating conversation. All `file:line` references are as of commit `95e359e4`; treat each as a "start here" anchor and grep the named symbol to confirm before editing.

---

## 1. Goal

A Career, KSC-origin Supply Route currently charges the GROSS launch cost at dispatch:

```
out at dispatch = launch cost (dry parts + loaded resources)
```

The run-cost DISPLAY feature shows the player the NET cost of one run:

```
net run cost = launch cost - recovered credits
```

but the ledger only ever debits the gross. The recovered credits are real funds KSP paid back when the player recovered the transport (and any jettisoned-and-recovered parts) during the recorded flight, captured as `FundsEarning(Recovery)` rows in the SOURCE tree. Today those captured recoveries are display-only: the route economy charges gross every cycle and never gives the recovery back.

This plan closes the gap. After it lands, the NET funds effect of one Career KSC cycle equals the displayed net:

```
funds(cycle) = - launch (at dispatch)  + recovered (one dispatch interval later)  =  - net (in steady state)
```

The realism is the TIMING, not a discount:

- You still front the full build cost to launch. The dispatch debit and the `WaitingForFunds` gate both stay on GROSS (section 4). You cannot launch a route you cannot fully fund.
- The recovery comes back ONE DISPATCH INTERVAL LATER, at the next dock crossing, when the run has flown and the transport would have been recovered. The credit is keyed on the PRIOR dispatched cycle (section 5.1), so it lands on a strictly later tick than the dispatch it pays back. Crediting in the dispatching cycle's own tick (collapsing the timing to net-at-dispatch) is REJECTED: it would let a player launch a route they cannot afford because the same-tick refund papers over the shortfall.
- The per-cycle credit is a CONSTANT (the source tree's full per-run recovery), so the net is exact in steady state but an approximation of each individual run's physical recovery-landing UT (section 5.5). Precise per-run landing with cycle overlap is deferred (OQ1).

Career + KSC-origin only. In Sandbox / Science there are no funds, so no credit. Non-KSC origins deduct physical cargo, not funds, so no credit there either (section 4, gate G5 from the display plan).

---

## 2. How to work (process for the implementer)

- Work ONLY in this worktree: `C:/Users/vlad3/Documents/Code/Parsek/Parsek-logistics-recovery-credit` (branch `logistics-recovery-credit`). Never edit, build, or commit in any other `Parsek-*/` worktree or the main `Parsek/` checkout. Use absolute paths under this worktree.
- Do NOT `git push`. Do NOT create, edit, close, merge, or comment on any GitHub PR. Commit locally on this branch only.
- Build + test from this worktree:
  - `cd Source/Parsek && dotnet build`
  - `cd C:/Users/vlad3/Documents/Code/Parsek/Parsek-logistics-recovery-credit/Source/Parsek.Tests && dotnet test`
  - Report pass / fail HONESTLY. Never claim a green run you did not observe.
- Every new method with logic gets unit tests and verbose `ParsekLog` logging. Pure logic is `internal static` for direct testability. Follow the established split: pure decisions in the Logistics / GameActions layer, no IMGUI in the credit path (this feature has no UI surface of its own beyond the reconciliation in section 8).
- Update `CHANGELOG.md` (1 line per item, user-facing, under `## 0.10.0`) and `docs/dev/todo-and-known-bugs.md` in the same commit that changes behavior. Also reconcile the run-cost display caveat (section 8).

### Hard constraints (do not violate)

- LEDGER SAFETY IS THE WHOLE POINT. Any new stock funds mutation MUST participate in the route ledger's epoch isolation, recompute, and rollback / tombstone contract (design doc sections 6.6 and 10.5). A funds mutation that cannot be reversed on rewind / re-fly / tombstone is a defect, not a feature. If a path cannot be made reversible, it must stay disabled (section 6 picks the reversible design precisely to honor this).
- ERS / ELS grep gate must stay green (`scripts/grep-audit-ers-els.ps1`, enforced by `GrepAuditTests`). The gate matches ONLY the literals `\bLedger\.Actions\b` and `\.CommittedRecordings\b`. Read effective ledger via `EffectiveState.ComputeELS()`; snapshot recordings via `ComputeERS()`. Never reference the two gated literals. `RouteOrchestrator.cs` is already on the allowlist for its raw `CommittedRecordings` / `CommittedTrees` read ([allowlist:271-286](../../../../scripts/ers-els-audit-allowlist.txt)); this plan adds no new raw reads, so no allowlist change is needed.
- No em dashes anywhere (chat, code comments, CHANGELOG, commits). Use a colon, parentheses, comma, or split the sentence.
- Plain ASCII only in markdown, code, comments, and any string. No emoji, no special Unicode. Do NOT use the KSP funds glyph; write a plain number and the word "funds".
- InvariantCulture for all numeric formatting (`ToString("R", CultureInfo.InvariantCulture)` for ledger / log values).
- No `Co-Authored-By` and no AI-attribution lines in commits.

---

## 3. Ground truth: the dispatch / charge path and the ledger (verified)

Read these before touching anything. Every claim below is anchored to a line in this worktree.

### 3.1 The cycle the credit pairs to

Every v0 route is a loop-route. The loop clock owns dispatch:

- `RouteOrchestrator.ProcessLoopRoute` ([RouteOrchestrator.cs:447](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) runs once per ~1 Hz `Tick` ([RouteOrchestrator.cs:237](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It builds the loop unit, asks `RouteLoopClock` for the span-clock state, detects a DOCK crossing, and on a confirmed crossing either emits the FULL cycle (`EmitLoopCycle`) or skips it.
- The cycle id is `"cycle-" + (route.CompletedCycles + route.SkippedCycles)` ([RouteOrchestrator.cs:544](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It pins the dispatch + debit + delivered triple under one id. The same formula is recomputed in `EmitDispatchDebit` callers and `ApplyDelivery` ([RouteOrchestrator.cs:1077](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
- Re-fire is guarded by `Route.LastObservedLoopCycleIndex` (a `long`, default -1, [Route.cs:230](../../../../Source/Parsek/Logistics/Route.cs)), snapped forward to `dockCycleIndex` in ALL crossing branches ([RouteOrchestrator.cs:560,581](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It is persisted sparsely (omitted when -1) by `RouteCodec` ([RouteCodec.cs:122-124](../../../../Source/Parsek/Logistics/RouteCodec.cs)). This is the PRIMARY save / reload re-fire guard the new credit's pairing must mirror.

### 3.2 What a cycle emits today (the charge)

`EmitLoopCycle` ([RouteOrchestrator.cs:788](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) runs two halves under one `cycleId`:

1. Dispatch + debit half, `EmitDispatchDebit` ([RouteOrchestrator.cs:911](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)):
   - `isCareerKsc = env.IsCareer && route.IsKscOrigin` ([:918](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - `computedCost = ComputeDispatchFundsCostForRoute(route)` (GROSS launch cost, ERS-backed, [:922](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Written to `route.KscDispatchFundsCost` (double, [:927](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - Emits `RouteDispatched` (Sequence 0) + `RouteCargoDebited` (Sequence 1). The debit row carries `RouteKscFundsCost = isCareerKsc ? (float)computedCost : 0f` ([:958](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Both via `Ledger.AddActions` ([:961](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
2. Delivery half, `ApplyDelivery` -> `ApplyDeliveryFromPlan` ([RouteOrchestrator.cs:1071,1219](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)):
   - STEP 7 is the ACTUAL stock funds charge: `if (ctx.IsCareer && ctx.IsKscOrigin && ctx.KscFundsCost > 0.0) ctx.FundsDebiter(ctx.KscFundsCost)` ([:1264](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). The production `FundsDebiter` is `LiveDebitFunds` ([:1204,1479](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), which calls `Funding.Instance.AddFunds(-cost, TransactionReasons.None)`.
   - Emits `RouteCargoDelivered` carrying the same `RouteKscFundsCost` ([:1313](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) + bumps `CompletedCycles` ([:1321](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - Idempotency: `IsDeliveryAlreadyInLedger(routeId, cycleId)` ([:1365](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) scans ELS for an existing `RouteCargoDelivered` with the same `(RouteId, RouteCycleId)`. This is the orchestrator's ONLY ELS read and is the save / reload double-fire backstop ([:800,1090](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).

CRITICAL ARCHITECTURE FACT (verified, load-bearing for section 6). The KSC charge has TWO independent effects:

- (a) the LIVE stock mutation via `LiveDebitFunds` -> `Funding.AddFunds`, applied once, at delivery time, and
- (b) the ledger ROWS (`RouteCargoDebited` / `RouteCargoDelivered`), which carry `RouteKscFundsCost` as a RECORD only.

`Ledger.AddAction` / `AddActions` do NOT trigger a recalc ([Ledger.cs:73,89](../../../../Source/Parsek/GameActions/Ledger.cs); they only `BumpStateVersion`). And `FundsModule` does NOT process ANY route action type ([FundsModule.cs:125-169](../../../../Source/Parsek/GameActions/FundsModule.cs) `ProcessAction` switch has no `RouteCargoDebited` / `RouteCargoDelivered` case; `ComputeTotalSpendings` [:188-234](../../../../Source/Parsek/GameActions/FundsModule.cs) has no route case either). So today the GROSS charge is a fire-and-forget stock mutation that does NOT flow through the recalc walk and is NOT reconciled by `KspStatePatcher.PatchFunds` ([KspStatePatcher.cs:619](../../../../Source/Parsek/GameActions/KspStatePatcher.cs)). On a Parsek timeline rewind, only a stock quicksave / load restores it (design doc section 10.6, [supply-routes-design.md:1015](../../../../docs/parsek-logistics-supply-routes-design.md)). This is exactly the "route effect that is not yet reversed through the modules" the design doc warns about; the new credit must NOT replicate that hole (section 6).

### 3.3 How recovery credits are captured (the amount source)

- Recovery payouts are already `GameAction` rows: `Type = FundsEarning`, `FundsSource = FundsEarningSource.Recovery` ([GameAction.cs:91-112](../../../../Source/Parsek/GameActions/GameAction.cs) enum value `Recovery = 2`), `FundsAwarded = (float)delta` (the real distance-scaled payout), `RecordingId` set to the recording the recovery happened in. Entry points: `LedgerOrchestrator.OnVesselRecoveryFunds` and the commit-time `CreateVesselCostActions` path (display plan section 3.2).
- `RouteRunCostCalculator.SumRecoveredCredits(route, els, treeRecordingIds, out count)` ([RouteRunCostCalculator.cs:96](../../../../Source/Parsek/Logistics/RouteRunCostCalculator.cs)) sums `FundsAwarded` over ELS for every `FundsEarning(Recovery)` row whose `RecordingId` is in the route's SOURCE TREE member set (NOT `Route.RecordingIds`: the fly-home-and-recover leg is post-undock, gotcha G1 from the display plan). `ResolveTreeRecordingIds(route)` ([:142](../../../../Source/Parsek/Logistics/RouteRunCostCalculator.cs)) builds that set via `Route.BackingMissionTreeId -> RouteTreeGuard.FindCommittedTree -> tree.Recordings.Keys`.
- This plan REUSES `SumRecoveredCredits` verbatim for the credit AMOUNT. Do not recompute a theoretical recovery from part costs (display plan gotcha G3).

DOUBLE-SOURCE INVARIANT (load-bearing, must hold for the economy to be correct). Two distinct funds rows quote the same recovered sum, and they must stay distinct forever:

- The ORIGINAL `FundsEarning(Recovery)` row in the SOURCE tree is a HISTORICAL, one-time earning. KSP paid it live exactly once during the recorded flight, and `FundsModule.ProcessFundsEarning` re-adds it on every walk because it genuinely happened once. It is captured in the source recording and never re-emitted.
- Each `RouteRecoveryCredited` row this feature emits is an INDEPENDENT, NEW earning, one per dispatched cycle. It is NOT a copy of the historical row and does NOT share its identity; it merely happens to carry the same AMOUNT (the per-run recovery, by `SumRecoveredCredits`).
- THE INVARIANT: a `RouteRecoveryCredited` row must NEVER be a `FundsEarning(Recovery)` row (it is a distinct `GameActionType`, section 6.1), and must NEVER enter the source tree's recording-id scope (it carries `RouteId`, `RecordingId == null`, so `ResolveTreeRecordingIds(route)` -> `SumRecoveredCredits` never sums it). Because of this, `SumRecoveredCredits` over the source tree is STABLE across cycles: emitting credits for cycle-0 .. cycle-N does NOT change the amount computed for cycle-(N+1). The source tree's recovery rows are a fixed historical set; the credit rows live outside that set.
- WHY THIS MATTERS: if a future change ever let a route's OWN credited or recovered legs feed back into the same tree's ELS-visible `FundsEarning(Recovery)` rows (or into its recording-id scope), `SumRecoveredCredits` would GROW per cycle and the credits would inflate without bound (over-credit). The intended economy is "give the per-run recovery back once per cycle," a CONSTANT, not a compounding sum. No code today violates the invariant; the design pins it so a later edit cannot quietly break it. T-AMOUNT (section 9) asserts the stability directly.

### 3.4 How `FundsEarning(Recovery)` is reversed today (the reversibility template)

This is the template the credit follows. A `FundsEarning` row flows through `FundsModule.ProcessFundsEarning` ([FundsModule.cs:265](../../../../Source/Parsek/GameActions/FundsModule.cs)): when `action.Effective`, it adds `FundsAwarded` to `runningBalance` and `totalEarnings`. The recalc walk's result is reconciled to live stock funds by `KspStatePatcher.PatchFunds`, which sets `Funding.Instance.Funds` to `funds.GetAvailableFunds()` ([KspStatePatcher.cs:646,670](../../../../Source/Parsek/GameActions/KspStatePatcher.cs)). Reversal happens two ways, both already wired:

- CUTOFF WALK (rewind / time-jump). `RecalculateAndPatchCore(utCutoff, ...)` ([LedgerOrchestrator.cs:1668](../../../../Source/Parsek/GameActions/LedgerOrchestrator.cs)) walks only actions up to `utCutoff` (the engine applies the cutoff before `PrePass`, [FundsModule.cs:101-105](../../../../Source/Parsek/GameActions/FundsModule.cs)). A `FundsEarning` at a future UT is EXCLUDED, so `GetAvailableFunds()` drops, and `PatchFunds` reduces live funds to match. This is how a rewind past a recovery un-credits it.
- TOMBSTONE / SUPERSEDE (re-fly). `EffectiveState.ComputeELS()` is "ledger minus tombstones" (design doc section 3.2). `SupersedeCommit.CommitTombstones` ([SupersedeCommit.cs:2152](../../../../Source/Parsek/SupersedeCommit.cs)) appends `LedgerTombstone`s for recording-scoped career actions in the superseded subtree, bumps `TombstoneStateVersion`, and the next recalc walks ELS (tombstoned rows excluded), so `PatchFunds` reconciles down. `RouteRunCostCalculator.SumRecoveredCredits` reads `ComputeELS()`, so once a recovery row is tombstoned the credit AMOUNT also drops automatically.

So: any new credit modeled as a row that `FundsModule` processes as an earning is reversible by BOTH mechanisms with zero new rollback code. That is the design in section 6.

### 3.5 How route rows interact with supersede today

`SupersedeCommit.IsWorldStateChangingRecordingAction` explicitly EXCLUDES all route action types ([SupersedeCommit.cs:1856-1861](../../../../Source/Parsek/SupersedeCommit.cs)): `RouteDispatched`, `RouteCargoDebited`, `RouteCargoDelivered`, `RoutePaused`, `RouteEndpointLost` return false (they are scheduler-emitted under a `RouteId`, not flight-recorder output, so supersede must not strict-block or retry-block on them). The new credit row is a route-scoped row and inherits this exclusion (section 7.4): it is not a flight-recorder artifact, so it never blocks an auto-seal, but its FUNDS effect is still reversed through the FundsModule earning path (section 3.4).

---

## 4. The funds gate stays on GROSS (unchanged)

This plan does NOT touch the dispatch debit, the `KscDispatchFundsCost`, or the funds gate:

- The dispatch evaluator's funds check is `env.KscFundsAvailable(route, out shortfall)` ([RouteDispatchEvaluator.cs:207](../../../../Source/Parsek/Logistics/RouteDispatchEvaluator.cs)), which compares live `Funding.Instance.Funds` against `ComputeDispatchFundsCostForRoute(route)` (GROSS, [LiveRouteRuntimeEnvironment.cs:118-141](../../../../Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs)). A shortfall yields `WaitFunds` ([RouteOrchestrator.cs:380](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). All of this is left EXACTLY as-is.
- `EmitDispatchDebit` still computes and emits the gross debit (section 3.2). Unchanged.
- The live `LiveDebitFunds(gross)` at delivery time (STEP 7) is unchanged. The credit is a SEPARATE, LATER funds movement, not a reduction of the debit.

The credit does not collapse into the gate or the debit. You must still front the full build cost to launch.

---

## 5. Credit timing and the loop clock that fires it

### 5.1 Decision D1: fire one crossing LATER, paired to the PRIOR dispatched cycle

DECIDED: the credit for a dispatched cycle K fires at the NEXT dock crossing (cycle K+1's `EmitLoopCycle`), one dispatch interval after cycle K's dispatch. The credit row carries `RouteCycleId = "cycle-K"` (the PRIOR dispatched cycle's id), so the credit's UT is the next crossing's UT, which is cycle K's dispatch UT plus one dispatch interval. Credit and debit therefore live under the SAME `cycle-K` id but at DIFFERENT UTs (debit at cycle K's dispatch UT, credit at cycle K+1's crossing UT). Rationale:

- A cycle's funds story must read: debit GROSS at dispatch (cycle K's dispatch UT), then credit recovered ONE INTERVAL LATER (the next crossing). If the credit fired in the SAME tick as cycle K's own dispatch + delivery (the loop model fires dispatch + debit + delivered in one tick per crossing, [RouteOrchestrator.cs:815-833](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), funds would go out and come straight back in one tick, which is net-at-dispatch, the design REJECTED below. The whole point of this feature is timing honesty, so the credit MUST land on a strictly LATER tick than the cycle it credits.
- The loop clock already fires exactly once per dock crossing with full save / reload idempotency (section 3.1), so reusing it for the credit's firing is the lowest-risk mechanism. The PRIOR-cycle pairing is the only pairing under which the loop clock gives real deferral: the credit is emitted from a crossing one interval after the dispatch it pays back.
- Pairing the credit to `cycle-K` (the dispatch it pays back) rather than to `cycle-(K+1)` (the crossing that emits it) keeps the per-cycle audit clean: a single `cycle-K` id owns the (debit, credit) pair, and the credit's idempotency key `(RouteId, "cycle-K")` is distinct from the next crossing's own debit key `(RouteId, "cycle-K+1")`. The debit and credit do NOT share a UT, which is exactly the deferral the feature exists to provide.

The Route tracks the PENDING credit across crossings (section 5.2): cycle K's dispatch records "cycle-K dispatched, credit owed at next crossing"; cycle K+1's crossing emits cycle-K's credit (then records its own pending credit for K+2). The final dispatched cycle's credit is emitted when the route stops / pauses (its last crossing) or, if the route runs indefinitely, on the crossing after its last dispatch; a route that is paused mid-flight after its last dispatch still owes one final credit, flushed on pause (section 5.4).

REJECTED alternative (credit at dispatch / same cycle): emit the credit in the SAME `EmitLoopCycle` call (and therefore the SAME UT) as the cycle's own dispatch + delivery. This collapses the timing to net-at-dispatch: funds out and back in one tick, the same-tick refund papers over the shortfall, and a player could launch a route they cannot afford. Explicitly rejected by the author. (An earlier draft of this plan chose this collapse by pairing the credit to the SAME `cycle-N` as the just-emitted debit; that is the rejected design and has been replaced by the prior-cycle pairing above.)

CONSIDERED alternative (recorded recovery UT mapped precisely into the loop): more physically precise, but the recorded recovery UT routinely lands BEYOND one dispatch interval (the transport flies home over many hours / days, far longer than the loop period), so run K's recovery physically lands during cycle K+3 or later: genuine cycle overlap. The model chosen here does NOT credit run K's recovery at the instant it physically lands; it credits a CONSTANT per-cycle amount (the source tree's full per-run recovery, computed by `SumRecoveredCredits`, section 3.3) one interval after each dispatch. This is correct in STEADY STATE (every cycle nets `-(gross - recovered)`), but it is an approximation, not a physical-landing model: it does not attempt to align each individual run's recovery to its true landing UT, and the very first credited cycle pays back a full per-run recovery one interval after a single dispatch even though that run's transport has not physically flown home yet. That approximation is accepted deliberately for v0 (section 5.5 states the limitation honestly); precise per-run landing with the attendant multi-cycle overlap bookkeeping is DEFERRED (section 12, OQ1).

### 5.2 Where it fires: the PRIOR cycle's credit, emitted at the next crossing

The credit is emitted from inside `EmitLoopCycle` ([RouteOrchestrator.cs:788](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), but it credits the PRIOR dispatched cycle, not the cycle the current `EmitLoopCycle` call is dispatching. Concretely, each `EmitLoopCycle(route, ut, env, "cycle-K")` call:

1. Emits the pending credit (if any) for the PRIOR dispatched cycle, at THIS UT, carrying that prior cycle's id. So a crossing that dispatches `cycle-K` first flushes `cycle-(K-1)`'s recovery credit (if cycle K-1 dispatched and owes one).
2. Runs the dispatch + debit half and the delivery half for `cycle-K` (unchanged, section 3.2).
3. Records that `cycle-K` now owes a credit, to be flushed on the NEXT crossing. This pending marker is a small `Route` field set, persisted by `RouteCodec`, so a save / reload between crossings does not lose or double-emit the owed credit (section 5.3, 5.6).

This gives the deferral the feature requires: cycle K's gross debit lands at cycle K's dispatch UT, and cycle K's recovery credit lands one dispatch interval later, at cycle K+1's crossing UT. The two never share a UT. The credit row's `RouteCycleId` names the cycle it pays back (`cycle-K`), NOT the crossing that emits it (`cycle-K+1`), so a per-cycle audit and the credit's idempotency key both key on the dispatched cycle. This is the OPPOSITE pairing from this plan's earlier draft (which paired credit to the same cycle as the debit and thereby collapsed to net-at-dispatch); that collapse is the rejected design (section 5.1).

The pending marker, on `Route`:

- `PendingRecoveryCreditCycleId` (string, default null): the id of the dispatched cycle whose credit has not yet been flushed. Null means no credit is owed (route just activated, or the last owed credit was already flushed).
- `PendingRecoveryCreditDispatchUT` (double, default -1): the dispatch UT of that pending cycle, recorded for the audit / log only (the credit's UT is the NEXT crossing's UT, not this).

Both persist through `RouteCodec` sparsely (omit when null / -1), exactly like `LastObservedLoopCycleIndex` (section 5.6). They are the credit's save / reload re-fire guard, mirroring the dispatch path's `LastObservedLoopCycleIndex`.

New helper, `EmitPendingRecoveryCredit(route, currentUT, env)` in `RouteOrchestrator.cs`, called from `EmitLoopCycle` at the TOP (before the dispatch + debit half), so the prior cycle's credit is flushed at this crossing's UT:

```
// Pseudocode, not final.
// Flush the PRIOR dispatched cycle's recovery credit at THIS crossing's UT.
// Returns true if a credit row was emitted (for the caller's log line).
internal static bool EmitPendingRecoveryCredit(Route route, double currentUT, IRouteRuntimeEnvironment env)
{
    // Nothing owed (fresh route, or already flushed). Not an error.
    string pendingCycleId = route.PendingRecoveryCreditCycleId;
    if (string.IsNullOrEmpty(pendingCycleId)) { /* verbose "no pending credit"; */ return false; }

    // Gate: Career + KSC origin only. Mirror EmitDispatchDebit's isCareerKsc.
    // (A pending id can only have been set on a Career-KSC dispatch, but re-check
    // defensively in case env flips, e.g. a save copied into Sandbox.)
    bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
    if (!isCareerKsc)
    {
        // Clear the stale pending marker so it does not linger forever.
        route.PendingRecoveryCreditCycleId = null;
        route.PendingRecoveryCreditDispatchUT = -1.0;
        /* verbose log "credit-skip non-career-ksc, cleared pending"; */
        return false;
    }

    // Idempotency backstop: do not emit a second credit for the same (RouteId, pendingCycleId).
    if (IsRecoveryCreditAlreadyInLedger(route.Id, pendingCycleId))
    {
        // Already emitted (save/reload re-presented this crossing). Clear and move on.
        route.PendingRecoveryCreditCycleId = null;
        route.PendingRecoveryCreditDispatchUT = -1.0;
        /* verbose replay log; */
        return false;
    }

    // Amount: reuse SumRecoveredCredits over the source tree, read from ELS.
    HashSet<string> treeIds = RouteRunCostCalculator.ResolveTreeRecordingIds(route);
    IReadOnlyList<GameAction> els = SafeComputeEls();   // see section 6.2
    double recovered = RouteRunCostCalculator.SumRecoveredCredits(route, els, treeIds, out int n);
    if (recovered <= 0.0)
    {
        // Zero recovery for this tree: nothing to credit. Clear the pending marker
        // so we do not retry the same dispatched cycle forever.
        route.PendingRecoveryCreditCycleId = null;
        route.PendingRecoveryCreditDispatchUT = -1.0;
        /* verbose log "credit-skip zero-recovery, cleared pending"; */
        return false;
    }

    // Emit the credit row (section 6.1) and apply the live stock credit (section 6.3).
    var credit = new GameAction {
        Type = GameActionType.RouteRecoveryCredited,
        UT = currentUT,                       // THIS crossing's UT (one interval after dispatch)
        RouteId = route.Id,
        RouteCycleId = pendingCycleId,        // the PRIOR dispatched cycle it pays back
        RouteStopIndex = -1,
        Sequence = 0,                         // emitted first at this crossing's UT, before this cycle's RouteDispatched
        RouteKscFundsCost = (float)recovered, // positive magnitude; the action TYPE carries the credit direction
    };
    Ledger.AddAction(credit);
    LiveCreditFunds(recovered);               // section 6.3, mirror of LiveDebitFunds

    // Clear the flushed pending marker. The current cycle's own pending marker is
    // set AFTER this returns (in EmitLoopCycle, right after EmitDispatchDebit).
    route.PendingRecoveryCreditCycleId = null;
    route.PendingRecoveryCreditDispatchUT = -1.0;
    // Info log: route, creditedCycleId=pendingCycleId, recovered, recoveryRows=n,
    //           ut, dispatchUT=PendingRecoveryCreditDispatchUT (before clearing).
    return true;
}
```

Setting the current cycle's pending marker happens in `EmitLoopCycle` itself, on the real-fire path only, right after `EmitDispatchDebit` records the dispatch for Career-KSC. Pseudocode:

```
// Inside EmitLoopCycle, after the prior-credit flush and the dispatch + debit half,
// before / around the delivery half. Only set the pending marker when this cycle
// actually dispatched a Career-KSC charge (otherwise no credit is owed).
if (env.IsCareer && route.IsKscOrigin)
{
    route.PendingRecoveryCreditCycleId = cycleId;        // "cycle-K"
    route.PendingRecoveryCreditDispatchUT = currentUT;   // cycle K's dispatch UT (for audit/log)
}
```

The credit's UT is the NEXT crossing's UT, so it is strictly later than the dispatch UT (the loop clock advances UT between crossings). That is the deferral T-FUNDS-OUT-THEN-BACK proves (section 9): after cycle K's tick, live funds are DOWN by gross only; after cycle K+1's tick, the credit lands and funds rise to the net.

### 5.3 No double-fire across save / reload

The credit's deferral means it crosses the save / reload boundary in TWO ways: the owed-but-not-yet-flushed pending marker must survive a save between crossings, and an already-flushed credit must not be re-emitted on a re-presented crossing. Three guards cover both:

1. PRIMARY (pending marker persistence): `PendingRecoveryCreditCycleId` / `PendingRecoveryCreditDispatchUT` persist through `RouteCodec` (section 5.6). A save taken after cycle K dispatched but before cycle K+1's crossing reloads with the pending marker intact, so cycle K's credit is still flushed at the next crossing. Without persistence, a reload between crossings would silently drop cycle K's owed credit forever.
2. PRIMARY (once-per-crossing): the loop clock fires `EmitLoopCycle` exactly once per dock crossing, gated by `LastObservedLoopCycleIndex` (section 3.1). The pending-credit flush is emitted INSIDE `EmitLoopCycle` (at its top), so it inherits that once-per-crossing guarantee. A save / reload mid-cycle re-presents the same crossing only if `LastObservedLoopCycleIndex` was not advanced, which the existing snap-forward logic prevents.
3. BACKSTOP (keyed): `IsRecoveryCreditAlreadyInLedger(routeId, cycleId)` (new, section 6.4), an exact mirror of `IsDeliveryAlreadyInLedger` ([RouteOrchestrator.cs:1365](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) but scanning for a `RouteRecoveryCredited` row with the same `(RouteId, RouteCycleId)`. If a save / reload re-presents a crossing whose pending credit was ALREADY flushed (e.g. the save landed after the credit row but before `LastObservedLoopCycleIndex` advanced, or a double-tick), `EmitPendingRecoveryCredit` finds the existing credit row keyed on the PENDING cycle id and emits NOTHING (then clears the stale pending marker). The credit's own keyed check is the authority; it is keyed on the credit's `RouteCycleId` (the dispatched cycle it pays back), NOT on the crossing's own delivery row, so a present delivery row is never mistaken for "credit already emitted" (gotcha G6).

Crash-window note: under PRIOR-cycle pairing the credit is the FIRST row emitted at a crossing (Sequence 0, before `RouteDispatched`), and the pending marker that drives it is persisted (guard 1). So the dangerous window is a crash AFTER the pending marker was set (on cycle K's dispatch) but BEFORE cycle K+1's crossing flushes it. That window is SAFE: the pending marker is on disk, so the reload re-presents the owed credit at the next crossing, and the keyed backstop (guard 3) prevents a double emit if the credit had actually already landed. There is no "delivery row present but credit row not yet emitted" half-state to recover from, because the credit no longer shares a tick with the delivery it used to be wedged after: it is emitted FIRST at the FOLLOWING crossing. A crash between `Ledger.AddAction(credit)` and clearing the pending marker simply re-presents the same `(RouteId, cycleId)` on reload, which guard 3 short-circuits. The replay short-circuit at the top of `EmitLoopCycle` ([:800](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) must therefore NOT skip the pending-credit flush: wire `EmitPendingRecoveryCredit` to run BEFORE that delivery-keyed short-circuit (or also from the replay branch), so a crossing that replay-skips its OWN delivery still flushes the PRIOR cycle's owed credit. Log which path emitted it.

CRASH-WINDOW + TOMBSTONE INTERACTION (the replay flush MUST recompute the amount fresh, never cache it). The pending marker stores only the pending cycle id and the dispatch UT (section 5.2); it deliberately does NOT store the recovered AMOUNT. This is REQUIRED, not incidental. Consider a crash between cycle K's dispatch (pending marker set) and cycle K+1's flush, with a re-fly / supersede happening between crash and reload that TOMBSTONES the source `FundsEarning(Recovery)` rows. On reload, the replay-path `EmitPendingRecoveryCredit` re-reads ELS via `SafeComputeEls()` -> `SumRecoveredCredits`, which now sees the tombstoned recovery hidden, so the amount recomputes to ZERO, the `recovered <= 0 -> clear pending, emit nothing` guard fires, and NO stale credit is emitted. That is the correct, tombstone-respecting outcome, and it depends ENTIRELY on the replay path recomputing from current ELS. If an implementer instead caches the recovered amount on the `Route` (or in the cycle) to "remember what to credit on replay," the tombstone reversal is silently DEFEATED: the cached pre-crash amount would credit funds the re-fly was supposed to remove. The replay-emit (section 5.3 guard, this paragraph) and the zero-recompute (section 6.2 amount-from-ELS) are NOT two independent mechanisms; they are one mechanism the replay path must honor: the replay flush runs the SAME fresh `SumRecoveredCredits` read as a live flush, against ELS as it stands at replay time. T-CRASH-WINDOW-TOMBSTONE (section 9) proves it: seed delivered-but-not-credited (pending marker on disk), tombstone the source recovery, run the crossing, assert NO credit row is emitted because the amount recomputes to 0 and the pending marker is cleared.

### 5.4 Final-credit flush on pause / stop (the deferral's tail)

Deferring the credit to the NEXT crossing introduces a tail: the LAST dispatched cycle's credit is owed but its "next crossing" may never come, because the player paused the route, the route lost an endpoint, or the route stopped ghost-driving. A loop-route that stops crossing returns early at the top of `ProcessLoopRoute` (status not `GhostDriving`, [RouteOrchestrator.cs:454-461](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) and never reaches `EmitLoopCycle`, so a pending credit set on its final dispatch would be stranded forever. The earlier same-tick design did not have this tail (it emitted the credit in the dispatching cycle's own tick); honest deferral creates it, and it must be handled, not ignored.

DECISION: flush the final owed credit at the moment the route leaves the dispatching state. There are two pause / stop transition points, both of which must call `EmitPendingRecoveryCredit` (idempotent via guard 3) before the route stops crossing:

- IMMEDIATE pause / stop: `TryPause` transitions a not-in-transit route straight to `Paused` ([RouteOrchestrator.cs:222-223](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). For a loop-route this is the common stop. Call `EmitPendingRecoveryCredit(route, currentUT, env)` here (the live env at pause time) so the last dispatched cycle's credit lands before the route goes quiet. `TryPause` currently has no `currentUT` / `env` in scope; thread the live UT (`Planetarium.GetUniversalTime()` at the call site, or pass it in) and a `LiveRouteRuntimeEnvironment`, mirroring how the dispatch path obtains them.
- ARMED pause-after-cycle: `ApplyDelivery` honors `PauseAfterCurrentCycle` and transitions to `Paused` at [RouteOrchestrator.cs:1338-1342](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs). The cycle that arms this still SET its pending credit during its own `EmitLoopCycle`, and there is no further crossing, so flush the pending credit at this transition point too, just before `TransitionTo(RouteStatus.Paused, ...)`.
- ENDPOINT-LOST / source-missing: a loop-route that flips to `EndpointLost` / `MissingSourceRecording` / `SourceChanged` also stops crossing. Flush the pending credit at those transitions as well (same `EmitPendingRecoveryCredit` call). If a route is genuinely broken (no live env, no funds context), the flush no-ops on the Career-KSC gate or zero-recovery branch, which is acceptable: a broken route that cannot resolve its tree simply does not credit, and the pending marker is cleared so it does not linger. The transition into `MissingSourceRecording` / `SourceChanged` is set by `RouteStore.RevalidateSources` ([RouteStore.cs:367,392](../../../../Source/Parsek/Logistics/RouteStore.cs)), and `ProcessLoopRoute` early-returns at its status gate for those statuses ([RouteOrchestrator.cs:454-461](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), so the flush CANNOT wait for the next crossing: `RevalidateSources` itself must call `EmitPendingRecoveryCredit` at the transition (resolving a live UT + env, or passing the defensive `ApplyDeliveryEnvAdapter` / a `-1` UT so the flush no-ops safely when no funds context exists, mirroring `TryPause`'s defensive resolution). This matters because `MissingSourceRecording` can auto-restore (the deferral merely resumes), but `SourceChanged` NEVER auto-recovers (design 7.4 requires recreation) and a route deleted while in either state is gone permanently; without a flush at the `RevalidateSources` transition the last dispatched cycle's owed credit is stranded forever and the pending marker lingers on disk. T-PAUSE-FLUSH-ENDPOINTLOST (section 9) covers the source-missing and source-changed flush.

A resumed route (player un-pauses) has already had its final-on-pause credit flushed and its pending marker cleared, so resume does NOT re-credit; the first post-resume crossing dispatches a fresh cycle and the deferral re-arms from there. T-PAUSE-FLUSH (section 9) proves the final credit lands exactly once on pause and is not double-emitted on resume.

### 5.5 Honest limitation: steady-state net, not per-run physical landing

This design models the STEADY-STATE per-cycle net, not the physical recovery-landing UT of each individual run. State this plainly in the implementation and the reconciliation note (section 8):

- The credited amount is a CONSTANT per cycle: `SumRecoveredCredits` sums the source tree's `FundsEarning(Recovery)` rows ([RouteRunCostCalculator.cs:96-131](../../../../Source/Parsek/Logistics/RouteRunCostCalculator.cs)), a fixed historical set, so every cycle's credit is the same per-run recovery amount. It is NOT recomputed per cycle from that cycle's own (nonexistent) physical run.
- In steady state this is exactly right: with the gross debited every cycle and the same recovered amount credited one interval later, the per-cycle net is `-(gross - recovered)` for every cycle, which equals the displayed net. The funds timeline reads: front gross at dispatch, get the run's worth of recovery back one interval later, repeat.
- It is an APPROXIMATION at the edges, and the plan does NOT claim otherwise. The recorded recovery physically lands many hours / days after dispatch (the transport flies home), routinely BEYOND one dispatch interval, so run K's recovery would physically land during cycle K+3 or later. The cycle-completion model credits a full per-run recovery one interval after EACH dispatch, including the very first, even though that first run's transport has not physically flown home yet. The model does not align each individual run's recovery to its true landing UT, and it does not handle the multi-cycle overlap that true alignment would require. It trades that physical precision for a clean per-cycle net and a single idempotency surface (the loop clock).
- This is a deliberate v0 cut, recorded as the deferred OQ1 (section 12). Precise per-run landing with overlap bookkeeping (a second clock keyed on the recorded recovery UT, mapping run K's recovery into whatever cycle it physically lands in) is out of scope here. Do not describe the v0 model as "handling cycle overlap"; describe it as "approximating the per-cycle net as a constant deferred credit, deferring precise per-run landing (OQ1)."

### 5.6 Persistence of the pending-credit marker (RouteCodec)

`PendingRecoveryCreditCycleId` / `PendingRecoveryCreditDispatchUT` are new mutable `Route` fields that MUST persist, exactly like `LastObservedLoopCycleIndex` ([Route.cs:223-230](../../../../Source/Parsek/Logistics/Route.cs)) and for the same reason: they are the credit's save / reload re-fire guard (section 5.3, guard 1). Wire them into `RouteCodec` sparsely, mirroring the `lastObservedLoopCycleIndex` pattern ([RouteCodec.cs:121-124](../../../../Source/Parsek/Logistics/RouteCodec.cs) serialize, [:253](../../../../Source/Parsek/Logistics/RouteCodec.cs) deserialize):

- SERIALIZE: write `pendingRecoveryCreditCycleId` only when non-null / non-empty; write `pendingRecoveryCreditDispatchUT` only when `>= 0` (omit the -1 default). Place them next to `lastObservedLoopCycleIndex` in the backing-mission block.
- DESERIALIZE: missing `pendingRecoveryCreditCycleId` -> null (no credit owed); missing `pendingRecoveryCreditDispatchUT` -> -1 (use `TryParseDoubleWithDefault(..., -1.0, ...)`). No migration: a pre-feature save simply has no pending marker, which is the correct "no credit owed yet" state.
- POST-CHANGE CHECKLIST (CLAUDE.md): two distinct serialized surfaces change and BOTH need round-trip coverage.
  - (a) the `Route` pending-marker fields: `ParsekScenario` OnSave / OnLoad already routes route persistence through `RouteCodec`, so no separate scenario wiring is needed, but add a `RouteCodec` round-trip test (T-CODEC, section 9) asserting both fields survive serialize -> deserialize, including the sparse-omit defaults.
  - (b) the new `RouteRecoveryCredited` GameActionType in the LEDGER: this is a new serialized action type stored in the ledger node that `ParsekScenario` OnSave / OnLoad persists. The Post-Change Checklist requires verifying `ParsekScenario` OnSave / OnLoad handles new serialized data, so add T-SCENARIO-ROUNDTRIP (section 9) proving a `RouteRecoveryCredited` row survives the ledger save -> load path (via the source-text gate pattern, since `ParsekScenario` OnSave / OnLoad cannot be driven directly from xUnit). T-TYPE alone (direct `SerializeInto` / `DeserializeFrom`) does NOT satisfy this checklist item; the ledger-node persistence path must be exercised or source-gated.

---

## 6. The ledger representation and reversibility (the core of the plan)

### 6.1 Decision D2: a NEW GameActionType, `RouteRecoveryCredited`

DECIDED: add a new `GameActionType.RouteRecoveryCredited = 28` ([GameAction.cs:80](../../../../Source/Parsek/GameActions/GameAction.cs) currently ends at `RouteEndpointLost = 27`; explicit int for serialization stability). Rationale for a new type over extending an existing row:

- The credit is a SEPARATE funds movement at a DIFFERENT UT (the NEXT crossing, one dispatch interval after the dispatch it pays back) than the debit (dispatch). Folding it onto the `RouteCargoDelivered` row would conflate two opposite-sign funds effects at one UT and break the "funds out, then back later" timing the feature exists to model. The credit is also emitted from a DIFFERENT `EmitLoopCycle` call (the following crossing) and carries the PRIOR dispatched cycle's `RouteCycleId`, so it cannot be a field on the dispatching cycle's own delivery row.
- A distinct type lets the recompute walk, the idempotency check, and any future per-cycle audit key on `(RouteId, RouteCycleId, Type)` cleanly. The route action vocabulary was explicitly designed to allow new route types additively (design doc section 13.4).
- Serialization is additive and forward-safe: an old reader hits `Enum.IsDefined` false and logs "Unknown action type id" then ignores the row ([GameAction.cs:678-682](../../../../Source/Parsek/GameActions/GameAction.cs)). No schema migration; the route `ROUTES` node and the ledger are both additive (design doc section 14).

Serialization: reuse the route-common codec. `SerializeRouteRecoveryCredited` writes `WriteRouteCommon(n)` (routeId / routeCycleId / routeStopIndex, [GameAction.cs:1222](../../../../Source/Parsek/GameActions/GameAction.cs)) plus the credit amount. Store the amount in `RouteKscFundsCost` (the existing float route-funds field, [GameAction.cs:460](../../../../Source/Parsek/GameActions/GameAction.cs)) so no new serialized field is needed; the action TYPE carries the credit direction, exactly as `RouteCargoDebited` / `RouteCargoDelivered` store a positive magnitude and let the type carry the sign (the same convention `RouteResourceManifest` uses, [GameAction.cs:425](../../../../Source/Parsek/GameActions/GameAction.cs)). Add the `case` to both `SerializeInto` ([:642-656](../../../../Source/Parsek/GameActions/GameAction.cs)) and `DeserializeFrom` ([:776-790](../../../../Source/Parsek/GameActions/GameAction.cs)).

### 6.2 Which module reverses it, and how epoch isolation applies

DECIDED: `FundsModule` reverses it, by processing `RouteRecoveryCredited` as a fund EARNING. This is the single most important decision in the plan, because it makes the credit reversible by the SAME two mechanisms that already reverse `FundsEarning(Recovery)` (section 3.4) with NO new rollback code:

- Add a `case GameActionType.RouteRecoveryCredited:` to `FundsModule.ProcessAction` ([FundsModule.cs:130-169](../../../../Source/Parsek/GameActions/FundsModule.cs)) that adds `(double)action.RouteKscFundsCost` to `runningBalance` and `totalEarnings` (mirror `ProcessFundsEarning` [:265](../../../../Source/Parsek/GameActions/FundsModule.cs), but reading `RouteKscFundsCost` instead of `FundsAwarded`). Gate on `action.Effective` for symmetry.
- Add the same delta to `TryGetProjectionDelta` ([FundsModule.cs:539](../../../../Source/Parsek/GameActions/FundsModule.cs)) as a positive earning so the cashflow-aware reservation projection sees the future credit. (Do NOT add it to `ComputeTotalSpendings` [:188](../../../../Source/Parsek/GameActions/FundsModule.cs); it is an earning, not a spending.)

Epoch isolation / cutoff-walk reversibility (rewind / time-jump). A `RouteRecoveryCredited` row at the next-crossing UT is EXCLUDED by a cutoff walk whose `utCutoff` is before that UT ([LedgerOrchestrator.cs:1668](../../../../Source/Parsek/GameActions/LedgerOrchestrator.cs) passes the cutoff to the engine; the engine applies it before `PrePass`). So `GetAvailableFunds()` drops by the credit, and `PatchFunds` reduces live funds to match. A rewind past the credit un-credits it, exactly like a recovery `FundsEarning`.

Tombstone / supersede reversibility (re-fly). Two layers both work:

- The credit AMOUNT is derived from `SumRecoveredCredits` over ELS. When a re-fly supersedes the source recording and `CommitTombstones` tombstones the underlying `FundsEarning(Recovery)` rows, ELS hides them, so any FUTURE `EmitPendingRecoveryCredit` computes a smaller (or zero) amount automatically.
- The already-emitted credit ROWS must also be reversed. Because the credit flows through `FundsModule` as an earning AND is a route-scoped row, it is reversed the same way the gross-charge gap is now CLOSED for the credit half: a cutoff walk past the supersede UT drops it, and (for the non-cutoff case) the credit rows are themselves tombstonable. See section 6.5 for the supersede tombstone wiring.

Why `FundsModule`, not a new module. The design doc (section 13.4, [supply-routes-design.md:1152](../../../../docs/parsek-logistics-supply-routes-design.md)) keeps open the option of splitting `RouteModule` into separate KSC-funds / origin-debit / delivery modules, but explicitly says the v0 skeleton observes route rows WITHOUT mutating funds. Routing the credit through the existing `FundsModule` earning path is the minimal reversible wiring: it reuses the proven cutoff + tombstone reconciliation rather than inventing a route-funds reversal module.

`RouteModule` observation case (diagnostics only, MUST NOT mutate funds). Add an observation `case GameActionType.RouteRecoveryCredited:` to `RouteModule.ProcessAction` ([RouteModule.cs:108-132](../../../../Source/Parsek/GameActions/RouteModule.cs), the switch that already has `RouteDispatched` / `RouteCargoDebited` / `RouteCargoDelivered` / `RoutePaused` / `RouteEndpointLost` cases) that increments a per-route credited counter for diagnostics / PostWalk logging, mirroring the existing route observation cases. It MUST NOT touch funds: funds reversal is `FundsModule`'s job, and `RouteModule` is observe-only by the design-doc contract (section 13.4). Leaving this case out would silently drop the credit from the per-route walk summary; leaving it untested would let a future edit add a funds mutation here with no guard, violating the observe-only invariant. T-ROUTEMODULE-OBSERVE (section 9) asserts both halves: the credited counter increments AND no funds mutation happens for that row.

### 6.3 The live stock credit, and why it is now reversible (unlike the gross charge)

`LiveCreditFunds(double amount)`, new, a mirror of `LiveDebitFunds` ([RouteOrchestrator.cs:1479](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) but with a POSITIVE delta: `Funding.Instance.AddFunds(+amount, TransactionReasons.None)`, defensively null-checked, try / caught, logged.

The reason this is safe where the gross charge is not: the gross charge is applied live AND is invisible to the recalc walk (section 3.2 architecture fact), so a Parsek rewind cannot reverse it (only a quicksave / load can). The credit, by contrast, ALSO flows through `FundsModule` (section 6.2), so the recalc walk's `PatchFunds` knows the credit's contribution and will UNDO it on a cutoff walk. To avoid double-application (live credit at emit time PLUS recalc adding it again), follow the SAME pattern the gross charge uses today: the live mutation is the immediate effect; the recalc walk's `PatchFunds` is a reconcile-to-target, not an additive replay (`PatchFunds` computes `delta = target - current` and applies that single delta, [KspStatePatcher.cs:646-670](../../../../Source/Parsek/GameActions/KspStatePatcher.cs)).

The reconcile target (`funds.GetAvailableFunds()`, [FundsModule.cs:606-613](../../../../Source/Parsek/GameActions/FundsModule.cs)) is NOT a single formula. It has TWO branches, and the reversibility argument MUST hold for BOTH. The cutoff-walk branch is the one the earlier draft of this section glossed over, and it is the live-event recalc path the route emit actually triggers.

- BRANCH (a), FULL WALK (no committed action after the recalc UT, `hasProjectedAvailableFunds == false`). `GetAvailableFunds()` returns `initialFunds + totalEarnings - totalCommittedSpendings` clamped at 0 ([FundsModule.cs:611](../../../../Source/Parsek/GameActions/FundsModule.cs)). Under Option A the credit is in `totalEarnings` and the gross debit is in `runningBalance` via `ProcessRouteCargoDebited` (and, for the committed-spendings tally, the debit subtracts there too). So when no rewind has happened, `target` already INCLUDES the credit and the debit, and `current` (live funds) already INCLUDES the live credit and the live debit, so `delta` is ~0 and `PatchFunds` is a no-op. After a rewind cutoff to before the credit UT, the credit row is excluded from `target`, `current` still holds the live credit, so `PatchFunds` subtracts it. This is the branch T-AMOUNT / T-FUNDSMODULE-* / T-RECONCILE-NOOP exercise.

- BRANCH (b), CUTOFF WALK / live-event recalc (any committed action exists after the recalc UT, `hasProjectedAvailableFunds == true`). This is the branch `LedgerOrchestrator.RecalculateAndPatchForLiveTimelineEvent` / `RecalculateAndPatchForCurrentTimelineIfFutureActions` ([LedgerOrchestrator.cs:1371,1390](../../../../Source/Parsek/GameActions/LedgerOrchestrator.cs)) take whenever future committed actions exist. Here `GetAvailableFunds()` returns `projectedAvailableFunds`, which is the RESERVATION MINIMUM, not `runningBalance`: `RecalculationEngine.ProjectAvailability` ([RecalculationEngine.cs:598-636](../../../../Source/Parsek/GameActions/RecalculationEngine.cs)) walks every FUTURE action via `TryGetProjectionDelta`, tracks the minimum projected balance, and returns `min(projected) > 0 ? min : 0` ([:624-630](../../../../Source/Parsek/GameActions/RecalculationEngine.cs)). That minimum can be STRICTLY LESS than the current live balance when committed future `FundsSpending` rows exist (the reservation system intentionally holds funds back for them). So on this branch `PatchFunds` sets `delta = projectedMin - currentLive`, which is NEGATIVE and REDUCES live funds, INDEPENDENT of the credit. This is NOT a same-frame no-op; it is the reservation floor doing its job. The credit's contribution stays consistent because `TryGetProjectionDelta` returns `+RouteKscFundsCost` for `RouteRecoveryCredited` and `-RouteKscFundsCost` for `RouteCargoDebited` ([FundsModule.cs:668-674](../../../../Source/Parsek/GameActions/FundsModule.cs), Option A confirmed): a future credit RAISES the projected floor and a future debit LOWERS it, so the projection sees the route's debit/credit pair exactly as the live mutations did. The reversibility property on this branch is: the credit's projection delta is included in the floor iff its UT is after the cutoff; a cutoff past the credit UT drops the `+credit` from the projection, lowering the floor, and `PatchFunds` reconciles live funds down by the credit. The reversal still holds; what changes is that `target` is the floor, not `runningBalance`, so the test must assert the floor INCLUDING the credit, not raw `runningBalance`.

Reversible by construction on both branches: the full walk reconciles to seed + earnings - spendings (credit in `totalEarnings`, debit in committed spendings); the cutoff walk reconciles to the reservation minimum the projection computes (credit and debit both visible to `TryGetProjectionDelta`). The cutoff-walk reversibility is NOT optional to prove: it is the branch the live route emit triggers whenever any future committed action exists, so the test matrix MUST include a cutoff walk AT the credit UT with a committed future `FundsSpending` present, asserting live funds land on the reservation floor including the credit (T-REWIND-RESERVATION, section 9), not on raw `runningBalance`.

IMPORTANT CONSISTENCY REQUIREMENT. The asymmetry hazard is what forces Option A. If the credit flowed through `FundsModule` (as an earning) but the gross DEBIT did NOT, the two would be asymmetric in the recalc walk: after a full (non-cutoff) recalc, `GetAvailableFunds()` would include the credit but NOT the gross debit (FundsModule would ignore route debit rows), so `target` would be inflated by the credit over a `current` (live funds) that already reflects (gross out, credit in). `PatchFunds`, reconciling to that inflated target, would ADD the credit back on top of live funds, double-counting it upward on EVERY recalc, not just after a rewind. That double-application in the no-rewind steady state is the one real hazard in the plan, and it is exactly the failure mode T-RECONCILE-NOOP (section 9) guards against. Resolve it one of two ways, and the implementer MUST pick and prove one before enabling:

- OPTION A (preferred, symmetric): ALSO route the gross DEBIT through `FundsModule` as a spending, so the recalc walk models the full per-cycle (out gross, in credit). Add a `case RouteCargoDebited:` to `FundsModule.ProcessAction` that subtracts `RouteKscFundsCost`, and to `ComputeTotalSpendings` / `TryGetProjectionDelta`. This makes `GetAvailableFunds()` reflect the true net of every committed cycle, so `PatchFunds` is a faithful reconcile. This is the clean end state and also retroactively CLOSES the existing gross-charge rewind hole (section 3.2) as a bonus. It is a larger blast radius (it changes how the gross charge reconciles), so it needs the full rewind test matrix (section 9) including the gross-debit-only legacy case (T-LEGACY-DEBIT).
- OPTION B (narrow, credit-only, ship-disabled-if-unproven): keep `FundsModule` ignoring the debit, and make the credit NOT flow through `FundsModule` either; instead reverse the live credit purely through the cutoff path by giving `RouteRecoveryCredited` its own reconcile. This is MORE code and re-implements what `FundsModule` already does, so it is not recommended. If Option A cannot be proven reversible in the test matrix, the feature stays DISABLED behind a settings flag (default off) rather than shipping a non-reversible funds mutation (design doc section 10.5 hard rule).

DECISION: implement OPTION A. It is the only design where both the gross debit and the recovery credit participate in the same recalc + patch reconciliation, which is what "the net funds effect of a cycle equals the displayed net" actually requires end to end. Treating the debit as a `FundsModule` spending and the credit as a `FundsModule` earning makes the whole cycle reversible through one mechanism. The plan's test matrix (section 9) is written for Option A.

INTENTIONAL BEHAVIOR CHANGE (OQ3 resolved, author-decided). Option A is KEPT deliberately, with eyes open about its blast radius. Routing the gross DEBIT through `FundsModule` as a spending makes the gross dispatch charge recalc / rewind reversible, which CLOSES the pre-existing fire-and-forget hole (section 3.2) where a Parsek timeline rewind did NOT reverse a route's gross charge (only a stock quicksave / load did). This is a deliberate behavior change, not an accident: it is strictly more correct that a Parsek rewind past a dispatch reverses BOTH halves of that cycle's funds movement (gross out and recovered in), exactly as it already reverses every other recorded funds row. The rationale for keeping it over the narrower scope-to-new-cycles fallback: a half-reversible cycle (credit reversible, gross debit not) is harder to reason about than a fully reversible one, and the reservation projection (`TryGetProjectionDelta`) is only correct if the debit is in the projection too. The cost is that a PRE-EXISTING save that charged gross live-only (the row present but never walked) will, after this lands, have that gross row pulled into the walk and reconciled by `PatchFunds` on the first load / recalc, which can MOVE funds even with no rewind. That movement is the intended correction (the live funds and the walk are brought into agreement), but it is a change to existing saves and MUST be proven, not assumed:

- This change REQUIRES the regression tests T-LEGACY-DEBIT (a bare legacy `RouteCargoDebited` row with no paired credit is now counted as a spending and not double-subtracted) and T-REWIND / T-REWIND-RESERVATION (a Parsek rewind now reverses the gross charge), both in section 9. The gross-charge reversal is the whole point of keeping Option A, so a PASSING test that a Parsek rewind reverses the gross charge is mandatory; the feature does not ship without it.
- If, during implementation, the audit shows Option A breaks a relied-upon existing behavior (e.g. a legacy save's funds visibly jump on first load in a way that corrupts a committed economy, or a non-route consumer depends on the gross charge staying out of the walk), this is NOT to be shipped silently: escalate it as a BLOCKER and fall back to scoping Option A to NEW cycles via the per-row flag (OPTION B / OQ3 fallback), with its own test. The default remains "apply to all" because reversibility is strictly more correct, but the escalation path is explicit so a discovered regression stops the ship rather than being papered over.

### 6.4 The idempotency backstop helper

`IsRecoveryCreditAlreadyInLedger(string routeId, string cycleId)`, new in `RouteOrchestrator.cs`, an exact structural mirror of `IsDeliveryAlreadyInLedger` ([RouteOrchestrator.cs:1365](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)): wrap `EffectiveState.ComputeELS()` in try / catch (treat a throw as not-in-ledger, [:1371-1380](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), scan for a `RouteRecoveryCredited` row with matching `(RouteId, RouteCycleId)` by `StringComparison.Ordinal`, return on first match. This reads ELS (supersede / tombstone aware), which is correct: a tombstoned credit row must NOT block re-emitting a fresh credit on a re-fly, so reading ELS (which hides tombstoned rows) is the right surface.

### 6.5 Supersede tombstone wiring for already-emitted credit rows

`SupersedeCommit.IsWorldStateChangingRecordingAction` ([SupersedeCommit.cs:1842-1862](../../../../Source/Parsek/SupersedeCommit.cs)) already excludes route types from the strict / retry block; add `case GameActionType.RouteRecoveryCredited: return false;` to that switch alongside the other route types (it is scheduler-emitted, not flight-recorder output). That keeps it from blocking auto-seal.

For the FUNDS effect: a `RouteRecoveryCredited` row's funds contribution is reversed by the cutoff walk (rewind) automatically (section 6.2). For the non-cutoff supersede case (re-fly merge that does NOT rewind live funds but tombstones the source subtree), the credit rows attributed to the superseded cycles should be tombstoned so a subsequent full recalc drops their earning. Route rows are not `RecordingId`-scoped the way flight rows are (they carry `RouteId`, and `RecordingId` is null), so the existing recording-subtree tombstone scan ([SupersedeCommit.cs:2152](../../../../Source/Parsek/SupersedeCommit.cs) `CommitTombstones`, which keys on `RecordingId` in the subtree set) will NOT pick them up. Two acceptable resolutions, pick one and test it:

- RESOLUTION 1 (rely on amount-recompute, simplest): do NOT tombstone old credit rows. After the source recovery `FundsEarning` is tombstoned, FUTURE credits compute zero (section 6.2 first bullet), and the already-applied past credits remain in the ledger as historical fact. On any cutoff walk to before the supersede UT, they are excluded; on a full recalc they still count, which is correct because those cycles genuinely happened and genuinely recovered funds before the re-fly. This is the conservative, no-new-tombstone-code path. It is consistent with how the existing gross-debit live charge is treated (past charges are not retroactively refunded by a re-fly that does not rewind).
- RESOLUTION 2 (full retroactive reversal): extend `CommitTombstones` (or a sibling) to also tombstone `RouteRecoveryCredited` rows whose `RouteId` belongs to a route whose source tree is in the superseded subtree. This is more invasive (route-to-subtree attribution is not currently in the tombstone scan) and risks over-reversing cycles that legitimately completed.

DECISION: RESOLUTION 1 for this cut. It is reversible on rewind (the safety-critical path) and does not retroactively rewrite completed-cycle history, matching the gross-charge precedent. Record RESOLUTION 2 as an open question (section 12, OQ2) for a future audit of re-fly + active-route interaction. Add a focused test that proves RESOLUTION 1's behavior (section 9, T-SUP).

---

## 7. Blocked / skipped cycle handling (no dispatch -> no credit FOR THAT CYCLE)

Only cycles that actually DISPATCHED (charged gross) ever set a pending credit, so only dispatched cycles are ever credited. The loop path separates these cleanly, but under PRIOR-cycle pairing one subtlety matters: a blocked crossing does not dispatch and owes NO credit of its own, but it may still need to FLUSH the PRIOR dispatched cycle's owed credit, because that prior cycle did dispatch and its "next crossing" is THIS blocked one.

- BLOCKED cycle (ineligible). `ProcessLoopRoute` checks `CheckEligibility`; when `!elig.Eligible` it emits NOTHING for its OWN cycle (no debit, no delivery), bumps `route.SkippedCycles`, snaps `LastObservedLoopCycleIndex` forward, and returns BEFORE `EmitLoopCycle` is ever called ([RouteOrchestrator.cs:552-567](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). The blocked cycle sets no pending credit (it did not dispatch), so it is never itself credited. Correct: no debit happened, so no credit is owed for the blocked cycle. BUT: if the PRIOR cycle dispatched and left a pending credit, the blocked crossing is that credit's "next crossing." DECISION: flush the prior pending credit on the blocked branch too. Insert `EmitPendingRecoveryCredit(route, currentUT, env)` at the top of the `!elig.Eligible` branch (before the early return), so a blocked cycle that follows a dispatched one still pays back the dispatched cycle's recovery one interval later. This keeps the deferral honest across a blocked gap: the prior dispatch's credit lands on the next crossing regardless of whether THAT crossing dispatches. (If the prior cycle did not dispatch, the pending marker is null and the flush is a no-op.)
- SKIPPED via `WaitFunds` / `WaitDestinationFull` / `EndpointLost` on the non-loop path: those routes never enter `EmitLoopCycle` either (they take `ApplyWait` / `ApplyEndpointLost`, [RouteOrchestrator.cs:379-389](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). The `EndpointLost` transition flushes the prior pending credit per section 5.4; the non-loop waits are dead for v0 loop-routes and own no pending credit.
- The cycleId formula `cycle-{CompletedCycles + SkippedCycles}` ([RouteOrchestrator.cs:544](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) advances past skipped cycles via `SkippedCycles`, so a blocked cycle's id is consumed and the next DISPATCHED cycle gets a fresh, unique id. The credit pairs to the DISPATCHED cycle's id (the one stored in the pending marker), never to a skipped id (a skipped cycle never sets a pending marker, so no credit row ever carries a skipped id).

Test T-BLOCK (section 9) asserts two things: (1) a blocked cycle emits zero ledger rows for ITS OWN cycle (no debit, no delivered, no credit keyed on the blocked id), `SkippedCycles` increments, and the next eligible cycle's debit carries the correctly-advanced cycleId; and (2) a blocked crossing that FOLLOWS a dispatched cycle DOES flush the prior dispatched cycle's credit (one `RouteRecoveryCredited` row keyed on the PRIOR cycle's id, at the blocked crossing's UT).

---

## 8. Reconciliation with the run-cost DISPLAY feature (REQUIRED)

The display feature (`docs/dev/done/plans/logistics-run-cost-display.md`, D1) shipped a caveat: "today the per-cycle charge is the GROSS launch cost; the net shown is what the run truly costs you once the transport is recovered." That caveat appears in:

- the run-cost display plan, Decision D1 ([logistics-run-cost-display.md:100-108](logistics-run-cost-display.md)), which calls crediting recovery per cycle "a much bigger, riskier change" and an explicit "Alternative, larger" to be split into its own design (this document).
- the design doc section 11 / KSC cost tuning note that the display plan's Phase 4 was to add (display plan section 6, item under Phase 4: "record that recovery-aware NET cost is now displayed, and that crediting recovery in the per-cycle CHARGE remains deferred").
- the detail-panel tooltip wording the display feature renders (display plan Phase 3.1: "the D1 caveat (per-cycle charge is currently gross)").

REQUIRED once this credit lands (do all three in the same commit that enables the credit):

1. Flip the display tooltip wording from "per-cycle charge is currently gross" to "the per-cycle charge now matches the displayed net: gross is fronted at dispatch and the recovered amount is credited back one cycle later." Keep it one short tooltip, ASCII, no funds glyph, InvariantCulture. Find the literal in the display feature's presentation layer (the display plan routes formatting through `LogisticsDeliveryPresentation` / a new `LogisticsCostPresentation`, display plan Phase 3.1) and update it there, with a matching presentation test.
   - CROSS-CHECK (timing dependency, do NOT decouple from section 5.1). The phrase "credited back one cycle later" is ACCURATE ONLY under the prior-cycle deferred pairing (section 5.1): the credit genuinely lands one dispatch interval after the debit, on a strictly later tick. If the design were ever reverted to the rejected same-tick pairing (credit in the dispatching cycle's own tick), this tooltip would be MORE misleading than the original gross caveat, because it would advertise a deferral the ledger does not perform (funds would dip and instantly recover in one tick, contradicting "credited back one cycle later"). The realized economy under the same-tick collapse is net-at-dispatch, so the tooltip flip is valid ONLY while section 5.1's deferred-by-one-crossing timing holds. Tie the tooltip change to that timing explicitly so a future timing regression also flags the now-wrong tooltip.
2. Update `docs/dev/done/plans/logistics-run-cost-display.md` Decision D1: mark the "Alternative, larger" as DONE (implemented by `logistics-recovery-credit.md`), and change the consequence line so it no longer says the charge is gross.
3. Update `docs/parsek-logistics-supply-routes-design.md` section 11 / the KSC cost tuning note: state that the per-cycle CHARGE now reconciles to the displayed net via the deferred recovery credit, and remove the "crediting recovery in the per-cycle charge remains deferred" deferral.

Do NOT change the run-cost calculator's net math or the display's read-side; the displayed net is already correct. This plan only makes the LEDGER match what the display already shows. The display still reads `SumRecoveredCredits` over ELS, which now also feeds the credit emit, so display and charge stay consistent through one source of truth.

---

## 9. Test matrix

xUnit, `[Collection("Sequential")]` for any test touching `Ledger` / `RecordingStore` / `ParsekScenario` shared static state, with `ResetForTesting()` in setup / dispose. Use the `ParsekLog.TestSinkForTesting` log-capture pattern (CLAUDE.md Testing Requirements) to assert the code path logged the expected credit data. Reuse the existing route-orchestrator test seams (`DeliveryApplierForTesting` [RouteOrchestrator.cs:847](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs), `LoopUnitResolverForTesting` [:429](../../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) and the FundsModule internal accessors (`GetRunningBalance` / `GetTotalEarnings` / `GetAvailableFunds`).

Pure / arithmetic (no live KSP):

- T-AMOUNT: `EmitPendingRecoveryCredit` amount == `SumRecoveredCredits` over the source tree (reuse the display feature's deterministic ELS lists). Single recovery, multiple recoveries summed, zero-recovery -> no credit row emitted (and the pending marker is cleared so the dispatched cycle is not retried forever). STABILITY (double-source invariant, section 3.3): after emitting `RouteRecoveryCredited` rows for cycle-0 .. cycle-N into the ledger, recomputing `SumRecoveredCredits` over the SAME source tree for cycle-(N+1) returns the IDENTICAL amount (the already-emitted credit rows do not enter the tree's recording-id scope and are not `FundsEarning(Recovery)`, so they never feed back into the sum). Assert the per-cycle credit amount is CONSTANT and does not grow with the number of credits already present.
- T-TYPE: `RouteRecoveryCredited` round-trips through `SerializeInto` / `DeserializeFrom` with `RouteId` / `RouteCycleId` / `RouteKscFundsCost` preserved; an unknown future type id deserializes to a warn + skip (existing `GameAction.cs:678-682` behavior, guard it does not throw).
- T-SCENARIO-ROUNDTRIP (CLAUDE.md Post-Change Checklist, section 6.1): a `RouteRecoveryCredited` row with `RouteId` / `RouteCycleId` / `RouteKscFundsCost` survives a full ledger save -> load through the `ParsekScenario` ledger persistence path (not just the direct `SerializeInto` / `DeserializeFrom` of T-TYPE). Because `ParsekScenario` OnSave / OnLoad cannot be driven directly from xUnit (Planetarium + Unity GameEvents are unguarded, see reference `ParsekScenario OnSave/OnLoad cannot be driven from xUnit`), use the established source-text gate pattern (`ChainSaveLoadTests.ChainStateNotPersistedInScenario` style) to confirm the ledger-node serialization path routes `RouteRecoveryCredited` through the same additive `GameAction` codec it routes the other route rows through, OR assert a `Ledger.SerializeInto` / round-trip over a node containing the credit row preserves it. The goal is to prove the new action type is not silently dropped at the scenario layer; pick whichever of the two satisfies the no-Unity constraint.
- T-CODEC (section 5.6): a `Route` with `PendingRecoveryCreditCycleId` / `PendingRecoveryCreditDispatchUT` set round-trips through `RouteCodec.SerializeInto` / `DeserializeFrom` with both fields preserved; a route with no pending marker omits both keys and deserializes to null / -1 (sparse-default proof).
- T-FUNDSMODULE-CREDIT: `FundsModule.ProcessAction(RouteRecoveryCredited)` adds the amount to `runningBalance` and `totalEarnings`; `!Effective` skips it; `TryGetProjectionDelta` returns the positive delta.
- T-FUNDSMODULE-DEBIT (Option A): `FundsModule.ProcessAction(RouteCargoDebited)` subtracts `RouteKscFundsCost`; `ComputeTotalSpendings` counts it; `TryGetProjectionDelta` returns the negative delta. A full walk over (FundsInitial, RouteCargoDebited gross, RouteRecoveryCredited recovered) yields `GetAvailableFunds() == initial - gross + recovered`. Confirm the projection signs explicitly (the cutoff-walk reservation floor depends on them): `TryGetProjectionDelta(RouteRecoveryCredited)` returns `+RouteKscFundsCost` and `TryGetProjectionDelta(RouteCargoDebited)` returns `-RouteKscFundsCost` ([FundsModule.cs:668-674](../../../../Source/Parsek/GameActions/FundsModule.cs)).
- T-ROUTEMODULE-OBSERVE (section 6.2): `RouteModule.ProcessAction(RouteRecoveryCredited)` increments the per-route credited diagnostic counter (asserted via the `PostWalk` summary log or the per-route accessor) and performs NO funds mutation (RouteModule never touches `Funding` / `runningBalance`; assert the observe-only invariant holds). This pins that the credit's funds reversal stays `FundsModule`'s job and a future edit cannot silently add a funds mutation to the RouteModule case.

Option A reconciliation (the load-bearing Option A safety proofs, section 6.3):

- T-RECONCILE-NOOP (the no-rewind steady-state double-count guard, section 6.3 IMPORTANT CONSISTENCY REQUIREMENT): seed a full (non-cutoff) action list of FundsInitial + `RouteCargoDebited(gross)` + `RouteRecoveryCredited(recovered)`, with the fake `Funding` target's live funds ALREADY at `initial - gross + recovered` (the live debit and live credit already applied). Run a full `RecalculateAndPatchCore` (no cutoff) and assert the computed `PatchFunds` delta is ~0 within epsilon. This proves Option A does NOT double-apply the credit upward on every recalc (the asymmetric-target failure mode the consistency requirement warns about): because the debit is a spending AND the credit is an earning in the walk, `target == current` and the reconcile is a true no-op.
- T-LEGACY-DEBIT (OQ3 intentional behavior change, section 6.3 INTENTIONAL BEHAVIOR CHANGE): full walk over FundsInitial + a BARE `RouteCargoDebited(gross)` row with NO paired credit (a pre-feature save's live-only gross charge, now pulled into the walk), with live funds already reflecting the historical charge (`initial - gross`). Assert `FundsModule` now counts the bare debit as a spending so `GetAvailableFunds() == initial - gross`, and that `PatchFunds` does NOT double-subtract (delta ~0 against the already-charged live funds). This is the highest-blast-radius part of Option A (it changes funds behavior on existing saves); the test makes that intentional change explicit and proven. If the implementation instead scopes Option A to new cycles via the per-row flag (the OQ3 fallback), retarget this test to assert legacy bare-debit rows are EXCLUDED from the walk and the flag gates the inclusion.

Cycle firing (orchestrator seams, no live Vessel):

- T-PAIR (prior-cycle pairing): drive TWO consecutive crossings. The FIRST crossing dispatches `cycle-K` (emits `RouteDispatched(seq0)`, `RouteCargoDebited(seq1, gross)`, `RouteCargoDelivered`, NO credit yet) and sets `PendingRecoveryCreditCycleId == "cycle-K"`. The SECOND crossing dispatches `cycle-(K+1)` AND emits exactly one `RouteRecoveryCredited(recovered)` row keyed on `RouteCycleId == "cycle-K"` (the PRIOR cycle) at the second crossing's UT. Assert the credit's UT > the first crossing's UT (real deferral, not same-tick), and that the credit's `RouteCycleId` names the dispatch it pays back, not the crossing that emitted it.
- T-CYCLE0 (the first-cycle edge, section 5.2): assert that under prior-cycle pairing the FIRST dispatched cycle (cycle-0) does NOT emit a credit on its own crossing (no prior pending marker exists when cycle-0 dispatches, so the top-of-`EmitLoopCycle` flush is a no-op), it only SETS `PendingRecoveryCreditCycleId == "cycle-0"`, and the net live-funds change after cycle-0's tick equals `-gross` ONLY (the recovered amount is NOT yet credited). This is the timing-honesty edge: cycle-0 is the one cycle whose credit is deferred to a tick that may never come if the route stops after one dispatch (then T-PAUSE-FLUSH covers the flush). Distinguishing this from the rejected same-tick design (where cycle-0 would net `-gross + recovered` in one tick) is the point of the test.
- T-STEADY-STATE (section 5.5): drive N >= 3 consecutive crossings; assert each crossing after the first emits exactly one credit, all credits carry the same `recovered` amount (the constant `SumRecoveredCredits`), and the running funds delta after crossing M (M >= 1) equals `M*(-gross) + (M-1)*(+recovered)` (the deferral tail: one fewer credit than debits at every step). This documents the approximation: a constant deferred credit, not a per-run physical landing.
- T-BLOCK (section 7): (1) an ineligible cycle emits ZERO route rows for its own id (no debit, no delivered, no credit keyed on the blocked id), increments `SkippedCycles`, and the next eligible cycle's debit carries the advanced cycleId. (2) A blocked crossing that FOLLOWS a dispatched cycle (pending marker set) DOES flush the prior dispatched cycle's credit: exactly one `RouteRecoveryCredited` row keyed on the PRIOR cycle's id, at the blocked crossing's UT.
- T-MODE-GATE: in non-Career (`env.IsCareer == false`) or non-KSC-origin (`route.IsKscOrigin == false`), neither a pending marker is set nor a credit row emitted (mirror the `EmitDispatchDebit` isCareerKsc gate); a pending marker carried in from a Career save that is reopened in Sandbox is CLEARED without emitting. One test per off-axis (Sandbox, Science, non-KSC Career).

Pause / stop tail (section 5.4):

- T-PAUSE-FLUSH (immediate `TryPause`): dispatch one cycle (pending marker set on `cycle-K`), then `TryPause` the route; assert exactly one `RouteRecoveryCredited` row keyed on `cycle-K` is emitted at pause time and the pending marker is cleared. Resume the route and run the next crossing; assert NO duplicate `cycle-K` credit (the marker was cleared) and the post-resume dispatch arms a fresh pending marker.
- T-PAUSE-FLUSH-ARMED (armed `PauseAfterCurrentCycle`, section 5.4): arm `route.PauseAfterCurrentCycle`, drive a crossing through `ApplyDelivery` so the armed-pause transition fires (`RouteOrchestrator.cs` ~1338-1342), and assert the final owed credit flushes EXACTLY ONCE at the armed-pause transition with the correct amount and the pending marker cleared. This path flushes through the bespoke `ApplyDeliveryEnvAdapter` whose Career flag drives the gate, so a wrong `IsCareer` wiring there would strand the final owed credit on the most common "pause after this cycle" UX path; the test pins the Career-KSC gate is correctly wired on the adapter.
- T-PAUSE-FLUSH-ENDPOINTLOST (`EndpointLost` / source-missing transition, section 5.4): drive an `EndpointLost`-at-delivery transition with a pending marker set and assert the owed credit flushes (or no-ops cleanly on the Career / zero-recovery gate, with the marker cleared in either case). Cover both the `EndpointLost` flush and the `RevalidateSources` -> `MissingSourceRecording` / `SourceChanged` flush (section 5.4): when `RouteStore.RevalidateSources` flips a route to `MissingSourceRecording` or `SourceChanged` between crossings, the pending credit must be flushed (or cleared via the defensive zero-recovery / no-funds-context no-op) at that transition, so the last dispatched cycle's owed credit is not stranded forever on a route that never crosses again (`SourceChanged` never auto-recovers). Mirror the `Pause_FlushesFinalOwedCredit` pattern.

Save / reload + idempotency:

- T-NODOUBLE: with `PendingRecoveryCreditCycleId == "cycle-K"` set, call the crossing twice for the same `(routeId, "cycle-K")` (simulating a save / reload double-tick); the credit row is emitted exactly ONCE; the second call hits `IsRecoveryCreditAlreadyInLedger`, emits nothing, and clears the stale pending marker. Assert the verbose "replay" log fired.
- T-CRASH-WINDOW (section 5.3): seed a state where `cycle-K` dispatched (pending marker on disk) but the credit row is NOT yet in ELS, simulate a reload, and run the next crossing; assert `cycle-K`'s credit IS emitted (no permanently-missing credit). Then seed the inverse (credit row already in ELS but pending marker also set, e.g. crash between emit and clear) and assert the keyed backstop suppresses the double emit and clears the marker.
- T-CRASH-WINDOW-TOMBSTONE (section 5.3 crash-window + tombstone interaction): seed `cycle-K` dispatched with the pending marker on disk but NO credit row yet emitted (the crash window), then tombstone the source `FundsEarning(Recovery)` rows (synthetic `LedgerTombstone` set + `BumpTombstoneStateVersion`) as if a re-fly / supersede happened between crash and reload, then run the next crossing (the replay flush). Assert NO credit row is emitted because the replay-path `EmitPendingRecoveryCredit` recomputes the amount FRESH from ELS (tombstoned recovery hidden -> `SumRecoveredCredits` returns 0 -> the `recovered <= 0` guard fires) and clears the pending marker. This proves the replay flush never uses a cached pre-crash amount, so a tombstone applied during the crash window correctly zeroes the owed credit. If an implementer ever caches the amount on the `Route`, this test fails (a stale non-zero credit would be emitted).

Rewind reversal (the safety-critical path):

- T-REWIND (the safety-critical rollback path, full-walk branch): build a ledger with FundsInitial + a dispatched cycle (gross debit at dispatch UT) + its recovery credit at the NEXT crossing UT, with NO committed action after the credit (so the recalc takes the FULL-walk branch, `hasProjectedAvailableFunds == false`). Run `RecalculateAndPatchCore` with `utCutoff` BEFORE the credit UT; assert `GetAvailableFunds()` (and a fake `Funding` target via the patcher's computed delta) drops by exactly the credit amount, then run a cutoff BEFORE the gross debit UT and assert the debit cutoff behaves symmetrically (Option A: funds rise back, the gross spending is excluded too). This proves a Parsek timeline rewind un-credits the recovery AND reverses the gross charge (the OQ3 intentional behavior change, section 6.3). The gross-charge reversal assertion is MANDATORY: it is the regression test the OQ3 decision requires.
- T-REWIND-RESERVATION (the cutoff-walk / reservation-floor branch, section 6.3 BRANCH (b)): the same dispatched cycle (gross debit + recovery credit), but ALSO seed a committed FUTURE `FundsSpending` after the credit UT so the recalc takes the CUTOFF / projection branch (`hasProjectedAvailableFunds == true`, `GetAvailableFunds()` returns the reservation minimum from `ProjectAvailability`, NOT raw `runningBalance`). Run the live-event recalc AT the credit UT and assert live funds land on the RESERVATION FLOOR that INCLUDES the credit (the future `+credit` raises the floor, the future `FundsSpending` and the gross `-debit` lower it), NOT on `runningBalance`. Then run a cutoff BEFORE the credit UT and assert the floor drops by the credit (the `+credit` projection delta is excluded). This is the branch the live route emit actually triggers whenever any future committed action exists; without it the reversibility proof only covers the full-walk branch the earlier draft assumed. Mirror `CommittedScienceCacheRebuildTests.RecalculateAndPatch_CutoffWalk_*` or the `RecalculationEngine` projection tests for the harness.
- T-FUNDS-OUT-THEN-BACK (the timeline assertion, the deferral proof): simulate two consecutive crossings across one dispatch interval. After the FIRST crossing (cycle K dispatch), live funds reflect ONLY the gross debit (out, not yet back) because cycle K's credit is owed but not yet emitted. After the SECOND crossing (cycle K+1, which flushes cycle K's credit), live funds reflect (gross K out + recovered K in) plus cycle K+1's own gross debit. Assert the intermediate state after crossing 1 shows funds DOWN by gross only (timing honesty), NOT the net. This is the test that proves the deferral is real and not collapsed to dispatch; it is satisfiable ONLY under the prior-cycle pairing (section 5.1) because that is the only pairing where the credit lands on a strictly later tick than the cycle it credits.

Re-fly / supersede:

- T-SUP (section 6.5 RESOLUTION 1): tombstone the source `FundsEarning(Recovery)` rows (via a synthetic `LedgerTombstone` set + `BumpTombstoneStateVersion`), then call `EmitPendingRecoveryCredit` (with a pending marker set); assert the FUTURE credit amount computes to zero because `SumRecoveredCredits` reads ELS (tombstoned rows hidden) and the pending marker is cleared. Assert already-emitted past credit rows are left intact (RESOLUTION 1) and that a cutoff walk to before the supersede UT still excludes them.
- T-SUP-NOBLOCK: assert `RouteRecoveryCredited` is excluded by `IsWorldStateChangingRecordingAction` (does not strict-block or retry-block a re-fly auto-seal).

In-game (only if a path genuinely needs live KSP). The funds mutation and the recalc reconcile are all xUnit-reachable through the seams above; no in-game test is required unless a reviewer identifies a live-`Funding` interaction the seams cannot model. If so, add an `[InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT)]` that dispatches one cycle and asserts the live funds timeline (out then back), per the Unity-runtime coverage rule.

---

## 10. Gotchas (read before coding)

- G1 (reversibility is the gate, not a nice-to-have). If the implementer cannot make Option A (section 6.3) prove reversible in T-REWIND and T-FUNDS-OUT-THEN-BACK, the feature ships DISABLED behind a settings flag (default off). A non-reversible funds mutation is forbidden (design doc section 10.5).
- G2 (do not collapse the timing). The credit fires at the NEXT crossing (one dispatch interval after the dispatch it pays back), keyed on the PRIOR dispatched cycle's id, NOT in the same `EmitLoopCycle` tick as the cycle's own dispatch. Crediting in the dispatching cycle's own tick collapses to net-at-dispatch, which is the rejected design (section 5.1). The deferral is the whole point: the credit MUST land on a strictly later tick than the cycle it credits (T-FUNDS-OUT-THEN-BACK is satisfiable only under this pairing). Keep the funds gate and the gross debit untouched (section 4).
- G3 (gross debit is currently NOT in the recalc walk). Section 3.2 architecture fact: the existing gross charge is fire-and-forget and invisible to `FundsModule`. Option A FIXES this by routing the debit through `FundsModule`; do NOT leave the debit out of the walk while putting the credit in, or `PatchFunds` double-counts the credit upward (section 6.3 IMPORTANT CONSISTENCY REQUIREMENT).
- G4 (amount source is ELS, tree-scoped). Reuse `SumRecoveredCredits(route, ComputeELS(), ResolveTreeRecordingIds(route))`. Never `Route.RecordingIds` (post-undock recovery leg is excluded, display gotcha G1). Never recompute from part costs (display gotcha G3). Never reference `Ledger.Actions` / `.CommittedRecordings`.
- G5 (Career + KSC origin only). Gate `EmitPendingRecoveryCredit` (and the pending-marker set in `EmitLoopCycle`) and `FundsModule` credit processing on the same `isCareerKsc` predicate the debit uses. No credit and no pending marker in Sandbox / Science / non-KSC; a stale pending marker carried in from a Career save reopened off-axis is cleared, not emitted.
- G6 (idempotency keyed on the credit's own row, the PRIOR cycle). `IsRecoveryCreditAlreadyInLedger` scans for `RouteRecoveryCredited` keyed on the PENDING (prior dispatched) cycle id, NOT `RouteCargoDelivered` and NOT the crossing's own cycle id. The delivery backstop and the credit backstop are separate keys; a present delivery row must not be read as "credit already emitted" (section 5.3).
- G9 (pending marker is the deferral's state, persist it). The credit is owed across crossings via `PendingRecoveryCreditCycleId` / `PendingRecoveryCreditDispatchUT` on `Route`; these MUST persist through `RouteCodec` (section 5.6) or a save / reload between crossings silently drops the owed credit. The final owed credit MUST be flushed on pause / stop / endpoint-lost (section 5.4) or the last dispatched cycle is never credited.
- G7 (positive magnitude, type carries the sign). Store the recovered amount as a positive `RouteKscFundsCost`; the `RouteRecoveryCredited` type means "credit". Mirrors the debit / delivered convention. Do not store a negative.
- G8 (no funds glyph, InvariantCulture). All logged / displayed amounts: plain number + the word "funds", `ToString("R", InvariantCulture)` for ledger values.

---

## 11. Out of scope (explicit)

- Changing the dispatch debit or the `KscFundsAvailable` gate (both stay on gross, section 4).
- Crediting recovery in the dispatching cycle's own tick / net-at-dispatch (rejected, section 5.1).
- Precise per-run recovery-UT-mapped credit timing with cycle overlap (deferred, section 5.5 / OQ1). The v0 model credits a constant per-cycle amount one interval later, not each run's physical landing.
- Retroactive tombstoning of already-emitted credit rows on re-fly (RESOLUTION 1 ships; RESOLUTION 2 is OQ2).
- Non-KSC origin funds modeling (those deduct cargo, not funds).
- Science / reputation incidentally earned on a supply run (not funds, not folded in).
- Any new UI surface beyond the reconciliation tooltip flip (section 8).

---

## 12. Open questions

1. OQ1 (precise per-run landing vs constant deferred credit): the v0 model credits a CONSTANT per-cycle amount one interval after each dispatch (section 5.5), which is the correct steady-state net but does NOT align each individual run's recovery to its true recorded landing UT (which routinely lands many cycles later, the genuine cycle-overlap case). Should the credit instead fire at the recorded recovery UT mapped into the loop, modeling run K's recovery landing during cycle K+3 or wherever it physically lands? That requires a SECOND clock keyed on the recorded recovery UT plus overlap bookkeeping (multiple runs' recoveries landing in the same cycle, each with its own idempotency surface). Revisit ONLY if a playtest shows the constant-deferred-credit timing reads wrong; build the concrete failing case (a route whose recovery lands several cycles after dispatch and whose constant credit visibly mis-attributes funds) before designing the second clock. Defaulting to the constant deferred credit (section 5.1, 5.5) unless a concrete case demands the precise model.
2. OQ2 (re-fly retroactive reversal): RESOLUTION 1 (section 6.5) leaves completed-cycle credit rows intact on a non-rewinding supersede. Is full retroactive reversal (RESOLUTION 2) ever wanted? Revisit when re-fly + active-route interaction gets a dedicated audit. Build the failing case first.
3. OQ3 (Option A blast radius) - RESOLVED (author decision: KEEP Option A as an intentional behavior change). Routing the gross debit through `FundsModule` (Option A) also closes the existing gross-charge rewind hole (section 3.2): a Parsek timeline rewind now reverses a route's gross charge, where before only a stock quicksave / load did. This is KEPT deliberately and documented as an intentional behavior change in section 6.3 (INTENTIONAL BEHAVIOR CHANGE), with the rationale that a fully-reversible cycle is more correct and easier to reason about than a half-reversible one, and that the reservation projection is only correct if the debit is in the walk too. The decision is gated on PASSING regression tests: T-LEGACY-DEBIT (a legacy bare gross-debit row is now a walk spending, not double-subtracted) and T-REWIND / T-REWIND-RESERVATION (a Parsek rewind reverses the gross charge), all in section 9; the feature does not ship without the gross-charge-reversal test passing. The escape hatch remains explicit: if implementation reveals Option A breaks a relied-upon existing behavior (e.g. a legacy save's committed economy visibly corrupts on first load), escalate as a BLOCKER and fall back to scoping Option A to NEW cycles via the per-row flag (with its own test), rather than shipping the regression. Default stays "apply to all" because reversibility is strictly more correct.
