# Plan: Zero-drift per-window reschedule - verify-and-harden (same-parent Mun / Minmus)

NOTE (2026-06-09): Phases A-C shipped on branch `zero-drift-harden`; the Phase D in-game VISUAL-parity tier (per-frame ghost activation, scheduled debris render, three-scene visual parity) is deferred to a follow-up.

Status: HARDENING. This is NOT a from-scratch design. The mechanism is ALREADY SHIPPED
and wired into every scene. The job here is to lift the shipped SAME-PARENT
(Kerbin -> Mun / Minmus, including land-and-return) looped-mission reschedule to a FLAWLESS
bar: close the test-coverage and verification gaps, add the missing self-defending guards,
and fix the stale docs so the tree of truth matches HEAD.

SCOPE LOCK (maintainer, 2026-06-09): SAME-PARENT ONLY. The cross-parent "Duna-One-class"
single-moon arrival hold is a different subsystem (re-aim, `UnsupportedCrossParent`) and is a
SEPARATE sibling pass, not part of this plan. See section 6, Decision 1.

Branch: TBD (off origin/main, fresh sibling worktree per the worktree rule).

All `file.cs:NNN` cites in this plan are HEAD-at-authoring and may drift a few lines; re-grep
the named symbols rather than trusting the line numbers (see the line-number caveat in
section 4).

References (read before implementing):
- `docs/dev/done/plans/zero-drift-reschedule.md` (the AUTHORITATIVE shipped spec: section
  2.1 offset-cancellation, 2.2 anchor-on-tightest-band, 2.3 minSpacing throttle, 3.1
  schedule-provider object, 4 structural-drift gating, 7-8 test/phase plan).
- `Source/Parsek/MissionPeriodicity.cs` (solver + `MissionRelaunchSchedule`).
- `Source/Parsek/MissionLoopUnitBuilder.cs` (routing + schedule attach).
- `Source/Parsek/GhostPlaybackLogic.cs` (the span clock + scene-shared resolvers).
- `Source/Parsek.Tests/MissionZeroDriftScheduleTests.cs`,
  `Source/Parsek.Tests/MissionPeriodicityTests.cs`,
  `Source/Parsek.Tests/MissionSpanClockTests.cs`,
  `Source/Parsek.Tests/MapRender/ChainSamplerTests.cs`.

---

## 1. Status banner - authoritative ground-truth verdict

**The feature is BUILT, wired, and pure-unit-tested. It is NOT deferred.** All six
ground-truth reports agree.

The doc/code contradiction the prompt flagged resolves IN FAVOR OF THE CODE:

- **STALE DOC** `docs/dev/plans/mission-periodicity-phases.md:116-127` still labels the
  zero-drift per-window reschedule "the deferred ... task ... (needs a non-uniform-cadence
  span clock, a larger engine change)" and headers Phase 2 "DONE - fixed-cadence variant".
- **CODE REALITY (HEAD):** the non-uniform-cadence span clock is the active path. It is the
  `if (schedule != null)` branch at `GhostPlaybackLogic.cs:7227-7238` (verified: resolves
  the largest scheduled launch <= currentUT via `schedule.TryResolveActiveLaunch`, measures
  phase from it, parks at spanEnd between launches via `isInInterCycleTail`, returns early
  before any loiterCut / arrival-hold logic). The schedule is built by
  `MissionPeriodicity.TryBuildRelaunchSchedule` (`MissionPeriodicity.cs:1049-1138`), held by
  the `MissionRelaunchSchedule` class (`MissionPeriodicity.cs:1565-1756`), and attached by
  `MissionLoopUnitBuilder.cs:296-315`.
- The in-code comment at `MissionPeriodicity.cs:502` already says zero-drift is "now
  implemented", and the plan doc was archived as DONE at
  `docs/dev/done/plans/zero-drift-reschedule.md`, with the v6 archive
  `docs/dev/done/todo-and-known-bugs-v6.md:10` listing it among shipped Missions work. Only
  the live `mission-periodicity-phases.md` Phase 2 note was not updated.

**What is built (same-parent / single-moon path):**
- The pure periodicity solver, anchor-on-tightest-duty-cycle, zero-drift residual that is an
  absolute function of `k` (non-accumulating by construction, not by comment).
- The lazily-generated `MissionRelaunchSchedule` + `TryResolveActiveLaunch` /
  `NextLaunchAfter` (binary search), the `MaxScheduleSteps=8192` safety cap.
- Builder routing: schedule attaches ONLY in the same-parent phase-locked block; the
  re-aim (cross-parent) block sets `relaunchSchedule=null` (`MissionLoopUnitBuilder.cs:431`).
  Schedule and loiterCuts/arrivalHold are MUTUALLY EXCLUSIVE by construction.
- Consumption wired into every scene: flight engine, KSC, Tracking Station, flight map,
  Missions UI countdown / Warp-to, logistics RouteLoopClock.

**What live coverage ALREADY exists (do not re-author this):**
- `RuntimeTests.cs:5383` `TrackingStationSpanClock_ZeroDriftSchedule_ResolvesScheduledLaunch`
  (Category=TrackingStation) constructs a REAL `MissionRelaunchSchedule` (synthetic 100/31,
  launches at 900/1300/1800) and drives `ResolveTrackingStationSampleUT` through the TS/map
  seam at the non-uniform launches, including inter-launch-tail-hide and parked-before-first.
- `RuntimeTests.cs:5432` `FlightSpanClock_ZeroDriftSchedule_EngineResolvesScheduledLaunches`
  (Category=Flight) builds a live `GhostPlaybackEngine`, feeds it a non-null-schedule LoopUnit
  via `SetLoopUnits`, asserts it takes the span-clock (not overlap) branch, and resolves
  `TryResolveUnitMemberPlaybackUT` + `DecideUnitMemberRender` at the same non-uniform launches
  plus the parked-before-first case.

