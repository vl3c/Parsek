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
   surface ops) -> constrains **B's rotation phase**, repeating every
   `B.rotationPeriod` (Kerbin sidereal day ~21,549 s). This is what realigns the
   segment over its ground location AND connects an ascent to its inertial orbit
   (or an orbit to its descent).

2. **Inertial orbit segment around body B** -> by itself imposes **no** phase
   constraint (B is always there; an inertial orbit is faithful at any UT). It
   inherits B's rotation constraint ONLY when an included surface / atmospheric
   segment of B is adjacent (the ascent->orbit / orbit->descent hand-off must line
   up over the launch / landing site).

3. **SOI entry into body C** (any intercept inside the included span - a capture, a
   transient flyby, OR a gravity assist; a non-capturing pass is just as binding as a
   capture, because the recorded arc still only reaches C where C will be) ->
   constrains C's **phase relative to the launch body's parent**, because the transfer
   time (launch -> encounter) is fixed in the recording, so aligning C's position at
   the encounter constrains the launch UT modulo that recurrence period:
   - **C orbits the SAME parent as the launch body** (Mun / Minmus from Kerbin):
     the recurrence is simply `C.orbit.period` (Mun ~138,984 s) - the launch body and
     the transfer frame are the same fixed reference, so aligning C's mean anomaly
     aligns the whole geometry.
   - **C orbits a DIFFERENT parent** (an interplanetary target like Duna, reached via
     the Sun): the launch body ALSO moves around the shared parent (the Sun) during
     the transfer, so the recurrence is the **synodic period** of the launch body and
     C about their common parent (`1 / |1/T_launchParentOrbit - 1/T_C|`), NOT
     `C.orbit.period`. Using `C.orbit.period` here would NOT realign the transfer.
     (Multi-hop transfers - Kerbin -> Mun -> elsewhere, gravity-assist chains - stack
     one such constraint per transited body; see the phased plan for when these land.)
   The transited bodies are read from the SOI changes (the `body` field across
   `OrbitSegments`) WITHIN the included segments only.

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
  launch-window cadence.
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
  `P = the launch/landing body's rotationPeriod` when an included surface /
  atmospheric segment must line up, else `MinCycleDuration` (a bare inertial orbit
  with no surface segment imposes no phase constraint).
- **Config that reaches another body:** `P` = the dominant intercept period (the
  target / last transited body's orbital period within the included span), so the
  transfer actually reaches the target. Accept a small residual launch-body-rotation
  offset (the orbit may sit slightly off the live launch site) for Tier 1; the
  intercept is the dominant visual break.

Both cases derive their bodies/frames from the INCLUDED segments (the trimmed member
set), so changing which intervals are checked re-derives `P`.

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

## UX

Looping is **always faithful** (it always respects the included config's
constraints); there is no "free / decorative" mode. The configuration the user
checks IS the contract, and the loop launches only at faithful windows.

- **New "T- to launch window" column** in the Missions tab (next to the period
  cell): a live countdown to the next faithful launch (`UT0 + k*P`). This is the
  primary surface for the periodicity - the user sees exactly when the next replay
  fires (for a Mun mission, the next Mun window; for a single-body launch, the next
  rotation-aligned slot). When a launch is in progress it shows the time to the next
  one; when `P = MinCycleDuration` (unconstrained config) it effectively reads "now"
  / continuous.
- The period cell still shows the effective cadence, now snapped to a multiple of
  `P`, labeled with why (e.g. "every Mun window ~1.6 d").
- **Quality / residual readout** (in the T- column or its tooltip): for an
  over-constrained config (no exact joint window - see Risks), the best-fit window
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
  offsets - see Risks), we take the best-fit window, show a **T- to launch window**
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

## Risks / edge cases

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
  report "not yet supported" rather than produce a wrong window.
- **Transient SOI passes / gravity assists are binding.** A flyby that does not
  capture, and a free-return, still require the assist body's phase (the recorded arc
  only reaches it where it will be). The SOI-change rule already registers them via
  the `body` field; treat a transient pass exactly like a capture.
- **Rendezvous / docking with another vessel is out of scope (for now).** Aligning to
  another (looped or live) vessel is not a celestial-body period; the solver only
  models bodies. Detect a rendezvous/dock in the included span and report it as
  unsupported for faithful looping rather than emit a body-only `P` that ignores it.
- **Tidally-locked bodies collapse two constraints into one (automatically).** The
  Mun's `rotationPeriod == orbit.period`, so a Mun-surface segment's rotation
  constraint and a Mun intercept's orbital constraint share the same period and
  phase. The joint-resonance solver handles this for free (one effective constraint);
  no special case, but worth a confirming test.
- **`rotationPeriod == 0` or retrograde / odd rotation.** Guard against divide-by-zero
  (a zero/near-zero rotation period = no rotation constraint) and handle the sign of
  retrograde rotation; read the actual value, never assume.
- **Future-dated recordings** (`UT0 > liveUT`, e.g. after a career rewind/warp): the
  next-window `k` is the smallest integer with `UT0 + k*P >= now`, which can be
  negative; and the span clock early-returns while `currentUT < phaseAnchorUT`, so a
  forward-snapped anchor simply parks the loop until the clock reaches it (which is
  the intended "wait for the window"). Reconcile the two so the T- countdown reads
  correctly across that boundary.
- **No-target / no-inertial-arc missions:** a config with no SOI entry and no
  rotating-surface-to-orbit hand-off imposes no constraint -> `P = MinCycleDuration`
  (loop freely). Detect the empty constraint set rather than forcing a rotation lock.
- **Scope:** this affects ONLY looping Missions. Non-looping ghosts and per-recording
  (non-mission) auto-loop are untouched; state this in the implementation so nobody
  expects single-recording ghosts to phase-lock.

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
