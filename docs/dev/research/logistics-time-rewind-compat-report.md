# Report: Logistics (Supply Routes) ↔ Time-Rewind Determinism

*Status: investigation report + recommendations. Deliverable of the task "is there a fundamental
compatibility issue between logistics and time rewind, and how do recurrent routes behave across a
rewind without time-travel paradoxes." Investigation only — no production code was changed. Method:
six forensic axis readers (A–F) over the live source, then a dedicated adversarial refutation pass.
Companion: `logistics-time-rewind-investigation-plan.md` (the scoped plan + its clean-context
review). Every claim below is anchored to `file:line` confirmed by direct reads.*

---

## 0. TL;DR

- **The firing model is "set-it-on and it works live," NOT "record and replay."** A route does not
  replay a recorded dispatch stream. Its *visual ghost* is replayed by the loop engine, but its
  *dispatch firing* is driven live by a UT-pinned loop clock (`RouteLoopClock` +
  `RouteOrchestrator.Tick`, ~1 Hz, in all scenes), and its *economic effects* are recorded as
  free-standing ledger rows for funds reconstruction. As live UT re-advances after a rewind, the
  route re-fires on the same grid. This is the right model and it is mostly deterministic.
- **"Disable before it existed" works correctly.** A route created after the rewind point is absent
  after the rewind (its definition reverts with the quicksave `.sfs`), and the loop clock cannot
  fire below its phase anchor. A route literally cannot act before it was created/activated.
- **There is ONE real, confirmed determinism bug — a genuine time-travel paradox** on the *physical
  cargo* surface: after rewinding past a dispatch and re-flying, **funds are charged but the goods
  are never re-delivered ("funds spent, no goods")**. Root cause: route ledger rows are free-standing
  and timeline-blind, so the abandoned-future rows survive the rewind un-neutralized, and the
  UT-blind dispatch dedup then suppresses the re-flown cycle that would have re-applied the live
  cargo. The adversarial refutation pass tried six escape hatches and confirmed the paradox stands.
- **Funds are never double-charged, but they are not actually timeline-correct either:** the single
  surviving charge can be a *phantom* charge/credit for an abandoned-future cycle the surviving
  timeline never reproduced.
- **The literal "Duna every 2 years" route cannot be created yet** (inter-body routes are
  scope-deferred to milestone M5), so the flagship worry is a *future* exposure — but the firing +
  economic model is identical, so the same paradox would bite it the moment inter-body lands.
- **Recommended fix (one change closes most of it): purge/retire the free-standing route ledger
  rows whose `UT > rewindCutoff` at rewind**, so the live re-fly re-creates them deterministically
  and re-applies funds *and* cargo symmetrically. Plus: fix the non-rewind-discard leak separately,
  and update the stale design-doc contract.

---

## 1. The two systems, in one paragraph each

**Supply routes (logistics).** A committed Supply Run (a flown, docked, undocked, cargo-transferring
recording chain) can be turned into a Route. A Route renders its backing mission segment as a
looping ghost and, on each loop-clock crossing of the recorded dock phase, *dispatches*: it charges
KSC funds (career), physically debits the origin depot, and physically delivers cargo to a live
destination vessel. The dispatch *phase* is owned by `RouteLoopClock` (a crossing detector over
`GhostPlaybackLogic.TryComputeSpanLoopUT`) + the persisted counter `Route.LastObservedLoopCycleIndex`,
driven by live game UT from `RouteOrchestrator.Tick(currentUT)`, which runs ~1 Hz from
`ParsekScenario.Update` **in all scenes** (`ParsekScenario.cs:924`). Each dispatch/delivery is a
ledger `GameAction` (`RouteDispatched` / `RouteCargoDebited` / `RouteCargoDelivered` /
`RouteRecoveryCredited` / …) stamped with `RouteId` + a counter-keyed `RouteCycleId`. The economic
ledger module `RouteModule` is **observe-only** — it counts rows and mutates nothing.