These two tests already prove the RESOLVER-LEVEL live path for a scheduled unit. They
honestly scope OUT (at `RuntimeTests.cs:5443-5457`) the genuinely-uncovered piece: the
per-frame ghost `GameObject` `SetActive`/spawn/destroy in
`GhostPlaybackEngine.UpdateUnitMemberPlayback`, the warp single-fire rebuild count, ride-along
debris live render, and three-scene VISUAL parity. The flawless gap below is exactly that
carve-out plus the missing pure tests, NOT a zero-coverage void.

**What is incomplete (the flawless gap, all in-scope):**
- No pure "N-cycle non-accumulation through the assembled `MissionRelaunchSchedule` object"
  test (the existing non-accumulation test chains the `NextJointNearCoincidenceUT` helper, not
  the assembled schedule object across many resolves).
- The existing `MissionPeriodicityTests.cs` already drives `MissionLoopUnitBuilder.Build` with
  a `StockFake()` bodyInfo and asserts `RelaunchSchedule == null` for single-constraint
  (`:1161`) and `!= null` + `MinIntervalSeconds >= span` for a drifting config (`:1193-1195`).
  The genuinely-missing builder delta is only the explicit
  `LoiterCuts == null && ArrivalHoldSeconds == 0` (INV-3) mutual-exclusion assertion.
- No xUnit test pins the flight resolver and the TS/map sampler agree on `loopUT` for a
  scheduled unit at the PARAM-FORWARDING level (a regression dropping `unit.RelaunchSchedule`
  from the TS call would go uncaught). NOTE both scene paths funnel through the same pure
  helper (`ResolveTrackingStationSampleUT` -> `DecideUnitMemberRender` -> `TryComputeSpanLoopUT`),
  so this is a param-threading regression test, not an independent-implementation parity proof.
- No save/load re-derive test tying schedule determinism to an OnSave/OnLoad round-trip.
- The schedule branch in `TryComputeSpanLoopUT` silently bypasses loiterCuts/arrivalHold and
  does NOT assert the upstream mutual-exclusion invariant (latent foot-gun).
- The per-frame ghost `GameObject` activation + the three-scene visual render parity are
  playtest-verified only, not covered by a live in-game test (the existing two scheduled
  in-game tests stop at the resolver, by their own scope comment).
- Two TS playtest verifications were left "confirm in next playtest" /
  "playtest log-grep pending" in `todo-and-known-bugs.md` for the same-parent TS render path.

**What is explicitly NOT built and is a NON-GOAL of this plan:**
- The single-moon CROSS-PARENT re-aim arrival (the "Duna-One-class" captured-then-land arrival
  hold). SCOPE DECISION (maintainer, confirmed 2026-06-09): this pass is SAME-PARENT ONLY. That
  arrival hold is a cross-parent re-aim construct (`ArrivalHoldPlanner`, classified
  `UnsupportedCrossParent`) hardened in a SEPARATE sibling pass, not here. So "single-moon" in
  this plan means same-parent Mun / Minmus only.
- 2+ constrained moons (Jool-class).
- Multi-hop / gravity-assist chains.
- Atmo-direct interplanetary.
- The broader cross-parent Phase-4 generalization (`UnsupportedCrossParent`). The periodicity
  solver correctly DETECTS cross-parent and hands off to the re-aim subsystem; that subsystem
  is audited elsewhere, not here.

---

## 2. Done vs Remaining (Remaining strictly scoped to the flawless bar)

| Area | Done (HEAD) | Remaining (flawless) |
|------|-------------|----------------------|
| Pure solver math | `TryBuildRelaunchSchedule`, `TryFindNextScheduleK`, `SelectAnchorConstraintIndex`, tolerance model, bounded-best fallback, NaN guards, safety cap - all unit-tested (`MissionZeroDriftScheduleTests.cs`) | none (math is sound) |
| Schedule object | `MissionRelaunchSchedule` resolve/next/extend, lazy cache, determinism test | N-cycle non-accumulation test THROUGH the object; live-warp single-frame cost spot-check |
| Builder routing | schedule attach gate + mutual exclusion by construction (`MissionLoopUnitBuilder.cs:296-431`); `Build` already attach/no-attach tested with `StockFake()` bodyInfo (`MissionPeriodicityTests.cs:1161/1193-1195`) | builder-level mutual-exclusion (`LoiterCuts==null && ArrivalHoldSeconds==0`) assertion via `MissionLoopUnitBuilder.Build` (the private `TryBuildMissionUnit` is not test-reachable) |
| Span clock consumption | `if (schedule != null)` branch + null-byte-identical regression; resolver-level scheduled-unit live tests already exist for Flight + TS (`RuntimeTests.cs:5383/5432`) | Flight==TS param-forwarding regression test for a SCHEDULED unit; self-defending invariant guard |
| Scene wiring | flight / KSC / TS / map / UI / logistics all thread `unit.RelaunchSchedule`; resolver-level live coverage shipped | new in-game tests for per-frame ghost activation, scheduled debris render, three-scene VISUAL parity (resolver level already covered) |
| Save/load | schedule derived from persisted inputs + signature rebuild; determinism test | OnSave/OnLoad round-trip re-derive test (source-gate or in-game) |
| TS render | startup loop-aware create, same-body carry fix (shipped + unit-tested) | close 2 playtest-pending TS verifications (todo lines ~527, ~529) |
| Docs | done-plan archived, code comments updated | fix `mission-periodicity-phases.md:116-127`; close playtest-pending todo lines |

---

## 3. The flawless bar - correctness INVARIANTS, each with a proving test

These are the properties that MUST hold for the shipped same-parent / single-moon path to be
flawless. Each names the test that proves it (new tests are built in section 4).

