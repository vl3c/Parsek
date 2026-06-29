# Investigation Plan: Logistics (Supply Routes) ↔ Time-Rewind Determinism

*Status: investigation plan (pre-report), REVISED after a clean-context plan review. Produced
for the task "is there a fundamental compatibility issue between logistics and time rewind, and
how do recurrent routes behave across a rewind without creating time-travel paradoxes." This
document scopes the investigation; the deliverable is a separate report + recommendations note.
No production code is changed by this plan or the report.*

*Revision note: the first draft's Axis C hypothesized that the supersede / ERS walk carves out
abandoned-future route ledger rows via a recording-id scope. The review proved that is
structurally impossible (route `GameAction`s are free-standing, null `RecordingId`) and surfaced
the REAL mechanism — a UT-blind, counter-keyed dispatch dedup over restored-future ledger rows
against reverted route counters — which is simultaneously the funds-safety story AND a suspected
physical-cargo paradox. The axes below are rewritten around that mechanism. Every "KNOWN" fact
tagged (verify) is a review claim the investigation must re-confirm with its own reads, not
accept on faith.*

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

## 2. What is already known (verify where flagged)

These are established facts the investigation builds on. The ones tagged **(verify)** are
load-bearing review claims the investigation must re-confirm with its own direct reads before
relying on them; the rest were confirmed in the first-pass read.

**Firing model**
- **Routes fire LIVE on a loop clock; they are NOT a recorded dispatch replay.** The dispatch
  *phase* is owned by `RouteLoopClock` (crossing detector over
  `GhostPlaybackLogic.TryComputeSpanLoopUT`, body in `GhostPlaybackLogic.SpanClock.cs`) +
  `Route.LastObservedLoopCycleIndex`, driven by live game UT.
  `RouteOrchestrator.Tick(currentUT)` runs ~1 Hz from `ParsekScenario` Update **in all scenes**
  (`ParsekScenario.cs:924`, gated by `TickIntervalSec`), so routes can fire while the player is
  not in the route's flight scene.
- **`cycleId` is COUNTER-keyed, not UT-keyed** (verify): `"cycle-" + (route.CompletedCycles +
  route.SkippedCycles)` (`RouteOrchestrator.cs` ~771/999/2710/3096). This is the linchpin of the
  whole rewind story and must be confirmed verbatim.

**Two effect surfaces, two reversal mechanisms**
- *Funds* (KSC dispatch charge on `RouteCargoDebited.RouteKscFundsCost`; recovery credit) flow
  through `FundsModule` in the recalc walk. **These two funds surfaces have DIFFERENT neutralizers
  and must be analyzed separately** (verify): the dispatch charge is applied unconditionally with
  no `Effective`/tombstone gate (`FundsModule.cs:~538-544`) and is reversed only by a cutoff
  recalc dropping `UT > cutoff` rows (`RecalculationEngine.cs:~152-165`); the recovery credit
  relies indirectly on the *source recording's* recovery `FundsEarning` rows being tombstoned
  (`SupersedeCommit.cs:~1864-1867`).
- *Physical* (origin cargo debit, per-window pickup, endpoint delivery to live vessel
  tanks/inventory) are applied **live at emit time** by `RouteOrchestrator` and are reverted by
  **the rewind quicksave restore** (the `.sfs` reload), NOT by the recalc walk. `RouteModule` is
  **observe-only** (`T-ROUTEMODULE-OBSERVE`): it counts rows and mutates nothing.

**The rewind asymmetry (THE seam the paradox sits on) (verify)**
- The Rewind-to-Separation quicksave is a full `GamePersistence.SaveGame` (`RewindPointAuthor.cs:
  ~488`) and DOES capture the `ParsekScenario` `ROUTES` node + route loop state (`LoopAnchorUT`,
  `LastObservedLoopCycleIndex`, `CompletedCycles`, `SkippedCycles`) at RP time.
- **On Restore, route DEFINITIONS/COUNTERS revert to RP time but the LEDGER does not.**
  `RouteStore` is NOT in `ReconciliationBundle` (`ReconciliationBundle.cs:19-72`), so it reverts
  via the loaded `.sfs` ROUTES node. But `ReconciliationBundle.Capture()` snapshots the full
  *pre-rewind* `Ledger.Actions` BEFORE `LoadGame`, and `Restore()` does `Ledger.Clear()` +
  `Ledger.AddActions(bundle.Actions)` (`ReconciliationBundle.cs:~114-120,194-196`) — so the
  abandoned-future route ledger rows are PRESERVED, they do not vanish on rewind.
