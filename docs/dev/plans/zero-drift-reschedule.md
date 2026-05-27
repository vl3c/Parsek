# Plan: Zero-drift per-window reschedule for looped-mission periodicity

Companion follow-up to `docs/dev/design-mission-periodicity.md` and
`docs/dev/plans/mission-periodicity-phases.md` (Phase 2 "fixed-cadence variant",
DONE). This is the deferred "Phase 2.5 / zero-drift per-window reschedule" task in
`docs/dev/todo-and-known-bugs.md`. It does NOT change the recording format or the
recorded trajectory, and it keeps every locked decision from the design doc:
**replay as-is** (we only choose WHEN to relaunch the recorded inertial trajectory,
never re-aim or re-plan it), faithful-only, physics-derived tolerance, read all
periods at runtime, supported configs only (cross-parent stays
`UnsupportedCrossParent`, Phase 4, not this task).

Phase 4 (cross-parent / interplanetary) is a SEPARATE task and is explicitly out of
scope here.

---

## 0. The problem (recap, precise)

Phase 2 picks a FIXED relaunch cadence `P = m * dominantPeriod`, where `m` is a whole
multiple in `[1, MaxJointMultiples=16]` chosen to best re-align the dropped
constraints (`MissionPeriodicity.FindBestJointMultiple` / `JointStepResidual`). The
dominant constraint (the longest-period intercept, e.g. the Mun) is locked EXACTLY at
every relaunch, but the dropped constraint (e.g. Kerbin's launch-pad rotation) is only
re-aligned to within the per-cycle residual, and **a fixed cadence makes that residual
ACCUMULATE across relaunches**: launch `n` lands the dropped body at
`n * (m*dominantPeriod mod period_drop)` from its recorded phase. For the stock
"Kerbal X" Mun mission (Kerbin sidereal rotation `21549.425 s` + Mun orbit
`138984.38 s`) the best fixed multiple is `m=9` (`~14.5 d`), per-cycle pad residual
`~993 s` (`~16 deg`); the 1st relaunch is `~993 s` off, the 2nd `~1986 s`, the 3rd
`~2978 s`, growing until it wraps near half a rotation (`~10775 s`, `~180 deg`). Only a
handful of relaunches near the wrap points are actually pad-aligned. That is
unacceptable for supply-route-grade accuracy.

The exact-zero-drift fixed period is the LCM of the constraint periods (`~4 Kerbin
years` for this case), which is useless as a cadence.

## 1. The goal

Replace the fixed cadence (for the configs that drift, see gating in section 4) with a
**per-window reschedule** that produces the densest attainable sequence of faithful
launch windows, which the player then THROTTLES down to their chosen relaunch period:

- Each candidate relaunch pins the TIGHTEST-tolerance constraint exactly (the launch pad
  for a Mun mission) and requires the others within their own physics tolerance (section
  2.2). This MAXIMIZES the faithful-window frequency and lands the launch pixel-perfect on
  the thing the player sees (the pad), with the looser bodies (the Mun) within tolerance.
- The relaunch error at each launch is the ABSOLUTE per-constraint error (the phase offsets
  cancel, section 2.1), so picking good windows keeps every launch BOUNDED instead of
  accumulating drift the way a fixed cadence does.
- The player's chosen period is a THROTTLE (`minSpacing`): the schedule launches at faithful
  windows no more often than the player asked (section 2.3). The point is a good MAXIMUM
  cadence; the player picks a lower one (supply routes launch once in a while, not every
  possible window).

Constraints preserved: replay-as-is; the first-play floor (never relaunch before the
original recording's first real play completes at `spanEndUT`); existing overlap /
instance-cap behavior; all current tests stay green; supported configs only.

---

## 2. The near-coincidence math (pinned)

### 2.1 Why the phase offsets cancel (the key simplification)

A constraint `i` requires body `i` to be at the SAME phase, at the replayed segment
time, that it was at the RECORDED segment time. The recorded segment happened at
`UT0 + offset_i`. Replaying the whole mission launched at UT `L` puts that segment at
`L + offset_i`. The body is at its recorded phase iff
`(L + offset_i) == (UT0 + offset_i)  (mod period_i)`, i.e. **`L == UT0 (mod period_i)`**.
The `offset_i` cancels. So a launch UT `L` satisfies constraint `i` (within tolerance
`tol_i`) iff:

```
CircularPhaseError(L - UT0, period_i) <= tol_i
```

This is consistent with the existing Phase-2 solver, whose `JointStepResidual` measures
`CircularPhaseError(step, period_i)` and never reads `PhaseOffsetSeconds`. (The offsets
still matter for EXTRACTION - which constraints exist, in which order - just not for the
launch-time near-congruence. The over-constrained "two same-body surface offsets" case
the design doc discusses is handled by the extractor emitting only ONE `Rotation(B)` per
body; two constraints with the SAME period are always jointly satisfied here, residual
0.)

### 2.2 Anchor on the TIGHTEST constraint; search `k` (REVISED)

> **Model revision (2026-05-27, after the s15 Mun-mission segment review).** The first
> cut anchored on the DOMINANT = longest-period constraint (the Mun) and required the rest
> within tolerance. That is the worst pairing: it pins the constraint with the *generous*
> tolerance to zero error while demanding the *tight* one (the pad) line up too, so for the
> stock Mun the first faithful window was ~803 Mun periods (~3.5 years) out - unusable.
> The frame-boundary analysis (the Mun-relative segments self-anchor to the live Mun, so
> the Mun only needs to be within its SOI-width tolerance at the SOI seam) shows the Mun
> should NOT be pinned. The corrected model anchors on the **tightest** constraint and
> maximizes window frequency, then lets the player throttle down.

Define each constraint's **duty cycle** `tol_i / period_i` (its fractional tolerance). The
ANCHOR is the constraint with the SMALLEST duty cycle (the tightest band - the launch pad
for a Mun mission). Pin the anchor EXACTLY: candidate launches are `L = UT0 + k *
anchorPeriod` for integer `k >= 1` (the anchor body is in its recorded phase at every such
`L`, since `CircularPhaseError(k*anchorPeriod, anchorPeriod) == 0`). For each candidate:

```
residual(k)  = max over OTHER constraints j of CircularPhaseError(k*anchorPeriod, period_j)
withinTol(k) = every other j has CircularPhaseError(k*anchorPeriod, period_j) <= tol_j
```

**Why the tightest anchor maximizes frequency:** the faithful-window rate is roughly
`anchorPeriod / product(other duty cycles)`. To make windows frequent you must NOT divide
the period by the smallest duty cycle - so you PIN the tightest constraint (taking it out
of the product) and let the looser ones fall within tolerance. Pinning the Mun (duty
~0.064) and requiring the pad (duty ~0.0014) gives `~138984/0.0014 ≈ 3.5 years`; pinning
the pad and requiring the Mun gives `~21549/0.064 ≈ 3.9 days`. ~45x better, and the launch
is now pixel-perfect over the pad (what the player sees) with the Mun within its SOI
tolerance (the transfer still reaches it - the Mun-relative arc re-anchors to the live Mun).

`residual(k)` is the ABSOLUTE worst other-body error at launch `k` (not relative to the
previous launch), so it never accumulates. `tol_j`: the existing physics-derived tolerance
(`ToleranceSecondsFor`) - rotation `period_j * RotationToleranceFraction` (0.25 deg);
orbital `SoiRadius / OrbitalVelocity` (one SOI-crossing time). Reused verbatim.

### 2.3 `NextJointNearCoincidenceUT(afterUT)` + the player throttle

`NextJointNearCoincidenceUT(afterUT)` = the smallest faithful `L > afterUT`: let
`kPrev = floor((afterUT - UT0)/anchorPeriod)`, search `k` in `(kPrev, kPrev +
LookaheadMultiples]`, return the SMALLEST `withinTol(k)` (its `L`), else the `k` with the
smallest `residual(k)` (bounded-best; ties -> smallest `k`). Because we step on the TIGHT
anchor grid and the OTHER constraints are loose, a faithful `k` is found within a small
look-ahead (the Mun's first is ~13 anchor steps), so the search is cheap. `LookaheadMultiples`
**4096** keeps generous headroom for planet packs / 3-constraint configs (the search returns
early at the first `withinTol`). A too-small look-ahead only yields more amber
(bounded-best) launches, never runaway drift (the search always restarts from `kPrev`).

The schedule is the increasing sequence `L_0 < L_1 < ...`:
- `L_0` = first faithful window `>= floorUT` (`floorUT = max(referenceUT, spanEndUT)`, the
  first-play floor + loop-enable reference). `L_0` IS the phase anchor (`PhaseAnchorUT`).
- `L_{n+1}` = `NextJointNearCoincidenceUT(L_n + minSpacing - epsilon)` where **`minSpacing`
  is the player's requested relaunch period** (the throttle). `minSpacing = 0` (or Auto)
  launches at EVERY faithful window = the **maximum attainable cadence**; a larger
  `minSpacing` skips faithful windows so the mission launches no more often than the player
  asked (snapped to a faithful window). The player can never launch FASTER than the max
  cadence (physics floor), only slower. This is the key product point: **engineer the best
  maximum cadence, then let the player pick a lower one** - supply routes will typically
  launch once in a while, not every possible window.

### 2.4 Worked case A - synthetic, fully hand-checkable

`anchorPeriod = 100` (the tightest constraint, pinned), one other `period = 31`,
`tol = 2`, `UT0 = 0`. `L = k*100`; `residual(k) = CircularPhaseError(100k, 31) =
CircularPhaseError(7k mod 31, 31)` (since `100 mod 31 = 7`). Folded error `min(m, 31-m)`:

```
k :  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20 21 22
7k%31: 7 14 21 28  4 11 18 25  1  8 15 22 29  5 12 19 26  2  9 16 23 30
resid: 7 14 10  3  4 11 13  6  1  8 15  9  2  5 12 12  5  2  9 15  8  1
```

- **Fixed cadence (Phase 2):** `FindBestJointMultiple` over `m in [1,16]` minimizes
  `residual(m)` -> `m=9` (residual 1). Launches at `k = 9, 18, 27, 36, 45, ...`,
  residuals **1, 2, 3, 4, 5, ...** - within `tol=2` only for the first two, then drifts
  out (`k=27` is 3 > 2). Accumulating.
- **Zero-drift, no throttle (max cadence):** faithful `k` (`residual <= 2`) are `k = 9, 13,
  18, 22, ...`, residuals **1, 2, 2, 1, ...** (all `<= tol`). Intervals `400, 500, 400` -
  **non-uniform**, **non-accumulating**.
- **Zero-drift, throttle `minSpacing = 700`:** launches `900 -> 1800 -> 3100` (each `>= 700`
  past the prior, snapped to a faithful window; skips `1300`, `2200`). The player launches
  less often than the max cadence. A reviewer can verify `7k mod 31` by hand.

### 2.5 Worked case B - the stock Kerbin-rotation + Mun-orbit case (REVISED)

Duty cycles: pad rotation `tol_rot = 21549.425 * 0.25/360 = ~14.96 s` -> duty `~7e-4`; Mun
intercept `tol_mun = SoiRadius/OrbitalVelocity = 2429559/543 = ~4475 s` -> duty `~0.064`.
The pad is ~45x tighter, so the **anchor is the pad** (`anchorPeriod = 21549.425 s`); the
Mun is the OTHER constraint (`period = 138984.38 s`, `tol = 4475 s`). Faithful `L = UT0 +
j*21549.425` where `CircularPhaseError(j*21549.425, 138984.38) <= 4475`:

```
j   :  1      6      13                ...
Mun resid: ~21549  ~9687  ~2174 (<=4475) ...   first faithful j = 13
```

- **First faithful window:** `j=13` -> `L - UT0 = 13*21549.425 = 280142 s ≈ 3.24 Earth
  days`. The pad is EXACT (j is integer, anchor pinned), the Mun is within `~2174 s ≈ 0.49
  SOI`. Subsequent faithful `j` recur quasi-periodically every ~13 anchor steps, so the
  **maximum cadence is ~3-4 Earth days**.
- **vs the prior (wrong) model** that anchored on the Mun (longest period) and required the
  pad within 0.25 deg: first window `~803 Mun periods ≈ 3.5 YEARS`. The corrected anchor is
  ~400x more frequent AND lands the pad pixel-perfect.
- The exact `j` sequence + the schedule are PINNED by unit tests computed from the exact
  stock values at test time (not hardcoded here, since they depend on the precise doubles).

So the maximum cadence for a Mun supply route is every few days; the player throttles down
to whatever period suits the route (e.g. monthly), and "Warp to..." jumps to the next
scheduled relaunch (section 5).

### 2.6 Degenerate / boundary cases (all pinned by tests)

- `anchorPeriod <= 0` / NaN -> no schedule (fall back to fixed cadence / free loop, as
  `Solve` already guards).
- 0 other constraints (single-constraint, unconstrained) -> `residual(k) == 0` for all
  `k` -> within-tol at `k=1` -> schedule is uniform `UT0 + k*anchorPeriod`. **Identical
  to today's fixed cadence**, so we do NOT attach a schedule for these (section 4).
- Tidal-collapse (all other constraints share the anchor period) -> `residual(k) == 0` ->
  same as above, no schedule.
- A multi-constraint config where the other constraints have a DISTINCT period from the
  anchor DRIFTS regardless of whether Phase-2's `[1,16]` search landed on `m>1`
  (`Method="joint-best-fit"`) or `m=1` (`Method="dominant-intercept"`, "the best of a bad
  set"). Both drift and both need the schedule. So the schedule gate keys on the STRUCTURAL
  property "at least two constraints with distinct periods (a non-zero achievable residual)"
  - computed directly, NOT on the fragile `Method` display string (section 4). The earlier
  draft wrongly excluded `dominant-intercept` configs as "uniform anyway"; they are not.
- NaN / non-positive period among dropped constraints: `CircularPhaseError` returns 0 for
  a degenerate period, which would make that constraint SPURIOUSLY count as satisfied
  (residual 0, `withinTol` true) and silently skip a real body. So `TryBuildRelaunchSchedule`
  FILTERS degenerate dropped periods out of the schedule inputs entirely and emits a Warn
  naming the body (a bad-body-data signal), rather than letting a NaN period read as
  "aligned". A constraint with a valid period is never dropped.
- Future-dated `UT0` / first-play floor: `floorUT = max(referenceUT, spanEndUT)`; `L_0 >=
  floorUT`. The span clock already renders nothing before `PhaseAnchorUT`, so a forward
  `L_0` parks the loop until then, exactly as today.

---

## 3. Schedule representation + engine consumption (pinned)

### 3.1 Options considered

- **(a) Precompute the next K relaunch UTs into the `LoopUnit`; the span clock picks the
  active instance from the list.** Simple to consume, but `LoopUnit` is an immutable
  struct rebuilt only on `BuildSignature` change (which does NOT include the live clock),
  so a fixed-K array cannot cover an arbitrary far-future warp without either an
  unbounded array or a stale tail.
- **(b) A schedule PROVIDER object the span clock queries each frame, holding the
  generation inputs + a lazily-extended cache.** Handles far-future warps (extends on
  demand), keeps the immutable `LoopUnit` cheap (one nullable reference), and keeps the
  generator a pure, fully-testable static. Cost: a small mutable cache, but Unity is
  single-threaded (main-thread only) so no locking is needed.

**Chosen: (b).** It is the only option that handles "warp years ahead" cleanly while
keeping the math pure.

### 3.2 Types

```
// Immutable; one per phase-locked drifting unit, null otherwise. Holds generation
// inputs + a lazily-extended cache of relaunch UTs. Main-thread only.
internal sealed class MissionRelaunchSchedule
    // inputs (immutable):
    double   UT0
    double   AnchorPeriodSeconds      // the tightest constraint's period (pinned exactly)
    double[] OtherPeriods             // the other constraints' periods (within-tolerance)
    double[] OtherTolerances          // their physics tolerances (precomputed)
    double   FloorUT                  // max(referenceUT, spanEndUT)
    int      LookaheadMultiples
    double   MinSpacingSeconds        // the player throttle (0 = every faithful window)
    double   FirstLaunchUT            // == L_0 == the unit's PhaseAnchorUT
    double   MinIntervalSeconds       // representative cadence (for the overlap gate + UI)
    // resolve (lazily extends an internal cache; pure given inputs):
    bool   TryResolveActiveLaunch(double currentUT, out double launchUT, out long cycleIndex)
    double NextLaunchAfter(double currentUT)   // next scheduled L > currentUT (UI / warp)
```

`MissionPeriodicity` gains the PURE generator (the unit-tested heart of this task):

```
internal static double NextJointNearCoincidenceUT(
    double afterUT, double ut0, double anchorPeriod,
    IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances,
    int lookaheadMultiples, out double residualSeconds, out bool withinTolerance)
internal static int SelectAnchorConstraintIndex(   // the tightest duty-cycle constraint
    IReadOnlyList<PhaseConstraint> constraints, IBodyInfo bodyInfo)

internal static bool TryBuildRelaunchSchedule(   // null/false for non-drifting configs
    IReadOnlyList<PhaseConstraint> constraints, Support support, double ut0,
    double floorUT, IBodyInfo bodyInfo, out MissionRelaunchSchedule schedule)
```

`LoopUnit` gains ONE field: `internal MissionRelaunchSchedule RelaunchSchedule { get; }`
(null for every existing config -> existing uniform behavior is byte-identical). A new
constructor overload threads it; existing overloads pass null.

### 3.3 Caching / extension

The cache holds the generated launch UTs as a growing `List<double>` plus the last
`(launchUT, anchor-k)` pair so generation RESUMES from the tail rather than from `L_0`
(honoring the `minSpacing` throttle when stepping ahead):
- `TryResolveActiveLaunch(currentUT)`: extend the list forward (calling
  `NextJointNearCoincidenceUT` from the tail) until the last entry `> currentUT`, then
  return the largest entry `<= currentUT` (binary search). Returns false (parked) when
  `currentUT < L_0`.
- Steady play: `currentUT` advances slowly, the cache extends by 0-1 entries per frame,
  amortized O(1).
- After a big warp / the "Warp to..." button: a one-time extend of O(jump / mean
  interval) relaunches, each an `O(Lookahead)` search - bounded and done once, then
  cached. The schedule always extends to cover `currentUT` (it never stops short and
  reintroduces drift for a legitimate warp - even a warp to the LCM horizon of section 0
  is just more within-tol launches). The ONLY safety cap is on total generation STEPS to
  bound per-frame CPU against a pathological tiny anchor period (e.g. a malformed body):
  `MaxScheduleSteps` (generously above any realistic within-tol count). If a single
  resolve would exceed it, that unit FALLS BACK to the fixed-cadence path (drop the
  schedule, log a Warn) rather than to a drifting uniform extrapolation - the fallback is
  the existing, correct fixed cadence, never a silent drift. Pinned so normal warps never
  trip it.
- The schedule object is rebuilt only when `MissionLoopUnitBuilder.Build` rebuilds the
  `LoopUnitSet` (signature change), so the cache lifetime == the unit lifetime. The
  engine and the UI build SEPARATE `LoopUnitSet`s (separate caches), but the generator is
  PURE over the snapshotted inputs only (it reads no live state - `UT0`, periods,
  tolerances, floor are all copied into the schedule at build), so both produce identical
  schedules and display can never diverge from playback.

### 3.4 Span-clock consumption

The single change point is the single-instance span clock. `TryComputeSpanLoopUT` and
`DecideUnitMemberRender` gain a trailing `MissionRelaunchSchedule schedule = null`
parameter:
- `schedule == null`: existing uniform code path, untouched, byte-identical.
- `schedule != null`: resolve the active launch `L` via `TryResolveActiveLaunch`. If not
  launched yet (`currentUT < L_0`) return false (parked, same as `currentUT <
  phaseAnchorUT` today). Else `phaseInCycle = currentUT - L`; `cycleIndex` = the
  schedule index; `loopUT = spanStartUT + min(phaseInCycle, span)`; `isInInterCycleTail =
  (phaseInCycle >= span)` (parked between launches -> caller hides all members, render
  nothing in the gap). No "cadence==span seamless wrap" branch is needed: a scheduled
  unit always has interval `> span` (it is non-overlapping by gating, section 4), so the
  gap between launches is real and the inter-cycle-tail naturally engages, exactly like
  today's `cadence > span` single-instance units - just with non-uniform gaps.

Consumers that pass the schedule through (all already receive the `LoopUnit` or its
fields) - the first review round found there are THREE engine call sites, not the two an
earlier draft implied:
- `GhostPlaybackEngine.UpdateUnitMemberPlayback` single-instance branch (the
  `DecideUnitMemberRender` call ~`GhostPlaybackEngine.cs:359`) -> pass `unit.RelaunchSchedule`.
- `GhostPlaybackEngine` loop-synced-debris parent-span branch (`TryComputeSpanLoopUT`
  ~`GhostPlaybackEngine.cs:1816`): this resolves DEBRIS off `parentUnit.PhaseAnchorUT /
  CadenceSeconds`, so it MUST pass `parentUnit.RelaunchSchedule` (NOT the debris's own).
  Missing this would desync ride-along debris from its scheduled parent's non-uniform
  launches - an explicit, easy-to-miss site. A Phase-B in-game test asserts debris rides
  the scheduled parent's launches.
- `ParsekKSC` `DecideUnitMemberRender` -> `unit.RelaunchSchedule`.
- `ParsekTrackingStation` via `ResolveTrackingStationSampleUT` (already takes the unit ->
  reads `unit.RelaunchSchedule` internally).
- `ParsekUI` flight-map custom marker + `GhostMapPresence` via the same
  `ResolveTrackingStationSampleUT`.

### 3.5 Overlap under a non-uniform schedule

`UnitMemberOverlaps(unit)` keys on `unit.OverlapCadenceSeconds < span`, so the gating
MUST be expressed through `OverlapCadenceSeconds`, not just asserted - the first review
round flagged that an attached-but-ignored schedule on an overlapping unit would silently
fall into the overlap engine path. The contract:

- When the builder attaches a schedule it SETS `OverlapCadenceSeconds = max(span, min
  scheduled interval)` (and `CadenceSeconds` likewise `>= span`), so `UnitMemberOverlaps`
  is ALWAYS false for a scheduled unit. INVARIANT: a `LoopUnit` with `RelaunchSchedule !=
  null` satisfies `UnitMemberOverlaps == false`.
- Gating condition 4 is computed from the SCHEDULE's actual minimum interval (after the
  player throttle), NOT from a pre-existing `OverlapCadenceSeconds`. The realistic case (the
  faithful windows are days apart, longer once throttled) gives a min interval `>> span` so
  this is comfortably satisfied. The edge the review raised - a multi-DAY mission span (a
  long Mun stay) where `span` could approach the min interval - is handled by the rule: if
  the schedule's MINIMUM interval `< span`,
  the builder REJECTS the schedule (drop it, keep the existing fixed-cadence overlap path,
  log a Warn). So a scheduled unit is non-overlapping by the invariant above, never by
  hope.

Therefore the overlap engine path (`UpdateOverlapPlayback` /
`TryComputeNewestOverlapPlaybackUT` / `ComputeNewestMissionInstanceSpanLoopUT`) never sees
a scheduled unit, and overlap behavior is unchanged. A unit test asserts
`UnitMemberOverlaps == false` for EVERY attached schedule; an in-game test guards that an
overlapping mission still overlaps (no schedule attached).

---

## 4. Backward-compatibility / gating (no regression)

A schedule is attached (and the fixed cadence replaced) ONLY when ALL hold:
1. `bodyInfo != null` (phase-lock wiring active; tests / unwired -> no schedule).
2. The solution phase-locks (`ShouldPhaseLock`) and is Supported, AND the constraint set is
   STRUCTURALLY drifting: after picking the anchor there is at least one OTHER constraint
   with a DISTINCT period (a non-zero achievable residual). This is computed directly from
   the constraints, NOT from the `Method` display string - it captures both the Phase-2
   `joint-best-fit` (`m>1`) and `dominant-intercept` (`m=1`, the best of a bad `[1,16]` set)
   configs, since both drift and both benefit from zero-drift. `single-rotation` /
   `single-orbital` / `tidal-collapse` / `unconstrained` configs have residual 0 (a uniform
   schedule), so they keep the exact fixed cadence and get NO schedule.
3. `TryBuildRelaunchSchedule` succeeds (after filtering degenerate other-periods, at least
   one valid distinct-period other constraint remains; finite floor; the safety step-cap not
   tripped, section 3.3).
4. The resulting schedule is non-overlapping: its MINIMUM interval `>= span` (section 3.5).
   If not, the schedule is rejected and the unit keeps the fixed-cadence overlap path.

Otherwise: `unit.RelaunchSchedule == null` and EVERYTHING is byte-identical to the merged
Phase-2 behavior. Unsupported / single-constraint / unconstrained / non-phase-locked /
non-mission ghosts: untouched. The fixed-cadence path remains as the fallback AND the
safety net.

`BuildSignature` already folds the transited-body set + their live rotation/orbit periods
when `bodyInfo` is supplied, so a body-geometry change re-derives the schedule and
rebuilds. It does NOT currently fold `SoiRadius` / `OrbitalVelocity` (which feed the
orbital tolerance), so a planet pack that changed a body's SOI radius WITHOUT changing its
orbit period would not rebuild; this task ADDS those two to `AppendTransitedBodyDigest`
(cheap, same loop) to close the gap, so a tolerance-affecting body change also rebuilds.
No persisted `Mission` field, no recording-format change, no save migration: the schedule
is derived each build, `PhaseAnchorUT` still reuses `Mission.LoopAnchorUT` semantics (now
`= L_0`).

---

## 5. UI impact

`UI/MissionsWindowUI.cs` (already builds a display-mirror `LoopUnitSet`):
- **TTL "Time to launch" countdown:** `ComputeNextRelaunchUT(unit, now)` branches: if
  `unit.RelaunchSchedule != null` return `schedule.NextLaunchAfter(now)`; else the
  existing uniform `phaseAnchor + n*relaunchCadence`. So the countdown targets the next
  SCHEDULED relaunch and never ticks to "T- 0s" on a skipped window.
- **Period cell:** for a scheduled unit the cadence is non-uniform, so the cell shows the
  mean / typical interval with a "varies" marker, e.g. `~Nd (Mun window, varies)`, via a
  new branch in `BuildPeriodCellDisplay` / a pure helper. The existing fixed-cadence
  display is unchanged for non-scheduled units. (Exact wording decided in Phase C; pinned
  by a pure formatting test.)
- **"Warp to..." button:** warps to `periodicity.NextRelaunchUT`, which is sourced from
  `ComputeNextRelaunchUT`. So the SINGLE `ComputeNextRelaunchUT` branch above retargets
  BOTH the TTL countdown and the warp button to the next scheduled relaunch; the warp
  button itself needs no further change, but it is not independently safe - it inherits
  correctness from that one branch, which the test plan covers.
- The amber within-tolerance readout already exists (`WithinTolerance`); for a scheduled
  unit it reflects whether the schedule's launches are within tolerance (true once the
  look-ahead reaches a within-tol recurrence; amber if the config is bounded-best only).

---

## 6. Diagnostic logging

Subsystem tags `MissionPeriodicity` / `Mission` (existing). Every decision logged:
- **Schedule build:** the `PhaseLock APPLIED` line carries `zeroDrift=yes` + `firstLaunch`
  + `minInterval` (anchor period, the player throttle, and the first scheduled interval), or
  `zeroDrift=no` / `zeroDrift=rejected-would-overlap` when a drifting config does NOT get a
  schedule (so the branch is never silent). Implemented on the existing `PhaseLock APPLIED`
  line.
- **Generator (`NextJointNearCoincidenceUT`):** `Verbose` per relaunch generated:
  `afterK -> chosenK`, residual, withinTol, method (within-tol vs bounded-best). Schedule
  EXTENSION events `VerboseRateLimited` (shared key) to avoid spam during a warp.
- **Safety cap hit:** `Warn` if `MaxScheduleHorizonMultiples` is reached (fell back to
  uniform extrapolation) - a "this config cannot be scheduled accurately past horizon"
  signal.
- **Span clock:** no per-frame logging in the pure helper (callers own rate-limiting);
  the scene drivers keep their existing `VerboseRateLimited` unit summaries, which now
  also note `scheduled=yes/no`.

---

## 7. Test plan

Pure math fully unit-tested (`MissionPeriodicityTests` + a new test class if it grows
large); wiring is xUnit where pure + in-game where it needs the live engine. Each test
states the regression it guards.

**Phase A - pure solver (`MissionPeriodicity`):**
- `NextJointNearCoincidenceUT`: the synthetic 100/31/tol=2 case (section 2.4) - assert the
  within-tol `k` sequence `9,13,18,22` and the non-uniform intervals (guards the core
  rule + non-accumulation).
- The stock Kerbin-rotation + Mun-orbit case - assert each generated relaunch's
  `residual <= tolerance` once the look-ahead reaches the within-tol recurrence, and that
  the residuals do NOT grow like the fixed-cadence `993, 1986, 2978` (guards the actual
  zero-drift property on the real case; values computed from the exact stock doubles, not
  hardcoded).
- Tolerance boundary: a `k` whose residual is just within vs just outside flips
  `withinTolerance` (guards the green/amber threshold).
- `SelectAnchorConstraintIndex` picks the tightest duty-cycle constraint (the pad, not the
  Mun); the throttle (`minSpacing`) skips faithful windows; no-throttle = every window.
- Degenerate: `anchorPeriod <= 0` / NaN, NaN other period, 0 other constraints
  (uniform schedule), tidal-collapse (uniform) -> no throw, correct fallback.
- First-play floor: `L_0 >= floorUT`; future-dated `UT0` resolves a forward `L_0`.
- Bounded-best fallback: a config with no within-tol `k` in the look-ahead returns the
  min-residual `k` (bounded), never throws, `withinTolerance == false`.
- `TryBuildRelaunchSchedule`: returns a schedule only for joint-best-fit configs; null for
  single/tidal/unconstrained/unsupported (guards the gating).
- `MissionRelaunchSchedule.TryResolveActiveLaunch` / `NextLaunchAfter`: monotonic, correct
  active-launch for a currentUT between / on / before launches; lazily extends; far-future
  warp resolves correctly; parked before `L_0` (guards the consumption contract).
- Log-assertion tests: schedule-build `Info`, generator `Verbose`, gating-skip `Info`,
  safety-cap `Warn` lines appear with the right fields.
- **REVIEW CHECKPOINT (math core) after Phase A.**

**Phase B - schedule representation + span-clock consumption:**
- `TryComputeSpanLoopUT` / `DecideUnitMemberRender` with `schedule == null`: byte-identical
  to today (regression guard - assert outputs match the no-schedule overload).
- With a schedule: active-launch resolution, phase clamp, inter-cycle-tail between
  launches, parked before `L_0` (pure tests).
- `MissionLoopUnitBuilder.TryBuildMissionUnit`: a joint-best-fit non-overlapping config
  attaches a schedule with `PhaseAnchorUT == L_0`; a single/unsupported/overlapping config
  attaches none and is byte-identical (guards gating + no regression).
- Overlap untouched + invariant: assert `UnitMemberOverlaps == false` for EVERY attached
  schedule (unit test); a config that self-overlaps (min interval `< span`) never gets a
  schedule (unit test); in-game test that an overlapping mission still overlaps.
- In-game (`InGameTests/RuntimeTests.cs`): a looped multi-constraint mission renders one
  faithful instance per scheduled launch and nothing between, across flight (live engine).
- In-game: ride-along DEBRIS of a scheduled member launches in lockstep with its scheduled
  parent (guards the easy-to-miss `parentUnit.RelaunchSchedule` site at engine:1816).
- **REVIEW CHECKPOINT (the risky integration) after Phase B.**

**Phase C - scene drivers + UI:**
- KSC / TS / flight parity: the same scheduled member renders identically in all three
  (in-game, since it needs the live scenes).
- UI pure helpers: `ComputeNextRelaunchUT` returns the next scheduled launch for a
  scheduled unit (xUnit); period-cell "varies" formatting (pure helper test); the TTL
  countdown targets the scheduled launch.

**Phase D - caching / persistence / overlap / perf:**
- Cache extension correctness under repeated frames + a warp (xUnit on the schedule
  object).
- Save/load: a looped scheduled mission re-derives an identical schedule after reload (no
  new persisted state); in-game or a source-gate test.
- Perf: generation cost bounded (assert the per-relaunch search is `O(Lookahead)` and the
  steady-state extend is `<= 1` entry/frame).

---

## 8. Phase breakdown (with review checkpoints)

- **Phase A - pure solver.** `NextJointNearCoincidenceUT`, `TryBuildRelaunchSchedule`,
  `MissionRelaunchSchedule` (inputs + lazy cache + resolve), the `LoopUnit.RelaunchSchedule`
  field + constructor overload (no consumers yet). Fully unit-tested. No behavior change in
  game (nothing attaches a schedule until Phase B wires the builder). **Review checkpoint
  (math core).**
- **Phase B - builder wiring + span-clock consumption.** Gate + attach the schedule in
  `TryBuildMissionUnit` (section 4, structural-drift gate + the `OverlapCadenceSeconds >=
  span` invariant + the `SoiRadius`/`OrbitalVelocity` signature additions); thread the
  schedule through `TryComputeSpanLoopUT` / `DecideUnitMemberRender`; the engine
  single-instance branch (engine:359) passes `unit.RelaunchSchedule` and the loop-synced
  debris branch (engine:1816) passes `parentUnit.RelaunchSchedule` (the easy-to-miss site).
  Fixed cadence stays the fallback. xUnit + in-game. **Review checkpoint (risky
  integration).**
- **Phase C - three scene drivers + UI parity.** KSC / TS map / flight map markers +
  `MissionsWindowUI` TTL / period cell / warp button. Mostly automatic (consumers already
  route through the threaded helpers); UI gets the new branches + tests.
- **Phase D - caching / persistence / overlap / perf hardening.** Cache extension under
  warp, safety cap, save/load re-derive, overlap-untouched guard, perf bounds.

Reviews at the Phase A and Phase B boundaries (the math core and the risky integration),
not after every commit and not all lumped at the end. Per the project workflow, each
commit runs `dotnet test` green, verifies the deployed DLL, and updates `CHANGELOG.md` +
`docs/dev/todo-and-known-bugs.md` (and this file / `.claude/CLAUDE.md` if layout/workflow
changes) in the SAME commit.

---

## 9. What does NOT change

- The recording format / `Recording` / sidecars (the schedule is derived; nothing
  persisted).
- The fixed-cadence path for single-constraint / tidal / unconstrained / unsupported /
  non-phase-locked configs (byte-identical).
- The overlap path + instance cap (scheduled units are non-overlapping; overlap engine
  never sees them).
- Non-looping ghosts and per-recording (non-mission) auto-loop (untouched).
- The replay-as-is locked decision (we only choose WHEN to relaunch; never re-aim).
- Phase 4 cross-parent / interplanetary (separate task, stays `UnsupportedCrossParent`).

## 10. References

- `docs/dev/design-mission-periodicity.md` - the model, constraints, locked decisions.
- `docs/dev/plans/mission-periodicity-phases.md` - Phase 2 (the fixed-cadence variant this
  replaces for drifting configs).
- `docs/dev/todo-and-known-bugs.md` - the "zero-drift per-window reschedule" + "first-play
  floor" + Phase 2 entries.
- `Source/Parsek/MissionPeriodicity.cs` - `ExtractConstraints`, `Solve`,
  `FindBestJointMultiple`, `JointStepResidual`, `CircularPhaseError`, `NextWindow`,
  `IBodyInfo`, `ToleranceSecondsFor`.
- `Source/Parsek/MissionLoopUnitBuilder.cs` - `TryBuildMissionUnit`,
  `QuantizeCadenceToMultipleOfP`, `BuildSignature`.
- `Source/Parsek/GhostPlaybackLogic.cs` - `LoopUnit`, `LoopUnitSet`,
  `TryComputeSpanLoopUT`, `DecideUnitMemberRender`, `ResolveTrackingStationSampleUT`,
  `UnitMemberOverlaps`, the overlap helpers.
- `Source/Parsek/UI/MissionsWindowUI.cs` - `ComputeNextRelaunchUT`, TTL column, period
  cell, "Warp to..." button.