- **INV-1 Zero accumulating drift.** For a stock same-parent config (Kerbin rotation + Mun
  orbit), successive scheduled launches resolved THROUGH the `MissionRelaunchSchedule` object
  keep every other-constraint residual within its physics tolerance and never grow like
  993, 1986, 2978 s (bounded, not monotonically increasing). The anchor residual is exactly 0 at
  every launch. CAVEAT (see R2): the "<= the m=9 fixed residual" bound holds for the
  WITHIN-TOLERANCE branch (the stock 2-constraint Mun case); a bounded-best 3-constraint config
  can exceed it, so scope this oracle to the proven within-tol set per R2(i) or relax it for an
  explicit bounded-best fixture per R2(ii).
  Proof: new `Schedule_NCycleResolve_ResidualBoundedNoAccumulation` (section 4.1).

- **INV-2 Flight and TS thread the schedule into the SAME shared resolver.** For a unit
  carrying a non-null `RelaunchSchedule`, the flight engine path (`TryComputeSpanLoopUT`) and
  the map/TS path (`ResolveTrackingStationSampleUT` -> `DecideUnitMemberRender`) resolve the
  SAME `loopUT`, `cycleIndex`, and `renderHidden` for the same currentUT, across a UT sweep
  that includes the pre-first-launch (parked) boundary, the launch instants, and the
  inter-cycle tails. STRUCTURAL CAVEAT: both scene paths funnel through the same pure helper
  (`ResolveTrackingStationSampleUT` -> `DecideUnitMemberRender` -> `TryComputeSpanLoopUT` at
  `GhostPlaybackLogic.cs:7471/7520`; flight `UpdateUnitMemberPlayback` -> `DecideUnitMemberRender`
  at `GhostPlaybackEngine.cs:2280`), so the xUnit "agreement" is `f(x) == f(x)` at the math
  level - its real value is a PARAM-FORWARDING regression test (does
  `ResolveTrackingStationSampleUT` thread schedule + cuts + hold + anchor into the shared
  helper, so a future regression dropping `unit.RelaunchSchedule` from the TS call is caught).
  The genuinely-independent three-scene parity (different per-frame machinery) is the in-game
  visual test (4.9), which carries the INV-2-live weight.
  Proof: new `Scheduled_FlightTsSampler_AgreeOverSweep` (section 4.2, param-forwarding) +
  in-game 4.9 (independent visual parity).

- **INV-3 Mutual exclusion is enforced, not merely true.** A unit with a non-null
  `RelaunchSchedule` always has `LoiterCuts == null` and `ArrivalHoldSeconds == 0`; the span
  clock's schedule branch is correct to bypass the cut/hold logic.
  Proof: new builder test `Build_Scheduled_HasNoLoiterCutsOrArrivalHold` (4.3) + a fail-loud
  guard at the consumption point (section 4.4).

- **INV-4 Schedule survives save/load and config re-snap.** A reload re-derives a
  byte-identical launch sequence (the schedule is never serialized; it is rebuilt from
  persisted `Mission.LoopAnchorUT` (`Mission.cs:85/116`) + live body geometry, folded into
  `BuildSignature` at `MissionLoopUnitBuilder.cs`). A tolerance-affecting body change rebuilds.
  Proof: existing determinism test PLUS new `Schedule_SaveLoadRoundTrip_RederivesIdentical`
  (4.5).

- **INV-5a Warp resolves the active launch in one call (resolver, pure-testable).** A single
  far-future currentUT (past many scheduled launches) resolves the correct active launch
  directly in one `TryResolveActiveLaunch` call (no per-launch replay catch-up). The cache
  lazily extends to cover the jump bounded by `MaxScheduleSteps=8192`, and a second resolve at
  a nearby UT is idempotent (does not re-extend, hits the grown cache via binary search at
  `MissionPeriodicity.cs:1714-1721`).
  Proof: new `Schedule_FarFutureWarp_ResolvesActiveOnce_WithinCap` (4.6).
- **INV-5b Warp triggers exactly one ghost rebuild (engine, in-game-only).** Across a live
  multi-launch TimeWarp jump the engine changes `cycleIndex` once and triggers exactly one
  ghost rebuild. This is a property of `GhostPlaybackEngine.UpdateUnitMemberPlayback`, not of
  the pure resolver, so a pure xUnit test cannot observe it.
  Proof: in-game warp spot-check folded into the flight render test (4.7).

- **INV-6 Faithful-instance-per-launch, nothing in the gap (live).** In live FLIGHT a looped
  same-parent multi-constraint mission renders exactly one faithful instance per scheduled
  launch and nothing in the inter-cycle tail; ride-along debris ride their scheduled parent.
  Note the resolver-level faithful-launch path is already covered by the two existing
  in-game tests (`RuntimeTests.cs:5383/5432`); this invariant's NEW weight is the per-frame
  ghost `GameObject` `SetActive`/spawn/destroy in `UpdateUnitMemberPlayback` plus the live
  ride-along debris render, which those tests scope out at `RuntimeTests.cs:5443-5457`.
  Proof: new in-game tests (4.7, 4.8).

- **INV-8 Pre-first-launch / future-dated anchor parks cleanly.** When currentUT is before
  the first scheduled launch (`FirstLaunchUT`, which may sit after a future-dated UT0 so the
  dense index `k` is negative), `schedule.TryResolveActiveLaunch` returns false
  (`GhostPlaybackLogic.cs:7229-7230` returns false -> `SpanClockUnresolved`), the render
  decision is the parked/hidden decision, and the flight resolver and TS sampler AGREE on the
  parked decision. The existing in-game tests exercise this incidentally; pin it as an
  explicit assertion target.
  Proof: folded into the 4.2 sweep (assert the parked decision at a pre-L0 UT, both paths
  agree) and the 4.1 N-cycle test (resolve at a pre-first-launch UT returns no active launch).

