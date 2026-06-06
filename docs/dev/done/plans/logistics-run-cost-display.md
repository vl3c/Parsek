# Logistics Supply-Route Run-Cost Display Plan

Status: REVIEWED, ready for implementation (two review+fix passes against the code; all symbol/line claims verified)
Branch base: `logistics-v0-implementation` (this plan authored on `docs-logistics-run-cost`, base commit `56c83cca`)
Scope: surface the per-run funds cost of a Supply Route in the Logistics UI. New pure calculator in the Logistics layer, a value added to the existing throttled legibility cache, and three read-only UI surfaces (route detail panel, route-creation summary, candidate row). Career-only display. No change to what the ledger actually charges per cycle (see Decision D1).

This document is the implementation brief. A fresh agent should be able to execute it without the originating conversation. All `file:line` references are as of base commit `56c83cca`; treat each as a "start here" anchor and grep the named symbol to confirm before editing.

---

## 1. Goal

Show the player how much one run of a Supply Route costs in funds (KSP credits). The cost the player cares about is the NET cost of repeating the recorded flight once:

```
net run cost = vehicle launch cost - recovered credits
```

- **Vehicle launch cost**: the stock funds cost of building and launching the transport, as recorded (dry parts plus the resources loaded at the recorded snapshot).
- **Recovered credits**: the funds KSP actually paid back when the player recovered the transport, and/or jettisoned-and-recovered parts of it, during the recorded flight.

The net is what the route costs you each time it runs. Today the Logistics window shows no cost at all, and the route-creation summary prints a literal `Dispatch cost: TBD` placeholder ([RouteCreationFormatters.cs:197](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs)). This plan fills that gap.

Display is **Career-only**. Sandbox and Science modes have no funds, so no cost is shown in those modes (no "n/a" placeholder, the line is simply absent). See Decision D2 for the exact predicate.

---

## 2. How to work (process for the implementer)

- Work in a dedicated sibling worktree off `logistics-v0-implementation`. Do not edit the main `Parsek/` checkout, and do not edit another task's worktree.
- Land work as GitHub PRs against `logistics-v0-implementation` (a feature branch, not `main`). One PR for the calculator plus tests, one for the UI wiring, is a good granularity.
- Before each commit that changes behavior, build, deploy, and verify the deployed DLL per `.claude/CLAUDE.md` (byte match plus a UTF-16 string grep for a new label you added). Run `dotnet test`.
- Every new method with logic needs unit tests and verbose logging. Pure logic is `internal static` so it is directly testable. Follow the established split: pure cost/formatting in the Logistics layer and `*Presentation`/`*Formatters`; IMGUI drawing in `LogisticsWindowUI.cs`. Precedent: `RouteFundsCalculatorTests.cs`, `LogisticsDeliveryPresentationTests.cs`.
- Update `CHANGELOG.md` (1 line per item, user-facing, under `## 0.10.0`) and `docs/dev/todo-and-known-bugs.md` in the same commit that changes behavior.

### Hard constraints (do not violate)
- No em dashes anywhere (chat, code comments, CHANGELOG, commits, PRs). Use a colon, parentheses, comma, or split the sentence.
- Plain ASCII only in markdown and UI strings. No emoji, no special Unicode. Do NOT use the KSP funds glyph; write a plain number, and the word "funds" where a unit is wanted.
- No `Co-Authored-By` and no AI-attribution lines in commits or PRs.
- InvariantCulture for all numeric formatting in UI strings (`ToString(..., CultureInfo.InvariantCulture)`).
- ERS/ELS grep gate must stay green (`scripts/grep-audit-ers-els.ps1`, enforced by `GrepAuditTests`). The gate matches ONLY the literals `\.CommittedRecordings\b` and `\bLedger\.Actions\b`. Reading the list returned by `EffectiveState.ComputeELS()` / `ComputeERS()` does NOT trip the gate. The recovery sum MUST read `ComputeELS()`, never `Ledger.Actions`, and the launch-cost path MUST read `ComputeERS()` (it already does). With that discipline no allowlist change is needed and `LogisticsWindowUI.cs` stays off the allowlist.

---

## 3. Ground truth: what already exists

Read `docs/parsek-logistics-supply-routes-design.md` section 6 and the `Source/Parsek/Logistics/` files first. The two halves of the cost are both already computable.