**Time rewind (Rewind-to-Separation / re-fly).** The player rewinds to a RewindPoint — a full
`GamePersistence.SaveGame` quicksave authored at a separation event (`RewindPointAuthor.cs:488`).
The flow snapshots the *current* in-memory Parsek state into a `ReconciliationBundle`, loads the RP
`.sfs` (reverting the live world), then `Restore()`s the bundle, strips non-selected vessels,
activates the rewound vessel, and arms a re-fly provisional. The player re-flies forward; on commit,
`SupersedeCommit` marks the abandoned subtree superseded and tombstones in-scope recording-attributed
ledger actions, and a final `RecalculateAndPatch(double.MaxValue)` rewrites the career scalars from
the ledger.

---

## 2. The load-bearing asymmetry (the seam the bug sits on)

A rewind reverts two kinds of state by two different mechanisms, and route data straddles them:

| State | Reverted by | Result after rewind |
|---|---|---|
| Live world (vessel tanks, inventory, funds scalar) | the RP `.sfs` reload (`GamePersistence.LoadGame`, `RewindInvoker.cs:608`) | reverted to RP time |
| Route **definitions + counters** (`CompletedCycles`, `SkippedCycles`, `LoopAnchorUT`, `LastObservedLoopCycleIndex`, `PendingRecoveryCreditCycleId`) | the RP `.sfs` ROUTES node (`RouteStore` is **NOT** in `ReconciliationBundle`, `ReconciliationBundle.cs:19-72`; reloaded by `RouteStore.LoadRoutesFrom`, `ParsekScenario.cs:3343`, which clears+rebuilds) | reverted to RP time |
| **`Ledger.Actions`** (incl. route rows) | the **`ReconciliationBundle`**, which `Capture()`s the *pre-rewind* ledger before `LoadGame` and `Restore()`s it via `Ledger.Clear()` + `AddActions(bundle.Actions)` (`ReconciliationBundle.cs:114-120, 194-196`) | **preserved** — abandoned-future rows survive |

So after a rewind the route *thinks* it has done nothing since RP time (counters reverted), the
*world* has nothing since RP time (tanks reverted), but the *ledger* still holds the abandoned-future
route rows. That preserved-ledger / reverted-everything-else asymmetry is the engine of the paradox.

For recording-attributed actions this asymmetry is fine: the supersede/tombstone walk marks
abandoned-subtree actions inactive (it gates on `RecordingId`). **Route actions are free-standing —
they carry `RecordingId = null`** (zero `RecordingId=` writes in `RouteOrchestrator.cs`;
`GameAction.RecordingId` defaults null, `GameAction.cs:220`), so `TombstoneAttributionHelper.InSupersedeScope`
returns false for them (`TombstoneAttributionHelper.cs:36`) and the tombstone walk can **never**
retire them. And `EffectiveState.ComputeELS()` — the input to both the recalc and the dispatch dedup
— filters by tombstoned `ActionId` only, **never by UT/timeline** (`EffectiveState.cs:1336-1347`).
The abandoned-future route rows are therefore permanently un-tombstonable and permanently
un-time-filtered.

---

## 3. Direct answers to the four sub-questions

**Q1 — "Trigger the route at past moments after a rewind."** The route is *not* re-triggered by
replaying recorded past dispatches. It re-fires *live* on its UT-pinned loop grid as the player
re-advances time past the activation/dock phase. The grid is a pure function of
`(effective PhaseAnchorUT, cadence, nowUT)` with no wall-clock or RNG input
(`GhostPlaybackLogic.SpanClock.cs:1092-1104`), so the firing is deterministic. (For a create-Active
route the grid is pinned to the fixed recorded `spanEnd`; for a Pause→Activate route it is pinned to
the live activation UT — see §6, axis B caveat.)

**Q2 — "Disable it before it existed." ✅ CORRECT.** A route created after the RP is simply absent
after the rewind (`RouteStore.LoadRoutesFrom` clears and rebuilds from the RP node, which has no such
route). A route that exists but is rewound below its phase anchor does not fire: `TryComputeSpanLoopUT`
early-returns no-fire for `currentUT < phaseAnchorUT` (`GhostPlaybackLogic.SpanClock.cs:1092-1093`).
A route cannot act before it was created/activated.

**Q3 — "Re-enable after." ✅ for firing + ⚠️ broken for effects.** Once live UT crosses the phase
again, the route re-fires deterministically (counters reverted, cycleId reproduces byte-identically).
But the *effects* it should re-apply are where the bug lives (Q4 / §4).

