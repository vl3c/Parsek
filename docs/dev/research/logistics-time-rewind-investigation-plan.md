# Investigation Plan: Logistics (Supply Routes) ↔ Time-Rewind Determinism

*Status: investigation plan (pre-report). Produced for the task "is there a fundamental
compatibility issue between logistics and time rewind, and how do recurrent routes behave
across a rewind without creating time-travel paradoxes." This document scopes the
investigation; the deliverable is a separate report + recommendations note. No production
code is changed by this plan or the report.*

---

## 1. The question, stated precisely

A supply route is a recurrent transfer (the flagship worry: "Duna every ~2 years, fuel to a
station there"). The player can **rewind** the timeline (Rewind-to-Separation / re-fly) to an
earlier UT and re-fly forward. The whole game is supposed to be **deterministic** — re-advancing
the same way must reproduce the same world — so that rewinds do not create time-travel paradoxes
(funds spent twice, fuel delivered into a past that no longer happened, a route that fires before
it was ever created).

The user's framing decomposes into four concrete sub-questions:

1. **Trigger in the past** — when the player rewinds to *before* a recurrent route was created and
   re-flies forward through the window where it was created, how (and whether) does the route
   start firing again?
2. **Disable before it existed** — a route created at UT=T must NOT fire at any UT < T after a
   rewind. How is "the route did not exist yet" enforced on the dispatch clock and on the ledger?
3. **Re-enable after** — once live time crosses the creation/activation UT again, the route must
   resume on the same cadence with the same economic + physical effects.
4. **Record-and-replay vs set-on-and-it-works** — is a route's past activity *replayed from a
   recorded dispatch stream*, or is the route simply *live* and re-fires on its loop clock as live
   UT advances? Which model is implemented, and is that model deterministic?

The output must answer all four and surface any paradox-producing gap.

## 2. What is already known (from design docs + first-pass code read)

These are established facts the investigation builds on — they are NOT to be re-derived, only
verified where flagged:

- **Routes fire LIVE on a loop clock, they are not a recorded dispatch replay.** The dispatch
  *phase* is owned by `RouteLoopClock` (a crossing detector over
  `GhostPlaybackLogic.TryComputeSpanLoopUT`) + `Route.LastObservedLoopCycleIndex`, driven by live
  game UT: `elapsed = nowUT − PhaseAnchorUT`, `PhaseAnchorUT = route.LoopAnchorUT` (set on
  activate). `RouteOrchestrator.ProcessOneRoute` runs ~1 Hz; on a confirmed dock crossing it calls
  `EmitLoopCycle`. (`RouteLoopClock.cs`, `RouteOrchestrator.cs`, plan
  `plan-logistics-routes-on-missions.md` Phase 4.)
- **Two effect surfaces with two different reversal mechanisms:**
  - *Funds* (KSC dispatch charge, recovery credit) flow through `FundsModule` in the ledger recalc
    walk → deterministically reconstructed on `RecalculateAndPatch` → reconciled under
    rewind/supersede/tombstone via `PatchFunds`.
  - *Physical* (origin cargo debit, per-window pickup, endpoint delivery to live vessel
    tanks/inventory) are applied **live at emit time** by `RouteOrchestrator` and are reverted by
    **the rewind quicksave restore** (the `.sfs` reload), NOT by the recalc walk. `RouteModule` is
    **observe-only** (`T-ROUTEMODULE-OBSERVE`): it counts rows and mutates nothing.
- **Each dispatch/delivery is a ledger `GameAction`** (`RouteDispatched`, `RouteCargoDebited`,
  `RouteCargoPickedUp`, `RouteCargoDelivered`, `RouteRecoveryCredited`, `RoutePaused`,
  `RouteEndpointLost`) carrying `RouteId` + `RouteCycleId`, stamped at live `currentUT`.
- **ELS `(routeId, cycleId)` idempotency** + the persisted `LastObservedLoopCycleIndex` are the
  re-fire guards; `EmitLoopCycle` checks `IsDispatchAlreadyInLedger` and explicitly cites "a
  save/reload mid-cycle, a Rewind, or a double-tick can re-present the SAME cycleId"
  (`RouteOrchestrator.cs:~1785`).
- **Routes are revalidated on every supersede bump** (`RouteStore.RevalidateSources`,
  `ParsekScenario.cs:252-263`): each `SourceRef` checked against ERS + a field fingerprint →
  `MissingSourceRecording` / `SourceChanged` → not ghost-driving, no dispatch.
- **Backing-mission selection freezes to creation-time members** (M-MIS-9,
  `plan-mmis9-route-branch-freeze.md`): post-creation branches (re-fly fork, switch-fly
  continuation) do not silently join the route's loop span / delivery cadence.
- **Stated design position (authoritative):** §2.4 item 11, §10.6, §13.4–13.5 say dispatches and
  deliveries are ledger-backed timeline events; reverts invalidate abandoned-timeline dispatches
  via epoch isolation + tombstone; stock quicksave/load restores stock vessels; **"if those
  modules are not implemented for a mutation path, that route effect path must stay disabled."**
  The implemented reality (observe-only `RouteModule` + quicksave restore for physical + FundsModule
  recalc for funds) must be checked against this stated contract.

## 3. The determinism axes to investigate

Each axis lists the question, the acceptance criterion (what "deterministic / paradox-free" means
for it), and the primary files/symbols to read.

### Axis A — Route-definition lifecycle across a rewind ("disable before it existed")
- **Q:** Is the route *definition* (in `RouteStore`, serialized in the `ROUTES` node of
  `ParsekScenario`) reverted when the player rewinds to before the route was created? Is the
  Rewind-to-Separation quicksave a full `GamePersistence` save that captures the `ROUTES` node and
  the route's `LoopAnchorUT` / `LastObservedLoopCycleIndex` at RP time?
- **Acceptance:** rewinding to RP at UT=T restores `RouteStore` exactly to its UT=T state — a route
  created at UT>T is absent; a route created at UT<T is present with its RP-time activation/cycle
  state. No route exists in a past where it had not been created.
- **Read:** `RewindInvoker.cs` (Restore/Strip/Activate; pre-load reconciliation bundle),
  `RewindPointAuthor.cs`, `RewindPoint.cs`, `ParsekScenario.cs` OnSave/OnLoad for `ROUTES`,
  `RouteStore.LoadRoutesFrom` / save path, `RouteCodec.cs`.

### Axis B — Dispatch clock across a rewind ("trigger in the past" / "re-enable after")
- **Q:** After a rewind, are `LoopAnchorUT` and `LastObservedLoopCycleIndex` restored to RP-time
  values? Does `TryComputeSpanLoopUT` early-return (no fire) when `nowUT < PhaseAnchorUT` (i.e. the
  route cannot fire before its activation)? Re-flying forward, does the clock re-fire the same
  cycles (same `cycleIndex` → same `cycleId`)?
- **Acceptance:** the dispatch clock is a pure function of `(LoopAnchorUT, cadence/schedule,
  nowUT)`; identical time advance reproduces identical fire UTs and cycleIds; no fire below
  `LoopAnchorUT`; activation reset of `LastObservedLoopCycleIndex` to −1 does not cause a
  double-fire under ELS.
- **Read:** `RouteLoopClock.cs`, `GhostPlaybackLogic.TryComputeSpanLoopUT`,
  `RouteOrchestrator.ProcessOneRoute` / `EmitLoopCycle` / the `LastObservedLoopCycleIndex` snap,
  `Route.cs` (`LoopAnchorUT`, `LastObservedLoopCycleIndex`, activate path), `RouteCodec.cs`.

### Axis C — Economic (funds) determinism across a rewind+re-fly+commit (THE CRUX)
- **Q:** When the player rewinds past route dispatches and re-flies+commits, are the abandoned-future
  route funds actions (KSC charge, recovery credit) **removed/superseded/tombstoned**, or do they
  persist and double-count? Are route `GameAction`s scoped to a recording (so the supersede walk
  carves them out via ERS/ELS) or free-standing (`RecordingId` cleared)? Is `Ledger.Actions`
  restored from the rewind quicksave (future route rows simply vanish) or preserved-and-reconciled?
- **Acceptance:** after rewind+re-fly+commit, `EffectiveState.ComputeERS/ComputeELS` and a
  `RecalculateAndPatch` reconstruct funds with each route cycle counted exactly once for the
  *surviving* timeline; no abandoned-future dispatch contributes; no surviving dispatch is dropped.
- **Read:** `SupersedeCommit.cs`, `EffectiveState.cs` (`ComputeERS`/`ComputeELS`/supersede walk),
  `GameActions/FundsModule.cs`, `GameActions/GameAction.cs` (RouteId / recordingId / scope fields),
  `GameActions/Ledger.cs` + `RecalculationEngine` / `LedgerOrchestrator`, `RewindInvoker.cs`
  (ledger handling on Restore), `LoadTimeSweep.cs`, `RouteModule.cs` (observe-only boundary).

### Axis D — Physical (cargo/inventory) determinism across a rewind
- **Q:** Are the live physical effects (origin debit, pickup, delivery) guaranteed to be covered by
  the quicksave restore for every touched vessel? What about a destination/endpoint vessel that was
  created *after* the RP (so it is absent post-rewind)? Can a physical effect fire to a vessel that
  is not in the restored `.sfs`, leaving an un-revertable mutation?
- **Acceptance:** every live physical mutation a route makes is to a vessel that is part of the same
  `.sfs` the rewind restores from, so the restore reverts it; endpoints that did not exist at RP
  time resolve to EndpointLost (no orphan cargo); no physical mutation survives a rewind that should
  have undone it.
- **Read:** `Logistics/LiveDeliveryWriters.cs`, `LiveOriginDebitWriters.cs`,
  `LiveInventoryPickupWriter.cs`, `LiveDeliveryCapacityProbe.cs`, `RouteEndpointResolver.cs`,
  `RouteOrchestrator` emit paths (`EmitDispatchDebit`, `EmitPickupHalf`, `ApplyDelivery`),
  `RewindInvoker.cs` (which `.sfs` is restored, and what "Strip" removes).

### Axis E — Recurrent / interplanetary specifics (the user's Duna example)
- **Q:** The recurrent cadence for an inter-body route is the synodic / re-aim schedule
  (`RouteLoopClock` schedule passthrough). For v0 only same-body routes ship; inter-body is the
  documented seam (M5). Does the determinism architecture extend to the recurrent inter-body case?
  Does the re-aim schedule depend on any live celestial state that could differ after a rewind
  (it should be a pure function of UT)?
- **Acceptance:** the recurrence is a pure function of `(LoopAnchorUT, synodic schedule, nowUT)`;
  bodies are deterministic functions of UT; the user's Duna-every-2-years case re-fires identically
  after a rewind, OR the gap (if inter-body routes are not yet shippable) is named explicitly.
- **Read:** `RouteLoopClock.cs` (schedule passthrough), `MissionPeriodicity.cs`,
  `design-mission-periodicity.md`, the M5 deferral in the logistics design doc §19.4, scope gates in
  `RouteBuilder` / `RouteAnalysisEngine` that currently restrict to same-body.

### Axis F — Paradox surfaces / known gaps (the report's risk register)
- **Q:** Enumerate the concrete failure modes:
  - Stated design says un-reversed mutation paths "must stay disabled," yet physical effects are
    *enabled* and rely on quicksave restore rather than a route ledger module — is that sound or a
    contract divergence?
  - Reverts that do NOT go through a rewind quicksave (a non-rewind discard,
    `PreserveIrreversibleLiveGameplayOnDiscard`) — do they leave physical route effects un-reverted?
  - RP granularity: rewind targets are separation events, not arbitrary UTs. Can a route fire
    between the last RP and the desired rewind target such that the physical effect is not in any
    RP's `.sfs`?
  - Recovery-credit deferral straddling a rewind boundary (`PendingRecoveryCreditCycleId` armed
    before, flushed after).
  - `LastObservedLoopCycleIndex` (codec-restored) vs ledger ELS keys (restored or not) diverging →
    double-fire or stuck-skip.
  - A route firing in the abandoned future to a vessel that is itself superseded/removed.
- **Acceptance:** each surface is either shown safe (with the mechanism that makes it safe) or
  listed as a concrete gap with severity + a recommended fix.
- **Read:** outputs of Axes A–E plus `MergeDialog.ReFlyDiscard.cs`,
  `LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard`, `todo-and-known-bugs.md`.

## 4. Method

1. One focused reader per axis (A–F), each producing a structured finding: the mechanism as
   implemented (with `file:line` evidence), whether it meets the acceptance criterion, and any gap.
2. **Adversarially verify** the load-bearing determinism claims — especially Axis C (does the ledger
   actually neutralize abandoned-future route funds rows?) and Axis D (is every physical mutation
   inside the restored `.sfs`?). A claim of "deterministic" must cite the exact code path that makes
   it so, not an assumption.
3. Synthesize into a report: the conceptual answer to the four sub-questions (§1), a determinism
   verdict per axis, a risk register, and concrete recommendations (what is sound, what is a gap,
   what to disable/guard, what to build for the inter-body recurrent case).

## 5. Deliverable

`docs/dev/research/logistics-time-rewind-compat-report.md` — findings + recommendations. No
production code changes in this task (investigation only); recommendations may propose follow-up
work items for `todo-and-known-bugs.md`.

## 6. Out of scope

- Implementing any fix (the task is investigate + report).
- The map/TS render rewrite in flight (ghost *visual* replay is orthogonal to dispatch firing and
  economic effect; note it only where a render-ownership change could touch route ghost-driving).
- Non-route timeline determinism (kerbal death tombstones, contract supersede) except where a route
  effect rides those same ledger mechanisms.