### 3.1 Launch-cost half (built)
- [RouteFundsCalculator.ComputeDispatchFundsCost](../../../Source/Parsek/Logistics/RouteFundsCalculator.cs) walks every `PART` of a recorded vessel snapshot summing `partCost + sum(RESOURCE.amount * resourceUnitCost)`, with both lookups injected (so it is unit-testable with a deterministic price dictionary).
- [RouteOrchestrator.ComputeDispatchFundsCostForRoute](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:1532) resolves the route's source `Recording` in ERS (via the try/catch wrapper `SafeComputeErs`, [RouteOrchestrator.cs:1578](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:1578), over `EffectiveState.ComputeERS()`, [EffectiveState.cs:1213](../../../Source/Parsek/EffectiveState.cs:1213), so the launch-cost path is already exception-safe) and feeds `source.VesselSnapshot` into the calculator with the live `PartLoader`/`PartResourceLibrary`-backed lookups [LiveRouteRuntimeEnvironment.LookupPartCost / LookupResourceUnitCost](../../../Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs:200). The snapshot is the full recorded vessel, so this is effectively the gross launch cost (dry parts plus loaded resources). Note it returns `0.0` when the snapshot is null or not yet hydrated ([RouteOrchestrator.cs:1557](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:1557)); see Gotcha G7.
- The result is written to [Route.KscDispatchFundsCost](../../../Source/Parsek/Logistics/Route.cs:77) and charged per dispatch in Career + KSC-origin routes via [RouteOrchestrator.EmitDispatchDebit](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:911) (the `isCareerKsc` branch at [:918](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:918)), and gated by [LiveRouteRuntimeEnvironment.KscFundsAvailable](../../../Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs:118).