**Q4 — "Record-and-replay, or set-on-and-it-works?" → SET-ON-AND-IT-WORKS (live), with a recorded
economic ledger for funds.** The route's ghost is replayed (visual only); the dispatch fires live;
the economic effect is recorded as ledger rows so funds can be reconstructed by recalc. This hybrid
is *almost* the right deterministic model — but it is incomplete: the physical cargo half has no
recalc reconstruction (it is a live side-effect reverted only by the quicksave), and the abandoned
ledger rows are never cleaned up, so the "it just works on re-fly" property holds for funds-accounting
but breaks for physical cargo.

---

## 4. The primary paradox (confirmed, with worked example)

**Scenario.** Career, KSC-origin route R, source recording S, dispatch cost 5000 funds, delivers
100 LiquidFuel to live destination station D, recorded dock UT 2000, RewindPoint RP at UT 1500.

| Step | D tank LF | Route rows in `Ledger.Actions` | `CompletedCycles` |
|---|---|---|---|
| Create | 0 | — | 0 |
| **Fire cycle-0 @ UT 2000** (abandoned future) | **0 → 100** (`LiveDeliveryWriters` `pr.amount += 100`, `:251`) | `RouteDispatched{R, cycle-0, recId=null}`, `RouteCargoDebited{…, KscFundsCost=5000}`, `RouteCargoDelivered{…}` | 1 |
| **Rewind to RP @ 1500** | **100 → 0** (`.sfs` reload reverts D) | rows **survive** (bundle `Capture`→`Restore`); counters revert to 0 via `.sfs` ROUTES | **0** |
| **Re-fly past UT 2000** | **stays 0** | `cycleId` reproduces `cycle-0`; `IsDispatchAlreadyInLedger` finds the surviving row (no UT compare, `:3540-3551`) → `EmitLoopCycle` returns at `:1818` **before** any writer → emits nothing, bumps counter | 1 |
| **Commit recalc** `double.MaxValue` (`RewindInvoker.cs:929`) | **stays 0** (`RouteModule` observe-only) | `FundsModule.ProcessRouteCargoDebited` charges 5000 **once**, unconditional (`FundsModule.cs:538-548`) | — |

**Surviving-timeline result: funds −5000 (charged once), destination tank = 0 (goods never
re-delivered). Paradox.**

**Why funds are "single-counted" but still not timeline-correct.** The dispatch dedup guarantees *at
most one* `RouteCargoDebited` charge per cycleId, so there is no double-charge. But the charge that
lands is the *abandoned-future* row, not a re-emitted surviving-timeline row. If the re-fly is
identical it happens to equal the right amount; if the re-fly *diverges* (player pauses the route,
flies elsewhere, or supersedes the source so the route flips `MissingSourceRecording` and never
re-fires) the surviving row **still** charges 5000 for a dispatch the surviving timeline never
performed — a phantom charge — and still never re-delivers. The refutation pass confirmed this branch
explicitly.

**Two affected populations.**
- *KSC-origin (career):* funds charged, no goods → "funds spent, no goods" (severity **high**).
- *Non-KSC origin:* the `RouteCargoDebited` row carries 0 funds cost (`RouteOrchestrator.cs:2901`,
  `FundsModule` early-returns), so its only effect was the live origin-depot drain. Reverted by the
  quicksave, never re-applied → the cargo the route should have shipped is **still sitting at the
  origin depot**, yet `CompletedCycles` advanced as if it shipped (severity **medium**: silent
  physical no-op + counter desync).

**Cadence drift.** The replay branch does `CompletedCycles += 1` even when it emits nothing
(`:1813`), so after a rewind the post-rewind cycleId sequence is offset from what a never-rewound run
would produce — the recurrence is not bit-identical even setting cargo aside.

**Adversarial refutation outcome: CONFIRMED-STANDS.** Six escape hatches were tried and each fails:
no UT/cutoff filter on the dedup or `ComputeELS`; no route-row purge/re-key at rewind (grepped
`RewindInvoker` / `PostLoadStripper` / `LoadTimeSweep` / `RecordingRewindRetirement`, zero matches);
counters are instance fields that *do* revert (so the cycleId collides); no re-delivery on the
replay branch or any load-time reconcile; `RevalidateSources` does not reset counters and the
source-superseded branch still charges; ELS cache-version bumps only rebuild the cache (still
including non-tombstoned rows).