- **Route `GameAction`s are FREE-STANDING (null `RecordingId`)** (verify): every route action is
  built with `RouteId`/`RouteCycleId`/`UT` only (`RouteOrchestrator.cs` emit sites; a
  `RecordingId=` grep returns zero hits). The supersede/tombstone walk gates entirely on
  `RecordingId` (`TombstoneAttributionHelper.InSupersedeScope` returns false for empty
  `RecordingId`, `cs:~36`; `SupersedeCommit.CommitTombstones` only considers gated actions).
  **Therefore supersede/ERS/tombstone can NEVER carve out an abandoned-future route row.**
- **Two recalc moments with different cutoffs** (verify): at rewind a recalc runs with a CUTOFF at
  the rewind UT (drops `UT > cutoff` rows → funds revert to RP time); at re-fly-commit a recalc
  runs with cutoff = `double.MaxValue` (`RewindInvoker.cs:~929`), re-applying the FULL ledger
  including the still-present abandoned-future rows.
- `RouteStore.RevalidateSources` runs on the supersede-version bump (`ParsekScenario.cs:263`,
  itself invoked from inside `ReconciliationBundle.Restore`, `cs:~212`) and OnLoad (`:3344`):
  each `SourceRef` checked against ERS + a field fingerprint → `MissingSourceRecording` /
  `SourceChanged` → not ghost-driving, no dispatch.
- Backing-mission selection freezes to creation-time members (M-MIS-9,
  `plan-mmis9-route-branch-freeze.md`): post-creation branches do not silently join the route's
  loop span / delivery cadence.

**The suspected paradox (the spine of the report; prove or refute with a worked example)**
On rewind, RouteStore counters revert (so a re-flown crossing reproduces the SAME `cycleId`) while
the abandoned-future `RouteDispatched` rows survive in the ledger UN-tombstoned (null
`RecordingId`). On re-fly, `EmitLoopCycle` calls `IsDispatchAlreadyInLedger` →
`EffectiveState.ComputeELS()`, which filters **only by tombstones, never by UT/timeline**
(`EffectiveState.cs:~1298-1328`) → the reproduced `cycleId` collides with the surviving row → the
re-flown dispatch is **SUPPRESSED** (`RouteOrchestrator.cs:~3540-3551`). Consequence: **funds are
charged exactly once (no double-charge — good), but the physical cargo the quicksave reverted is
never re-delivered (the dispatch that would deliver it is dedup-suppressed) → "funds spent into an
abandoned future, no goods on the surviving timeline."** This is the concrete time-travel paradox
the investigation exists to confirm or refute, and it must be traced through ONE real cycle with
the actual `GameAction` list before/after, not asserted abstractly.

## 3. The determinism axes to investigate

Each axis: the question, the acceptance criterion, and the primary files/symbols to read.

### Axis A — Route-definition lifecycle across a rewind ("disable before it existed")
- **Q:** Is the route *definition* reverted when the player rewinds to before the route was
  created? State the **RouteStore-vs-Ledger asymmetry** explicitly (RouteStore reverts via the
  `.sfs`; `Ledger.Actions` is preserved by the bundle) — this one sentence is the seam the whole
  paradox sits on. Confirm the `Restore → BumpSupersedeStateVersion → RevalidateSources` path runs
  on every rewind.
- **Acceptance:** rewinding to RP at UT=T restores `RouteStore` exactly to its UT=T state (a route
  created at UT>T is absent; a route created at UT<T is present with RP-time counters); the
  asymmetry with the preserved ledger is named with `file:line`; revalidation re-runs against the
  restored ERS.
- **Read:** `RewindInvoker.cs`, `ReconciliationBundle.cs` (Capture/Restore; which fields are in the
  bundle), `RewindPointAuthor.cs`, `RewindPoint.cs`, `ParsekScenario.cs` (`SaveRoutesTo`/
  `LoadRoutesFrom`, ROUTES node), `RouteStore` save/load, `RouteCodec.cs`.

### Axis B — Dispatch clock + cycleId reproduction across a rewind ("trigger" / "re-enable")
- **Q:** After a rewind, do `LoopAnchorUT` and the route counters restore to RP-time values? Verify
  `LoopAnchorUT` is **game UT, not wall-clock** (`RouteOrchestrator.cs:205`). Resolve the effective
  `PhaseAnchorUT` precisely: `RouteOrchestrator.cs:197-199` says it floors to `spanEnd` while
  `MissionLoopUnitBuilder` uses `Math.Max(anchor, spanEnd)` — determine, for both a create-Active
  route (anchor seeded to recording `StartUT`) and a Pause→Activate route (anchor = activation UT),
  what the cadence grid is actually pinned to. Does `TryComputeSpanLoopUT` early-return (no fire)
  below the phase anchor? **The linchpin:** is the re-flown `cycleId` byte-identical to the
  abandoned-future `cycleId` (pure function of reverted counters), and is the ELS dedup that reads
  it sound given `ComputeELS` does not filter by UT/timeline?
