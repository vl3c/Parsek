# Plan: fix logistics ↔ time-rewind determinism (route ledger rows must revert with the world)

Status: BUILT + HEADLESS-GREEN (Rec-1), in-game repro WIRED, PENDING the live playtest
(clean-context reviewed: GO-WITH-FIXES; the review's must-fix Capture->Restore consistency +
the #2/#3/#4/#5/#7 clarity fixes are folded in). Authored on branch
`claude/logistics-rewind-fix-r8ud6s`, finished (build + `dotnet test` green + in-game test
wired) on branch `logistics-rewind-rec1` off `origin/main` @ `6ae12b076` (current version
`0.10.3`). The touched files — `ReconciliationBundle.cs`, `RewindInvoker.cs`, `Logistics/*`
(`RouteLedgerRetire.cs`, `RouteRevertSafety.cs`) — are disjoint from the map-render refactor
stack, so no map-render dependency. Rec-1 is the shipped fix. Rec-3 (non-rewind discard
leak): the observability slice is WIRED + VERIFIED on a real toolchain (build clean +
`dotnet test` 16799 passed / 0 failed + grep-audit exit 0, 2026-07-06), and the maintainer
RATIFIED both-persist as correct (option C, 2026-07-06): reverse-on-discard is DECLINED,
not deferred. See Phase 4.

Companion report (read first): `docs/dev/research/logistics-time-rewind-compat-report.md`
(per-axis verdicts, 13-row risk register, worked example, Rec-1..Rec-5). This plan
turns Rec-1/Rec-3/Rec-4/Rec-5 into build steps and surfaces (does NOT decide) Rec-2.

---------------------------------------------------------------------------------

## 1. The bug, in one screen (verified against HEAD)

A recurrent supply route fires **live** on a UT-pinned loop grid (`RouteLoopClock` +
`RouteOrchestrator.Tick`, ~1 Hz, all scenes), not by replaying a recorded dispatch
stream. Each dispatch writes **free-standing** ledger rows (`RouteDispatched`,
`RouteCargoDebited`, `RouteCargoDelivered`, `RouteRecoveryCredited`,
`RouteCargoPickedUp`, plus route-level `RoutePaused` / `RouteEndpointLost`) carrying
`RouteId` + a counter-keyed `RouteCycleId` and **never a `RecordingId`** (confirmed:
zero `RecordingId =` writes for route actions in `RouteOrchestrator.cs`;
`GameAction.RecordingId` defaults null at `GameAction.cs:220`). The physical cargo
move is a **live side-effect** (origin-debit / delivery / pickup writers mutate live
or unloaded vessels) with **no reverse method** — its only rollback is the rewind
quicksave restore.

A rewind reverts state by two mechanisms and route data straddles them:

| State | Reverted by | Result after rewind |
|---|---|---|
| Live world (tanks, inventory, funds scalar) | RP `.sfs` reload (`RewindInvoker.cs:608` `GamePersistence.LoadGame` → `FlightDriver.StartAndFocusVessel`) | reverted to RP UT |
| Route definitions + counters (`CompletedCycles`, `SkippedCycles`, `LastObservedLoopCycleIndex`, `LoopAnchorUT`, `PendingRecoveryCreditCycleId`) | RP `.sfs` `ROUTES` node (`RouteStore.LoadRoutesFrom` clears + rebuilds; `RouteStore` is **NOT** a `ReconciliationBundle` field, `ReconciliationBundle.cs:19-72`) | reverted to RP UT |
| **`Ledger.Actions`** (incl. route rows) | the **`ReconciliationBundle`** (`Capture` snapshots the pre-rewind ledger `:114-120`, `Restore` does `Ledger.Clear()` + `AddActions` `:194-196`) | **preserved** — abandoned-future route rows survive |

So after a rewind the route *thinks* it has done nothing since RP UT (counters
reverted), the *world* has nothing since RP UT (tanks reverted), but the *ledger*
still holds the abandoned-future route rows. On re-fly the counter-keyed
`cycleId = "cycle-" + (CompletedCycles + SkippedCycles)` reproduces byte-identically
(`RouteOrchestrator.cs:771, 999`) and collides in the **UT-blind** dispatch dedup
(`IsDispatchAlreadyInLedger` matches `RouteDispatched` on `(RouteId, RouteCycleId,
RouteStopIndex)` only, **no UT compare**, via `EffectiveState.ComputeELS()`,
`RouteOrchestrator.cs:3518-3553`). `EmitLoopCycle` then returns at `:1818` (after
`CompletedCycles += 1` at `:1813`) **before** any live writer runs (`EmitDispatchDebit`
at `:1826`), so the physical cargo is never re-applied, while
`FundsModule.ProcessRouteCargoDebited` charges the surviving row **unconditionally**
(no `Effective` gate, `FundsModule.cs:538-551`) on the commit recalc
(`RewindInvoker.cs:929`, `double.MaxValue`).

Result: **funds spent, no goods** (KSC-origin), or **cargo still at origin while
`CompletedCycles` advanced** (non-KSC origin, the debit carries 0 funds cost,
`RouteOrchestrator.cs:2901`). Funds are never double-charged, but the surviving
charge can be a **phantom** for a cycle the surviving timeline never reproduced.
`ProcessRouteRecoveryCredited` has an `Effective` gate (`FundsModule.cs:566-588`)
that is **inert dead code** for routes — no first-tier module flips Effective=false
for `RouteRecoveryCredited` — so its abandoned-future row is also a phantom credit.

---------------------------------------------------------------------------------

## 2. Chosen mechanism (Rec-1) + rationale + rejected alternatives

### 2.1 Decision: route-row-scoped **DROP** at rewind, keyed by `RouteId`-bearing action + `UT > cutoff`

At rewind, **remove** from the captured/restored ledger every free-standing route
`GameAction` whose `UT > the loaded RP cutoff UT`. The surviving ledger then matches
the reverted world, and the live re-fly re-emits each cycle deterministically as UT
re-advances — re-charging funds (once) AND re-delivering physical cargo (once), with
the dedup correctly seeing an empty slate for those cycles.

The retire is expressed as a **pure predicate** + a small ledger helper so it is
directly unit-testable and reused by both the rewind path and (Rec-3) the discard
path:

```
// EffectiveState.cs (or a new RouteLedgerRetire.cs static) — PURE, Unity-free
internal static bool IsFreeStandingRouteAction(GameAction a)
    => a != null
       && !string.IsNullOrEmpty(a.RouteId)
       && IsRouteActionType(a.Type);   // Type in {RouteDispatched(23), RouteCargoDebited(24),
                                        //          RouteCargoDelivered(25), RoutePaused(26),
                                        //          RouteEndpointLost(27), RouteRecoveryCredited(28),
                                        //          RouteCargoPickedUp(29)}

internal static bool ShouldRetireRouteActionAtRewind(GameAction a, double cutoffUT)
    => IsFreeStandingRouteAction(a) && a.UT > cutoffUT;   // strict > : the at-cutoff row is at/before RP, kept
```

The double gate `RouteId-present AND route Type` is deliberate (belt + suspenders):
the Type set is the authority; the `RouteId` non-empty check guards against any
future non-route action that ever reuses a route Type value, and documents intent.
Use **strict `>`** (not `>=`): a row stamped exactly at the RP UT is part of the
at-RP state the quicksave keeps, mirroring the world-revert boundary. (The RP UT is
a separation instant; route dispatches land on the ~1 Hz grid, so an exact tie is
vanishingly unlikely, but the boundary must be defined and must match the world.)

### 2.2 Why DROP, not tombstone, not a global ELS UT filter

- **(a) DROP the rows — CHOSEN.** Route rows are *live-game artifacts with no
  supersede identity*; treating them like the vessels they mutate (revert with the
  world) is the minimal, intention-revealing change. After the drop, the live re-fly
  re-creates them, so the ledger is reconstructed forward exactly as an un-rewound
  run would have produced it. Bounds multi-rewind accumulation (each Capture
  re-snapshots a ledger that no longer carries the prior rewind's orphans). Mirrors
  the EXISTING `Ledger.PruneOrphanActionsAfterUT(cutoffUT, inclusive)` pattern
  (`Ledger.cs:219`) that already drops *untagged* orphan rows after a revert cutoff —
  this is the same shape, scoped to route Types instead of "untagged". **Parity argument
  (review #3, the strongest safety point):** that prune ALREADY runs on the STOCK-revert
  path (`ParsekScenario.cs:2984`) and ALREADY drops route rows there (route rows carry null
  `RecordingId`, are non-seed, so they match its predicate). The Rewind-to-Separation path
  is the ONLY revert that PRESERVES route rows — precisely because it routes through
  `ReconciliationBundle` (which deliberately snapshots the FULL ledger) instead of
  `PruneOrphanActionsAfterUT`. So Rec-1 does not invent a new behavior; it restores PARITY:
  route rows revert on a rewind exactly as they already do on a stock revert.

- **(b) Tombstone the rows — REJECTED.** `LedgerTombstone` is keyed on `ActionId`
  (`EffectiveState.ComputeELS` filters by `t.ActionId`, `:1340-1345`) and a tombstone
  keyed on a route row's `ActionId` *would* hide it from ELS, so it is *technically
  possible*. But it is strictly worse here: (1) it invents a parallel "route
  supersede" concept the codebase deliberately avoids (`SupersedeCommitTests.cs:160-167`
  classifies every route Type `false,false` for retry/strict blocking precisely
  because "route rows are scheduler-emitted under a RouteId, not flight-recorder
  output"); (2) the tombstone rows then accumulate across stacked rewinds (each
  rewind would re-tombstone the same re-emitted cycleIds) and must themselves be
  reaped; (3) it bumps `TombstoneStateVersion` and grows the tombstone list for
  rows that have no surviving timeline meaning. DROP is cleaner: the row is gone, the
  re-fly re-makes it, nothing to reap.

- **(c) UT-gate the dispatch dedup + a route-row UT filter at recalc — REJECTED as
  the primary mechanism.** Adding a UT compare to `IsDispatchAlreadyInLedger` and a
  UT filter to the recalc would *also* defeat the collision, but it does NOT fix the
  paradox: the abandoned-future row still SITS in the ledger and
  `ProcessRouteCargoDebited` (unconditional, no UT awareness — the engine pre-filters
  by cutoff but the commit recalc uses `double.MaxValue`) would still charge it. A
  recalc-time route-row UT filter would have to live in EVERY route-aware module and
  the commit recalc, replicating the cutoff in multiple places. **Most importantly,
  the report's constraint:** do NOT add a global UT filter to `ComputeELS` — non-route
  modules rely on `ComputeELS` being **UT-blind** (the cutoff is applied by the
  recalc walk, not by ELS membership; e.g. the post-rewind scene-load recalc passes
  `loadedUT` to `RecalculateAndPatchForCurrentTimelineUT`, `ParsekScenario.cs:596-599`,
  while the commit recalc passes `double.MaxValue`). A UT-blind `ComputeELS` is load-
  bearing for non-route ledger reconstruction staying **byte-identical**. The DROP
  removes the rows from `Ledger.Actions` *before* `ComputeELS` ever runs, so ELS stays
  UT-blind and non-route reconstruction is untouched.

### 2.3 The determinism invariant this preserves

**Non-route ledger reconstruction MUST stay byte-identical.** The predicate gates on
route Type + `RouteId`, so it can only ever remove route rows. Every non-route action
(`FundsEarning`, `ContractComplete`, `MilestoneAchievement`, `FundsInitial`, …) is
invisible to the retire and flows through `Capture`/`Restore`/`ComputeELS` exactly as
today. The `ReconciliationBundleTests.Capture_RoundTrip_RestoresEveryDomain` baseline
(2 non-route actions round-trip, `:179, :245`) stays green unchanged.

---------------------------------------------------------------------------------

## 3. Insertion site + cutoff UT source

### 3.1 Site: retire inside `ReconciliationBundle.Restore` on the SUCCESS post-load path only (NOT Capture, NOT the failed-load rollback)

Three candidate sites were considered; the failed-load rollback path forces the
decision (see the **resolved** R1 analysis in §9 — confirmed by reading the code):

- **`ReconciliationBundle.Capture` (prune the snapshot) — REJECTED.** Capture runs in
  `RewindInvoker.StartInvoke` (`:543`) before `LoadGame`. It is the most "obvious"
  site, but it is WRONG because the SAME bundle is re-installed on the **failed-load
  rollback** (`RewindInvoker.cs:618, 660` → `TryRestoreBundle` → `Restore`, confirmed
  `:3198-3209`). On a failed `LoadGame` the rewind did NOT happen — the player stays in
  the **pre-rewind world** with its pre-rewind tanks and funds — so the pre-rewind
  route rows are STILL meaningful and must be restored intact. Pruning at Capture would
  silently drop those rows on every failed rewind, diverging the ledger (funds-charge
  rows gone while the delivered goods remain live). So Capture must NOT prune.
- **`RewindInvoker.ConsumePostLoad` post-load (after `Restore`)** — workable (re-fetch
  the cutoff from `RewindInvokeContext.RewindPoint.UT` and re-prune the now-restored
  `Ledger.Actions`), but it double-touches the ledger (Restore installs, then a second
  pass removes) and bumps `StateVersion` twice. The flagged-Restore below is tighter.
- **`ReconciliationBundle.Restore` with a `dropFutureRouteRows` parameter — CHOSEN.**
  Restore is the single funnel for BOTH the success post-load path (`ConsumePostLoad`
  step 1, `:715`) and the failed-load rollback (`TryRestoreBundle`, `:3202`). Adding an
  explicit flag lets the success path prune while the rollback path does not — exactly
  the discrimination R1 requires, with no second ledger pass.

  **Shape:**

  ```
  // ReconciliationBundle.cs
  // existing signature: rollback + tests use it, NEVER prunes
  public static void Restore(ReconciliationBundle bundle)
      => Restore(bundle, dropRouteRowsAfterUT: double.PositiveInfinity);

  public static void Restore(ReconciliationBundle bundle, double dropRouteRowsAfterUT)
  {
      ...
      // Ledger actions — DROP free-standing route rows with UT > cutoff (success path only).
      Ledger.Clear();
      if (bundle.Actions != null && bundle.Actions.Count > 0)
      {
          var kept = RouteLedgerRetire.RetireFutureRouteActions(
              bundle.Actions, dropRouteRowsAfterUT, out int retired);
          if (kept.Count > 0) Ledger.AddActions(kept);
          // log: $"Restored ... actions={kept.Count} routeRowsRetired={retired} cutoffUT={dropRouteRowsAfterUT:R}"
      }
      ...
  }
  ```

  - `RewindInvoker.ConsumePostLoad` (the SUCCESS path) calls
    `ReconciliationBundle.Restore(bundle, RewindInvokeContext.RewindPoint.UT)` — the
    RP UT parked on the context (`RewindPoint.cs:33`, the Planetarium UT the quicksave
    was written at = the rewind cutoff). The context survives the scene reload, so the
    cutoff is available in the new scenario.
  - `TryRestoreBundle` (the FAILED-load rollback) calls the parameterless
    `Restore(bundle)` → `+∞` → prunes nothing → the pre-rewind ledger is restored
    intact. **No change to `TryRestoreBundle` is needed** (it already calls the
    parameterless overload).
  - `ReconciliationBundleTests` builds + restores via the parameterless overload → `+∞`
    → route-blind → existing tests unchanged.

  Rationale for default `+∞` not `MaxValue`: a route row with `UT == double.MaxValue`
  can't exist (UTs are finite Planetarium seconds), but `+∞` makes "retire nothing"
  unambiguous and avoids the `> MaxValue` always-false edge being mistaken for a bug.

  Note: Capture is left entirely UNCHANGED — the bundle carries the full pre-rewind
  ledger (correct for the rollback). The prune is a property of the *successful
  restore-into-the-reverted-world*, which is exactly where the world/ledger boundary
  must align.

### 3.2 The `ELS-exempt` audit: no change needed

`ReconciliationBundle.cs` is already on `scripts/ers-els-audit-allowlist.txt`
(`:140-144`, reads `Ledger.Actions` raw to snapshot it). The new code reads the SAME
`Ledger.Actions` it already reads — no new raw-access site, no new allowlist entry.
The pure predicate (`RouteLedgerRetire` / the `IsFreeStandingRouteAction` helper) reads
NEITHER `Ledger.Actions` NOR `CommittedRecordings` (it takes a `GameAction` argument),
so it is not a grep-gate concern. **Confirm during impl:** the grep gate
(`scripts/grep-audit-ers-els.ps1` via `GrepAuditTests`) matches `\bLedger\.Actions\b`
and `\.CommittedRecordings\b`; the new helper file must not reference either token
(it takes the action by parameter). If the helper lives inside `EffectiveState.cs`
(already allowlisted) that is also fine.

### 3.3 Interaction with the post-load scene recalc ordering

`ParsekScenario.OnLoad` runs the post-rewind scene-load recalc at `:3182`
(`RecalculateAndPatchForPostRewindFlightLoad(loadedUT)`) **before**
`DispatchRewindPostLoadIfPending()` at `:3201` (which drives `ConsumePostLoad` →
`Restore` → commit recalc). At the `:3182` moment during a Re-Fly invoke the bundle
has NOT been restored yet. **CORRECTION (edge-case review finding #6):** during a rewind
the IN-MEMORY ledger SURVIVES the scene change (the sidecar reload is skipped, so the
bundle is authoritative), so `:3182` actually sees the full pre-rewind ledger WITH the
future route rows — NOT a clean `.sfs` ledger. It is SAFE anyway because
`RecalculateAndPatchForPostRewindFlightLoad` applies a UT cutoff (`loadedUT`) that filters
`UT > loadedUT` out of the funds patch, AND it is transient (after the Restore-time drop
the commit recalc at `:929` over the post-drop ledger is authoritative). So the Restore-time drop
(`ConsumePostLoad` step 1, which runs inside `DispatchRewindPostLoadIfPending` at
`:3201`, AFTER `:3182`) is the one authoritative point and there is no second site to
patch. The commit recalc inside `RunStripActivateMarker` (`:929`, `double.MaxValue`)
then walks the post-Restore ledger, which is already free of the orphan route rows.
(This is exactly why the retire must be in the bundle Restore, not at `:3182`.)

**Ordering, made precise (review #2).** The whole rewind invocation — `Restore`(drop, `:715`)
-> Strip -> Activate -> commit recalc (`:929`, `double.MaxValue`) — completes BEFORE the player
re-flies a single frame. The route cycles are re-emitted LATER, during live
`RouteOrchestrator.Tick` after the invocation ends, and charged by SUBSEQUENT normal recalcs.
So the `:929` commit recalc is precisely the recalc that WOULD have charged the surviving
phantom rows; the drop at `:715`, immediately before it in the same `ConsumePostLoad`, is what
defuses it. There is no recalc between the drop and the live re-fly that could re-charge the
dropped rows, so funds are never charged before re-delivery. (The earlier "Restore -> re-fly ->
commit" framing was wrong on the order; the conclusion holds for this reason instead.)

---------------------------------------------------------------------------------

## 4. Interaction analysis (the four required decisions + the cross-cutting risks)

### 4.1 Multiple successive rewinds (risk #5) — resolved

Each rewind's `Restore(bundle, rp.UT)` drops route rows > that rewind's cutoff from the
re-installed ledger; the NEXT rewind's `Capture()` (parameterless, unchanged) then
re-snapshots a live ledger the prior rewind already cleaned. Because the prior rewind
dropped its own future rows and the live re-fly re-emitted only the cycles it actually
re-flew, the ledger never accumulates orphan route rows across stacked rewinds. Bounded
by construction.

### 4.2 Recovery-credit deferral straddling a rewind (risk #8) — resolved by Rec-1, no separate change required

`PendingRecoveryCreditCycleId` (`Route.cs:391`) arms on a KSC-origin dispatch
(`RouteOrchestrator.cs:1849`) and flushes a `RouteRecoveryCredited` row at the NEXT
crossing (`EmitPendingRecoveryCredit`, `:3708-3784`). Across a rewind:
- The arm (`PendingRecoveryCreditCycleId`) lives on the Route and **reverts via the
  `.sfs` `ROUTES` node** (it is not a bundle field). So after rewind the route's
  pending-credit marker is whatever it was at RP UT.
- Any *already-emitted* `RouteRecoveryCredited` row with `UT > cutoff` is **dropped**
  by the retire (it is in the route Type set, #28). Any `RouteRecoveryCredited` with
  `UT <= cutoff` is at/before RP and is correctly kept.
- On re-fly the route re-arms and re-flushes deterministically from the reverted
  marker. So the owed credit is neither dropped-and-never-paid nor double-paid.

**Decision: do NOT make the credit non-deferred** (the report floats "emit at the same
crossing" as an option). The deferral is an intentional design property
(logistics-recovery-credit) and the `.sfs`-revert of the arm + the UT-drop of emitted
rows already makes it deterministic under Rec-1. Changing the emit cadence is a larger,
unrelated change with its own test surface. Leave the deferral; Rec-1 covers #8. (Note
this explicitly in the plan so a reviewer does not expect a credit-cadence change.)

### 4.3 The inert `RouteRecoveryCredited` `Effective` gate (`FundsModule.cs:566-588`) — LEAVE it, with a one-line clarifying comment

The gate `if (!action.Effective) return;` is dead for routes (no module flips it
false). Three options:
- **Wire it** (have a first-tier module flip Effective=false for abandoned credits) —
  REJECTED: that re-introduces the tombstone-style "mark inactive" path Rec-1
  deliberately replaces with DROP; the dropped row never reaches FundsModule at all.
- **Remove it** — REJECTED: the gate is harmless and the matching EARNING path
  (`RouteRecoveryCredited` is processed as a fund earning so a rewind reverses it
  through the same recalc path as a recovery `FundsEarning`, per `GameAction.cs:91-94`)
  keeps the gate for symmetry with the rest of FundsModule's earning handlers. Removing
  it would break that symmetry for no behavioral gain and churn `FundsModuleTests`
  (`RouteRecoveryCredited_NonEffective_Skipped` at `FundsModuleTests.cs:1048` asserts
  the gate works).
- **LEAVE it — CHOSEN.** Add a one-line code comment at `FundsModule.cs:566` noting
  that for routes the gate is currently inert-by-design because abandoned-future
  credit rows are DROPPED at rewind (Rec-1) rather than marked non-effective, so the
  gate only fires if a future first-tier route module ever sets Effective=false. The
  existing `RouteRecoveryCredited_NonEffective_Skipped` test stays valid (it sets
  Effective=false directly).

### 4.4 ELS cache versioning — no change needed

`EffectiveState.elsCache` is keyed on `Ledger.StateVersion` + `TombstoneStateVersion`
(`EffectiveState.cs:1303-1310`). The retire happens **inside `Restore`**: `Ledger.Clear()`
+ `AddActions(kept)` bumps `Ledger.StateVersion` (`Ledger.cs:82, 105, 418`), so the next
`ComputeELS()` after Restore rebuilds against the pruned actions automatically. No manual
cache bump required. (The DROP changes which rows enter the ledger on restore, not how ELS
is cached.)

### 4.5 `Route.LastObservedLoopCycleIndex` / `CompletedCycles` / `SkippedCycles` — no change required; confirm the re-fire path

These counters are Route fields persisted by `RouteCodec` and **reverted via the
`.sfs` `ROUTES` node** on rewind (`RouteStore.LoadRoutesFrom` clears + rebuilds;
`LastObservedLoopCycleIndex` is documented to persist through the codec so a save/reload
mid-cycle does not double-fire, `Route.cs:371-378`). After rewind:
- counters read their at-RP values (so the route believes it has done nothing since RP),
- the surviving ledger has no route rows > cutoff (Rec-1 dropped them),
- on the next crossing `EmitLoopCycle` rebuilds `cycleId` from the reverted counters,
  `IsDispatchAlreadyInLedger` finds **no** matching row (they were dropped), so the
  early-return at `:1818` is NOT taken, `EmitDispatchDebit` (`:1826`) runs the live
  writers, and the funds/cargo re-apply once.

**No counter change is needed** — the fix is entirely "remove the colliding surviving
rows." Add a focused test (PRECISELY the inverse of
`RouteLoopDeliveryFireTests.ReplayedCycleId_EmitsNothing_NoDoubleCharge`,
`:752-786`): after the retire empties the ledger of the cycle's rows, the SAME cycleId
+ SAME UT now FIRES (writers run, three rows re-emitted). The existing
`ReplayedCycleId_*` tests stay valid and unchanged — they assert the dedup still
suppresses a *genuine* duplicate while the rows are present (which is still correct
within one timeline); Rec-1 does not change the dedup, it changes whether the rows are
present after a rewind.

---------------------------------------------------------------------------------

## 5. Phased implementation steps

### Phase 1 — pure retire predicate + helper (no behavior change yet)

1. New file `Source/Parsek/Logistics/RouteLedgerRetire.cs` (or a region in
   `EffectiveState.cs`; prefer the standalone file so the grep gate is trivially
   satisfied and the logistics ownership is clear). Pure, Unity-free, `internal static`:
   - `IsRouteActionType(GameActionType t)` → true for 23..29 (the seven route Types).
     Implement as an explicit switch over the named enum members (not a numeric range)
     so adding a non-route Type at 30+ can't silently widen the set.
   - `IsFreeStandingRouteAction(GameAction a)` → `a != null && IsRouteActionType(a.Type)
     && !string.IsNullOrEmpty(a.RouteId)`.
   - `ShouldRetireRouteActionAtRewind(GameAction a, double cutoffUT)` →
     `IsFreeStandingRouteAction(a) && a.UT > cutoffUT`.
   - `RetireFutureRouteActions(IList<GameAction> source, double cutoffUT, out int retired)`
     → returns a NEW filtered `List<GameAction>` preserving order, counting drops.
     (Pure list→list so it is testable without touching the static `Ledger`.)
2. Unit tests `Source/Parsek.Tests/Logistics/RouteLedgerRetireTests.cs`
   (`[Collection("Sequential")]` only if it touches `ParsekLog`; the pure helper does
   not, so a plain class is fine):
   - each route Type with `UT > cutoff` retired; with `UT == cutoff` kept (strict `>`);
     with `UT < cutoff` kept.
   - non-route Types (`FundsEarning`, `ContractComplete`, `MilestoneAchievement`,
     `FundsInitial`) NEVER retired regardless of UT.
   - a route Type with null/empty `RouteId` NOT retired (guards the belt-and-suspenders
     gate).
   - `RetireFutureRouteActions` preserves order + count of survivors; `+∞` cutoff
     retires nothing; mixed list keeps non-route rows interleaved in order.

### Phase 2 — wire the retire into the rewind Restore (success path only)

3. `ReconciliationBundle.cs`:
   - add `Restore(ReconciliationBundle bundle, double dropRouteRowsAfterUT)`; keep
     `Restore(bundle)` delegating with `double.PositiveInfinity`.
   - in the ledger re-install block, replace the `Ledger.AddActions(bundle.Actions)`
     with `RouteLedgerRetire.RetireFutureRouteActions(bundle.Actions,
     dropRouteRowsAfterUT, out retired)` → `Ledger.AddActions(kept)`.
   - extend the existing `Restored: …` Info log with `routeRowsRetired={retired}
     routeRetireCutoffUT={cutoff:R}` (single summary line, per the logging convention).
   - **Capture is UNCHANGED** (the bundle still carries the full pre-rewind ledger, so
     the failed-load rollback restores it intact).
4. `RewindInvoker.ConsumePostLoad` (`:715`, the SUCCESS post-load Restore): change
   `ReconciliationBundle.Restore(bundle)` to
   `ReconciliationBundle.Restore(bundle, RewindInvokeContext.RewindPoint.UT)`. Add an
   Info line noting the route-retire cutoff (`rp.UT`), so KSP.log shows the cutoff that
   scoped the drop.
5. **No change to `TryRestoreBundle`** (`:3198`): it keeps calling the parameterless
   `Restore(bundle)` → `+∞` → prunes nothing → the failed-load rollback restores the
   pre-rewind ledger intact (resolves R1).
6. Tests:
   - extend `Source/Parsek.Tests/ReconciliationBundleTests.cs` with
     `Restore_WithRouteCutoff_DropsFutureRouteRows_KeepsPastAndNonRoute`: build a bundle
     (via `Capture()`) holding (a) a non-route action at UT 3000, (b) a
     `RouteCargoDebited` at UT 2000 (≤ cutoff 2500), (c) a `RouteDispatched` +
     `RouteCargoDelivered` + `RouteRecoveryCredited` at UT 3000 (> cutoff);
     `Restore(bundle, 2500)` → assert `Ledger.Actions` keeps (a) and (b), drops the
     three future route rows; a second test `Restore(bundle)` (no cutoff) keeps all
     (the rollback contract).
   - the existing `Capture_RoundTrip_RestoresEveryDomain`, `Restore_Idempotent_*`,
     `Restore_AfterBundle_*` stay UNCHANGED and green (they use `Restore(bundle)` → `+∞`).

### Phase 3 — end-to-end rewind→re-fly→commit determinism test (the core proof)

7. New `Source/Parsek.Tests/Logistics/RouteRewindRedeliveryTests.cs`
   (`[Collection("Sequential")]`; resets `Ledger`/`RecordingStore`/`RouteStore`/
   `LedgerOrchestrator` per the shared-static rule). These are HEADLESS xUnit driving
   the ledger/recalc paths (the live physical writers early-return on null singletons,
   so assert on the **re-emission of the rows + the FundsModule charge**, which is the
   economic proof; the physical re-delivery itself is covered by an in-game test, step
   12). Cases:
   - **Funds-once + cargo-row-re-emitted (KSC):** simulate "cycle-0 fired before
     rewind" by seeding `RouteDispatched`+`RouteCargoDebited(KscFundsCost=5000)`+
     `RouteCargoDelivered` at UT 2000; `Capture()` a bundle (FULL ledger — the rows are
     still present in the bundle, Capture is unchanged); `Restore(bundle, 1500)`; assert
     `Ledger.Actions` no longer holds the three future route rows; then drive
     `EmitLoopCycle` (via the existing `RouteLoopDeliveryFireTests` harness/test seam) at
     the cycle-0 crossing and assert it FIRES (not suppressed) → re-emits the three rows
     once; recalc and assert FundsModule charges 5000 exactly once (not twice, not zero).
   - **Non-KSC pure-physical:** same but `RouteCargoDebited` carries 0 funds cost;
     assert the row re-emits (so the live writer would re-run) and FundsModule charges
     nothing, and `CompletedCycles` advances exactly once (no double-bump).
   - **Multi-rewind:** rewind twice — `Restore(bundle1, 1500)`, then re-emit a cycle live
     and `Capture()` + `Restore(bundle2, 1800)`; assert `Ledger.Actions` never accumulates
     orphan route rows (after each Restore it holds only rows ≤ that cutoff plus whatever
     the live re-fly re-emitted).
   - **Recovery-credit straddle:** seed a `RouteRecoveryCredited` at UT 2000 with a
     pending arm; `Restore(bundle, 1500)` drops the credit row from the ledger; assert the re-fly re-flushes a
     single credit (counter/arm reverted via the simulated `.sfs` ROUTES reload — model
     the revert by resetting the Route's `PendingRecoveryCreditCycleId` to its at-RP
     value) and FundsModule credits it exactly once.
   - **Phantom-charge avoidance on divergent re-fly:** seed cycle-0 rows at UT 2000,
     `Restore(bundle, 1500)`, then do NOT re-fly cycle-0 (simulate the player pausing the route
     so the crossing never re-fires); recalc and assert FundsModule charges **nothing**
     for cycle-0 (the abandoned row was dropped; no phantom charge).

### Phase 4 — Rec-3: the non-rewind discard leak (separate root cause)

> **RESOLUTION (2026-07-06, maintainer): Rec-3 CLOSED as reframed, RATIFY both-persist as correct (option C).**
> The OPEN BLOCKER below (route rows are AMBIENT, no `RecordingId`, so a UT-window reverse would wrongly undo a
> CONCURRENT committed route's deliveries) is resolved in favor of ratification: ambient route deliveries are NOT
> effects of the discarded flight (they would have fired regardless), so keeping them is CORRECT and matches the
> 0.10.2 preserve-live-earned doctrine. The reverse-on-discard writers are DECLINED, not built, not merely
> deferred. The behavior-neutral observability slice (the `[Rec-3 residual]` Warn) is the FINAL Rec-3 deliverable;
> it was VERIFIED on a real toolchain (build clean + `dotnet test` 16799 passed / 0 failed + grep-audit exit 0,
> 2026-07-06). The "DECISION LOG" and "OPEN BLOCKER" notes below are retained for history but are SUPERSEDED by
> this resolution.
>
> **DECISION LOG (2026-07-06, maintainer) — observability slice SHIPPED; full fix DEFERRED with a settled design [SUPERSEDED by the RESOLUTION above: declined in favor of option C].**
> Scope confirmed as option (i): wire the Rec-3 OBSERVABILITY now, schedule the full reverse-on-discard fix.
> The tentative option (ii) ("disable physical route mutation at the three writer call sites") is **REJECTED as unimplementable**: under always-tree mode (#271) every in-flight segment can still COMMIT, so the disposition (keep vs discard) is a *future* user choice unknowable at write time — no write-time boolean is both complete (covers every discard) and non-disruptive (never suppresses a will-commit flight); `RouteRevertSafety` is therefore a *discard-time* model, not a write-time gate.
> **Settled deferred design:** reverse-on-discard AT the three non-rewind discard cores, over the discarded subtree's UT window, with an **all-or-nothing-per-cycle** lockstep-funds retire — a cycle's physical rows and its paired funds row retire together only if EVERY physical row reversed fully; any un-reversible row (burned/transferred fuel, moved/recovered endpoint — the `RouteCargoDelivered` row carries no destination pid) keeps the WHOLE cycle intact and logs a residual. This guarantees the fix can never introduce a free-resources or double-charge desync; it degrades to today's consistent keep-both per un-reversible cycle. Skip `MergeDialog.ReFlyDiscard` (RP-backed = classifier-revertable). The reverse writers touch live vessel tanks/inventory and are the only surface that needs an in-game harness.
> **Reframed severity (investigation finding):** the leak is a discard-INTENT residual, NOT an economic desync — on every non-rewind discard funds AND cargo both persist consistently today (the free-standing route rows survive the recording-scoped purge), which actually matches the 0.10.2 preserve-live-earned-gameplay doctrine. The `preserve=false` abandon path is not a new lost-resources risk: `LedgerOrchestrator.OnLoad` → `Ledger.LoadFromFile` clears + rebuilds the in-memory ledger from the persisted ledger on every load, and the reporter skips that path.
> **OPEN BLOCKER for the deferred reverse fix — attribution (review nice-to-have A):** route rows are AMBIENT (dispatch fires ~1 Hz in ALL scenes and carries no `RecordingId`), so a discarded flight's UT window ALSO captures the rows of any CONCURRENT committed route that merely delivered while you flew. A reverse pass keyed on the UT window alone would therefore wrongly UNDO an unrelated committed route's delivery. The observability Warn is harmless here (it only over-reports, and its wording now says the window may include a concurrent committed route), but the reverse pass MUST first attribute each cycle to the discarded flight before it may reverse — and there is no `RecordingId` link to do so. This sharpens the doctrine question: ambient route deliveries are arguably NOT effects of the discarded flight (they would have fired regardless), which argues for RATIFYING both-persist as correct (option C) rather than building the reverse writers. Resolve this attribution/doctrine question BEFORE implementing Phase B — do not reverse by bare UT window. **RESOLVED 2026-07-06: the maintainer chose option C (ratify both-persist as correct); the reverse writers are DECLINED and this blocker is moot.**
> **What SHIPPED in the observability slice** (branch `claude/development-priorities-ftr2ye`, stacked on `logistics-rewind-rec1`; VERIFIED 2026-07-06 on a real toolchain: build + `dotnet test` 16799-green): new `Logistics/RouteDiscardObservability.cs` (window compute + pure `SummarizePhysicalLeak` + the `ReportDiscardLeakForRecordings` Warn) wired into `DiscardPendingTree` (preserve=true only), `TryDiscardActiveSwitchSegmentAttempt`, and `AutoDiscardActiveTreeCore`; `RouteLedgerRetire.SelectFreeStandingRouteActionsInWindow` + `IsPhysicalRouteMutation`; tests `RouteDiscardObservabilityTests` + `RouteLedgerRetireTests` additions. Behavior-neutral; reuses `RouteRevertSafety.PhysicalMutationRevertable`. Superseded item 8's "observability only" wording below.

The discard cores (`RecordingStore.DiscardPendingTree`,
`TryDiscardActiveSwitchSegmentAttempt`, `ParsekFlight.AutoDiscardActiveTreeCore`) and
`MergeDialog.ReFlyDiscard` touch **zero** route state and have **no quicksave**. The
physical route writers (`LiveDeliveryWriters.WriteResource/WriteInventory`,
`LiveOriginDebitWriters.WriteResourceDebit`, `LiveInventoryPickupWriter.RemoveOne`)
have **no reverse method** — their only rollback is the rewind quicksave. So a route
that physically delivered/debited cargo during a since-discarded segment leaks that
mutation into the surviving timeline with no rollback path.

**MAINTAINER DECISION FIRST (review #5).** The FULL Rec-3 fix is DEFERRED in this PR — pick
one of: (i) ship Rec-1 + the Rec-3 OBSERVABILITY now and schedule the full fix, or (ii)
require the full fix here (chosen direction: "disable physical route mutation for any revert
lacking a quicksave," gated at the three writer call sites). Do NOT implement the full disable
without that confirmation; this plan implements ONLY the observability below.

**Severity + asymmetry (do not under-sell).** Report risk #6 rates this **Medium-High** — NOT
a minor tail; it is comparable to the primary paradox. But the leak is NARROW (a route must
physically fire INSIDE a segment the player then discards WITHOUT a rewind) and a true reverse
writer is a large, fragile surface (partial-fill, unloaded proto modules, inventory-slot
determinism), so deferring the full fix is defensible. **Rec-1 does NOT touch this path:**
`MergeDialog.ReFlyDiscard` reverts via `RecalculateAndPatchForCurrentTimelineIfFutureActions`
(`:307`, NO `LoadGame`/quicksave) and NEVER calls `ReconciliationBundle.Restore`, so the
route-row drop never runs there — the leaked physical effect persists AND its surviving ledger
rows are still charged/counted. After Rec-1 the rewind path is correct but the
non-rewind-discard path still leaks BOTH the physical effect and the rows.

**Chosen Rec-3 scope (observability only):**

8. **Document + log the leak as a known residual, and add the cheap guard that is
   actually reachable:** the only discard with a *quicksave-backed* counterpart is
   re-fly/rewind discard (`MergeDialog.ReFlyDiscard`), which reverts via the RP
   quicksave — and that path, post-Rec-1, also needs the route rows dropped. Confirm
   `ReFlyDiscard`'s revert reloads the session's quicksave (it does NOT — it drops the
   ghost only, no quicksave; per the discard-paths read it calls
   `RecalculateAndPatchForCurrentTimelineIfFutureActions`, no `LoadGame`). Therefore
   ReFlyDiscard ALSO leaks route physical effects with no rollback, exactly like a
   plain discard.
   - **Minimal concrete fix that is in-scope:** in the non-rewind discard cores, after
     the existing economy-preserve step, **drop free-standing route rows that have no
     surviving-timeline meaning for the discarded window is NOT possible** (the rows
     are not recording-scoped and the window is not a UT cutoff the discard owns), so
     do NOT touch the ledger here. Instead, gate the **physical writers** behind a
     single new predicate `RouteRevertSafety.PhysicalMutationAllowed()` that returns
     true only when the live session has a quicksave-backed revert available for the
     route's dispatch (today: always true during normal play — routes ARE meant to
     mutate; the rewind quicksave is the revert). Concretely, the writers already rely
     on "the rewind quicksave is the revert" (`LiveInventoryPickupWriter.cs:37`,
     `RouteStore.cs:59-61`), which holds for the *rewind* path. For the *discard* path
     there is no quicksave, so the leak is intrinsic to discard, not to the writers.

   **Resolution: scope Rec-3 to (a) a documented known-residual + (b) a guard only
   where a clean hook exists, and DEFER the full reverse-writer/disable design to a
   tracked todo.** Specifically:
   - Add a Warn log at each physical writer call site when it runs inside a segment
     that can be discarded without a quicksave (i.e. a non-rewind in-flight segment),
     naming the route + amount, so the leak is observable in KSP.log
     (`[Route] physical mutation under un-revertable segment …`). This is the cheap,
     correct-today step and matches the logging-requirements rule.
   - Add a `todo-and-known-bugs.md` "Known residual (not yet fixed)" entry for the
     discard physical leak with the two future options (reverse writers vs. disable
     physical route mutation for un-quicksaved reverts), cross-linking the report
     risk #6.

   **Reviewer note (flag explicitly):** Rec-3's *full* fix (reverse-on-discard or
   disable-without-quicksave) is deliberately DEFERRED here because both options are
   larger than Rec-1 and the leak requires a route to physically fire inside a segment
   the player then discards without rewinding — rare relative to the rewind paradox.
   The maintainer should confirm whether to (i) ship Rec-1 + the Rec-3 observability
   now and schedule the full Rec-3 fix, or (ii) require the full Rec-3 fix in this same
   PR (in which case the chosen direction is "disable physical route mutation for any
   revert lacking a quicksave," gated at the three writer call sites). **This is a
   scope decision for the maintainer; do not implement the full Rec-3 disable without
   that confirmation.**

9. Tests for the Rec-3 observability: a unit test that the Warn predicate
   (`RouteRevertSafety` pure helper) classifies a non-rewind in-flight segment as
   un-revertable and a rewind-backed segment as revertable, asserted via the log sink.

### Phase 5 — Rec-2: surface (do NOT decide) the inter-body creation gate

10. **No code change in this plan.** Document the policy decision and the exact gate
    site so the maintainer can choose:
    - Inter-body routes are creatable TODAY — there is no hard same-body reject.
      `RouteAnalysisEngine` reject reasons are body-agnostic and
      `RouteCandidateFinder` gates only on sealed + eligible + dedup (per the report
      Axis E / risk #12). A cross-parent Supply Run DELIVERS — a destination
      station/rendezvous route flags `UnsupportedRendezvous` so its VISUAL relaunch
      phase is arbitrary (no re-aim), but it is "FUNCTIONALLY unaffected — delivery
      fires at the `RecordedDockUT` loop-clock marker regardless of visual alignment"
      (`todo-and-known-bugs.md` ~451; the synodic-faithful visual is the deferred
      re-aim layer, ~1622). So it is exposed to the paradox now (NOT "degraded": it
      delivers; only the visual faithfulness is deferred).
    - **Rec-1 covers inter-body for free** because `EmitLoopCycle`/`cycleId` is
      cadence-source-agnostic (the retire keys on route Type + UT, not on cadence
      shape), so an inter-body route's abandoned-future rows drop exactly like a
      same-body route's.
    - **The OPEN policy question (for the maintainer):** should inter-body routes be
      *hard-blocked* until M5? If yes, add an explicit cross-parent creation reject
      with a player-facing reason. **Proposed gate site:** the route-creation
      acceptance path in `RouteAnalysisEngine` (where reject reasons are produced),
      adding a `CrossParentBodyNotSupported` reason when the origin and destination
      endpoints resolve to vessels under different parent bodies — i.e. alongside the
      existing body-agnostic reject reasons, BEFORE the candidate is promoted to a
      route. (Confirm the precise method during impl by reading
      `RouteAnalysisEngine.cs` for the reject-reason enum + the promotion site;
      `RouteCandidateFinder` is the alternate site but the analysis-engine reject is
      the player-facing one.)
    - This plan deliberately leaves Rec-2 as a flagged decision; it is NOT implemented.

### Phase 6 — Rec-4 + Rec-5: docs reconcile

11. Update `docs/parsek-logistics-supply-routes-design.md`:
    - **§2.4 #11 (lines ~220-221)** and **§10.6 (lines ~1020-1022)** and **§13.4 (lines
      ~1157-1159):** replace the stale "un-reversed mutation paths must stay disabled"
      contract with the SHIPPED model: physical route effects ride the full-world
      quicksave restore (not a reversing module); funds ride the recalc; `RouteModule`
      stays observe-only; and AT REWIND the free-standing route ledger rows whose
      `UT > cutoff` are DROPPED so the live re-fly re-emits them deterministically
      (Rec-1). Name the residual Rec-3 discard leak (a non-rewind discard has no
      quicksave) as the one case where the original "must stay disabled" stance still
      applies, tracked as a known residual.
    - **Rec-5 (§ near the loop-anchor / Pause→Activate description):** add a
      determinism caveat — a create-Active route is fully recorded-deterministic (grid
      pinned to recorded `spanEnd`) while a Pause→Activate route's grid is pinned to
      the live activation UT (`RouteOrchestrator.cs:205`, `MissionLoopUnitBuilder.cs`
      ~228-229), so its post-rewind determinism is conditional on the re-fly
      reproducing the activation event at the same UT. Documentation only.

### Phase 7 — CHANGELOG + todo

12. `CHANGELOG.md` under `## 0.10.2` → `### Fixes` (user-facing, ASCII, no em dash):
    - "Fixed a time-rewind bug with recurring supply routes: if you rewound past a
      route delivery and re-flew, the route would charge you again but never re-deliver
      the cargo (or, for a depot-to-depot route, silently skip the shipment while
      counting it as done). Route deliveries that happened after the rewind point are
      now undone with the rest of the world, so re-flying re-runs each cycle and moves
      the cargo (and charges the funds) exactly once."
13. `docs/dev/todo-and-known-bugs.md`:
    - flip the existing "Known bug (investigated, not yet fixed) - Supply routes ↔
      time-rewind" entry to a "Fixed (pending in-game validation)" entry, summarizing
      Rec-1 (route-row DROP in the flagged `ReconciliationBundle.Restore`), keeping the root-cause paragraph,
      adding the test names (`RouteLedgerRetireTests`, `ReconciliationBundleTests`
      additions, `RouteRewindRedeliveryTests`), and cross-linking THIS plan.
    - add a new "Known residual (not yet fixed) - route physical effects leak on a
      non-rewind discard" entry (Rec-3 deferred-full-fix) cross-linking report risk #6.
    - add a one-line note that Rec-2 (inter-body hard-block) is an OPEN product
      decision, not implemented.

---------------------------------------------------------------------------------

## 6. Test plan summary (xUnit unless noted)

| Test | File | Asserts |
|---|---|---|
| `IsRouteActionType` / `IsFreeStandingRouteAction` / `ShouldRetireRouteActionAtRewind` matrix | `Logistics/RouteLedgerRetireTests.cs` (new) | each route Type 23-29; non-route never; null RouteId never; strict `>` boundary |
| `RetireFutureRouteActions` order/count | `Logistics/RouteLedgerRetireTests.cs` (new) | survivors keep order; `+∞` retires nothing; mixed list interleaving |
| `Restore_WithRouteCutoff_DropsFutureRouteRows_KeepsPastAndNonRoute` | `ReconciliationBundleTests.cs` (extend) | `Restore(bundle, cutoff)` drops future route rows from the ledger, keeps past + non-route; bundle itself unchanged |
| existing `Capture_RoundTrip_*`, `Restore_Idempotent_*`, `Restore_AfterBundle_*` | `ReconciliationBundleTests.cs` (UNCHANGED) | non-route reconstruction byte-identical (regression guard) |
| Funds-once + cargo-row-re-emitted (KSC) | `Logistics/RouteRewindRedeliveryTests.cs` (new) | after Capture drop, re-fly FIRES (not suppressed), funds charged once |
| Non-KSC pure-physical | same | row re-emits, funds unchanged, `CompletedCycles` +1 once |
| Multi-rewind no accumulation | same | post-second-capture bundle holds only rows ≤ its cutoff |
| Recovery-credit straddle | same | single re-flushed credit, charged once |
| Phantom-charge avoidance (divergent re-fly) | same | no charge for an un-re-flown cycle |
| Re-fire after retire (inverse of dedup test) | `Logistics/RouteLoopDeliveryFireTests.cs` (extend) | empty ledger → same cycleId+UT FIRES |
| existing `ReplayedCycleId_EmitsNothing_NoDoubleCharge` / `ReplayedCycleId_EmitsNoDebit` | `RouteLoopDeliveryFireTests.cs` (UNCHANGED) | dedup still suppresses a *present* duplicate within one timeline |
| `RouteRevertSafety` classification (Rec-3 observability) | `Logistics/RouteRevertSafetyTests.cs` (new) | non-rewind segment = un-revertable; rewind segment = revertable; Warn logged |
| In-game: physical re-delivery after rewind | `InGameTests/RuntimeTests.cs` or `ExtendedRuntimeTests.cs` (new, `Scene = FLIGHT`, career, reversible) | drives a real route cycle + rewind + re-fly and asserts the destination tank is re-filled once and funds net once (the live writer path the xUnit early-returns out of) |

**Coverage map (review #7 — what proves what).** The HEADLESS suite proves the ECONOMIC /
dedup half: funds-charged-once, the dispatch dedup no longer suppresses after the drop (rows
re-emit), no phantom charge on a divergent re-fly, no multi-rewind accumulation, and the
boundary. It does NOT prove the cargo physically moves — the live writers early-return on null
singletons headlessly. The IN-GAME test (the last table row) is therefore LOAD-BEARING and
REQUIRED-FOR-MERGE: it is the only assertion of the user-visible symptom (destination tank
re-filled exactly once after rewind+re-fly), and without it the headless suite alone cannot
catch a regression where the dedup is defused but the writer path silently no-ops. It needs a
career FLIGHT scene with a real route + real RP + real re-fly (a heavy harness); the "Fixed"
claim is gated on it.

Full suite must be green (`cd Source/Parsek.Tests && dotnet test`). Note the test
working-dir / `[Collection("Sequential")]` rules for shared statics (`Ledger`,
`RouteStore`, `RecordingStore`, `LedgerOrchestrator`, `ParsekScenario`).

### Tests that encode the current (buggy-for-routes) behavior

- **None require flipping.** The dedup tests (`ReplayedCycleId_*`,
  `RouteLoopDeliveryFireTests.cs:752, 1046`) remain correct: the dedup is right
  *within a timeline*; Rec-1 changes whether the colliding rows survive a rewind, not
  the dedup. `SupersedeCommitTests.cs:160-167` (route Types classified non-blocking)
  and `RouteModuleRegistrationTests` (observe-only) stay valid — Rec-1 does not change
  route-action supersede classification or RouteModule. `FundsModuleTests.cs:1117-1179`
  (Option-A full-walk invariants) stay valid. The retire is additive, scoped to the
  rewind Capture, and route-Type-gated, so no existing assertion contradicts it.

---------------------------------------------------------------------------------

## 7. ERS / ELS grep-gate consideration (explicit)

- `ReconciliationBundle.cs` is already allowlisted for raw `Ledger.Actions` reads
  (`ers-els-audit-allowlist.txt:140-144`); the new retire loop reads the same list it
  already snapshots → **no new allowlist entry, no new exemption**.
- The pure helper (`RouteLedgerRetire` / `IsFreeStandingRouteAction`) takes a
  `GameAction` by parameter and reads NEITHER `Ledger.Actions` NOR
  `.CommittedRecordings`, so the grep gate (`\bLedger\.Actions\b` /
  `\.CommittedRecordings\b`, enforced by `GrepAuditTests`) does not flag it. Keep the
  helper free of those tokens (it must not call `Ledger.Actions` directly; the bundle
  passes the list in).
- The Rec-3 observability predicate (`RouteRevertSafety`) likewise takes its inputs by
  parameter and reads neither token.

---------------------------------------------------------------------------------

## 8. Review checkpoints

1. **After Phase 1-2 (core mechanism):** review the predicate + the flagged-`Restore`
   wiring in isolation. Confirm: strict `>` boundary matches the world-revert boundary; the
   route Type set is exactly 23-29 via an explicit switch; the default-arg `+∞`
   overload leaves every existing caller route-blind; the `Captured: …` log carries
   the retire count + cutoff. This is the highest-leverage, schema-adjacent change —
   give it a full review.
2. **After Phase 3 (e2e proof):** confirm the headless e2e actually exercises the
   re-fire path (not just the bundle drop) and that the phantom-charge-avoidance case
   asserts ZERO charge for an un-re-flown cycle.
3. **After Phase 4 (Rec-3):** confirm the scope is the documented-residual + the
   observability Warn ONLY, and that the plan did NOT silently implement the full
   disable/reverse (that is the maintainer's decision). If the maintainer chose the
   full disable, re-review the three writer call-site gates.
4. **Docs pass (Phase 6-7):** run `git diff --cached` and confirm CHANGELOG + todo +
   design-doc §2.4/§10.6/§13.4 match the SHIPPED contract and do not contradict each
   other; cross-links to the report are present.
5. **Final:** one full review before the PR (this is a serialization-adjacent /
   rewind-path change → it is in the "risky" set per the review policy). Full suite
   green; build deploys the right DLL (worktree verification recipe).

---------------------------------------------------------------------------------

## 9. Risks for the reviewer to scrutinize

- **R1 — failed-`LoadGame` rollback must NOT prune (RESOLVED in this plan; re-verify
  in review).** Confirmed by reading the code: on a failed `GamePersistence.LoadGame`
  (returns null `:610` or throws `:653`), `TryRestoreBundle(bundle)` → `Restore(bundle)`
  (`:3198-3209`) re-installs the pre-rewind ledger because the rewind did NOT happen and
  the player stays in the **pre-rewind world** (tanks + funds unreverted), so the
  pre-rewind route rows are STILL meaningful. This is precisely why §3.1 puts the prune
  in a FLAGGED `Restore` (success path passes `rp.UT`, the rollback path passes `+∞` via
  the parameterless overload) and leaves Capture unpruned. **Reviewer: re-verify** that
  no other caller of `Restore` exists that should prune (grep `ReconciliationBundle.Restore`;
  today only `ConsumePostLoad` (success) and `TryRestoreBundle` (rollback) call it), and
  that `RewindInvokeContext.RewindPoint.UT` is populated and survives the scene reload at
  the `ConsumePostLoad` call.
- **R2 — strict `>` vs `>=` at the exact RP UT (review #4: justify by emit ordering, not
  probability).** Keep strict `>`. The deciding question is whether the RP quicksave's world
  snapshot includes the physical effect of a dispatch stamped exactly at `rp.UT`. The ledger
  row and the physical write are emitted in the SAME synchronous `EmitLoopCycle` tick from the
  same `currentUT` (`EmitDispatchDebit` writes the row only after the live writer runs), so a
  row existing at `UT == rp.UT` means its physical write already landed and IS in the
  quicksave's tanks -> keep the row (`>`), matching the world-revert. (The tie is also
  near-impossible on the ~1 Hz grid, but the emit-ordering argument — not probability — is the
  justification.) Lock the boundary with the Phase-1 boundary test AND a one-line code comment
  at the predicate stating the physical-capture reasoning, so a future reader does not "fix" it
  to `>=`.
- **R3 — Rec-3 scope.** The plan defers the full discard-leak fix. Confirm the
  maintainer accepts ship-Rec-1-now-defer-full-Rec-3, or escalate to the full disable
  gate in this PR.
- **R4 — `RouteCargoPickedUp` / `RoutePaused` / `RouteEndpointLost` in the retire
  set.** These are in the route Type set and will be dropped > cutoff. Confirm that is
  correct: a `RoutePaused` emitted after the cutoff is an abandoned-future state change
  and should revert with the route's `.sfs` counters (it does); a `RouteEndpointLost`
  after cutoff likewise. None of these feed a non-route module, so dropping them is
  ledger-consistent — but verify no consumer reads them post-rewind expecting them
  present.

---------------------------------------------------------------------------------

## 10. Post-implementation review resolutions (adversarial edge-case + test-file reviews)

The implemented change set was put through a by-inspection compile review (verdict
LIKELY-COMPILES-AND-CORRECT), an adversarial edge-case review, and a test-file review
(both test files TESTS-SOUND / will compile, no MUST-FIX). Outcomes:

- **Edge-case finding #4 (LOW, FIXED).** The retire cutoff must be the UT the WORLD
  actually reverted to (the loaded quicksave UT). `RewindPoint.UT` is captured at
  `RewindPointAuthor.Begin` one frame BEFORE the deferred quicksave save, so it is
  ~one frame EARLIER than the `.sfs`-embedded UT — a route row in the
  `(rp.UT, quicksaveUT]` window would be wrongly dropped and the re-fly would
  double-apply. **Fix applied:** `RewindInvoker.ConsumePostLoad` now passes the POST-LOAD
  live UT (`SafeNow()`, fallback `rp.UT`) as the cutoff instead of `rp.UT` — at that point
  the universe has just loaded at the quicksave UT, so the cutoff equals the world-revert
  boundary and the strict-`>` emit-ordering justification becomes literally true. (Every
  "cutoff = `rp.UT`" reference earlier in this plan is superseded by this.) `RewindPointAuthor`
  is deliberately NOT modified (avoids touching RP authoring); the fix is scoped to the
  consumer and does not affect the unit/e2e/in-game tests (they call `Restore` with explicit
  cutoffs).
- **Edge-case finding #6 (doc, FIXED above in §3.3).** The pre-Restore `:3182` recalc sees
  the surviving in-memory ledger filtered by a `loadedUT` cutoff, not a clean `.sfs` ledger;
  corrected. Outcome unchanged (safe).
- **SAFE (confirmed, no change):** RoutePaused/RouteEndpointLost drops (observe-only, state
  reverts via `.sfs`), recovery-credit re-flush (arm reverts via `.sfs`, future credit row
  dropped → exactly one re-flush), multi-rewind (pure stateless filter, each Capture sees a
  clean ledger), other ledger-restore paths (stock-revert prune already drops route rows;
  cold load skipped during rewind; journal/sweep run post-drop), and all degenerate cases.
- **Test reviews:** the 7 headless `[Fact]`s prove the drop→re-fire non-trivially and the
  headless economic-magnitude approximation is honestly scoped (real charge is the in-game
  test's job); the in-game test is a faithful `ConsumePostLoad` proxy and fully reversible.
  Build-finish resolution: the cutoff dispatch-debit charges live `Funding` synchronously
  inside `RouteOrchestrator.Tick` (via `LiveDebitFunds`), so NO `RecalculateAndPatch` is
  needed in the in-game test to witness or restore the charge — the earlier `TODO(build-env)`
  cleanup caveat is resolved (the test resets live `Funding` directly to the baseline in the
  rewind approximation and in the `finally`). The 9 in-game `TODO(build-env)` markers are all
  wired; the only remaining gate is the single live-KSP playtest.