### 3.2 Recovery-credits half (built, in the ledger)
Vessel-recovery payouts are already captured as ledger actions:
- A recovery payout becomes a `GameAction` with `Type = GameActionType.FundsEarning`, `FundsSource = FundsEarningSource.Recovery`, `FundsAwarded = (float)delta` (the actual funds KSP paid), and `RecordingId` set to the recording the recovery happened in. See [LedgerRecoveryFundsPairing.TryAddVesselRecoveryFundsAction](../../../Source/Parsek/GameActions/LedgerRecoveryFundsPairing.cs:615) and the entry points [LedgerOrchestrator.OnVesselRecoveryFunds](../../../Source/Parsek/GameActions/LedgerOrchestrator.cs:3088) (post-flight / tracking-station recovery) and `CreateVesselCostActions` (commit-time, when a recording's `TerminalState == Recovered`).
- The amount is the real distance-scaled payout; the recovery factor is captured alongside in [RecoveryPayoutContext.RecoveryFactor](../../../Source/Parsek/RecoveryPayoutContext.cs:79) but the ledger stores the already-scaled funds, which is what we want.
- The effective (re-fly / supersede aware) ledger is read via `EffectiveState.ComputeELS()` ([EffectiveState.cs:1298](../../../Source/Parsek/EffectiveState.cs), returns `IReadOnlyList<GameAction>`). The Logistics window already scans it on the ~1 Hz cache: [LogisticsWindowUI.cs:2230](../../../Source/Parsek/UI/LogisticsWindowUI.cs:2230) (delivery scan).

### 3.3 The UI surfaces (where cost must appear, currently empty of cost)
- Route detail panel [LogisticsWindowUI.DrawRouteDetail](../../../Source/Parsek/UI/LogisticsWindowUI.cs:1127): shows Delivers / Status / Interval / Transit / Cycles / source recordings. No cost line.
- The throttled per-route legibility cache [RouteLegibility struct](../../../Source/Parsek/UI/LogisticsWindowUI.cs:108), populated once per ~1 Hz in `RefreshLegibilityCacheIfDue`. This is where the cost values get cached (Phase 2).
- Route-creation summary [RouteCreationFormatters.BuildSummaryBlock](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs:112), which prints `Dispatch cost: TBD` at [:197](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs:197).
- Candidate row [LogisticsWindowUI.DrawCandidateRow](../../../Source/Parsek/UI/LogisticsWindowUI.cs:1077) and its "Would deliver" cell.

---

## 4. The cost model (ground truth the calculator must honor)

One Supply-Route cycle, viewed as a funds ledger (Career, KSC origin):

| Flow | Direction | Captured by |
| --- | --- | --- |
| Build + launch the transport (dry parts + loaded resources) | out | launch cost (3.1) |
| Recover the transport at the end of the run | in | recovery credits (3.2) |
| Recover jettisoned parts/stages mid-run | in | recovery credits (3.2) |
| Cargo delivered to the destination (left behind) | out | IMPLICIT in (launch - recovery) |
| Transit fuel burned | out | IMPLICIT in (launch - recovery) |

**Net = launch - recovered.** Delivered cargo and burned fuel are NOT separate line items: you paid for full tanks and full cargo at launch, you recover the post-delivery, post-burn vessel, so the delta already accounts for both. Adding a separate "delivered cargo" subtraction would double-count it (see Gotcha G4). This "implicit" accounting assumes the recorded snapshot is the fully-loaded launch state. For v0 that is not something the calculator itself guarantees, and it does not need to: the launch figure shown is exactly the value the existing per-dispatch charge already uses ([Route.KscDispatchFundsCost](../../../Source/Parsek/Logistics/Route.cs:77)), so display and charge stay consistent regardless of when the snapshot was captured.

### Things the model must get right (the non-obvious parts)

1. **Recovery is distance-scaled and already baked in.** Use the actual `FundsAwarded` from the ledger. Do not recompute a theoretical recovery from part costs. A transport recovered far from KSC returns far less than its build cost; one flown/driven home returns nearly full value. Net cost naturally rewards bringing the transport home.

2. **"Parts of it" means multiple recovery events.** A run may drop and recover boosters and then recover the transport. Each is a separate `FundsEarning(Recovery)` row. Sum them all.

3. **Recovery happens AFTER undock, OUTSIDE the route's rendered loop window.** This is the most important implementation fact. The route renders `[launch .. undock]` and its `RecordingIds` / `SourceRefs` cover only the `[root..undock]` member set; the "fly home and recover" leg is excluded from the render span ([Route.BackingMissionTreeId at Route.cs:183](../../../Source/Parsek/Logistics/Route.cs:183), [ExcludedIntervalKeys at Route.cs:195](../../../Source/Parsek/Logistics/Route.cs:195)). The recovery is in the same source TREE but typically not in any `Route.RecordingIds` entry. Therefore the recovery sum MUST be scoped to the whole source tree (`Route.BackingMissionTreeId`), resolved through `RecordingTree.Recordings` (the keys are recording ids, [RecordingTree.cs:22](../../../Source/Parsek/RecordingTree.cs:22)), NOT to `Route.RecordingIds`. Scoping to `RecordingIds` would return zero recovery on a transport that flies home after undocking.

4. **Career + KSC-origin only.** Funds exist only in `Game.Modes.CAREER`. A non-KSC origin deducts physical cargo, not funds, even in Career. So the credits display predicate is `IsCareer && route.IsKscOrigin`, matching the same gate the dispatch debit already uses (`isCareerKsc`, [RouteOrchestrator.cs:918](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:918)).

5. **No-recovery flights are normal.** Many recorded transports are never recovered (left parked, or expendable). Then recovered = 0 and net = full launch cost (the vehicle is thrown away each cycle). The UI must render this cleanly, and should hint that recovering the transport lowers the route cost.

6. **Cost can drift on re-fly / supersede.** Reading recovery through `ComputeELS()` and the snapshot through `ComputeERS()` keeps the cost consistent with tombstone / supersede state. Recompute on the cache; never persist a stale net.

---

## 5. Decisions

### D1. What the ledger charges per cycle (economy) vs. what we display
DECIDED (author, 2026-06-05): **display-only.** Show net (launch - recovered) as the headline, but do NOT change what the orchestrator charges or what the funds gate checks. Rationale:
- Realistically you need the FULL build cost up front; recovery comes back at the end of the cycle. So the existing gross charge at dispatch and the gross `KscFundsAvailable` gate are defensible as-is.
- Crediting recovery per cycle in the ledger touches `GameActionType` epoch-isolation / rollback contracts (design section 6.6 and the route ledger modules) and is a much bigger, riskier change.
- This plan therefore leaves [Route.KscDispatchFundsCost](../../../Source/Parsek/Logistics/Route.cs:77) and `EmitDispatchDebit` untouched and only ADDS display.

Consequence to call out in the UI tooltip: the per-cycle charge now matches the displayed net. The gross launch cost is fronted at dispatch and the recovered amount is credited back one cycle later (logistics-recovery-credit), so in steady state the per-cycle net equals the displayed net. If the player does not recover the transport in the recorded flight, gross == net.

(Alternative, larger: actually credit recovery per cycle so the economy matches the headline. DONE: implemented by `docs/dev/done/plans/logistics-recovery-credit.md`. The orchestrator now emits a deferred per-cycle `RouteRecoveryCredited` ledger row at the next dock crossing, keyed on the prior dispatched cycle, and FundsModule reverses both the gross debit and the credit through the recalc + tombstone path. The dispatch debit and the `KscFundsAvailable` gate stay on gross, so you still front the full build cost to launch.)

### D2. Display predicate
Show the cost line/block only when `IsCareer && route.IsKscOrigin`. Reuse the career probe shape from [LiveRouteRuntimeEnvironment.IsCareer](../../../Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs:50) (`HighLogic.CurrentGame.Mode == Game.Modes.CAREER`, defensively wrapped). For the route-creation summary the route object does not exist yet, so gate on `IsCareer && analysis-implies-KSC-origin`. Caution: the existing `Dispatch cost: TBD` line is gated on `mode == Game.Modes.CAREER` ONLY ([RouteCreationFormatters.cs:193](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs:193)), with NO KSC-origin check, so a Career non-KSC route prints TBD today. The new block must ADD the KSC-origin gate (see Phase 3.2); do not assume the Career non-KSC path is already cost-free.

### D3. UI footprint
DECIDED (author, 2026-06-05): a **detail-panel line plus tooltip**, the **creation-summary block**, and a **candidate-row tooltip/suffix**. Do NOT add a sortable "Cost/run" column in this cut: the route table is already 1410px wide ([MinWindowWidth](../../../Source/Parsek/UI/LogisticsWindowUI.cs:244)) and a column costs horizontal space and a sort key. Add the column later only if at-a-glance comparison across routes is wanted.

---

## 6. Implementation phases

### Phase 1: pure calculator `RouteRunCostCalculator`
New file `Source/Parsek/Logistics/RouteRunCostCalculator.cs`, `internal static`, fully unit-testable with injected dependencies (mirror `RouteFundsCalculator`'s injection shape).

Return struct:
```
internal struct RouteRunCost
{
    public bool Applicable;        // Career + KSC origin
    public bool CostKnown;         // Applicable AND LaunchCost resolved (> 0); false when the source snapshot is missing / not yet hydrated
    public double LaunchCost;      // gross, from the recorded snapshot
    public double RecoveredCredits;// sum of recovery payouts over the source tree
    public double NetCost;         // max(0, LaunchCost - RecoveredCredits)
    public int RecoveryEventCount; // how many recovery rows summed (for the tooltip/log)
}
```

Methods:
- `ComputeLaunchCost(route)`: delegate to the existing `RouteOrchestrator.ComputeDispatchFundsCostForRoute(route)` (already correct, ERS-backed). Do not duplicate the snapshot walk.
- `SumRecoveredCredits(route, els, treeRecordingIds)`: sum `FundsAwarded` over `els` where `Type == FundsEarning && FundsSource == FundsEarningSource.Recovery && treeRecordingIds.Contains(RecordingId)`. `treeRecordingIds` is built from the route's source tree (`Route.BackingMissionTreeId` -> `RecordingTree.Recordings.Keys`), NOT from `Route.RecordingIds` (Gotcha G1). Inject `els` so tests pass a deterministic list.
- `Compute(route, isCareer, els, treeRecordingIds)`: assemble the struct; `Applicable = isCareer && route.IsKscOrigin`; `CostKnown = Applicable && LaunchCost > 0` (a null / not-yet-hydrated `VesselSnapshot` makes the launch cost return `0` at [RouteOrchestrator.cs:1557](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:1557); treat that as cost-unknown, do NOT render a misleading "0 funds" line, see G7); `NetCost = max(0, Launch - Recovered)`.
- Verbose log one line: `RunCost route={shortId} applicable={b} known={b} launch={L} recovered={R} net={N} recoveries={n} treeId={id}`.

Keep tree-membership resolution (treeId -> recording-id set) in a small helper. There is no single route-to-tree-members one-call helper, so a small new one is justified, but reuse the existing tree-by-id lookup `RouteTreeGuard.FindCommittedTree(treeId)` ([RouteTreeGuard.cs:289](../../../Source/Parsek/Logistics/RouteTreeGuard.cs:289); it reads `RecordingStore.CommittedTrees`, which is NOT a grep-gated surface) and enumerate `tree.Recordings.Keys` (precedents: [RouteBackingMission.cs:294](../../../Source/Parsek/Logistics/RouteBackingMission.cs:294), [RouteAnalysisEngine.cs:479](../../../Source/Parsek/Logistics/RouteAnalysisEngine.cs:479)). Handle a null/empty `BackingMissionTreeId` or a tree that does not resolve as recovered = 0 (degenerate route).

### Phase 2: cache it in the legibility pass
- Add `RouteRunCost RunCost;` to the [RouteLegibility struct](../../../Source/Parsek/UI/LogisticsWindowUI.cs:108).
- Populate it in `RefreshLegibilityCacheIfDue` ([:2027](../../../Source/Parsek/UI/LogisticsWindowUI.cs:2027)). Call `EffectiveState.ComputeELS()` for the recovery sum. It is memoized (`elsCache`, [EffectiveState.cs:1308](../../../Source/Parsek/EffectiveState.cs:1308)), so re-calling it during the same refresh is cheap and adds NO second ledger walk; the existing delivery scan already calls it per route inside `CollectRouteDeliverySummary` ([:2221](../../../Source/Parsek/UI/LogisticsWindowUI.cs:2221)) for exactly that reason. Do NOT assume an els list is already in scope at the legibility-loop level: it is not, the `:2230` fetch is local to that per-route delivery helper. Resolve the route's tree-member id set once per route per refresh.
- This keeps the O(actions) recovery scan on the ~1 Hz timer, never on the IMGUI draw path, exactly like the existing H2/H3 cumulative-delivery scan.

### Phase 3: UI surfaces (read-only)
1. **Detail panel** ([DrawRouteDetail](../../../Source/Parsek/UI/LogisticsWindowUI.cs:1127)): when `leg.RunCost.Applicable && leg.RunCost.CostKnown`, add one `DetailLine` after the Interval/Transit/Cycles line ([:1180](../../../Source/Parsek/UI/LogisticsWindowUI.cs:1180)):
   - With recovery: `Cost/run: 5,200 funds  (launch 12,500 - recovered 7,300)`
   - No recovery: `Cost/run: 12,500 funds  (transport not recovered in the recording)` plus the recover-to-reduce hint in the tooltip.
   - Tooltip: explain net = launch - recovered, that recovered is the actual distance-scaled payout, and the timing (gross fronted at dispatch, recovered credited back one cycle later, per logistics-recovery-credit). Pure formatting goes in `LogisticsDeliveryPresentation` (or a new `LogisticsCostPresentation`) for unit testing; the `*UI` file only draws.
   - When `!Applicable` (not Career, or non-KSC origin) OR `!CostKnown` (launch cost unavailable), draw nothing (no "n/a" line, no "0 funds" line).
2. **Creation summary** ([BuildSummaryBlock](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs:112)): replace the `Dispatch cost: TBD` line with the real block when Career + KSC origin:
   ```
   Cost per run: 5,200 funds
     Launch: 12,500 funds
     Recovered: 7,300 funds
   ```
   The existing TBD line is gated on `mode == Game.Modes.CAREER` only ([RouteCreationFormatters.cs:193](../../../Source/Parsek/Logistics/RouteCreationFormatters.cs:193)), with no KSC-origin check, so a Career non-KSC route prints TBD today. Replace it so the block shows ONLY for Career + KSC origin and nothing otherwise: this ADDS a KSC-origin gate that does not exist today (do not assume the Career non-KSC path is already silent). The summary runs before a `Route` exists, so compute from the candidate's source recording + tree directly (same inputs, no `Route` object).
3. **Candidate row** ([DrawCandidateRow](../../../Source/Parsek/UI/LogisticsWindowUI.cs:1077)): add a compact net-cost suffix or tooltip to the "Would deliver" cell so cost is visible before the player promotes the candidate. Career + KSC origin only.

### Phase 4: tests + docs
- xUnit `RouteRunCostCalculatorTests`:
  - launch-only, no recovery -> net == launch.
  - single recovery -> net == launch - payout.
  - multiple/partial recoveries -> summed.
  - **recovery row whose RecordingId is a tree member but NOT in `Route.RecordingIds` -> still counted** (the Gotcha G1 regression guard; this is the test that proves the tree-scoping).
  - recovery row from a DIFFERENT tree -> excluded.
  - non-Career -> `Applicable == false`, and the UI predicate suppresses display.
  - launch cost unavailable (null / not-yet-hydrated `VesselSnapshot`, launch == 0) -> `CostKnown == false`, UI suppresses (no "0 funds" line). (G7 regression guard.)
  - net floors at 0 when recovered > launch (e.g. a value bug or odd refund).
  - superseded/tombstoned recovery excluded because the input is `ComputeELS()` (feed an els list that already excludes it; document that the live ELS does the exclusion).
- Presentation test for the formatted line/block (InvariantCulture, no em dashes, ASCII only, both with-recovery and no-recovery shapes).
- CHANGELOG `## 0.10.0`: one user-facing line, e.g. "Logistics: route detail and creation summary now show the per-run funds cost (launch minus recovered credits) in Career."
- `docs/dev/todo-and-known-bugs.md`: mark the run-cost display item done. The D1 deferral (per-cycle charge stays gross) is now CLOSED: the deferred recovery credit reconciles the per-cycle charge to the displayed net (logistics-recovery-credit).
- `docs/parsek-logistics-supply-routes-design.md` section 11 "KSC cost tuning": record that recovery-aware NET cost is now displayed, and that the per-cycle CHARGE now reconciles to the displayed net via the deferred recovery credit (no longer deferred).

---

## 7. Gotchas (read before coding)

- **G1 (tree scope, not member scope).** Recovery is summed over `Route.BackingMissionTreeId` -> `RecordingTree.Recordings.Keys`, NOT over `Route.RecordingIds`. The recovery leg is post-undock and excluded from the route member set. Scoping to `RecordingIds` silently returns zero. This is the single most likely way to ship a wrong number that looks plausible. Conversely, tree-scoping can UNDER-count in one edge: a recovery whose `RecordingId` fell to the global-latest-by-EndUT fallback tier in `PickRecoveryRecordingId` ([LedgerOrchestrator.cs:3196](../../../Source/Parsek/GameActions/LedgerOrchestrator.cs:3196)) can be tagged to a same-named craft's recording in a DIFFERENT tree, which the tree-scoped sum excludes. That overstates cost (shows full launch as not-recovered), an acceptable conservative failure: the displayed net is an upper bound, never a free-looking under-statement.
- **G2 (use ELS, not Ledger.Actions).** The recovery sum reads `EffectiveState.ComputeELS()`. Referencing `Ledger.Actions` trips the grep gate AND ignores supersede/tombstone state. The launch cost stays on `ComputeERS()` (already there).
- **G3 (do not recompute recovery from parts).** Use the ledger's `FundsAwarded` (actual distance-scaled payout). A part-cost recompute would ignore the recovery factor and overstate the credit.
- **G4 (no double-count).** Net is exactly `launch - recovered`. Do not also subtract a delivered-cargo manifest value; delivered cargo is already inside that delta.
- **G5 (Career + KSC origin only).** Predicate is `IsCareer && route.IsKscOrigin`. A non-KSC Career route has no funds charge; its cost is the physical cargo manifest (already shown as the delivery manifest), so no credits line there either.
- **G6 (no special chars).** No KSP funds glyph, no em dash. Plain ASCII, InvariantCulture, `F0`/thousands formatting consistent with the rest of the window.
- **G7 (launch cost may be unavailable -> do not render 0).** `ComputeDispatchFundsCostForRoute` returns `0` when the source recording is not in ERS or its `VesselSnapshot` is null/not-yet-hydrated ([RouteOrchestrator.cs:1557](../../../Source/Parsek/Logistics/RouteOrchestrator.cs:1557); `VesselSnapshot` is transient, hydrated from the `.craft` sidecar, and an early-load tick can legitimately see it null). With `NetCost = max(0, 0 - recovered) = 0` that would render "Cost/run: 0 funds" as if the run were free. The `CostKnown = Applicable && LaunchCost > 0` flag exists to suppress the line in that case; the UI must check it.

---

## 8. Out of scope (explicit)

- Changing the per-cycle ledger charge or the `KscFundsAvailable` gate (Decision D1; this is display-only).
- Science and reputation: a supply run may incidentally earn science/rep, but that is not a funds cost and is not folded into the run cost.
- A sortable Cost/run column (Decision D3; detail line + tooltip first).
- Multi-stop routes (v0 is single-stop).
- Non-KSC origin funds modeling (those deduct cargo, not credits).

---

## 9. Decisions and remaining questions

Resolved by the author (2026-06-05):
1. D1: **display-only net.** Do not credit recovery per cycle in the ledger; the per-cycle charge stays gross, only the displayed cost is net.
2. D3: **detail line + tooltip only.** No sortable Cost/run column in this cut.

Remaining minor question for the implementer:
3. No-recovery wording: is "transport not recovered in the recording" the right phrasing, plus a one-line hint that recovering the transport at the end of the recorded flight lowers the route cost? Default to that phrasing unless a playtest suggests better.