- **INV-7 (RESOLVED VACUOUS - same-parent scope).** The maintainer confirmed this pass is
  SAME-PARENT ONLY (section 6, Decision 1). The "Duna-One-class single-moon captured-then-land
  arrival hold" is a CROSS-PARENT re-aim construct (`ArrivalHoldPlanner`, `UnsupportedCrossParent`)
  NOT served by the zero-drift schedule, so it is a SEPARATE sibling pass, not part of this
  flawless bar. On the in-scope path (same-parent Mun / Minmus, including land-and-return) there
  is NO arrival hold, so INV-7 is vacuous and INV-1 / INV-2 / INV-6 fully cover the in-scope
  single-moon cases. No arrival-hold unit is attached in any Phase-D fixture.

---

## 4. Phased work items

Ordered by dependency and risk. Phases A-C are the SHIPPABLE CORE: they close INV-1, INV-2
(param-forwarding), INV-3, INV-4, INV-5a, and INV-8 plus the builder/guard work with ZERO live
dependency, and each is independently shippable and testable. Phase D is an OPTIONAL
live-verification tier gated on live KSP and a new fixture (see Phase D for the fallback if the
fixture is blocked). Review checkpoints are placed at the math-core verification milestone and
the wiring milestone, not per commit (per the project workflow).

LINE-NUMBER CAVEAT: all `file.cs:NNN` cites below are HEAD-at-authoring and may drift a few
lines. Implementers should re-grep the named symbols (`TryComputeSpanLoopUT`,
`TryResolveActiveLaunch`, `relaunchSchedule = sched` / `= null`, `parentUnit.RelaunchSchedule`,
`ResolveTrackingStationSampleUT`, `DecideUnitMemberRender`) rather than trusting the cites; the
symbol names are stable.

### Phase A - Pure schedule-object hardening tests (lowest risk, no production change)

**Scope:** prove the SHIPPED math holds through the assembled schedule object over many
cycles, and prove Flight==TS at the resolver layer. No source changes; pure xUnit only.

**Files:**
- `Source/Parsek.Tests/MissionZeroDriftScheduleTests.cs` (add tests)
- `Source/Parsek.Tests/MissionSpanClockTests.cs` (add the scheduled-unit parity test;
  reuse its `MakeSingleUnitSet` / `ThreeMemberUnit` helpers). REUSE the EXISTING
  `SyntheticSchedule()` factory at `MissionZeroDriftScheduleTests.cs:481` (do NOT author a
  parallel one that could drift)
- Read-only confirms: `MissionPeriodicity.cs:1565-1756`, `GhostPlaybackLogic.cs:7227-7238`,
  `GhostPlaybackLogic.cs:7461-7535` (`DecideUnitMemberRender` + `ResolveTrackingStationSampleUT`).

**Gap closed:** the existing non-accumulation test (`MissionZeroDriftScheduleTests.cs:174-215`)
chains `NextJointNearCoincidenceUT` 12 times - it proves the HELPER, not the consumed schedule
object across many resolves (INV-1). And while the live resolver path for a scheduled unit IS
covered in-game (`RuntimeTests.cs:5383/5432`), no PURE xUnit test pins that
`ResolveTrackingStationSampleUT` forwards `unit.RelaunchSchedule` (+ cuts/hold/anchor) into the
shared helper, so a param-dropping regression on the TS call would go uncaught at the unit
level (INV-2, param-forwarding).

**Tests to add:**
1. **4.1 `Schedule_NCycleResolve_ResidualBoundedNoAccumulation`** (INV-1, + INV-8 pre-L0): build
   the stock Kerbin-rotation + Mun-orbit schedule; loop `k=0..K` (K>=64) calling
   `schedule.TryResolveActiveLaunch` / `NextLaunchAfter`; at each resolved launchUT recompute
   `CircularPhaseError(launchUT - ut0, otherPeriod)` and assert it stays <= the m=9 fixed
   residual (~993 s) and never monotonically grows. Also assert that a resolve at a
   pre-first-launch UT returns no active launch (INV-8).
2. **4.2 `Scheduled_FlightTsSampler_AgreeOverSweep`** (INV-2 param-forwarding, + INV-8): build a
   unit WITH a synthetic schedule; over a currentUT sweep covering the pre-first-launch parked
   boundary, several launches, and inter-cycle tails, assert
   `TryComputeSpanLoopUT(...schedule)` and `ResolveTrackingStationSampleUT(...unit-with-schedule)`
   agree on `loopUT` and the hidden/tail/parked decision. Frame this as a PARAM-FORWARDING
   regression test: both scene paths share the same pure helper, so the assertion's value is
   that the schedule + cuts + hold + anchor are threaded into the shared call (a dropped
   `unit.RelaunchSchedule` on the TS call is what this catches), NOT an independent
   cross-implementation parity proof. The independent visual parity is in-game 4.9.
3. **4.6 `Schedule_FarFutureWarp_ResolvesActiveOnce_WithinCap`** (INV-5a): resolve at a single
   far-future currentUT (past many launches) in one call; assert it returns the correct active
   launch + dense index, the cache stayed bounded (<= `MaxScheduleSteps`), and a second resolve
   at a nearby UT does not re-extend (idempotent cache). This is the PURE half of warp; the
   "exactly one rebuild / cycleIndex changes once" half (INV-5b) is in-game-only (4.7).

**Validation / exit:** `dotnet test` green; the three new tests fail if the schedule-object
resolve drifts or the two scene resolvers disagree. **REVIEW CHECKPOINT (math core):** a
clean-context review that the N-cycle residual oracle is measuring the right quantity (the
other-constraint phase error in the same frame the solver uses) and that the parity sweep
covers the tail boundaries, not just mid-span.

