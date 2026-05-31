# Design: Mission Periodicity (launch-window phase-locked looping)

*Status: design note for a feature that follows the Mission Abstraction +
whole-mission looping work (PR #958, now merged to `main`). It does NOT change the
recording format or the recorded trajectory; it changes only WHEN a looped mission
relaunches, so the replay lines up with the live sky. Builds directly on
`MissionLoopUnitBuilder` / `ComputeTrimmedMemberWindows`, the span clock in
`GhostPlaybackLogic`, and the map-presence loop fix. This is the accuracy foundation
for SUPPLY ROUTES (which consume "when does this mission faithfully repeat"), so the
window scheduling must be physically correct, not approximate-looking. Ships as its
own PR, built in the phases in `docs/dev/plans/mission-periodicity-phases.md`.*

---

## Why this exists

Looping a mission as a unit (#958) replays the recorded trajectory faithfully
relative to its own recorded clock, but it launches each replay at an arbitrary
time (the UT the loop was enabled, `Mission.LoopAnchorUT`, used as the span
clock's `phaseAnchor`). For a mission that stays on one body that is fine. For a
mission that depends on celestial geometry (a launch into orbit, and especially
an inter-body transfer), an arbitrary launch time means the replay no longer
matches the live solar system:

- The recorded **orbit** is stored in the parent body's **inertial frame** at a
  fixed orientation (Keplerian `inclination` / `LAN` / `argumentOfPeriapsis`).
  Replayed at a different time it is drawn at that same recorded orientation, but
  the **launch body has rotated**, so the orbit's periapsis (originally over the
  launch site) no longer sits over the live launch site, and the surface-relative
  ascent (which renders at the launch site's *current* world position) does not
  connect to the orbit's recorded insertion point.
- The recorded **transfer** reaches the **target body's recorded position** at the
  recorded encounter time. Replayed at a different time the **target body is at a
  different orbital phase**, so the transfer ellipse's far point aims at empty
  space, no intercept happens, and the target-relative arc (the Mun approach +
  landing) renders at the target's *current* location, disconnected from the
  transfer.

### Concrete evidence (playtest 2026-05-26, `logs/2026-05-26_2329_mun-mission-map-desync`)

The "Kerbal X" Mun-landing tree, looped, member #15 replays a recorded Kerbin
transfer ellipse (`sma=6,274,896 ecc=0.873 peri=799 km`) then a recorded Mun
approach hyperbola (`sma=-720,778 ecc=1.380 peri=273 km`), with a Kerbin -> Mun
SOI handoff baked into the recording. The loop ran at an arbitrary
`phaseAnchor=54042.92` / `5861.38`, unrelated to the recorded launch UT
(`span start = 3991.76`). There is zero launch-window / re-aim / phase-align logic
in the codebase today (grep count 0). The result on the map and in the Tracking
Station: the orbit is not over KSC and the transfer does not reach the Mun.

This is INHERENT to replaying a celestial-phase-dependent trajectory at the wrong
time. It is not a regression, and it is not caused by the map-presence epoch-shift
fix (`loopEpochShiftSeconds`) - that fix only places the ghost at the right phase
ALONG its recorded ellipse; it cannot move the live target body or re-orient the
recorded inertial ellipse.

---

## Terminology

- **Faithful (replay):** the looped ghost's map/Tracking-Station trajectory lines up
  with the live sky - the orbit sits over the launch site and any transfer reaches
  the target body where it actually is.
- **Constraint:** a phase requirement one included segment imposes. `Rotation(B)` (a
  surface/atmospheric segment must sit over its ground spot and connect to its
  inertial orbit) or `Orbital(C)` (an SOI entry must reach body C where C will be).
  Each carries a recurrence period + a phase offset (the segment's recorded UT
  relative to `UT0`).
- **`UT0`:** the recorded launch UT = the trimmed mission's span start (the earliest
  included member's start, from `ComputeTrimmedMemberWindows`).
- **`P`:** the recurrence period at which the included config's constraints all line
  up (best-fit within tolerance). The next faithful launch is the smallest
  `UT0 + k*P >= now`.
- **Tolerance:** the max phase error still counted as faithful, derived from physics
  (orbital: `~SOI_radius(C) / orbital_velocity(C)`; rotation: a small fraction of a
  degree).
- **Residual:** the best-fit window's actual max phase error across constraints;
  green when `<= tolerance`, flagged when not (over-constrained config).
- **Config:** which composition intervals are checked (`Mission.ExcludedIntervalKeys`)
  - this determines the included segments and therefore which constraints exist.

---

## The goal

For each replay of a looped mission to be geometrically faithful, the launch must
happen at a UT where the bodies the mission depends on are back in (close to)
their recorded-launch configuration. Faithful launch times are:

```
UT_launch = UT0 + k * P          (k = 0, 1, 2, ...)
```

where:

- `UT0` = the recorded launch UT = the mission owner member's recorded `StartUT`
  (the earliest member after the span sort in `MissionLoopUnitBuilder`; the span
  start).
- `P` = the celestial-geometry repeat period for the mission: the period at which
  every body the mission depends on returns to its recorded-launch phase.

The loop should relaunch on a cadence that is a multiple of `P`, with the span
clock's `phaseAnchor` aligned so cycle 0 lands on a faithful `UT0 + k*P`.

---

## What determines `P`

`P` is set by which bodies and frames the LOOPED replay actually depends on, and
that depends on the mission CONFIGURATION - which composition intervals are checked
(`Mission.ExcludedIntervalKeys`). So `P` is derived from the trimmed member set
(`MissionLoopUnitBuilder.ComputeTrimmedMemberWindows`, the same source of truth the
span + cadence already use), NOT from the full recorded tree. All periods are read
from the live universe at build time (never hardcoded - planet packs like RSS change
them).

Each INCLUDED segment contributes only the phase constraint its own frame imposes:

1. **Surface / atmospheric segment on a rotating body B** (launch ascent, landing,
   surface ops) -> constrains **B's rotation phase** (repeating every
   `B.rotationPeriod`, Kerbin sidereal day ~21,549 s) ONLY when the INCLUDED set ALSO
   contains an inertial-orbit segment of the SAME body B (the ascent->orbit /
   orbit->descent hand-off). The rotation phase matters solely to line that hand-off
   up over the launch/landing site. A surface arc is recorded surface-relative and
   renders at its correct ground location at ANY universe time (it rotates with B), so
   a **surface-only / atmospheric-only config of B imposes NO phase constraint** (this
   is the "no-inertial-arc -> MinCycleDuration" edge case). The rotation constraint
   requires the surface<->inertial-orbit hand-off, not a bare surface segment.

2. **Inertial orbit segment around body B** -> by itself imposes **no** phase
   constraint (B is always there; an inertial orbit is faithful at any UT). Its only
   role is to ENABLE rule 1's `Rotation(B)` hand-off: the rotation lock is emitted iff
   the INCLUDED set has BOTH a surface/atmospheric segment of B AND an inertial-orbit
   segment of B. "Adjacent" is NOT list-adjacency or UT-adjacency: the hand-off is a
   property of the included set (does any included surface segment of B AND any
   included inertial orbit of B exist?), so trimming away EITHER side removes the
   constraint (the surviving bare arc/orbit is free). The orbit alone contributes no
   NEW constraint - it only completes the pair rule 1 needs.

3. **SOI entry into body C** (any intercept inside the included span - a capture, a
   transient flyby, OR a gravity assist; a non-capturing pass is just as binding as a
   capture, because the recorded arc still only reaches C where C will be) ->
   constrains C's position at the encounter; because the transfer time
   (launch -> encounter) is fixed in the recording, that constrains the launch UT
   modulo a recurrence period that depends on C's relationship to the LAUNCH BODY:
   - **C orbits the launch body directly** (`C.referenceBody == launchBody`; Mun /
     Minmus from Kerbin): the recurrence is simply `C.orbit.period` (Mun ~138,984 s) -
     the launch body and the transfer frame are the same fixed reference, so aligning
     C's mean anomaly aligns the whole geometry.
   - **C is a sibling of the launch body** (`C.referenceBody == launchBody.referenceBody`;
     an interplanetary target like Duna, both orbiting the Sun): the launch body ALSO
     moves around the shared parent during the transfer, so the recurrence is the
     **synodic period** of the launch body and C about that shared parent
     (`1 / |1/T_launchBodyOrbit - 1/T_C|`), NOT `C.orbit.period`. Using `C.orbit.period`
     here would NOT realign the transfer.
   - **Anything deeper** (C is a moon of a sibling, multi-hop / gravity-assist chains
     stacking several transited bodies): each transited body adds its own constraint;
     see the phased plan for when these land.
   The **launch body** is the body of the earliest INCLUDED surface/atmospheric
   segment (or, if none, the `bodyName` of the earliest included `OrbitSegment`). The
   transited bodies are read from the SOI changes (the `bodyName` field across the
   included segments' `OrbitSegments`).

`P` is the joint resonance (least common period) of just the constraints the
included segments impose. An empty constraint set (e.g. looping a bare Kerbin orbit
with the ascent trimmed off) collapses `P` to `MinCycleDuration` - faithful at any
time. The constraint periods are generally **incommensurate**, so an exact common
period rarely exists; the joint period is a best-fit: the smallest `P` for which
`P mod` each constraint period is within a chosen tolerance.

### Worked examples (same recorded Mun-landing tree, different configs)

- **Launch + Kerbin orbit only** (Mun transfer / landing intervals unchecked): only
  Kerbin's rotation matters -> `P = Kerbin.rotationPeriod` (~21,549 s). The Mun is
  not in the included span, so its phase is irrelevant.
- **Full mission** (through the Mun landing): Kerbin rotation AND the Mun's orbital
  phase must both line up -> `P = joint(Kerbin rotation, Mun orbit)` - the long
  launch-window cadence. (This joint result is the Tier-2 / Phase-2 best-fit; Tier-1 /
  Phase-1 locks only the dominant `Orbital(Mun)` intercept and accepts the
  Kerbin-rotation residual - see Proposed design.)
- **Orbital coast only** (ascent + all surface segments trimmed off): no
  rotating-surface segment is included, so nothing constrains the phase ->
  `P = MinCycleDuration`, loop as fast as you like.

### Important consequences

- Because `P` is **config-dependent**, it must be recomputed whenever the included
  segments change and folded into the loop-unit rebuild signature alongside
  `ExcludedIntervalKeys` (which `BuildSignature` already hashes).
- For a config that reaches another body, the long `P` is the **physically real
  launch-window cadence** - you cannot faithfully launch to the Mun more often than
  Mun windows recur - so it is correct, not a limitation of the feature.
- This **naturally bounds the instance count**: a faithful inter-body cadence is
  long, so overlap (period < span) rarely engages and only a few instances are ever
  live. The separate "limit ghosts per render distance instead of per whole mission
  span" idea (see Relationship to other work) is therefore parked behind this;
  revisit it only if frequent same-body looping still needs it.

---

## Proposed design (two tiers)

### Tier 1 - intercept-period phase lock (the visible fix)

Compute `P` per looping mission and phase-lock the loop:

- **Config that stays in one body's SOI** (no intercept in the included span):
  `P = the launch/landing body's rotationPeriod` when the included set has a
  surface<->inertial-orbit hand-off (a surface/atmospheric segment of B AND an
  inertial orbit of B that must line up over the launch/landing site), else
  `MinCycleDuration` (a bare inertial orbit with no surface segment, OR a bare surface
  /atmospheric arc with no inertial orbit of B, imposes no phase constraint).
- **Config that reaches another body** (the flagship Mun case, which has TWO
  constraints: `Rotation(Kerbin)` + `Orbital(Mun)`): Tier 1 deliberately locks ONLY
  the dominant intercept constraint - `P` = the target's recurrence period (rule 3) -
  so the transfer actually reaches the target, and **accepts the residual
  launch-body-rotation offset** (the orbit may sit slightly off the live launch site).
  The intercept is the dominant visual break; the launch-site offset is the smaller
  one and is closed in Tier 2.

This is an explicit Tier-1 SIMPLIFICATION, not the over-constrained best-fit: Tier 1
locks the single dominant constraint and ignores the others; **Tier 2** (the joint
best-fit, below) is what respects ALL the included config's constraints simultaneously
and reports the residual. The "never silently drop a constraint" rule in Key decisions
is the Tier-2/over-constrained contract; Tier 1 drops the rotation residual KNOWINGLY
and logs that it did. Both tiers derive their bodies/frames from the INCLUDED segments
(the trimmed member set), so changing which intervals are checked re-derives `P`.

Mechanics:

- Quantize the effective loop cadence to the nearest multiple of `P` at or above
  the existing floors (`LoopTiming.MinCycleDuration` and the overlap-cap floor
  from `ComputeEffectiveLaunchCadence`).
- Align `phaseAnchor` to `UT0 + k*P` (the first faithful launch at or after the UT
  the loop was enabled) instead of the raw enable UT, so every cycle launches
  faithfully.
- Everything downstream (the span clock `TryComputeSpanLoopUT`, per-member windows,
  the map epoch-shift) is unchanged - it already replays correctly relative to
  `phaseAnchor`; we are only choosing a geometry-aware `phaseAnchor` + cadence.

### Tier 2 - joint best-fit + multi-transfer (polish)

- Joint best-fit of launch-body rotation AND target orbital phase so the orbit
  also sits over the launch site (ascent connects to the orbit).
- Multi-transfer missions (Kerbin -> Mun -> Minmus, interplanetary), where several
  bodies' phases must align: product resonance, even longer / more approximate.
- A tolerance / quality readout so the user knows how close the best-fit window is.

---

## Where it plugs into the existing code

- **A new pure constraint extractor** turns the trimmed member set
  (`MissionLoopUnitBuilder.ComputeTrimmedMemberWindows`) into the ordered list of
  phase constraints (rotation-of-B / orbital-of-C-relative-to-its-parent), reading
  the per-segment frame (surface vs inertial orbit vs SOI entry) from the recordings'
  `OrbitSegments` / TrackSection reference frames and the bodies from `FlightGlobals`.
  Pure + unit-testable (synthetic body sequences); this is the heart of the feature.
- **A new pure `P` solver** takes that constraint list + the recorded inter-segment
  time offsets and returns the best-fit recurrence period `P`, the next launch UT
  (`UT0 + k*P >= now`), and a residual/quality (max phase error vs the physics
  tolerance). Best-fit = smallest `P` within tolerance via continued-fraction /
  Stern-Brocot rational approximation of the period ratios, bounded by a max-`P`
  search. The **tolerance is derived from physics, not a free knob**: for an orbital
  constraint, `tol ~ SOI_radius(C) / orbital_velocity(C)` (a tight bound - the Mun
  moves ~543 m/s, so a few hundred seconds of error already misses its ~2.4 Mm SOI);
  for a rotation constraint, a small fraction of a degree of the body's spin.
- **`MissionLoopUnitBuilder.TryBuildMissionUnit`** consumes the solver: it snaps
  `phaseAnchorUT` to the next faithful launch (`UT0 + k*P`) and quantizes the cadence
  to a multiple of `P` (on top of the existing `MinCycleDuration` / overlap-cap floors
  in `ComputeEffectiveLaunchCadence`). The snapped anchor is stored in
  `Mission.LoopAnchorUT` (already persisted + already hashed by `BuildSignature`).
- **`MissionLoopUnitBuilder.BuildSignature`** folds in the constraint inputs (the
  transited-body set + their periods) so the cached unit rebuilds when the included
  segments or the live body geometry change. (It already folds `ExcludedIntervalKeys`
  + `LoopAnchorUT`.)
- No new persisted `Mission` field is required for faithful-only behavior (`P` is
  derived; the snapped anchor reuses `LoopAnchorUT`). No change to the recording
  format, `Recording`, the span clock, the map epoch-shift, or the watch handoff.

---

## Data Model

All new types are derived (computed each loop-unit build) - **nothing new is
persisted** (`P` is recomputed; the snapped launch UT reuses the existing
`Mission.LoopAnchorUT`). Shape (indented, not final names):

```
enum ConstraintKind { Rotation, Orbital }

struct PhaseConstraint            // one per constraining included segment
    ConstraintKind Kind
    string BodyName               // B (Rotation) or C (Orbital)
    double PeriodSeconds          // rotationPeriod, orbit.period, or synodic (Phase 4)
    double PhaseOffsetSeconds     // segment's recorded UT - UT0 (the fixed offset)
    bool   RelativeToParent       // Orbital: same-parent (false) vs cross-parent (true)

enum Support                      // Supported, or a reason it is not yet solvable:
    Supported
    UnsupportedCrossParent        //   sibling/interplanetary target (until Phase 4)
    UnsupportedRendezvous         //   aligns to another vessel, not a body (out of scope)
    UnsupportedMultiConstraintPreP2  // >1 independent constraint before Phase 2

struct PeriodicitySolution
    double  P                     // recurrence period (MinCycleDuration if unconstrained)
    double  NextWindowUT          // smallest UT0 + k*P >= now (k may be negative)
    double  ResidualSeconds       // best-fit max phase error across constraints
    bool    WithinTolerance       // ResidualSeconds <= tolerance
    Support Support

interface IBodyInfo               // test seam over FlightGlobals
    double RotationPeriod(string bodyName)
    double OrbitPeriod(string bodyName)
    string ReferenceBodyName(string bodyName)   // parent body (for same-parent vs sibling)
    double SoiRadius(string bodyName)           // for the tolerance formula
    double OrbitalVelocity(string bodyName)     // approx, for the tolerance formula
```

- `MissionPeriodicity.ExtractConstraints(view, compRoots, committed, excludedKeys, IBodyInfo)`
  -> `List<PhaseConstraint>` + a Support flag. Pure.
- `MissionPeriodicity.Solve(constraints, ut0, nowUT, tolerance)` -> `PeriodicitySolution`.
  Pure.

## Behavior

- **On loop enable / config change** (anything that moves `BuildSignature`):
  `TryBuildMissionUnit` extracts constraints from the trimmed member set, solves for
  `P` + the next window, snaps `phaseAnchorUT = NextWindowUT`, and quantizes the
  cadence to a multiple of `P`. The span clock then drives playback off that anchor
  exactly as today.
- **Unsupported config** (cross-parent before Phase 4 / rendezvous / multi-constraint
  before Phase 2): the solver returns a Support flag; `TryBuildMissionUnit` does NOT
  phase-lock and keeps today's behavior (anchor = raw `LoopAnchorUT`), so nothing
  regresses for cases we don't yet handle.
- **Per frame:** the Missions tab shows a live `T-` countdown to the ENGINE's next
  ACTUAL relaunch and the residual/quality state. The countdown is NOT
  `NextWindowUT - now` (the next faithful `P`-window): the loop builder relaunches the
  whole mission every `relaunchCadence = QuantizeCadenceToMultipleOfP(cadence, P) = m*P`
  (m can be >= 2 when the recording span or the user period exceeds `P`), so it LAUNCHES
  only every m-th `P`-window and skips the rest. The UI rebuilds the real `LoopUnitSet`
  (display-only, same inputs as the scene drivers) and reads the unit's relaunch cadence
  (`OverlapCadenceSeconds` when it overlaps the span, else `CadenceSeconds`) + its
  `phaseAnchorUT`, then counts down to `T- = nextRelaunchUT - now` where
  `nextRelaunchUT = phaseAnchorUT + n * relaunchCadence`,
  `n = max(0, ceil((now - phaseAnchorUT) / relaunchCadence))`. This coincides with the
  next `P`-window only when `relaunchCadence == P` (the common single-body / Mun case);
  for `m >= 2` it correctly targets the m-th window the engine actually launches at, so
  the countdown never reaches "T- 0s" on a window with no launch. A mission with no
  engine unit built (every loop member trimmed off) reads "not aligned". No engine work
  per frame beyond the existing draw (the UI's `LoopUnitSet` is cached per frame and
  never fed back to the engine).
- **Unconstrained config** (`P = MinCycleDuration`): behaves like today's free loop
  (T- reads continuous).

## Diagnostic Logging

Every decision point logs (subsystem tag `Mission` / `MissionPeriodicity`):
- **Constraint extraction:** one `Verbose` summary per build: mission id, included
  member count, the constraint list (kind/body/period/offset), and the Support flag
  (and WHY when unsupported - which segment/body triggered cross-parent/rendezvous).
- **Solve:** one `Verbose` line: `P`, NextWindowUT, ResidualSeconds, WithinTolerance,
  and the method (single-constraint / joint-best-fit / unconstrained).
- **Phase-lock applied vs skipped:** `Info`-on-change when `phaseAnchorUT` is snapped
  (old->new + the window) and a distinct line when phase-lock is SKIPPED (the
  unsupported reason), so a misbehaving config is never a silent branch.
- **Over-constrained:** `Warn` when `WithinTolerance == false` (residual vs tolerance,
  the dominant missed constraint) - this is the "config can't loop accurately" signal.
- Reuse `VerboseRateLimited` for any per-frame countdown diagnostics (shared key).

---

## UX

Looping is **always faithful** (it always respects the included config's
constraints); there is no "free / decorative" mode. The configuration the user
checks IS the contract, and the loop launches only at faithful windows.

- **New "Time to launch" column** in the Missions tab: a live countdown shown on
  each mission's launch (first) vessel row, under that column header, to the engine's
  next ACTUAL relaunch
  (`phaseAnchorUT + n * relaunchCadence`, where the relaunch cadence is the faithful
  period `P` quantized up to the cap, i.e. `m*P`). This is the primary surface for the
  periodicity - the user sees exactly when the next replay fires (for a Mun mission, the
  next launched Mun window; for a single-body launch, the next rotation-aligned slot the
  engine launches at). It equals the next `P`-window only when the relaunch cadence ==
  `P`; when the cadence is `m*P` (`m >= 2`) the engine launches only every m-th window
  and the countdown targets that one, not an intervening skipped window. When a launch
  is in progress it shows the time to the next one; when `P = MinCycleDuration`
  (unconstrained config) it reads "continuous"; a mission with no engine unit reads
  "not aligned".
- The period cell still shows the effective cadence, now snapped to a multiple of
  `P`, labeled with why (e.g. "every Mun window ~1.6 d").
- **Quality / residual readout** (in the "Time to launch" cell or its tooltip): for an
  over-constrained config (no exact joint window - see Edge cases), the best-fit window
  carries a residual phase error. Surface it: green when within the physics tolerance
  (the intercept/landing site genuinely lines up), amber/flagged when the best
  achievable window still misses tolerance, so the user knows the config cannot be
  looped accurately as checked and can adjust which segments are included.
- Accuracy is non-negotiable: this scheduling (next-window UT + faithful cadence) is
  the **foundation for supply routes**, which consume "when does this mission
  faithfully repeat" directly. A window that silently misses its intercept would
  break a route, so the residual readout and the physics-derived tolerance are core,
  not cosmetic.

---

## Key decisions (locked unless re-opened)

- **Replay as-is; do not re-aim the trajectory.** We choose WHEN to launch the
  recorded mission; we do NOT transform / re-plan the recorded inertial trajectory
  to intercept the target's *current* position. Re-aiming would mean re-solving the
  transfer per launch (a different mission each time) and is explicitly out of scope.
- **Faithful-only; no free / decorative mode.** Looping always phase-locks to the
  included config's constraints. The checked configuration is the contract.
- **Over-constrained configs are never refused.** When no exact joint window exists
  (e.g. a Mun landing-AND-return that pins Kerbin's rotation at two incompatible
  offsets - see Edge cases), we take the best-fit window, show a **"Time to launch"**
  countdown, and surface the residual; we still respect the full constraint set
  (never silently drop one). The user adjusts which segments are included if the
  residual is unacceptable.
- **Tolerance is physics-derived, not a free knob** (`SOI_radius / orbital_velocity`
  for orbital constraints; a fraction of a degree for rotation). Accuracy is
  paramount because this is the **supply-routes foundation** - routes consume the
  next-window schedule, so a window must actually hit its intercept.
- **Read all periods at runtime** from the live bodies (`rotationPeriod`,
  `orbit.period`, and the synodic combination for cross-parent targets); never
  hardcode stock values. Planet packs must work.
- **Same-parent targets first; interplanetary (synodic) is a later phase.** The
  early phases handle single-body + same-parent intercepts (Mun / Minmus from
  Kerbin), where the constraint is exactly `C.orbit.period`. Sun-orbiting targets
  (the synodic model) and multi-hop / gravity-assist chains come in a later phase;
  until then a cross-parent target is detected and reported as "not yet supported"
  rather than silently mis-scheduled.
- **Build on #958** (now merged to `main`): depends on `MissionLoopUnitBuilder`,
  `ComputeTrimmedMemberWindows`, the span clock, and the shipped map epoch-shift.
- **No recording-format change.** The body sequence + `UT0` are already derivable
  from existing recorded data; `P` is derived (not persisted); the snapped anchor
  reuses the existing `Mission.LoopAnchorUT`.

## Still open (to tune in playtest, not blockers)

- The exact tolerance constants (the `SOI_radius / orbital_velocity` fraction, the
  rotation degrees) and the max-`P` search bound - pin starting values, refine by
  feel.
- Whether to draw the orbit/transfer **decoratively** during a long T- wait (so the
  map is not empty before the first faithful launch) or show nothing until launch.
- Whether the residual readout warrants a dedicated icon/state vs a tooltip color.

---

## Relationship to other work

- **PR #958 (Mission abstraction + looping)** is the base. The known limitation it
  documents (looped inter-body missions do not phase-align to the live sky) is what
  this feature closes.
- **Render-distance instance cap** ("limit ghosts per render distance, e.g. 20 per
  ~50 km / `LoopSimplifiedMeters`, instead of `span/20` in
  `GhostPlaybackLogic.ComputeEffectiveLaunchCadence`"): parked behind this. Faithful
  inter-body cadence is naturally long, so the instance count stays small without
  changing the cap. Revisit only if frequent same-body looping still needs it.

## Edge cases

Each is scenario -> expected behavior -> [v1 phase / deferred]. v1 = Phases 0-3
(single-body + same-parent intercepts, the common cases); the phase numbers refer to
`docs/dev/plans/mission-periodicity-phases.md`.

- **Over-constrained configs (no exact joint `P`).** A Mun landing-AND-return pins
  Kerbin's rotation at BOTH the launch site (t_launch) and the re-entry site
  (t_reentry); since the recording fixes `t_reentry - t_launch`, both are satisfiable
  only if that separation is itself a multiple of `Kerbin.rotationPeriod`, which it
  generically is NOT. So some configs have no exact faithful window. Handling (per
  Key decisions): take the best-fit window, show the T- countdown + residual, never
  refuse; flag amber when the residual exceeds tolerance so the user can re-trim
  (e.g. drop the re-entry segment to loop just the outbound mission). Two same-body
  surface constraints at incompatible offsets are the canonical over-constrained
  case; design tests around it.
- **Interplanetary uses the synodic period, not the target's orbital period.** A
  cross-parent target (Duna via the Sun) recurs on the launch-body/target synodic
  period; using `C.orbit.period` would mis-schedule. Same-parent targets (Mun /
  Minmus) are unaffected. Cross-parent is a later phase; until then, detect it and
  report "not yet supported" rather than produce a wrong window. **[Deferred: Phase 4;
  same-parent intercepts are v1.]**
- **Transient SOI passes / gravity assists are binding.** A flyby that does not
  capture, and a free-return, still require the assist body's phase (the recorded arc
  only reaches it where it will be). The SOI-change rule already registers them via
  the `body` field; treat a transient pass exactly like a capture.
- **Rendezvous / docking with another vessel is out of scope (for now).** Aligning to
  another (looped or live) vessel is not a celestial-body period; the solver only
  models bodies. Detect a rendezvous/dock in the included span and report it as
  unsupported for faithful looping rather than emit a body-only `P` that ignores it.
  **[Deferred / out of scope for now; detected + reported, not solved.]**
- **Tidally-locked bodies collapse two constraints into one (automatically).** The
  Mun's `rotationPeriod == orbit.period`, so a Mun-surface segment's rotation
  constraint and a Mun intercept's orbital constraint share the same period and
  phase. The joint-resonance solver handles this for free (one effective constraint);
  no special case, but worth a confirming test.
- **`rotationPeriod == 0` or retrograde / odd rotation.** Guard against divide-by-zero
  (a zero/near-zero rotation period = no rotation constraint) and handle the sign of
  retrograde rotation; read the actual value, never assume.
- **Future-dated recordings** (`UT0 > liveUT`, e.g. after a career rewind/warp): the
  `phaseAnchorUT` is snapped to `NextWindowUT` (the smallest `UT0 + k*P >= now`, `k` may
  be negative); the span clock early-returns while `currentUT < phaseAnchorUT`, so a
  forward-snapped anchor simply parks the loop until the clock reaches it (which is the
  intended "wait for the window"). Reconcile the two: the `T-` countdown is to the next
  ACTUAL engine relaunch (`nextRelaunchUT = phaseAnchorUT + n * relaunchCadence`,
  `n = max(0, ceil((now - phaseAnchorUT) / relaunchCadence))`), which coincides with the
  next `P`-window only when `relaunchCadence == P`; for a multiple-of-`P` relaunch cadence
  (`m >= 2`) it targets the m-th window the engine actually launches at, never an
  intervening skipped window. While the loop is parked (`now < phaseAnchorUT`) `n` clamps
  to 0, nothing renders, and the T- column shows the positive countdown to the anchor.
  [v1: Phase 1 next-window math + Phase 3 readout, corrected to the real relaunch cadence.]
- **Config changed during a long T- wait:** while a window is counting down (loop
  parked), toggling an interval moves `BuildSignature`, rebuilds the unit, and
  re-derives `UT0` + constraints, so `phaseAnchorUT` re-snaps and the countdown can
  jump (possibly by in-game days). Expected: the T- column simply reflects the new
  next window immediately; this is correct (the config IS the contract). Log the
  re-snap (old->new window) so the jump is not a mystery. [v1: Phase 1/3.]
- **Backgrounded / on-rails transfer coast.** Per CLAUDE.md, on-rails BG vessels emit
  no env-classified per-frame TrackSections; a packed close emits
  `OrbitalCheckpoint`/`Checkpoint`-wrapped `OrbitSegment`s (orbit-only bridges). The
  extractor keys on the `bodyName` transition across `OrbitSegments`, so a SOI change
  that happened while backgrounded must still surface as a `bodyName` change in those
  checkpoint-wrapped segments. Expected: it does (the segment still carries its body);
  Phase 0 must include a test with a checkpoint-bridged SOI change so a backgrounded
  transfer does NOT silently extract zero `Orbital(C)` constraints and mis-schedule.
  [v1: Phase 0 test.]
- **No-target / no-inertial-arc missions:** a config with no SOI entry and no
  rotating-surface-to-orbit hand-off imposes no constraint -> `P = MinCycleDuration`
  (loop freely). Detect the empty constraint set rather than forcing a rotation lock.
- **Scope:** this affects ONLY looping Missions. Non-looping ghosts and per-recording
  (non-mission) auto-loop are untouched; state this in the implementation so nobody
  expects single-recording ghosts to phase-lock.

## What doesn't change

- **The recording format / `Recording` / sidecars.** `P` is derived; the body
  sequence + `UT0` come from existing data.
- **The span clock, the map epoch-shift, the watch handoff, KSC/TS parity.** They
  consume `phaseAnchorUT` + cadence as they do today; we only choose a geometry-aware
  anchor + cadence.
- **Non-looping ghosts and per-recording (non-mission) auto-loop.** Untouched - only
  looping Missions phase-lock.
- **The `ExcludedIntervalKeys` / `ComputeTrimmedMemberWindows` contract.** We read it;
  we do not change how trimming works.
- **The "one loop per tree" rule and concurrent-mission looping.** Each looping
  mission solves its own `P` independently; no cross-mission coupling.

## Backward compatibility

- **Existing looped missions (saved before this lands):** on first build after the
  update, their `phaseAnchorUT` is re-snapped to the next faithful window for their
  config. A supported config starts launching on its real cadence; an unsupported one
  keeps today's behavior. No save migration is needed (nothing new is persisted; the
  snapped anchor reuses `LoopAnchorUT`, already saved/loaded/cloned).
- **Saves load unchanged**; `BuildSignature` simply rebuilds the unit once with the
  new constraint inputs folded in.

## Test plan

Pure math is fully unit-testable (the whole point of the `IBodyInfo` seam); the
phase-lock wiring + UI are playtest-verified. Each test states the regression it
guards.

- **Constraint extraction** (Phase 0), synthetic body sequences via `IBodyInfo`:
  - single-body orbit, ascent trimmed -> empty set (guards: we don't invent a
    rotation lock for a bare inertial orbit).
  - launch + Kerbin orbit -> one `Rotation(Kerbin)` (guards: surface segment
    produces a rotation constraint; the orbit alone does not).
  - Mun mission -> `Rotation(Kerbin)` + same-parent `Orbital(Mun)` (guards: SOI entry
    produces the orbital constraint with the right offset).
  - cross-parent (Duna) -> `UnsupportedCrossParent` (guards: we never emit a
    same-parent `orbit.period` constraint for a heliocentric target).
  - transient flyby -> `Orbital` constraint present (guards: a non-capture still
    binds).
- **Solver** (Phases 1-2):
  - 0 constraints -> `P = MinCycleDuration` (guards: unconstrained = free loop).
  - 1 `Rotation` -> `P = rotationPeriod`; 1 same-parent `Orbital` -> `P = orbit.period`
    (guards: the single-constraint cases).
  - next-window `k` incl. `UT0` in the future -> negative/forward `k` resolves to the
    smallest `UT0 + k*P >= now` (guards: future-dated recordings).
  - tidally-locked Mun -> rotation+orbital collapse to one period (guards: no double
    counting).
  - over-constrained (two `Rotation(Kerbin)` at incompatible offsets) -> a best-fit
    `P` with `WithinTolerance == false` and a non-zero residual; never throws (guards:
    over-constrained is handled, not refused).
  - `rotationPeriod == 0` -> no divide-by-zero, treated as no rotation constraint
    (guards: degenerate body data).
  - tolerance boundary: residual just within vs just outside flips `WithinTolerance`
    (guards: the green/amber readout threshold).
- **Builder wiring** (Phase 1), `MissionLoopUnitBuilderTests`:
  - a supported single-body / same-parent config snaps `phaseAnchorUT` to the next
    window and quantizes cadence (guards: the lock is applied).
  - an unsupported config leaves `phaseAnchorUT` at the raw `LoopAnchorUT` (guards: no
    regression for cases we don't handle).
- **Log assertion tests:** capture the test sink and assert the constraint-summary,
  solve, and phase-lock-applied/skipped lines appear with the right fields (guards:
  the diagnostic coverage in the Diagnostic Logging section survives refactoring).
- **UI countdown formatting** (Phase 3): pure `T-` formatting helper (guards: the
  countdown string; the rest of the UI is playtest-verified).

## References

- `docs/dev/design-mission-abstractions.md` - the Mission hierarchy + looping this
  builds on (especially "Mission-level looping (span-clock integration)").
- `docs/dev/todo-and-known-bugs.md` - the deferred TODO + the shipped known
  limitation this closes.
- `Source/Parsek/MissionLoopUnitBuilder.cs` - `TryBuildMissionUnit` (span,
  cadence, phase anchor), `BuildSignature`.
- `Source/Parsek/GhostPlaybackLogic.cs` - `TryComputeSpanLoopUT`,
  `ComputeEffectiveLaunchCadence`, the span clock.
- `Source/Parsek/GhostMapPresence.cs` / `ParsekPlaybackPolicy.cs` - the map-presence
  loop fix (`loopEpochShiftSeconds`) that places the ghost at the right phase along
  the recorded ellipse (orthogonal to, and unaffected by, this feature).