- **Acceptance:** the dispatch grid is a pure function of `(effective PhaseAnchorUT, cadence/
  schedule, nowUT)` independent of wall-clock; no fire below the anchor; the re-flown cycleId
  reproduces identically AND the UT-blind ELS dedup over it is shown to be correct-by-design rather
  than accidental (i.e. the report states exactly why the collision is safe for funds and unsafe
  for cargo).
- **Read:** `RouteLoopClock.cs`, `GhostPlaybackLogic.SpanClock.cs` (`TryComputeSpanLoopUT`),
  `MissionLoopUnitBuilder` (anchor floor), `RouteOrchestrator.ProcessOneRoute`/`EmitLoopCycle`/the
  `LastObservedLoopCycleIndex` snap + `IsDispatchAlreadyInLedger`, `Route.cs` (LoopAnchorUT,
  counters, activate path), `RouteCodec.cs`.

### Axis C — Economic (funds) determinism across rewind+re-fly+commit (THE CRUX)
- **Q:** Trace ONE concrete route cycle through create → fire (abandoned future) → rewind → re-fly
  → commit, reading the actual `Ledger.Actions` at each step. For BOTH funds surfaces separately:
  (1) the **dispatch KSC charge** — confirm it has no tombstone/`Effective` gate
  (`FundsModule.cs:~538-544`), is dropped only by the cutoff recalc at rewind, and is RE-INCLUDED
  by the `double.MaxValue` commit recalc; determine whether the surviving abandoned-future charge
  is the one that ends up applied and whether that is counted exactly once. (2) the **recovery
  credit** — confirm it zeroes only via the source recording's recovery `FundsEarning` rows being
  tombstoned, and check that coupling actually holds when the source subtree is superseded by the
  re-fly.
- **Acceptance:** a worked `GameAction`-list example showing each funds surface is counted exactly
  once for the SURVIVING timeline, with the exact code path that enforces it; any case where a
  surface double-counts, drops, or charges for an abandoned-only event is flagged with severity.
- **Read:** `ReconciliationBundle.cs`, `RecalculationEngine.cs` (cutoff handling),
  `RewindInvoker.cs` (the two recalc invocations), `EffectiveState.cs`
  (`ComputeERS`/`ComputeELS` — confirm no UT filter), `GameActions/FundsModule.cs`,
  `GameActions/GameAction.cs` (RouteId / RecordingId fields), `SupersedeCommit.cs`,
  `TombstoneAttributionHelper.cs`, `RouteModule.cs` (observe-only boundary), `RouteOrchestrator`
  `IsDispatchAlreadyInLedger`.

### Axis D — Physical (cargo/inventory) determinism + the dedup-suppression paradox
- **Q:** Are the live physical effects covered by the quicksave restore for every touched vessel?
  Then the decisive question raised by Axis B/C: when the re-flown dispatch is **dedup-suppressed**
  by the surviving abandoned-future row, is the physical cargo (which the quicksave reverted)
  **ever re-applied**? If not, confirm the "funds spent, no goods" paradox with the exact emit path
  that is skipped on the replay branch. Also: an endpoint vessel created AFTER the RP (absent
  post-rewind) → EndpointLost / no orphan cargo?
- **Acceptance:** every live physical mutation is to a vessel inside the restored `.sfs` (so the
  restore reverts it); the re-fly either re-applies the physical effect on the surviving timeline
  OR the report names the suppression-without-re-delivery as a concrete paradox with severity;
  post-RP endpoints resolve to EndpointLost with no orphan cargo.
- **Read:** `Logistics/LiveDeliveryWriters.cs`, `LiveOriginDebitWriters.cs`,
  `LiveInventoryPickupWriter.cs`, `LiveDeliveryCapacityProbe.cs`, `RouteEndpointResolver.cs`,
  `RouteOrchestrator` emit paths (`EmitDispatchDebit`, `EmitPickupHalf`, `ApplyDelivery`, and the
  `IsDispatchAlreadyInLedger` replay-skip branch — what it does and does NOT re-apply),
  `RewindInvoker.cs`/`ReconciliationBundle.cs` (restore scope; "Strip").