### Phase B - Builder-level mutual-exclusion test + self-defending guard

**Scope:** add the one genuinely-missing builder assertion (schedule => no cuts/hold), and make
the span-clock bypass fail-loud instead of silent.

**Files:**
- `Source/Parsek.Tests/MissionPeriodicityTests.cs` (ADD the mutual-exclusion assertion here -
  this file ALREADY drives `MissionLoopUnitBuilder.Build` with a `StockFake()` bodyInfo and
  asserts `RelaunchSchedule == null` for single-constraint (`:1161`) and `!= null` +
  `MinIntervalSeconds >= span` for the drifting config (`:1193-1195`)). NOTE (corrected per the
  clean review): a `MissionLoopUnitBuilderTests.cs` DOES exist, but its `Build` helper uses the
  NO-bodyInfo overload and never drives the schedule path, so the schedule-attach assertions
  belong in `MissionPeriodicityTests.cs` (which calls `Build(..., StockFake())`), not there.
  Route the new test
  through the internal `MissionLoopUnitBuilder.Build` entrypoint
  (`MissionLoopUnitBuilder.cs:38`); the schedule-attach if-chain's `TryBuildMissionUnit`
  (`MissionLoopUnitBuilder.cs:111`) is PRIVATE static and is NOT test-reachable even with
  `InternalsVisibleTo`, so `Build` is the only seam that drives the live if-chain.
- `Source/Parsek/GhostPlaybackLogic.cs:7227-7238` (add the guard)
- `Source/Parsek/MissionPeriodicity.cs` (R3, corrected fix: surface the schedule's worst-launch
  `AllLaunchesWithinTolerance` / `WorstResidualSeconds` from `TryFindNextScheduleK` through
  `ExtendOnce` onto `MissionRelaunchSchedule`, instead of dropping it via `out _` at `:1693`)
- `Source/Parsek/UI/MissionsWindowUI.cs:1332` (R3: tint amber off the surfaced schedule flag, NOT
  the fixed-fit `WithinTolerance` and NOT unconditionally off `IsScheduled`)
- Read-only confirms: `MissionLoopUnitBuilder.cs:267-315` (phaseLocked attach),
  `MissionLoopUnitBuilder.cs:431` (re-aim null), `:561-565` (LoopUnit construction).

**Gap closed:** (INV-3) the schedule branch returns early before the loiterCut /
arrival-hold remap and trusts an UPSTREAM, undocumented-at-the-consumption-point invariant
(schedule => phaseLocked => no cuts/hold). Safe today, but a future same-parent loiter
compression or a re-aim unit that ever co-attached a schedule would silently drop the
cut/hold with no warning. The existing `Build`-driven tests already pin schedule
attach/no-attach; the only missing builder delta is the explicit
`LoiterCuts == null && ArrivalHoldSeconds == 0` mutual-exclusion assertion.

**Production change (minimal, fail-loud, RATE-LIMITED):** `TryComputeSpanLoopUT` is a per-frame
HOT path called every frame from the flight engine (`GhostPlaybackEngine.cs:359/1878/2280`),
the TS seam (`ResolveTrackingStationSampleUT`), KSC, the map, and logistics `RouteLoopClock`
(`RouteLoopClock.cs:107`). Its doc contract (`GhostPlaybackLogic.cs:7184-7185`) states it is
pure on the common path "except a single rate-limited Verbose line". A raw `ParsekLog.Warn`
would therefore fire EVERY FRAME for EVERY scheduled-with-cuts unit across multiple scenes -
log spam that violates the project's per-frame logging convention. So: in the
`if (schedule != null)` block, before the early return, when
`loiterCuts != null || arrivalHoldSeconds > 0`, emit a RATE-LIMITED / keyed warning - either
`ParsekLog.VerboseRateLimited` or a Warn-once keyed on mission identity
(`phaseAnchorUT + spanStartUT`), mirroring the existing per-loop-hold key at
`GhostPlaybackLogic.cs:7298`. This is belt-and-suspenders: it changes no behavior on the shipped
path (the predicate is always false there) but converts a latent silent-drop into a loud
contract violation WITHOUT per-frame spam, and stays consistent with the function's documented
purity contract. Keep it rate-limited and degrading, not a per-frame throw, so a future misuse
degrades visibly rather than crashing playback. (A debug-only `Assert` - Open Decision 2 - is
acceptable as an additional CI catch, but the live-build path must not spam.)

**Tests to add:**
1. **4.3 `Build_Scheduled_HasNoLoiterCutsOrArrivalHold`** (INV-3): drive
   `MissionLoopUnitBuilder.Build` (NOT the private `TryBuildMissionUnit`) with a same-parent
   drifting config + non-null `StockFake()` bodyInfo; assert `RelaunchSchedule != null`,
   `PhaseAnchorUT == FirstLaunchUT`, `MinIntervalSeconds >= span`, AND `LoiterCuts == null` &&
   `ArrivalHoldSeconds == 0`. The first three assertions overlap the existing
   `Build_DriftingLongSpan...` test (`MissionPeriodicityTests.cs:1193-1195`); the
   `LoiterCuts`/`ArrivalHold` pair is the new delta. (May be folded into the existing test
   rather than a new method.)
2. **`Build_SingleConstraint_TidalCollapse_Unsupported_NoSchedule`** (matrix): single /
   tidally-locked / `UnsupportedCrossParent` configs attach NO schedule and do not regress the
   anchor (complements the existing `MissionPeriodicityTests.cs:1145/1248`, driven through
   `MissionLoopUnitBuilder.Build`).
3. **Guard log test:** a log-capture test (per `RewindLoggingTests` pattern) that feeds the
   span clock a contrived unit with both a schedule AND loiterCuts and asserts the rate-limited
   warning fires (on the first frame / first key occurrence).

