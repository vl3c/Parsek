# Logistics Recovery-Credit Plan

Status: DRAFT, ready for review (all symbol / line claims verified against the worktree at commit `95e359e4`)
Branch base: `logistics-recovery-credit` (stacked on the run-cost DISPLAY feature, which already landed `RouteRunCostCalculator` + `SumRecoveredCredits` on this branch)
Scope: turn the run-cost feature's DISPLAYED net (launch minus recovered) into the ACTUAL net funds effect of a cycle, by adding a deferred per-cycle RECOVERY CREDIT to the route ledger. The gross launch cost is still charged at dispatch (unchanged); the recovery comes back LATER in the cycle as a new ledger row. Career + KSC-origin only. The whole point is ledger safety: the credit must participate in the route ledger's epoch isolation, recompute, and rollback / tombstone contract, so rewind / re-fly / supersede reverse it. If a path cannot be made reversible it stays disabled (design doc section 10.5).

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
funds(cycle) = - launch (at dispatch)  + recovered (later in the cycle)  =  - net
```

The realism is the TIMING, not a discount:

- You still front the full build cost to launch. The dispatch debit and the `WaitingForFunds` gate both stay on GROSS (section 4). You cannot launch a route you cannot fully fund.
- The recovery comes back LATER in the cycle, when the run has flown and the transport would have been recovered. Crediting at dispatch (collapsing the timing) is REJECTED: it would let a player launch a route they cannot afford because the same-tick refund papers over the shortfall.

Career + KSC-origin only. In Sandbox / Science there are no funds, so no credit. Non-KSC origins deduct physical cargo, not funds, so no credit there either (section 4, gate G5 from the display plan).

---

## 2. How to work (process for the implementer)

- Work ONLY in this worktree: `C:/Users/vlad3/Documents/Code/Parsek/Parsek-logistics-recovery-credit` (branch `logistics-recovery-credit`). Never edit, build, or commit in any other `Parsek-*/` worktree or the main `Parsek/` checkout. Use absolute paths under this worktree.
- Do NOT `git push`. Do NOT create, edit, close, merge, or comment on any GitHub PR. Commit locally on this branch only.
- Build + test from this worktree:
  - `cd Source/Parsek && dotnet build`
  - `cd C:/Users/vlad3/Documents/Code/Parsek/Parsek-logistics-recovery-credit/Source/Parsek.Tests && dotnet test`
  - Report pass / fail HONESTLY. Never claim a green run you did not observe.
- Every new method with logic gets unit tests and verbose `ParsekLog` logging. Pure logic is `internal static` for direct testability. Follow the established split: pure decisions in the Logistics / GameActions layer, no IMGUI in the credit path (this feature has no UI surface of its own beyond the reconciliation in section 5).
- Update `CHANGELOG.md` (1 line per item, user-facing, under `## 0.10.0`) and `docs/dev/todo-and-known-bugs.md` in the same commit that changes behavior. Also reconcile the run-cost display caveat (section 5).

### Hard constraints (do not violate)

- LEDGER SAFETY IS THE WHOLE POINT. Any new stock funds mutation MUST participate in the route ledger's epoch isolation, recompute, and rollback / tombstone contract (design doc sections 6.6 and 10.5). A funds mutation that cannot be reversed on rewind / re-fly / tombstone is a defect, not a feature. If a path cannot be made reversible, it must stay disabled (section 7 picks the reversible design precisely to honor this).
- ERS / ELS grep gate must stay green (`scripts/grep-audit-ers-els.ps1`, enforced by `GrepAuditTests`). The gate matches ONLY the literals `\bLedger\.Actions\b` and `\.CommittedRecordings\b`. Read effective ledger via `EffectiveState.ComputeELS()`; snapshot recordings via `ComputeERS()`. Never reference the two gated literals. `RouteOrchestrator.cs` is already on the allowlist for its raw `CommittedRecordings` / `CommittedTrees` read ([allowlist:271-286](../../../scripts/ers-els-audit-allowlist.txt)); this plan adds no new raw reads, so no allowlist change is needed.
- No em dashes anywhere (chat, code comments, CHANGELOG, commits). Use a colon, parentheses, comma, or split the sentence.
- Plain ASCII only in markdown, code, comments, and any string. No emoji, no special Unicode. Do NOT use the KSP funds glyph; write a plain number and the word "funds".
- InvariantCulture for all numeric formatting (`ToString("R", CultureInfo.InvariantCulture)` for ledger / log values).
- No `Co-Authored-By` and no AI-attribution lines in commits.

---

## 3. Ground truth: the dispatch / charge path and the ledger (verified)

Read these before touching anything. Every claim below is anchored to a line in this worktree.

### 3.1 The cycle the credit pairs to

Every v0 route is a loop-route. The loop clock owns dispatch:

- `RouteOrchestrator.ProcessLoopRoute` ([RouteOrchestrator.cs:447](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) runs once per ~1 Hz `Tick` ([RouteOrchestrator.cs:237](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It builds the loop unit, asks `RouteLoopClock` for the span-clock state, detects a DOCK crossing, and on a confirmed crossing either emits the FULL cycle (`EmitLoopCycle`) or skips it.
- The cycle id is `"cycle-" + (route.CompletedCycles + route.SkippedCycles)` ([RouteOrchestrator.cs:544](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It pins the dispatch + debit + delivered triple under one id. The same formula is recomputed in `EmitDispatchDebit` callers and `ApplyDelivery` ([RouteOrchestrator.cs:1077](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
- Re-fire is guarded by `Route.LastObservedLoopCycleIndex` (a `long`, default -1, [Route.cs:230](../../../Source/Parsek/Logistics/Route.cs)), snapped forward to `dockCycleIndex` in ALL crossing branches ([RouteOrchestrator.cs:560,581](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). It is persisted sparsely (omitted when -1) by `RouteCodec` ([RouteCodec.cs:122-124](../../../Source/Parsek/Logistics/RouteCodec.cs)). This is the PRIMARY save / reload re-fire guard the new credit's pairing must mirror.

### 3.2 What a cycle emits today (the charge)

`EmitLoopCycle` ([RouteOrchestrator.cs:788](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) runs two halves under one `cycleId`:

1. Dispatch + debit half, `EmitDispatchDebit` ([RouteOrchestrator.cs:911](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)):
   - `isCareerKsc = env.IsCareer && route.IsKscOrigin` ([:918](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - `computedCost = ComputeDispatchFundsCostForRoute(route)` (GROSS launch cost, ERS-backed, [:922](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Written to `route.KscDispatchFundsCost` (double, [:927](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - Emits `RouteDispatched` (Sequence 0) + `RouteCargoDebited` (Sequence 1). The debit row carries `RouteKscFundsCost = isCareerKsc ? (float)computedCost : 0f` ([:958](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Both via `Ledger.AddActions` ([:961](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
2. Delivery half, `ApplyDelivery` -> `ApplyDeliveryFromPlan` ([RouteOrchestrator.cs:1071,1219](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)):
   - STEP 7 is the ACTUAL stock funds charge: `if (ctx.IsCareer && ctx.IsKscOrigin && ctx.KscFundsCost > 0.0) ctx.FundsDebiter(ctx.KscFundsCost)` ([:1264](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). The production `FundsDebiter` is `LiveDebitFunds` ([:1204,1479](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), which calls `Funding.Instance.AddFunds(-cost, TransactionReasons.None)`.
   - Emits `RouteCargoDelivered` carrying the same `RouteKscFundsCost` ([:1313](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) + bumps `CompletedCycles` ([:1321](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).
   - Idempotency: `IsDeliveryAlreadyInLedger(routeId, cycleId)` ([:1365](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) scans ELS for an existing `RouteCargoDelivered` with the same `(RouteId, RouteCycleId)`. This is the orchestrator's ONLY ELS read and is the save / reload double-fire backstop ([:800,1090](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)).

CRITICAL ARCHITECTURE FACT (verified, load-bearing for section 7). The KSC charge has TWO independent effects:

- (a) the LIVE stock mutation via `LiveDebitFunds` -> `Funding.AddFunds`, applied once, at delivery time, and
- (b) the ledger ROWS (`RouteCargoDebited` / `RouteCargoDelivered`), which carry `RouteKscFundsCost` as a RECORD only.

`Ledger.AddAction` / `AddActions` do NOT trigger a recalc ([Ledger.cs:73,89](../../../Source/Parsek/GameActions/Ledger.cs); they only `BumpStateVersion`). And `FundsModule` does NOT process ANY route action type ([FundsModule.cs:125-169](../../../Source/Parsek/GameActions/FundsModule.cs) `ProcessAction` switch has no `RouteCargoDebited` / `RouteCargoDelivered` case; `ComputeTotalSpendings` [:188-234](../../../Source/Parsek/GameActions/FundsModule.cs) has no route case either). So today the GROSS charge is a fire-and-forget stock mutation that does NOT flow through the recalc walk and is NOT reconciled by `KspStatePatcher.PatchFunds` ([KspStatePatcher.cs:619](../../../Source/Parsek/GameActions/KspStatePatcher.cs)). On a Parsek timeline rewind, only a stock quicksave / load restores it (design doc section 10.6, [supply-routes-design.md:1015](../../../docs/parsek-logistics-supply-routes-design.md)). This is exactly the "route effect that is not yet reversed through the modules" the design doc warns about; the new credit must NOT replicate that hole (section 7).

### 3.3 How recovery credits are captured (the amount source)

- Recovery payouts are already `GameAction` rows: `Type = FundsEarning`, `FundsSource = FundsEarningSource.Recovery` ([GameAction.cs:91-112](../../../Source/Parsek/GameActions/GameAction.cs) enum value `Recovery = 2`), `FundsAwarded = (float)delta` (the real distance-scaled payout), `RecordingId` set to the recording the recovery happened in. Entry points: `LedgerOrchestrator.OnVesselRecoveryFunds` and the commit-time `CreateVesselCostActions` path (display plan section 3.2).
- `RouteRunCostCalculator.SumRecoveredCredits(route, els, treeRecordingIds, out count)` ([RouteRunCostCalculator.cs:96](../../../Source/Parsek/Logistics/RouteRunCostCalculator.cs)) sums `FundsAwarded` over ELS for every `FundsEarning(Recovery)` row whose `RecordingId` is in the route's SOURCE TREE member set (NOT `Route.RecordingIds`: the fly-home-and-recover leg is post-undock, gotcha G1 from the display plan). `ResolveTreeRecordingIds(route)` ([:142](../../../Source/Parsek/Logistics/RouteRunCostCalculator.cs)) builds that set via `Route.BackingMissionTreeId -> RouteTreeGuard.FindCommittedTree -> tree.Recordings.Keys`.
- This plan REUSES `SumRecoveredCredits` verbatim for the credit AMOUNT. Do not recompute a theoretical recovery from part costs (display plan gotcha G3).

### 3.4 How `FundsEarning(Recovery)` is reversed today (the reversibility template)

This is the template the credit follows. A `FundsEarning` row flows through `FundsModule.ProcessFundsEarning` ([FundsModule.cs:265](../../../Source/Parsek/GameActions/FundsModule.cs)): when `action.Effective`, it adds `FundsAwarded` to `runningBalance` and `totalEarnings`. The recalc walk's result is reconciled to live stock funds by `KspStatePatcher.PatchFunds`, which sets `Funding.Instance.Funds` to `funds.GetAvailableFunds()` ([KspStatePatcher.cs:646,670](../../../Source/Parsek/GameActions/KspStatePatcher.cs)). Reversal happens two ways, both already wired:

- CUTOFF WALK (rewind / time-jump). `RecalculateAndPatchCore(utCutoff, ...)` ([LedgerOrchestrator.cs:1668](../../../Source/Parsek/GameActions/LedgerOrchestrator.cs)) walks only actions up to `utCutoff` (the engine applies the cutoff before `PrePass`, [FundsModule.cs:101-105](../../../Source/Parsek/GameActions/FundsModule.cs)). A `FundsEarning` at a future UT is EXCLUDED, so `GetAvailableFunds()` drops, and `PatchFunds` reduces live funds to match. This is how a rewind past a recovery un-credits it.
- TOMBSTONE / SUPERSEDE (re-fly). `EffectiveState.ComputeELS()` is "ledger minus tombstones" (design doc section 3.2). `SupersedeCommit.CommitTombstones` ([SupersedeCommit.cs:2152](../../../Source/Parsek/SupersedeCommit.cs)) appends `LedgerTombstone`s for recording-scoped career actions in the superseded subtree, bumps `TombstoneStateVersion`, and the next recalc walks ELS (tombstoned rows excluded), so `PatchFunds` reconciles down. `RouteRunCostCalculator.SumRecoveredCredits` reads `ComputeELS()`, so once a recovery row is tombstoned the credit AMOUNT also drops automatically.

So: any new credit modeled as a row that `FundsModule` processes as an earning is reversible by BOTH mechanisms with zero new rollback code. That is the design in section 7.

### 3.5 How route rows interact with supersede today

`SupersedeCommit.IsWorldStateChangingRecordingAction` explicitly EXCLUDES all route action types ([SupersedeCommit.cs:1856-1861](../../../Source/Parsek/SupersedeCommit.cs)): `RouteDispatched`, `RouteCargoDebited`, `RouteCargoDelivered`, `RoutePaused`, `RouteEndpointLost` return false (they are scheduler-emitted under a `RouteId`, not flight-recorder output, so supersede must not strict-block or retry-block on them). The new credit row is a route-scoped row and inherits this exclusion (section 7.4): it is not a flight-recorder artifact, so it never blocks an auto-seal, but its FUNDS effect is still reversed through the FundsModule earning path (section 3.4).

---

## 4. The funds gate stays on GROSS (unchanged)

This plan does NOT touch the dispatch debit, the `KscDispatchFundsCost`, or the funds gate:

- The dispatch evaluator's funds check is `env.KscFundsAvailable(route, out shortfall)` ([RouteDispatchEvaluator.cs:207](../../../Source/Parsek/Logistics/RouteDispatchEvaluator.cs)), which compares live `Funding.Instance.Funds` against `ComputeDispatchFundsCostForRoute(route)` (GROSS, [LiveRouteRuntimeEnvironment.cs:118-141](../../../Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs)). A shortfall yields `WaitFunds` ([RouteOrchestrator.cs:380](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). All of this is left EXACTLY as-is.
- `EmitDispatchDebit` still computes and emits the gross debit (section 3.2). Unchanged.
- The live `LiveDebitFunds(gross)` at delivery time (STEP 7) is unchanged. The credit is a SEPARATE, LATER funds movement, not a reduction of the debit.

The credit does not collapse into the gate or the debit. You must still front the full build cost to launch.

---

## 5. Credit timing and the loop clock that fires it

### 5.1 Decision D1: fire at CYCLE COMPLETION, paired by RouteCycleId

DECIDED: the credit fires at CYCLE COMPLETION, one dispatch interval after the dispatching cycle (when the run has flown). It is paired to the dispatching cycle by `RouteCycleId`. Rationale:

- A cycle's funds story should read: debit GROSS at dispatch (cycle N's dispatch UT), credit recovered LATER (cycle N's completion). "Completion" in the loop model is the NEXT dock crossing for that route: one dock crossing == one ghost relaunch == one delivery. So cycle N's recovery credit is naturally emitted when cycle N+1's dock crossing fires, carrying `RouteCycleId = "cycle-N"`.
- This keeps the timing honest (funds out, then back one interval later) without needing a sub-cycle timer. The loop clock already fires exactly once per dock crossing with full save / reload idempotency (section 3.1), so reusing it is the lowest-risk firing mechanism.

REJECTED alternative (credit at dispatch): collapses the timing, lets a player launch a route they cannot afford because the same-tick refund hides the shortfall. Explicitly rejected by the author.

CONSIDERED alternative (recorded recovery UT mapped into the loop): more precise, but the recovery UT routinely lands BEYOND one dispatch interval (the transport flies home over many hours / days, far longer than the loop period), producing cycle overlap (cycle N's recovery would land during cycle N+3). Handling that overlap is a second clock with its own idempotency surface. DEFERRED as an open question (section 9, OQ1). The cycle-completion model maps the WHOLE recovery to one credit at the next completion, which is the correct net per cycle and avoids the overlap bookkeeping.

### 5.2 Where it fires: a third row inside the loop cycle

The credit is emitted from inside `EmitLoopCycle` ([RouteOrchestrator.cs:788](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), as a new third leg AFTER the dispatch + debit half and the delivery half, under a `RouteCycleId` that names THIS cycle. Concretely, when `EmitLoopCycle` fires cycle N (which is the COMPLETION of the prior dispatch), it emits the recovery credit FOR cycle N's own dispatch (so credit and debit share `cycle-N`). The first cycle (`cycle-0`) has a dispatch but its completion-credit is emitted on its own crossing in the same tick as its delivery (zero-or-positive transit; the loop model fires dispatch + debit + delivered in one tick per crossing, [RouteOrchestrator.cs:815-833](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Pairing credit to the SAME `cycle-N` as the debit (rather than to the prior cycle) keeps the (debit, credit) pair self-consistent under one id, which is what the idempotency check and the rollback walk key on.

New helper, `EmitRecoveryCredit(route, currentUT, env, cycleId)` in `RouteOrchestrator.cs`, called from `EmitLoopCycle` right after the delivery half (and ONLY on the real-fire path, never the replay-backstop path):

```
// Pseudocode, not final.
internal static void EmitRecoveryCredit(Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
{
    // Gate: Career + KSC origin only. Mirror EmitDispatchDebit's isCareerKsc.
    bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
    if (!isCareerKsc) { /* verbose log "credit-skip non-career-ksc"; */ return; }

    // Idempotency backstop: do not emit a second credit for the same (RouteId, cycleId).
    if (IsRecoveryCreditAlreadyInLedger(route.Id, cycleId)) { /* verbose replay log; */ return; }

    // Amount: reuse SumRecoveredCredits over the source tree, read from ELS.
    HashSet<string> treeIds = RouteRunCostCalculator.ResolveTreeRecordingIds(route);
    IReadOnlyList<GameAction> els = SafeComputeEls();   // see section 6.2
    double recovered = RouteRunCostCalculator.SumRecoveredCredits(route, els, treeIds, out int n);
    if (recovered <= 0.0) { /* verbose log "credit-skip zero-recovery"; */ return; }

    // Emit the credit row (section 6.1) and apply the live stock credit (section 6.3).
    var credit = new GameAction {
        Type = GameActionType.RouteRecoveryCredited,
        UT = currentUT,
        RouteId = route.Id,
        RouteCycleId = cycleId,
        RouteStopIndex = -1,
        Sequence = 2,                       // after RouteDispatched(0) / RouteCargoDebited(1) at dispatch UT; here it is the completion UT
        RouteKscFundsCost = (float)recovered, // positive magnitude; the action TYPE carries the credit direction
    };
    Ledger.AddAction(credit);
    LiveCreditFunds(recovered);             // section 6.3, mirror of LiveDebitFunds
    // Info log: route, cycleId, recovered, recoveryRows=n, ut.
}
```

### 5.3 No double-fire across save / reload

Two independent guards, mirroring the existing dispatch / delivery model:

1. PRIMARY: the loop clock fires `EmitLoopCycle` exactly once per dock crossing, gated by `LastObservedLoopCycleIndex` (section 3.1). The credit is emitted INSIDE `EmitLoopCycle`, so it inherits that once-per-crossing guarantee. A save / reload mid-cycle re-presents the same crossing only if `LastObservedLoopCycleIndex` was not advanced, which the existing snap-forward logic prevents.
2. BACKSTOP: `IsRecoveryCreditAlreadyInLedger(routeId, cycleId)` (new, section 6.4), an exact mirror of `IsDeliveryAlreadyInLedger` ([RouteOrchestrator.cs:1365](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) but scanning for a `RouteRecoveryCredited` row with the same `(RouteId, RouteCycleId)`. A save / reload that re-presents the same cycleId (or a double-tick) finds the existing credit row in ELS and emits NOTHING. This is essential because, unlike the debit (which is gated by `EmitLoopCycle`'s own `IsDeliveryAlreadyInLedger` replay short-circuit at [:800](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), the credit needs its own keyed backstop so a partial replay (delivery row already present but credit row not yet emitted, e.g. a crash between `Ledger.AddAction(delivered)` and `Ledger.AddAction(credit)`) does not silently skip the credit forever. Place the credit emit BEFORE the `EmitLoopCycle` replay short-circuit cannot reach it: the credit's own keyed check is the authority.

Crash-window note: `EmitLoopCycle` already has a replay short-circuit at the top ([:800](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) that returns early when the DELIVERY row exists. If a crash lands AFTER the delivery row but BEFORE the credit row, the next crossing's `EmitLoopCycle` would replay-skip the whole cycle and never emit the credit. To avoid a permanently-missing credit, `EmitRecoveryCredit` must be reachable on the replay path too: either (a) move the credit emit into the replay branch as well (emit-if-missing on replay), or (b) accept the one-cycle credit loss as a known, logged edge (a single missed credit on a mid-cycle crash). DECISION: option (a). The credit's own `IsRecoveryCreditAlreadyInLedger` check makes emitting it from the replay branch idempotent, so wire `EmitRecoveryCredit` into BOTH the fire path and the replay-backstop branch of `EmitLoopCycle` ([:800-813](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Log which path emitted it.

---

## 6. The ledger representation and reversibility (the core of the plan)

### 6.1 Decision D2: a NEW GameActionType, `RouteRecoveryCredited`

DECIDED: add a new `GameActionType.RouteRecoveryCredited = 28` ([GameAction.cs:80](../../../Source/Parsek/GameActions/GameAction.cs) currently ends at `RouteEndpointLost = 27`; explicit int for serialization stability). Rationale for a new type over extending an existing row:

- The credit is a SEPARATE funds movement at a DIFFERENT UT (cycle completion) than the debit (dispatch). Folding it onto the `RouteCargoDelivered` row would conflate two opposite-sign funds effects at one UT and break the "funds out, then back later" timing the feature exists to model.
- A distinct type lets the recompute walk, the idempotency check, and any future per-cycle audit key on `(RouteId, RouteCycleId, Type)` cleanly. The route action vocabulary was explicitly designed to allow new route types additively (design doc section 13.4).
- Serialization is additive and forward-safe: an old reader hits `Enum.IsDefined` false and logs "Unknown action type id" then ignores the row ([GameAction.cs:678-682](../../../Source/Parsek/GameActions/GameAction.cs)). No schema migration; the route `ROUTES` node and the ledger are both additive (design doc section 14).

Serialization: reuse the route-common codec. `SerializeRouteRecoveryCredited` writes `WriteRouteCommon(n)` (routeId / routeCycleId / routeStopIndex, [GameAction.cs:1222](../../../Source/Parsek/GameActions/GameAction.cs)) plus the credit amount. Store the amount in `RouteKscFundsCost` (the existing float route-funds field, [GameAction.cs:460](../../../Source/Parsek/GameActions/GameAction.cs)) so no new serialized field is needed; the action TYPE carries the credit direction, exactly as `RouteCargoDebited` / `RouteCargoDelivered` store a positive magnitude and let the type carry the sign (the same convention `RouteResourceManifest` uses, [GameAction.cs:425](../../../Source/Parsek/GameActions/GameAction.cs)). Add the `case` to both `SerializeInto` ([:642-656](../../../Source/Parsek/GameActions/GameAction.cs)) and `DeserializeFrom` ([:776-790](../../../Source/Parsek/GameActions/GameAction.cs)).

### 6.2 Which module reverses it, and how epoch isolation applies

DECIDED: `FundsModule` reverses it, by processing `RouteRecoveryCredited` as a fund EARNING. This is the single most important decision in the plan, because it makes the credit reversible by the SAME two mechanisms that already reverse `FundsEarning(Recovery)` (section 3.4) with NO new rollback code:

- Add a `case GameActionType.RouteRecoveryCredited:` to `FundsModule.ProcessAction` ([FundsModule.cs:130-169](../../../Source/Parsek/GameActions/FundsModule.cs)) that adds `(double)action.RouteKscFundsCost` to `runningBalance` and `totalEarnings` (mirror `ProcessFundsEarning` [:265](../../../Source/Parsek/GameActions/FundsModule.cs), but reading `RouteKscFundsCost` instead of `FundsAwarded`). Gate on `action.Effective` for symmetry.
- Add the same delta to `TryGetProjectionDelta` ([FundsModule.cs:539](../../../Source/Parsek/GameActions/FundsModule.cs)) as a positive earning so the cashflow-aware reservation projection sees the future credit. (Do NOT add it to `ComputeTotalSpendings` [:188](../../../Source/Parsek/GameActions/FundsModule.cs); it is an earning, not a spending.)

Epoch isolation / cutoff-walk reversibility (rewind / time-jump). A `RouteRecoveryCredited` row at the completion UT is EXCLUDED by a cutoff walk whose `utCutoff` is before that UT ([LedgerOrchestrator.cs:1668](../../../Source/Parsek/GameActions/LedgerOrchestrator.cs) passes the cutoff to the engine; the engine applies it before `PrePass`). So `GetAvailableFunds()` drops by the credit, and `PatchFunds` reduces live funds to match. A rewind past the credit un-credits it, exactly like a recovery `FundsEarning`.

Tombstone / supersede reversibility (re-fly). Two layers both work:

- The credit AMOUNT is derived from `SumRecoveredCredits` over ELS. When a re-fly supersedes the source recording and `CommitTombstones` tombstones the underlying `FundsEarning(Recovery)` rows, ELS hides them, so any FUTURE `EmitRecoveryCredit` computes a smaller (or zero) amount automatically.
- The already-emitted credit ROWS must also be reversed. Because the credit flows through `FundsModule` as an earning AND is a route-scoped row, it is reversed the same way the gross-charge gap is now CLOSED for the credit half: a cutoff walk past the supersede UT drops it, and (for the non-cutoff case) the credit rows are themselves tombstonable. See section 6.5 for the supersede tombstone wiring.

Why `FundsModule`, not a new module. The design doc (section 13.4, [supply-routes-design.md:1152](../../../docs/parsek-logistics-supply-routes-design.md)) keeps open the option of splitting `RouteModule` into separate KSC-funds / origin-debit / delivery modules, but explicitly says the v0 skeleton observes route rows WITHOUT mutating funds. Routing the credit through the existing `FundsModule` earning path is the minimal reversible wiring: it reuses the proven cutoff + tombstone reconciliation rather than inventing a route-funds reversal module. `RouteModule` ([RouteModule.cs:108](../../../Source/Parsek/GameActions/RouteModule.cs)) can ADD an observation `case RouteRecoveryCredited` (increment a per-route credited counter for diagnostics / PostWalk logging) but must NOT mutate funds; funds reversal is `FundsModule`'s job.

### 6.3 The live stock credit, and why it is now reversible (unlike the gross charge)

`LiveCreditFunds(double amount)`, new, a mirror of `LiveDebitFunds` ([RouteOrchestrator.cs:1479](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) but with a POSITIVE delta: `Funding.Instance.AddFunds(+amount, TransactionReasons.None)`, defensively null-checked, try / caught, logged.

The reason this is safe where the gross charge is not: the gross charge is applied live AND is invisible to the recalc walk (section 3.2 architecture fact), so a Parsek rewind cannot reverse it (only a quicksave / load can). The credit, by contrast, ALSO flows through `FundsModule` (section 6.2), so the recalc walk's `PatchFunds` knows the credit's contribution and will UNDO it on a cutoff walk. To avoid double-application (live credit at emit time PLUS recalc adding it again), follow the SAME pattern the gross charge uses today: the live mutation is the immediate effect; the recalc walk's `PatchFunds` is a reconcile-to-target, not an additive replay (`PatchFunds` computes `delta = target - current` and applies that single delta, [KspStatePatcher.cs:646-670](../../../Source/Parsek/GameActions/KspStatePatcher.cs)). So when no rewind has happened, `target` already INCLUDES the credit (FundsModule processed it) and `current` already INCLUDES the live credit, so `delta` is ~0 and `PatchFunds` is a no-op. After a rewind cutoff, the credit row is excluded from `target`, `current` still has the live credit, so `PatchFunds` subtracts it. Reversible by construction.

IMPORTANT CONSISTENCY REQUIREMENT. Because the credit now flows through `FundsModule` but the gross DEBIT does NOT, the two are asymmetric in the recalc walk: after a full (non-cutoff) recalc, `GetAvailableFunds()` includes the credit (as an earning) but NOT the gross debit (FundsModule ignores route debit rows). If `PatchFunds` ran with that asymmetric target it would ADD the credit back on top of live funds that already reflect (gross out, credit in), double-counting the credit upward. This is the one real hazard in the plan. Resolve it one of two ways, and the implementer MUST pick and prove one before enabling:

- OPTION A (preferred, symmetric): ALSO route the gross DEBIT through `FundsModule` as a spending, so the recalc walk models the full per-cycle (out gross, in credit). Add a `case RouteCargoDebited:` to `FundsModule.ProcessAction` that subtracts `RouteKscFundsCost`, and to `ComputeTotalSpendings` / `TryGetProjectionDelta`. This makes `GetAvailableFunds()` reflect the true net of every committed cycle, so `PatchFunds` is a faithful reconcile. This is the clean end state and also retroactively CLOSES the existing gross-charge rewind hole (section 3.2) as a bonus. It is a larger blast radius (it changes how the gross charge reconciles), so it needs the full rewind test matrix (section 8) including the gross-debit-only case.
- OPTION B (narrow, credit-only, ship-disabled-if-unproven): keep `FundsModule` ignoring the debit, and make the credit NOT flow through `FundsModule` either; instead reverse the live credit purely through the cutoff path by giving `RouteRecoveryCredited` its own reconcile. This is MORE code and re-implements what `FundsModule` already does, so it is not recommended. If Option A cannot be proven reversible in the test matrix, the feature stays DISABLED behind a settings flag (default off) rather than shipping a non-reversible funds mutation (design doc section 10.5 hard rule).

DECISION: implement OPTION A. It is the only design where both the gross debit and the recovery credit participate in the same recalc + patch reconciliation, which is what "the net funds effect of a cycle equals the displayed net" actually requires end to end. Treating the debit as a `FundsModule` spending and the credit as a `FundsModule` earning makes the whole cycle reversible through one mechanism. The plan's test matrix (section 8) is written for Option A.

### 6.4 The idempotency backstop helper

`IsRecoveryCreditAlreadyInLedger(string routeId, string cycleId)`, new in `RouteOrchestrator.cs`, an exact structural mirror of `IsDeliveryAlreadyInLedger` ([RouteOrchestrator.cs:1365](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)): wrap `EffectiveState.ComputeELS()` in try / catch (treat a throw as not-in-ledger, [:1371-1380](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)), scan for a `RouteRecoveryCredited` row with matching `(RouteId, RouteCycleId)` by `StringComparison.Ordinal`, return on first match. This reads ELS (supersede / tombstone aware), which is correct: a tombstoned credit row must NOT block re-emitting a fresh credit on a re-fly, so reading ELS (which hides tombstoned rows) is the right surface.

### 6.5 Supersede tombstone wiring for already-emitted credit rows

`SupersedeCommit.IsWorldStateChangingRecordingAction` ([SupersedeCommit.cs:1842-1862](../../../Source/Parsek/SupersedeCommit.cs)) already excludes route types from the strict / retry block; add `case GameActionType.RouteRecoveryCredited: return false;` to that switch alongside the other route types (it is scheduler-emitted, not flight-recorder output). That keeps it from blocking auto-seal.

For the FUNDS effect: a `RouteRecoveryCredited` row's funds contribution is reversed by the cutoff walk (rewind) automatically (section 6.2). For the non-cutoff supersede case (re-fly merge that does NOT rewind live funds but tombstones the source subtree), the credit rows attributed to the superseded cycles should be tombstoned so a subsequent full recalc drops their earning. Route rows are not `RecordingId`-scoped the way flight rows are (they carry `RouteId`, and `RecordingId` is null), so the existing recording-subtree tombstone scan ([SupersedeCommit.cs:2152](../../../Source/Parsek/SupersedeCommit.cs) `CommitTombstones`, which keys on `RecordingId` in the subtree set) will NOT pick them up. Two acceptable resolutions, pick one and test it:

- RESOLUTION 1 (rely on amount-recompute, simplest): do NOT tombstone old credit rows. After the source recovery `FundsEarning` is tombstoned, FUTURE credits compute zero (section 6.2 first bullet), and the already-applied past credits remain in the ledger as historical fact. On any cutoff walk to before the supersede UT, they are excluded; on a full recalc they still count, which is correct because those cycles genuinely happened and genuinely recovered funds before the re-fly. This is the conservative, no-new-tombstone-code path. It is consistent with how the existing gross-debit live charge is treated (past charges are not retroactively refunded by a re-fly that does not rewind).
- RESOLUTION 2 (full retroactive reversal): extend `CommitTombstones` (or a sibling) to also tombstone `RouteRecoveryCredited` rows whose `RouteId` belongs to a route whose source tree is in the superseded subtree. This is more invasive (route-to-subtree attribution is not currently in the tombstone scan) and risks over-reversing cycles that legitimately completed.

DECISION: RESOLUTION 1 for this cut. It is reversible on rewind (the safety-critical path) and does not retroactively rewrite completed-cycle history, matching the gross-charge precedent. Record RESOLUTION 2 as an open question (section 9, OQ2) for a future audit of re-fly + active-route interaction. Add a focused test that proves RESOLUTION 1's behavior (section 8, T-SUP).

---

## 7. Blocked / skipped cycle handling (no dispatch -> no credit)

Only cycles that actually DISPATCHED (charged gross) get a credit. The loop path already separates these cleanly:

- BLOCKED cycle (ineligible). `ProcessLoopRoute` checks `CheckEligibility`; when `!elig.Eligible` it emits NOTHING (no debit, no delivery), bumps `route.SkippedCycles`, snaps `LastObservedLoopCycleIndex` forward, and returns BEFORE `EmitLoopCycle` is ever called ([RouteOrchestrator.cs:552-567](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). Because `EmitRecoveryCredit` lives INSIDE `EmitLoopCycle`, a blocked cycle never reaches it. No credit. Correct by construction: no debit happened, so no credit is owed.
- SKIPPED via `WaitFunds` / `WaitDestinationFull` / `EndpointLost` on the non-loop path: those routes never enter `EmitLoopCycle` either (they take `ApplyWait` / `ApplyEndpointLost`, [RouteOrchestrator.cs:379-389](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)). No credit.
- The cycleId formula `cycle-{CompletedCycles + SkippedCycles}` ([RouteOrchestrator.cs:544](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) advances past skipped cycles via `SkippedCycles`, so a blocked cycle's id is consumed and the next DISPATCHED cycle gets a fresh, unique id. The credit pairs to the dispatched cycle's id, never to a skipped id (there is no credit row for a skipped id at all).

Test T-BLOCK (section 8) asserts: a blocked cycle emits zero ledger rows of ANY route type (no debit, no delivered, no credit), `SkippedCycles` increments, and the next eligible cycle's credit carries the correctly-advanced cycleId.

---

## 8. Reconciliation with the run-cost DISPLAY feature (REQUIRED)

The display feature (`docs/dev/plans/logistics-run-cost-display.md`, D1) shipped a caveat: "today the per-cycle charge is the GROSS launch cost; the net shown is what the run truly costs you once the transport is recovered." That caveat appears in:

- the run-cost display plan, Decision D1 ([logistics-run-cost-display.md:100-108](../logistics-run-cost-display.md)), which calls crediting recovery per cycle "a much bigger, riskier change" and an explicit "Alternative, larger" to be split into its own design (this document).
- the design doc section 11 / KSC cost tuning note that the display plan's Phase 4 was to add (display plan section 6, item under Phase 4: "record that recovery-aware NET cost is now displayed, and that crediting recovery in the per-cycle CHARGE remains deferred").
- the detail-panel tooltip wording the display feature renders (display plan Phase 3.1: "the D1 caveat (per-cycle charge is currently gross)").

REQUIRED once this credit lands (do all three in the same commit that enables the credit):

1. Flip the display tooltip wording from "per-cycle charge is currently gross" to "the per-cycle charge now matches the displayed net: gross is fronted at dispatch and the recovered amount is credited back at cycle completion." Keep it one short tooltip, ASCII, no funds glyph, InvariantCulture. Find the literal in the display feature's presentation layer (the display plan routes formatting through `LogisticsDeliveryPresentation` / a new `LogisticsCostPresentation`, display plan Phase 3.1) and update it there, with a matching presentation test.
2. Update `docs/dev/plans/logistics-run-cost-display.md` Decision D1: mark the "Alternative, larger" as DONE (implemented by `logistics-recovery-credit.md`), and change the consequence line so it no longer says the charge is gross.
3. Update `docs/parsek-logistics-supply-routes-design.md` section 11 / the KSC cost tuning note: state that the per-cycle CHARGE now reconciles to the displayed net via the deferred recovery credit, and remove the "crediting recovery in the per-cycle charge remains deferred" deferral.

Do NOT change the run-cost calculator's net math or the display's read-side; the displayed net is already correct. This plan only makes the LEDGER match what the display already shows. The display still reads `SumRecoveredCredits` over ELS, which now also feeds the credit emit, so display and charge stay consistent through one source of truth.

---

## 9. Test matrix

xUnit, `[Collection("Sequential")]` for any test touching `Ledger` / `RecordingStore` / `ParsekScenario` shared static state, with `ResetForTesting()` in setup / dispose. Use the `ParsekLog.TestSinkForTesting` log-capture pattern (CLAUDE.md Testing Requirements) to assert the code path logged the expected credit data. Reuse the existing route-orchestrator test seams (`DeliveryApplierForTesting` [RouteOrchestrator.cs:847](../../../Source/Parsek/Logistics/RouteOrchestrator.cs), `LoopUnitResolverForTesting` [:429](../../../Source/Parsek/Logistics/RouteOrchestrator.cs)) and the FundsModule internal accessors (`GetRunningBalance` / `GetTotalEarnings` / `GetAvailableFunds`).

Pure / arithmetic (no live KSP):

- T-AMOUNT: `EmitRecoveryCredit` amount == `SumRecoveredCredits` over the source tree (reuse the display feature's deterministic ELS lists). Single recovery, multiple recoveries summed, zero-recovery -> no credit row emitted.
- T-TYPE: `RouteRecoveryCredited` round-trips through `SerializeInto` / `DeserializeFrom` with `RouteId` / `RouteCycleId` / `RouteKscFundsCost` preserved; an unknown future type id deserializes to a warn + skip (existing `GameAction.cs:678-682` behavior, guard it does not throw).
- T-FUNDSMODULE-CREDIT: `FundsModule.ProcessAction(RouteRecoveryCredited)` adds the amount to `runningBalance` and `totalEarnings`; `!Effective` skips it; `TryGetProjectionDelta` returns the positive delta.
- T-FUNDSMODULE-DEBIT (Option A): `FundsModule.ProcessAction(RouteCargoDebited)` subtracts `RouteKscFundsCost`; `ComputeTotalSpendings` counts it; `TryGetProjectionDelta` returns the negative delta. A full walk over (FundsInitial, RouteCargoDebited gross, RouteRecoveryCredited recovered) yields `GetAvailableFunds() == initial - gross + recovered`.

Cycle firing (orchestrator seams, no live Vessel):

- T-PAIR: one loop cycle emits `RouteDispatched(seq0)`, `RouteCargoDebited(seq1, gross)`, `RouteCargoDelivered`, and `RouteRecoveryCredited(recovered)` ALL under the same `cycle-N`.
- T-BLOCK (section 7): an ineligible cycle emits ZERO route rows (no debit, no delivered, no credit), increments `SkippedCycles`, and the next eligible cycle's credit carries the advanced cycleId.
- T-MODE-GATE: in non-Career (`env.IsCareer == false`) or non-KSC-origin (`route.IsKscOrigin == false`), `EmitRecoveryCredit` emits NO credit row and applies no live credit (mirror the `EmitDispatchDebit` isCareerKsc gate). One test per off-axis (Sandbox, Science, non-KSC Career).

Save / reload + idempotency:

- T-NODOUBLE: calling `EmitLoopCycle` twice for the same `(routeId, cycleId)` (simulating a save / reload double-tick) emits the credit row exactly ONCE; the second call hits `IsRecoveryCreditAlreadyInLedger` and emits nothing. Assert the verbose "replay" log fired.
- T-CRASH-WINDOW (section 5.3 option a): seed an ELS that already has the `RouteCargoDelivered` row but NOT the credit row, then run the crossing; assert the credit IS emitted from the replay-backstop path (no permanently-missing credit).

Rewind reversal (the safety-critical path):

- T-REWIND: build a ledger with FundsInitial + a dispatched cycle (gross debit) + its recovery credit at completion UT. Run `RecalculateAndPatchCore` with `utCutoff` BEFORE the credit UT; assert `GetAvailableFunds()` (and a fake `Funding` target via the patcher's computed delta) drops by exactly the credit amount (and, Option A, that the gross debit cutoff behaves symmetrically). This proves a Parsek timeline rewind un-credits the recovery.
- T-FUNDS-OUT-THEN-BACK (the timeline assertion): simulate two consecutive ticks across one dispatch interval. After the dispatch tick, live funds reflect ONLY the gross debit (out, not yet back). After the completion tick, live funds reflect (gross out + recovered in). Assert the intermediate state shows funds DOWN by gross (timing honesty), not the net. This is the test that proves the deferral is real and not collapsed to dispatch.

Re-fly / supersede:

- T-SUP (section 6.5 RESOLUTION 1): tombstone the source `FundsEarning(Recovery)` rows (via a synthetic `LedgerTombstone` set + `BumpTombstoneStateVersion`), then call `EmitRecoveryCredit`; assert the FUTURE credit amount computes to zero because `SumRecoveredCredits` reads ELS (tombstoned rows hidden). Assert already-emitted past credit rows are left intact (RESOLUTION 1) and that a cutoff walk to before the supersede UT still excludes them.
- T-SUP-NOBLOCK: assert `RouteRecoveryCredited` is excluded by `IsWorldStateChangingRecordingAction` (does not strict-block or retry-block a re-fly auto-seal).

In-game (only if a path genuinely needs live KSP). The funds mutation and the recalc reconcile are all xUnit-reachable through the seams above; no in-game test is required unless a reviewer identifies a live-`Funding` interaction the seams cannot model. If so, add an `[InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT)]` that dispatches one cycle and asserts the live funds timeline (out then back), per the Unity-runtime coverage rule.

---

## 10. Gotchas (read before coding)

- G1 (reversibility is the gate, not a nice-to-have). If the implementer cannot make Option A (section 6.3) prove reversible in T-REWIND and T-FUNDS-OUT-THEN-BACK, the feature ships DISABLED behind a settings flag (default off). A non-reversible funds mutation is forbidden (design doc section 10.5).
- G2 (do not collapse the timing). The credit fires at cycle COMPLETION, not at dispatch. Crediting at dispatch is the rejected design (section 1). Keep the funds gate and the gross debit untouched (section 4).
- G3 (gross debit is currently NOT in the recalc walk). Section 3.2 architecture fact: the existing gross charge is fire-and-forget and invisible to `FundsModule`. Option A FIXES this by routing the debit through `FundsModule`; do NOT leave the debit out of the walk while putting the credit in, or `PatchFunds` double-counts the credit upward (section 6.3 IMPORTANT CONSISTENCY REQUIREMENT).
- G4 (amount source is ELS, tree-scoped). Reuse `SumRecoveredCredits(route, ComputeELS(), ResolveTreeRecordingIds(route))`. Never `Route.RecordingIds` (post-undock recovery leg is excluded, display gotcha G1). Never recompute from part costs (display gotcha G3). Never reference `Ledger.Actions` / `.CommittedRecordings`.
- G5 (Career + KSC origin only). Gate `EmitRecoveryCredit`, `FundsModule` credit processing reuse the same `isCareerKsc` predicate the debit uses. No credit in Sandbox / Science / non-KSC.
- G6 (idempotency keyed on the credit's own row). `IsRecoveryCreditAlreadyInLedger` scans for `RouteRecoveryCredited`, NOT `RouteCargoDelivered`. The delivery backstop and the credit backstop are separate keys; a present delivery row must not be read as "credit already emitted" (section 5.3).
- G7 (positive magnitude, type carries the sign). Store the recovered amount as a positive `RouteKscFundsCost`; the `RouteRecoveryCredited` type means "credit". Mirrors the debit / delivered convention. Do not store a negative.
- G8 (no funds glyph, InvariantCulture). All logged / displayed amounts: plain number + the word "funds", `ToString("R", InvariantCulture)` for ledger values.

---

## 11. Out of scope (explicit)

- Changing the dispatch debit or the `KscFundsAvailable` gate (both stay on gross, section 4).
- Crediting recovery at dispatch (rejected, section 1).
- Recorded-recovery-UT-mapped credit timing with cycle overlap (deferred, OQ1).
- Retroactive tombstoning of already-emitted credit rows on re-fly (RESOLUTION 1 ships; RESOLUTION 2 is OQ2).
- Non-KSC origin funds modeling (those deduct cargo, not funds).
- Science / reputation incidentally earned on a supply run (not funds, not folded in).
- Any new UI surface beyond the reconciliation tooltip flip (section 8).

---

## 12. Open questions

1. OQ1 (precise timing): should the credit ever fire at the recorded recovery UT mapped into the loop instead of at cycle completion? Only if a playtest shows cycle-completion timing reads wrong. Requires a second clock + overlap handling (recovery UT beyond one dispatch interval). Defaulting to cycle-completion (section 5.1) unless a concrete case demands it.
2. OQ2 (re-fly retroactive reversal): RESOLUTION 1 (section 6.5) leaves completed-cycle credit rows intact on a non-rewinding supersede. Is full retroactive reversal (RESOLUTION 2) ever wanted? Revisit when re-fly + active-route interaction gets a dedicated audit. Build the failing case first.
3. OQ3 (Option A blast radius): routing the gross debit through `FundsModule` (Option A) also closes the existing gross-charge rewind hole (section 3.2). Confirm with a reviewer that retroactively making the gross charge rewind-reversible is desired here and does not surprise existing saves (a save where a route charged gross before this change, then rewound, would now see the gross charge reversed by the recalc where before only quicksave/load reversed it). If undesired, scope Option A to NEW cycles only via a per-row flag. Default: apply to all, since reversibility is strictly more correct.