### Axis E — Recurrent / interplanetary specifics (the user's Duna example)
- **Q:** Confirm inter-body routes are NOT yet shippable (scope-gated to same-body) so the literal
  Duna-every-2-years route cannot yet be created — making this case a *documented deferral*, not a
  live bug. Verify the recurrence is a pure function of `(PhaseAnchorUT, synodic schedule, nowUT)`
  and that the re-aim schedule depends only on UT (deterministic bodies), so the architecture would
  extend cleanly once inter-body lands. Keep this axis LIGHT relative to B/C/D.
- **Acceptance:** the same-body-only gate is cited (`RouteBuilder`/`RouteAnalysisEngine`); the
  recurrence is shown UT-pure; the report states whether the determinism gaps in C/D would or
  would not also bite the inter-body case (they would — the firing model is identical).
- **Read:** `RouteLoopClock.cs` (schedule passthrough), `MissionPeriodicity.cs`,
  `design-mission-periodicity.md`, M5 deferral in the logistics design doc §19.4, same-body gate in
  `RouteBuilder`/`RouteAnalysisEngine`.

### Axis F — Paradox surfaces / known gaps (the report's risk register)
- **Q:** Enumerate and classify the concrete failure modes:
  - The C/D paradox (funds-once, cargo-never-re-delivered) — primary.
  - Stated design (§2.4 #11, §10.6, §13.4) says un-reversed mutation paths "must stay disabled,"
    yet physical effects are ENABLED and rely on quicksave restore, not a route ledger module — is
    that a contract divergence?
  - **Multiple successive rewinds**: each rewind re-captures the then-current `Ledger.Actions`
    (including prior abandoned futures); does stacking accumulate orphan route rows / compounding
    paradox?
  - **Background / on-rails firing**: does `ProcessOneRoute` run for routes whose vessels are
    unloaded? Can a BG-fired cycle be stranded by a rewind (physical effect on an unloaded vessel,
    or no physical effect at all but a ledger row)?
  - **Rewind landing BETWEEN a dispatch and its deferred recovery-credit / delivery**
    (`PendingRecoveryCreditCycleId`): the pending arm is route state (reverts via quicksave) while
    the emitted dispatch row persists (bundle) — trace the mismatch.
  - Non-rewind reverts (`PreserveIrreversibleLiveGameplayOnDiscard`, `MergeDialog.ReFlyDiscard`):
    do they leave physical route effects un-reverted?
  - RP granularity: rewind targets are separation events, not arbitrary UTs — can a physical effect
    fall outside every RP's `.sfs`?
- **Acceptance:** each surface is shown safe (with the mechanism) or listed as a concrete gap with
  severity + a recommended fix, in the risk-register table format (§5).
- **Read:** outputs of Axes A–E plus `MergeDialog.ReFlyDiscard.cs`,
  `LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard`, `BackgroundRecorder.cs` (route
  relevance), `todo-and-known-bugs.md`.

## 4. Method

1. One focused reader per axis (A–F), each producing a structured finding: the mechanism as
   implemented (with `file:line` evidence), whether it meets the acceptance criterion, and any gap.
2. **Adversarially verify** the load-bearing claims, explicitly targeting BOTH (a) Axis C/D's
   suspected paradox and (b) §2's funds-reversal facts (the dispatch-charge-has-no-tombstone-gate
   claim and the ledger-preserved-not-restored claim). The verifier MUST produce a concrete worked
   example: one route, one dispatch, one rewind, one re-fly, with the actual `Ledger.Actions`
   (RouteId, cycleId, UT, KscFundsCost) and the live destination tank value BEFORE and AFTER each
   step — not a prose verdict. A "deterministic" or "paradox" claim is accepted only when the exact
   code path that makes it so is cited.
3. Synthesize into a report: the conceptual answer to the four sub-questions (§1), a determinism
   verdict per axis, the risk register, and concrete recommendations.

## 5. Deliverable

`docs/dev/research/logistics-time-rewind-compat-report.md` — findings + recommendations. It MUST
include a risk-register table with columns **{surface, mechanism, deterministic? (Y/N/partial),
evidence (file:line), severity, recommended fix}** so the primary paradox cannot be lost in prose.
No production code changes in this task (investigation only); recommendations may propose follow-up
work items for `todo-and-known-bugs.md`.

## 6. Out of scope

- Implementing any fix (the task is investigate + report).
- The map/TS render rewrite in flight (ghost *visual* replay is orthogonal to dispatch firing and
  economic effect; note it only where a render-ownership change could touch route ghost-driving).
- Non-route timeline determinism (kerbal death tombstones, contract supersede) except where a route
  effect rides those same ledger mechanisms (the recovery-credit↔source-tombstone coupling in
  Axis C IS in scope because a route effect depends on it).