**Validation / exit:** `dotnet test` green; the guard log line appears only on the contrived
misuse. **REVIEW CHECKPOINT (wiring):** review the guard wording/level and that the builder
tests actually exercise the live if-chain (not a hand-built LoopUnit shortcut).

### Phase C - Save/load re-derive coverage

**Scope:** tie schedule determinism to an actual persistence round-trip (INV-4).

**Files:**
- `Source/Parsek.Tests/MissionPeriodicityTests.cs` (or a focused new test class)
- Read-only confirms: `Mission.cs:85/116` (`LoopAnchorUT` serialized),
  `MissionLoopUnitBuilder.cs` `BuildSignature` (folds `LoopAnchorUT` +
  `SoiRadius`/`OrbitalVelocity` + `TransitedBodyRotationMode`).

**Gap closed:** the existing `Schedule_Deterministic_TwoIndependentBuilds...` proves two builds
from the SAME in-memory inputs match; nothing ties that to OnSave/OnLoad of a looped Mission.
Per the `ParsekScenario` xUnit limitation (Planetarium/GameEvents unguarded), use the
source-gate pattern (`ChainSaveLoadTests.ChainStateNotPersistedInScenario` style) OR a focused
in-game test if a live save round-trip is needed.

**Tests to add:**
1. **4.5 `Schedule_SaveLoadRoundTrip_RederivesIdentical`** (INV-4): serialize the persisted
   inputs (`LoopAnchorUT` + the body-geometry digest), rebuild, and assert the resolved launch
   sequence over `k=0..K` is identical pre/post. If a true OnLoad round-trip is required, add
   it as the in-game variant in Phase D.

**Validation / exit:** `dotnet test` green.

### Phase D - In-game scene coverage + the synthetic-recording fixture (OPTIONAL live tier, highest cost, last)

**Scope:** close the genuinely-uncovered live piece (the per-frame ghost `GameObject`
activation, ride-along debris render, three-scene VISUAL parity for INV-6) and the warp
single-fire rebuild spot-check (INV-5b). This is NEW coverage BEYOND the existing
resolver-level scheduled in-game tests (`RuntimeTests.cs:5383/5432`), not the filling of a
zero-coverage void. It is the only phase that needs live KSP and a new fixture, and is the
OPTIONAL live-verification tier: Phases A-C ship a fully-verified resolver/builder/save-load
tier independently of it.

**Sub-phase D0 - the fixture (a hard prerequisite; make its cost visible before D1-D3):**
- `Source/Parsek.Tests/Generators/RecordingBuilder.cs` (+ `ScenarioWriter.cs`): add a
  looped same-parent Mun mission fixture. Today no generator produces a looped same-parent Mun
  mission tree, which is WHY the live gaps cannot be cheaply closed; the fixture unlocks all
  three in-game tests below. If the fixture proves expensive or blocked, the honest close is
  doc-hygiene task 3 (annotate the done-plan that the in-game tier was carried forward); do NOT
  leave D1-D3 half-done.

**Files (D1-D3):**
- `Source/Parsek/InGameTests/RuntimeTests.cs` (add the three tests, `Category = "Periodicity"`,
  `Scene = GameScenes.FLIGHT`)
- Read-only confirms: `GhostPlaybackEngine.cs:368` (single-instance), `:1881` (the easy-to-miss
  ride-along-debris `parentUnit.RelaunchSchedule` site), `:2283`; `ParsekKSC.cs:1189`;
  `ParsekTrackingStation.cs` -> `GhostMapPresence.cs:6494`.

**Gap closed (INV-6 / INV-5b):** the existing scheduled in-game tests
(`RuntimeTests.cs:5383/5432`) cover the RESOLVER level for a scheduled same-parent unit (TS
sampler + live `GhostPlaybackEngine` member-UT/render decision), but by their own scope comment
(`RuntimeTests.cs:5443-5457`) they do NOT cover the per-frame ghost `GameObject`
`SetActive`/spawn/destroy in `GhostPlaybackEngine.UpdateUnitMemberPlayback`, the warp
single-fire rebuild count, the live ride-along debris render, or the three-scene VISUAL parity
(flight-map / TS ProtoVessel repositioning in `GhostMapPresence` + the KSC mesh path). Those
remain playtest-verified only. (The Periodicity-category in-game tests
`CrossParentReaimCanaryInGameTest` / `ReaimEndToEndInGameTest` are cross-parent Kerbin->Duna and
attach no schedule; they are NOT scheduled-path coverage, but the scheduled-path resolver IS
covered by the two tests named above.)

**Tests to add:**
1. **4.7 Flight render** (INV-6, + INV-5b warp): a looped same-parent multi-constraint mission
   renders exactly one faithful instance per scheduled launch and nothing in the inter-cycle
   tail; include a warp step across >=2 launches and assert exactly one rebuild (the
   engine-internal single-fire property the pure 4.6 cannot observe).
2. **4.8 Ride-along debris** (INV-6): debris of a scheduled member launches in lockstep with
   its scheduled parent (the `GhostPlaybackEngine.cs:1881` site). At minimum add a PURE test
   that the debris branch resolves the SAME launchUT/cycle as the parent member for a scheduled
   unit (the inputs are available); the live render is the in-game follow-up.
3. **4.9 Three-scene parity** (INV-2 live): the same scheduled member renders identically in
   flight, KSC, and TS for the same UT.

**Validation / playtest exit:**
- All new in-game tests pass via Ctrl+Shift+T; results in `parsek-test-results.txt`.
- A single-moon (Mun) TS-entry + orbit-gap playtest closes the two pending TS verifications
  (section 5): grep `KSP.log` for `source=None reason=before-terminal-orbit` at a loop-mapped
  UT (todo startup-flash line) and zero `destroy reason=gap-between-orbit-segments` recreate
  churn across the orbit-segment gap (todo 1-2 s line).