---

## 5. Risk register

| # | Surface | Mechanism | Deterministic? | Evidence (file:line) | Severity | Recommended fix |
|---|---|---|---|---|---|---|
| 1 | **Physical cargo re-delivery after rewind** | abandoned-future `RouteDispatched` row survives the bundle; UT-blind dedup suppresses the re-flown cycle so the live delivery writer never runs; `RouteModule` observe-only | **No (paradox)** | `RouteOrchestrator.cs:1805-1818, 3540-3551`; `ReconciliationBundle.cs:194-196`; `EffectiveState.cs:1336-1347`; `RouteModule.cs:65-70` | **High** | Purge/retire free-standing route rows `UT > rewindCutoff` at rewind (§6 Rec-1) |
| 2 | **KSC dispatch funds charge (no double-charge)** | single surviving row + dedup-suppressed re-fire → charged exactly once | Partial — no double-charge, but can be a phantom charge on a divergent re-fly | `FundsModule.cs:538-548`; dedup as above | Medium | Same as #1 (re-emit live on re-fly) |
| 3 | **Recovery credit** | `RouteRecoveryCredited` row survives un-tombstoned; its `FundsModule` `Effective` gate is **inert** (no module flips it for routes); neutralized only at *emit* time via source-recovery sum, which can't retro-neutralize an already-emitted row | **No (phantom credit)** | `FundsModule.cs:566-580`; `RouteRunCostCalculator.cs:133-147`; `SupersedeCommit.cs:1861-1867` | Medium | Same as #1; remove the dead `Effective` gate or wire it |
| 4 | **Cadence/counter identity** | replay branch bumps `CompletedCycles` on a never-emitted cycle → post-rewind cycleId sequence offset | **No** (not bit-identical to unrewound run) | `RouteOrchestrator.cs:1813` | Medium | Resolved by #1 (no suppression → counters track the live run) |
| 5 | **Multiple successive rewinds** | each rewind re-captures the growing ledger; route rows never tombstoned → orphan rows accumulate monotonically; commit recalc (`double.MaxValue`) re-includes all | **No** (unbounded accumulation, compounding) | `ReconciliationBundle.cs:114-120`; `RewindInvoker.cs:929` | Medium | Resolved by #1 (purge bounds it) |
| 6 | **Non-rewind reverts (plain / Re-Fly discard)** | a plain discard / `ReFlyDiscard` has **no quicksave**; `PreserveIrreversibleLiveGameplayOnDiscard` and `ReFlyDiscard` do not touch routes; free-standing rows aren't in the discarded id set | **No** (un-reverted live mutation, no rollback path at all) | `LedgerOrchestrator.cs:2936-3094`; `MergeDialog.ReFlyDiscard.cs` (whole file) | **Medium-High** | Separate fix (§6 Rec-3): reverse route effects on discard, or disable physical effects for un-quicksaved reverts |
| 7 | **Background / on-rails firing** | `Tick` has no scene gate; writers mutate unloaded/packed proto-vessels; resolver ignores packed state → paradox fires on vessels the player isn't watching (amplifier) | n/a (blast-radius amplifier of #1) | `ParsekScenario.cs:893-930`; `LiveDeliveryWriters.cs:265-298`; `RouteEndpointResolver.cs:50-60` | Medium-High (amplifier) | Resolved-in-effect by #1; optionally log a Warn on a dedup-suppressed physical skip |
| 8 | **Recovery-credit deferral straddling a rewind** | `PendingRecoveryCreditCycleId` arm reverts via `.sfs` while the emitted dispatch row persists in the bundle → owed credit can be silently dropped | **No** | `RouteOrchestrator.cs:1847-1854, 3708`; `RouteCodec.cs:168-172` | Medium | Make the credit non-deferred (emit at the same crossing), then #1 covers it |
| 9 | **Route-definition lifecycle ("disable before it existed")** | definition + counters revert via the `.sfs`; route absent if created after RP; no fire below phase anchor | **Yes** | `RouteStore.cs:594-624`; `ParsekScenario.cs:3343`; `GhostPlaybackLogic.SpanClock.cs:1092-1093` | — (sound) | none |
| 10 | **RP granularity** | RP quicksave is full-world, so a physical effect cannot fall *outside* coverage; rewinding to any RP rolls the whole world back | **Yes** (coverage sound) | `RewindPointAuthor.cs:488` | Low | none (the residual is #1, independent of RP spacing) |
| 11 | **Pause→Activate phase anchor** | grid pinned to the live activation UT (not a recorded UT); deterministic across rewind only if the re-fly reproduces the activation event at the same UT | Partial (conditional) | `RouteOrchestrator.cs:205`; `MissionLoopUnitBuilder.cs:228-229` | Low | document as a determinism caveat |
| 12 | **Inter-body recurrent routes (the Duna case)** | not creatable today (cross-parent → no synodic schedule → degrades to no-phase-lock); same-body is a *soft* property of the cadence solver, not a hard creation reject | Deferred (M5) | `MissionPeriodicity.cs:476, 494-500, 653-658`; design §19.4 M5 | Low (future) | the #1 fix is cadence-source-agnostic, so M5 inherits it for free; optionally add an explicit cross-parent creation reject |
| 13 | **Design-doc contract divergence** | §2.4#11 / §10.6 / §13.4 say un-reversed mutation paths "must stay disabled," but v0 enables physical effects relying on the quicksave restore, not a reversing module | n/a (documentation) | design lines 220-221, 1022, 1159 | Medium (doc) | update the contract to describe the shipped quicksave-revert model + the residual #1 gap |

---

## 6. Recommendations

### Rec-1 (primary, highest leverage) — Make free-standing route ledger rows revert with the world at rewind
The root cause is uniform: route rows are *live-game artifacts with no supersede mechanism*, yet
`ReconciliationBundle` preserves them like recording-attributed timeline rows. The clean fix is to
treat them like the vessels they mutate — **at rewind, retire (drop, or stamp inactive) every
free-standing route `GameAction` whose `UT > the loaded RP cutoff UT`**, so the preserved ledger
matches the reverted world. Then the live re-fly re-emits each cycle deterministically as time
re-advances, re-applying funds (charged once) *and* physical cargo (re-delivered once), with the
dedup correctly seeing an empty slate.

This single change closes risk-register rows #1, #2, #3, #4, #5 and defuses #7 and #8:
- cargo re-delivers (no more "funds spent, no goods");
- the funds charge becomes a real surviving-timeline charge, not a phantom;
- the recovery-credit phantom is purged;
- counters track the live re-fly exactly (no cadence drift);
- orphan rows can't accumulate across stacked rewinds.

Implementation notes: the cutoff UT is already known at the rewind site (the loaded RP UT, the same
value `RecalculateAndPatchForPostRewindFlightLoad(loadedUT)` consumes, `ParsekScenario.cs:3182`). Do
the retire inside the rewind path (e.g. in `ReconciliationBundle.Restore` or `RewindInvoker`
post-load), keyed by `RouteId`-bearing rows with `UT > cutoff`. Do **not** add a global UT filter to
`ComputeELS` — non-route modules rely on its UT-blindness plus the cutoff recalc; the fix must be
route-row-scoped. Alternative (more machinery, not recommended): give route actions a supersede
attribution key so the tombstone walk can retire them — but routes aren't recordings, so this invents
a parallel route-supersede concept for no extra benefit over the UT-cutoff retire.

### Rec-2 — Decide the same-body→inter-body story before shipping M5
The flagship "Duna every 2 years" route is the exact case this paradox is worst for (long cadence,
high-value cargo, many rewinds across a 2-year transfer). Because `EmitLoopCycle`/`cycleId` is
cadence-source-agnostic, Rec-1 fixes the inter-body case for free — but **M5 must not ship before
Rec-1**, or the paradox extends to the headline feature. Optionally, add an explicit cross-parent
*creation reject* (with a player-facing reason) so "same-body only" is a hard gate, not just a soft
consequence of the cadence solver denying a synodic schedule.

### Rec-3 — Close the non-rewind discard leak (separate root cause)
Risk #6 is independent of Rec-1: a plain discard or `ReFlyDiscard` has no quicksave, so physical
route effects applied during a since-discarded segment have *no* rollback path. Either reverse the
physical route effects for discarded segments, or (matching the design's own "must stay disabled"
stance) gate physical route mutation off for any revert path that lacks a quicksave. This is the one
case that genuinely violates §2.4 #11 today.

### Rec-4 — Reconcile the design doc + add a tracked todo
Update `parsek-logistics-supply-routes-design.md` §2.4 #11 / §10.6 / §13.4 to describe the *shipped*
contract (physical effects ride the full-world quicksave restore, funds ride the recalc, `RouteModule`
stays observe-only) and to name the residual route-row-not-reverted gap, so the divergence is tracked
rather than silently contradicted. Add a `todo-and-known-bugs.md` item capturing risks #1–#8 under the
Rec-1 umbrella (none exists today — grep found only the M-MIS-9 freeze and the M1–M6 roadmap).

### Rec-5 (minor) — Pause→Activate determinism caveat
Document that a create-Active route is fully recorded-deterministic (grid pinned to recorded `spanEnd`)
while a Pause→Activate route's grid is pinned to the live activation UT, so its post-rewind determinism
is conditional on the re-fly reproducing that activation. Low priority; documentation only.

---

## 7. Determinism verdict per axis

| Axis | Scope | Verdict |
|---|---|---|
| A | Route-definition lifecycle | **Deterministic** — definition/counters revert via `.sfs`; absent if created after RP; asymmetry with preserved ledger confirmed |
| B | Dispatch clock + cycleId | **Partial** — grid + cycleId reproduce deterministically; UT-blind ELS collision is funds-safe, cargo-unsafe; Pause→Activate caveat |
| C | Funds | **Partial** — dispatch charge no-double-count via dedup, but phantom-charge/credit on divergent re-fly; recovery-credit `Effective` gate is inert dead code |
| D | Physical cargo | **Paradox (confirmed)** — reverted by quicksave, never re-applied; two populations; cadence drift |
| E | Recurrent / inter-body | **Deferred** — not creatable today; C/D gaps would bite it identically once M5 lands |
| F | Risk surfaces | **Partial** — 6 surfaces; primary paradox + non-rewind-discard leak are the actionable ones; RP granularity safe |
| Refute | Adversarial | **Paradox CONFIRMED-STANDS** — six escape hatches each fail |

---

## 8. Key evidence index

- Firing is live, UT-pinned: `RouteLoopClock.cs`; `GhostPlaybackLogic.SpanClock.cs:1092-1104`;
  `RouteOrchestrator.Tick` driven all-scenes `ParsekScenario.cs:924`.
- cycleId counter-keyed: `RouteOrchestrator.cs:771, 999` (`"cycle-" + (CompletedCycles + SkippedCycles)`).
- Route rows free-standing (null RecordingId): `RouteOrchestrator.cs:2852-2902, 3774-3784`;
  `GameAction.cs:220`; `TombstoneAttributionHelper.cs:36`; `SupersedeCommit.cs:1856-1878, 2222-2227`.
- Ledger preserved across rewind: `ReconciliationBundle.cs:19-72` (no RouteStore field), `:114-120`,
  `:194-196`. RouteStore reverts via `.sfs`: `RouteStore.cs:594-624`; `ParsekScenario.cs:1002, 3343`.
- ELS tombstone-only, no UT filter: `EffectiveState.cs:1290-1347`. Recalc walks ELS:
  `LedgerOrchestrator.cs:1841`. Re-fly commit recalc cutoff `double.MaxValue`: `RewindInvoker.cs:929`.
- Dispatch dedup, no UT compare: `RouteOrchestrator.cs:3518-3551`; replay branch emits nothing,
  bumps counter: `:1805-1818`.
- Funds dispatch charge unconditional: `FundsModule.cs:538-548`. Recovery credit `Effective` gate
  inert: `:566-580` + only `Effective=false` writers `MilestonesModule.cs:107`,
  `ContractsModule.cs:326-355`.
- Physical writers mutate live (and unloaded) vessels: `LiveDeliveryWriters.cs:251, 292, 265-298`;
  `LiveOriginDebitWriters.cs:247, 289`; `LiveInventoryPickupWriter.cs:36-38`. `RouteModule`
  observe-only: `RouteModule.cs:65-80`.
- Inter-body deferral: `MissionPeriodicity.cs:476, 494-500, 653-658`; design §19.4 M5.
- Design contract divergence: design §2.4 #11 (lines 220-221), §10.6 (1022), §13.4 (1159).