- **DLL verification before the playtest** (CLAUDE.md hard rule): grep the deployed
  `GameData/Parsek/Plugins/Parsek.dll` for a distinctive UTF-16 string from the new guard /
  test before launching, because sibling worktrees share the deployed DLL.

**Fallback if the D0 fixture is blocked:** Phases A-C are the shippable core and close
INV-1/2/3/4/5a/8 with zero live dependency. If the looped same-parent Mun fixture proves
expensive or blocked, the honest close is doc-hygiene task 3: annotate the done-plan that the
in-game tier (4.7-4.9) was carried forward to a follow-up rather than leaving Phase D
half-done. The two existing scheduled in-game tests (`RuntimeTests.cs:5383/5432`) already keep
the resolver-level live path covered in the interim.

---

## 5. Doc-hygiene tasks (match docs to HEAD)

1. **`docs/dev/plans/mission-periodicity-phases.md:116-127`** - the one real doc/code
   contradiction. Update the Phase 2 body to state the zero-drift per-window reschedule
   SHIPPED, drop "deferred" / "a larger engine change", and link
   `docs/dev/done/plans/zero-drift-reschedule.md`. Do not delete the fixed-cadence-variant
   history; just correct the forward-looking "deferred" claim.
2. **`docs/dev/todo-and-known-bugs.md`** (the "Done - Missions: looped inter-body
   launch-window phase-lock" block, ~lines 527 and 529-530) - the TS startup wrong-position
   icon flash (`source=None reason=before-terminal-orbit`) and the TS 1-2 s render gap across
   recorded orbit-segment gaps are marked FIXED / Subsumed but close with "playtest log-grep
   pending" / "Confirm in the next TS playtest". After the Phase-D Mun TS playtest, update
   these to closed with the grep evidence (or, if the grep does NOT show the expected lines,
   reopen with the captured frame - do not mark closed on faith, per the
   confirm-diagnosis-against-evidence rule).
3. **`docs/dev/done/plans/zero-drift-reschedule.md`** - this done-plan's test plan
   (lines 519-527) lists three in-game tests as delivered scope. The done-plan is only PARTLY
   overstated: two scheduled resolver-level in-game tests DO exist
   (`RuntimeTests.cs:5383/5432`), but the per-frame ghost activation / ride-along debris render
   / three-scene visual parity tier does NOT. After Phase D lands those, the done-plan is
   accurate; if Phase D is deferred, add a one-line note in that done-plan that the per-frame
   visual in-game tier was carried to this hardening plan so the archive does not over-state
   delivered validation (the resolver-level tier is already shipped).
4. This hardening plan moves to `docs/dev/done/plans/` when all phases land.

---

## 6. Highest residual risks + open DECISIONS for the maintainer

**Open decisions:**

1. **Scope of "single-moon" - RESOLVED 2026-06-09: SAME-PARENT ONLY.** The maintainer confirmed
   this hardening pass covers the SAME-PARENT zero-drift schedule path (Kerbin -> Mun / Minmus,
   including land-and-return) only. The cross-parent "Duna-One-class" single-moon arrival hold
   (`ArrivalHoldPlanner`, `MissionLoopUnitBuilder.cs:527`, classified `UnsupportedCrossParent`
   at `MissionPeriodicity.cs:451-457`) is a DIFFERENT subsystem and is hardened in a separate
   sibling pass, NOT here. Consequence: INV-7 is vacuous, no Phase-D fixture attaches an
   arrival-hold unit, and every Phase-D fixture is same-parent Mun. (Recorded so a later reader
   does not reopen the reader disagreement that originally flagged this.)

2. **Guard severity at the span-clock bypass.** The live-build path is FIXED as a rate-limited
   warning (not a raw per-frame Warn) because `TryComputeSpanLoopUT` is a per-frame hot path
   with a documented purity contract - see Phase B. The remaining maintainer call is only
   whether to ADD a debug-only `Assert` (throws in test builds, catches the misuse harder in
   CI) ON TOP of the rate-limited live warning, or rely on the warning + the Phase-B guard log
   test alone. A debug assert risks nothing in live playback (compiled out) but adds a CI trip.

3. **Same-parent loiter compression (parity gap, out of scope but worth a yes/no).** A
   same-parent Mun/Minmus land-and-return with a long parking loiter takes the schedule path
   and replays the FULL recorded span (loiter included) at each relaunch -
   `ReaimLoiterCompressor.ComputeCuts` runs ONLY in the re-aim branch
   (`MissionLoopUnitBuilder.cs:441`). Geometry stays faithful; only supply-route density is
   affected. Is full-faithful-span replay the intended same-parent behavior indefinitely, or a
   future item? Flag, do not design.

**Highest residual risks:**

- **R1 - Live-warp single-frame cache cost is unverified.** On a very large single-frame
  TimeWarp jump, `TryResolveActiveLaunch`'s while-extend loop
  (`MissionPeriodicity.cs:1711-1713`) can call `ExtendOnce` many times in one frame, each an
  O(lookahead <= `ScheduleLookaheadMultiples = 4096`) `TryFindNextScheduleK` /
  `CircularPhaseError` scan (`MissionPeriodicity.cs:526`), bounded only by
  `MaxScheduleSteps = 8192` (`:533`). So the absolute single-frame worst case is doubly bounded
  at ~8192 extends * up to a 4096-multiple scan - large but finite, and the cache persists in
  `cachedLoopUnits` so it is a one-time cost per far jump. The pure tests warp to ~1e9 in xUnit
  time, NOT a single live frame. Likely fine, but the worst-case live-frame cost is unmeasured;
  the two constants above let the maintainer judge the paper bound before deciding whether the
  Phase-D warp spot-check (4.7) is necessary - it remains the cheapest empirical confirm.

- **R2 - Over-tolerance (bounded-best) launches replay silently (PROMOTED: it has teeth in two
  places).** `TryResolveActiveLaunch` returns only launchUT + index; the per-launch residual /
  withinTolerance from `TryFindNextScheduleK` are dropped (`out _` in `ExtendOnce`,
  `MissionPeriodicity.cs:1693`). The stock Mun 2-constraint case always finds within-tol windows,
  but a 3-constraint same-parent config (e.g. pad rotation + a Mun intercept + a second body
  constraint) that falls to bounded-best within `ScheduleLookaheadMultiples = 4096` would replay
  genuinely over-tolerance launches. This is NOT just a diagnostic nicety: (a) it falsifies the
  "within tolerance by construction" premise (so the R3 fix surfaces the worst-launch flag and
  tints off it); and (b) INV-1's oracle asserts the per-launch residual stays `<=` the m=9 fixed
  residual (~993 s), which a bounded-best config could EXCEED. REQUIRED for the flawless bar:
  EITHER (i) prove + assert that no in-scope same-parent config reaches bounded-best within the
  lookahead, and scope INV-1's oracle to that proven set; OR (ii) add a bounded-best same-parent
  fixture and assert the schedule still resolves MONOTONICALLY INCREASING launches with the
  worst-launch amber flag surfaced (R3), relaxing INV-1's residual bound for that fixture. The
  surfaced flag from the R3 fix is the shared mechanism. The implementing agent picks (i) or (ii)
  after determining whether such a same-parent config is even constructible.

- **R3 - UI amber-while-faithful tint is a CONFIRMED shipping mismatch (small fix + test, in
  the flawless-bar scope).** This is no longer an open verify-item: the CODE-REALITY review
  traced it. `MissionsWindowUI.cs:1332` computes
  `bool amber = periodicity.IsPhaseLockedConstrained && !periodicity.Solution.WithinTolerance`.
  `IsPhaseLockedConstrained` (`:1168`) is true for a scheduled drifting same-parent unit
  (`Solved && ShouldPhaseLock && P > MinCycleDuration` - the exact gate the schedule attaches
  under at `MissionLoopUnitBuilder.cs:267`), and `Solution.WithinTolerance` reflects the FIXED
  m*P fit and is FALSE for the stock Mun (~993 s residual). So the T- countdown cell tints
  AMBER for a scheduled unit whose ACTUAL scheduled launches ARE within tolerance by
  construction - exactly the display-vs-reality mismatch. (The PERIOD cell is fine: it reads
  `RelaunchSchedule.AverageIntervalSeconds` with a "varies" label at
  `MissionsWindowUI.cs:1250-1258`.)
  FIX (corrected per the clean review - do NOT just force `amber=false` for `IsScheduled`): a
  scheduled unit is within tolerance ONLY when EVERY scheduled launch found a within-tolerance
  window. The BOUNDED-BEST path (`TryFindNextScheduleK` returns `withinTolerance=false` at
  `MissionPeriodicity.cs:949-953` when no within-tol k exists in the lookahead) can still produce
  genuinely over-tolerance launches, and that flag is currently DROPPED (`out _` at
  `MissionPeriodicity.cs:1693`). So the honest fix is to SURFACE the schedule's own worst-launch
  tolerance (a new `MissionRelaunchSchedule.AllLaunchesWithinTolerance` / `WorstResidualSeconds`
  over the cached prefix, threaded up from `TryFindNextScheduleK` through `ExtendOnce`) and tint
  amber off THAT for a scheduled unit, NOT off the fixed-fit `WithinTolerance` and NOT
  unconditionally off `IsScheduled`. This closes R2 in the same stroke. Tests: `amber == false`
  for a within-tol scheduled unit (stock Mun) AND `amber == true` for a contrived bounded-best
  scheduled unit (so a real over-tolerance schedule is NOT silently hidden). This now touches
  `MissionPeriodicity` (surface the flag), not only the UI file; fold into Phase B. Re-grep the
  cites before editing - they are HEAD-at-authoring.

- **R4 - Deployed-DLL drift.** Sibling `Parsek-*` worktrees share
  `GameData/Parsek/Plugins/Parsek.dll`; an in-game playtest can silently run a different
  branch's build. Always grep the deployed DLL for a distinctive UTF-16 string from this work
  before any Phase-D playtest.

---

## Open verification items (readers disagreed or could not confirm in a read-only pass)

- (RESOLVED 2026-06-09) The single-moon scope is SAME-PARENT ONLY (section 6, Decision 1); the
  cross-parent Duna arrival hold is a separate sibling pass. No longer an open item.
- Whether the per-launch residual/withinTolerance is logged at schedule-EXTEND time at all
  (the done-doc section 6 specs Verbose-per-relaunch logging; the out params are dropped via
  `out _` at `MissionPeriodicity.cs:1693`) - confirm by grep on a live run before assuming the
  diagnostic exists (R2). This is the only diagnostic-surface question left open; if no shipped
  same-parent config falls to bounded-best (verify per R2) the surface is moot.
- The three-scene VISUAL parity (4.9) is the one INV-2 property a read-only pass cannot
  confirm: flight, KSC, and TS each run independent per-frame ghost-placement machinery, so
  whether they render the scheduled member identically can only be shown live in Phase D, not
  by the shared-helper xUnit test (which is param-forwarding only, see INV-2).

NOTE: R3 (UI amber-while-faithful tint) was an open verify-item in earlier drafts; the
CODE-REALITY review traced it to a confirmed shipping mismatch at `MissionsWindowUI.cs:1332`,
so it is now a concrete fix in section 6 R3, no longer an open verification question.
