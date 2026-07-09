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

## ⚠️ Superseded: transfer re-aim is the live approach (2026-06-02)

> **This document's "replay the recorded transfer as-is; never re-aim" decision is
> OBSOLETE for the interplanetary transfer leg.** The current, implemented approach
> generates the heliocentric transfer per launch window with a Lambert solve so it
> always intercepts the target — see
> `docs/dev/done/plans/reaim-interplanetary-transfers.md`.
>
> **Why it changed:** the recorded celestial configuration (the launch body's rotation
> AND every transited body's orbital phase) is set by periods that are mutually
> incommensurate, so the exact recorded configuration only recurs on their joint
> resonance — for an interplanetary target that best-fit window is effectively
> centuries away. A pure replay-as-is loop would almost never get to launch. Re-aim
> removes that wall: the transfer is recomputed for whatever window we choose, so a
> usable cadence AND good transfer geometry are always available.
>
> **How the two layers now compose (the reconciliation):** the window scheduling in
> this doc still owns the parts that timing alone *can* make faithful — the
> launch-site rotation lock, and reusing the recorded SOI-local arcs (ascent/parking,
> arrival/landing) at a favorable body configuration. Re-aim owns the one part timing
> alone cannot fix: the heliocentric bridge between SOIs. So everything below about
> `P`, the constraint extractor, the next-window countdown, and loiter trimming stays
> valid for the *recorded, replayed* segments; only the premise "the recorded transfer
> is replayed unchanged" is replaced by "the transfer is regenerated deterministically
> per window." Correspondingly, the "cross-parent / interplanetary is not yet
> supported — detect and report" caveats below (the `UnsupportedCrossParent` Support
> flag, the Phase-4 deferral) are superseded where re-aim now covers a single
> launch-SOI → other-SOI hop.

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

## Degrees of freedom: looped re-aimed interplanetary missions

Everything above describes the SAME-PARENT model: pick ONE launch UT (`UT0 + k*P`)
where every dependent body recurs to its recorded phase. For a CROSS-PARENT
(interplanetary) mission the transfer is no longer replayed as-is; it is regenerated
per window by re-aim (`docs/dev/done/plans/reaim-interplanetary-transfers.md`, see the
Superseded banner). That regeneration changes the problem: the launch and the arrival
are no longer locked to one instant, so the question becomes WHICH degrees of freedom
are free and which are recorded, and how the free ones are spent JOINTLY per window to
align the arrival without degrading the launch cadence.

This section pins that for a looped re-aimed interplanetary mission that LANDS (the
flagship case is save `s15` "Duna One", Kerbin -> Duna). The next section is the
design that consumes it.

### The flexibility chain (deterministic vs flexible)

Each link is DETERMINISTIC (a hard constraint that replays exactly relative to its OWN
recorded clock) or FLEXIBLE (slack the scheduler may spend). DETERMINISTIC here does
NOT mean "absolute-UT fixed"; it means "exact relative to its own recorded clock once
we choose when that clock starts."

```
L0  moment of launch          FLEXIBLE        delay until the schedule says "go"
L1  launch -> loiter orbit     DETERMINISTIC   recorded ascent (mesh + animation exact)
L2  launch loiter orbit count  FLEXIBLE        trim recorded N orbits down (launch side)
L3  orbit -> SOI exit          DETERMINISTIC   recorded escape burn
L4  departure SOI exit          FLEXIBLE        angle AND timing free (high-warp coast)
L5  heliocentric transfer      DETERMINISTIC*  solver-REGENERATED per window, then exact
L6  destination SOI entry       FLEXIBLE        angle AND timing free (favorable config)
L7  in-SOI recorded arcs       DETERMINISTIC   recorded RELATIVE to SOI entry
L8  destination loiter count    FLEXIBLE        trim to align with recorded descent
L9  descent / landing          DETERMINISTIC   recorded body-fixed, plays exactly
```

`L5` is "deterministic after regeneration": its SHAPE (recorded time-of-flight,
handedness) is fixed, but it is recomputed each window so it points where the bodies
actually are. This is the shipped re-aim contract
(`ReaimPlaybackResolver.BuildWindowSegments`). It is what makes the chain solvable: a
fixed-shape transfer recomputed per window lets a launch-aligned departure and a
destination-aligned arrival both be hit, because the transfer's inertial orientation
is a free OUTPUT, not a recorded input.

The SOI entry/exit DIRECTION and ANGLE (`L4`, `L6`) never appear as constraints. The
recorded in-SOI arcs are anchored to entry, so WHERE the ghost crosses the SOI edge is
irrelevant; only WHEN it crosses (which destination configuration is live then)
matters. One honesty note on the table's `L4` / `L6` "timing free": the DIRECTION/angle
is genuinely free, and WHEN is flexible in the user's sense (wait as long as needed for a
favorable configuration). That WHEN is spent as a per-window knob acting at the FLEXIBLE
arrival region: an ARRIVAL HOLD that inserts dead time at the heliocentric->capture
boundary so the in-SOI replay (and with it the deorbit instant) starts LATER in live
time, plus a PRE-LANDING TRIM that removes time from the orbital segment right before the
landing to shift the deorbit EARLIER. Both retime the in-SOI replay via the loop clock
(the recorded-span -> live remap), never the launch or the transfer. They are knobs spent
JOINTLY per window, NOT a selection of a rare naturally-aligned window. See the
cadence-preserving multi-knob model below.

### What is actually misaligned across the loop shift

Replayed at an arbitrary loop-shifted time, only two live-sky quantities inside the
destination SOI can be wrong, and both are pure periodic functions of absolute UT:

1. The destination body's **rotation phase**, period `T_rot(dest)`
   (`IBodyInfo.RotationPeriod`). This is the Bug-2 symptom: across the `s15` loop shift
   (~`1.572e9` s) the destination rotates an arbitrary fraction of a turn, so the
   inertial capture orbit and the body-fixed landing site diverge (~131 degrees on the
   map; the icon overshoots the landing, then snaps onto the body-fixed descent
   polyline).
2. Each inner moon's **orbital phase**, period `T_orb(moon)`
   (`IBodyInfo.OrbitPeriod`).

The recorded ascent, escape, in-SOI arcs, and descent are all recorded relative to
their own clock or body-fixed, so they replay faithfully the moment we fix WHEN that
clock starts.

### HARD REQUIREMENT: preserve the synodic launch cadence

> **The looped launch cadence MUST stay at the synodic period (about every 2 Kerbin
> years for Kerbin -> Duna). The feature does NOT relaunch only at rare naturally
> aligned windows.** Every synodic window is a valid launch opportunity. Arrival
> alignment is achieved by spending the FLEXIBLE knobs WITHIN each window (the loop-clock
> ARRIVAL HOLD that defers the in-SOI replay, the PRE-LANDING TRIM that advances the
> deorbit, the destination loiter trim when one was recorded, and the launch-side loiter
> trim), NEVER by skipping ahead to a rare window where the bodies happen to be naturally
> aligned.
>
> The knobs work JOINTLY per window: for a given synodic window the resolver spends them
> together to land the arrival on the recorded destination configuration. If a given
> window genuinely cannot be aligned by any available knob, the feature FAILS CLOSED to
> faithful for THAT window (it shows the un-aligned arrival as a visible state) and moves
> on to the next synodic window; it does NOT skip the window or stretch the cadence. So
> the synodic launch cadence is preserved either way: aligned-within-window when the
> knobs reach, visibly-un-aligned when they do not.
>
> This is the load-bearing requirement of the whole feature. Earlier drafts mis-modelled
> it as collapsing to "select a naturally aligned window index on a ~2-Kerbin-year grid,"
> which would degrade launch opportunities from every ~2 years to (for a Loose tolerance,
> see the feasibility envelope) roughly every ~140 years. That single-grid-scalar
> conclusion is WRONG and is replaced by the multi-knob model below.

### The arrival-alignment TARGET and the two arrival flex points

The alignment TARGET is the DEORBIT instant: the moment where the inertial proto-vessel
orbit hands off to the body-fixed descent. Aligning the deorbit to the destination body's
RECORDED rotation phase is what lands the body-fixed descent on the recorded site and
removes the orbit->descent teleport. The in-SOI replay's live UT is set by the loop clock
(the recorded-span -> live remap); to shift the deorbit we adjust that remap in the
FLEXIBLE arrival region. There are exactly TWO knobs, both at the arrival region, neither
requiring a parking loiter, and together they are bidirectional room (one moves the
deorbit later, one earlier):

- **Arrival flex point #1: ARRIVAL HOLD (the deorbit shifts LATER).** Insert dead time at
  the heliocentric->capture boundary. This is the exact INVERSE of a loiter cut: a loiter
  cut REMOVES recorded-span time, the hold INSERTS it, so the entire in-SOI replay
  (including the deorbit) starts LATER in live time. The ghost waits at SOI arrival. It is
  CONTINUOUS (any hold W in `[0, T_rot)`) and POSITION-CONTINUOUS (a hold is a wait, never
  a teleport). Implemented as pure helpers in Phase 3a:
  `GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` (the minimal forward W that aligns
  the deorbit rotation phase) and `GhostPlaybackLogic.ApplyArrivalHoldToPhase` (the
  effective-span -> compressed-span remap, composing with the existing loiter-cut
  `DecompressSpanUT`).
- **Arrival flex point #2: PRE-LANDING TRIM (the deorbit shifts EARLIER).** Remove time
  from the proto-vessel orbital segment RIGHT BEFORE the landing, reusing the loiter-cut
  machinery. Usable when that segment is "long enough to be trimmed", and it works even
  without parking-loiter orbits. CONSTRAINT: it must keep the proto-vessel
  POSITION-CONTINUOUS. Skipping WHOLE orbital periods is clean (the orbit returns to the
  same point); trimming a PARTIAL arc of a non-periodic approach jumps the proto-vessel
  along its orbit, and that jump is acceptable ONLY in the flexible SOI-edge region (which
  the design already designates as flexible, where the trajectories need not line up).
  When a destination loiter WAS recorded this is the `L8` integer-period re-timer (whole
  recorded loiter revolutions); a short or non-loiter approach offers only the
  flexible-region partial trim.

The alignment picks the MINIMAL / least-disruptive combination of trim (earlier) and hold
(later) that lands the deorbit on the recorded rotation phase: prefer a short trim over a
long wait. **The two knobs are not fully independent of the in-SOI sequence shape.** When
a mission records NO destination loiter (see the Duna One worked example below), the
in-SOI sequence is rigid (capture-then-land within a single destination day), so the hold
is the primary CONTINUOUS knob and the pre-landing trim adds only the limited room the
flexible SOI-edge approach arcs allow. When a long, low destination loiter WAS recorded,
the integer-period pre-landing trim supplies dense room and the hold fine-trims the
residual. Because both knobs act on the loop clock AFTER SOI entry, the destination's
rotation phase at the deorbit (and, for a 0/1-moon body, any inner moon's orbital phase)
is brought onto its recorded value without touching the launch or the transfer.

**Implementation status.** Phase 3a (the pure hold helpers
`GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` / `ApplyArrivalHoldToPhase`, composing
with the existing loiter-cut `DecompressSpanUT`) is DONE and unwired. Phase 3b wires the
hold + the pre-landing trim into `GhostPlaybackLogic.TryComputeSpanLoopUT` via a new
`LoopUnit` arrival-hold/trim field populated by `MissionLoopUnitBuilder` (gated; off =>
byte-identical to today). Phase 3c is the live-KSP playtest on the s15 Duna One case. See
the companion plan's section 7 for the phased breakdown.

### The cadence-preserving multi-knob model

The arrival is aligned by a SET of jointly-spent per-window knobs, NOT by collapsing to a
single grid-selected scalar. Per synodic window `k` (the coarse cadence knob, fixed at the
synodic period and never lengthened) the resolver spends, jointly:

- **Coarse (selects the window, preserves cadence):** the synodic window index `k`. Window
  `k` has departure `D_k = RecordedDepartureUT + k*synodic` and arrival near `D_k + tof`.
  Every `k` is a valid launch opportunity; we do not skip `k` values to find a naturally
  aligned one.
- **Arrival hold (within the window, arrival flex point #1, the deorbit shifts LATER):**
  the loop-clock dead time inserted at the heliocentric->capture boundary
  (`GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` /
  `GhostPlaybackLogic.ApplyArrivalHoldToPhase`). CONTINUOUS over `[0, T_rot)` and
  position-continuous (a wait, never a teleport). This is the primary continuous knob for a
  no-loiter mission.
- **Pre-landing trim (within the window, arrival flex point #2, the deorbit shifts
  EARLIER):** time removed from the orbital segment right before the landing, reusing the
  loiter-cut machinery (`L8` whole-revolution steps when a loiter was recorded; a
  flexible-region partial trim otherwise, kept position-continuous per the SOI-edge
  flexibility).
- **Launch-side trim:** the `L2` launch-parking compression (already ships).

Given the chosen window `k` and the arrival hold + pre-landing trim, `L0` / `L4` / `L6`
(launch moment, departure-wait, SOI-entry timing) follow: the regenerated transfer
(recorded tof) sets the departure near `arrival - tof`, and the launch back-snaps to a
whole launch-body sidereal day via the EXISTING `PadAlignLaunch`. `L5` is regenerated, not
chosen. The point is that the arrival is aligned WITHIN each synodic window by spending the
loop-clock hold and trim jointly, never by hunting across windows for a rare natural
alignment.

### The honest qualifier (the deorbit is moved by the loop-clock hold + trim)

The deorbit instant is moved by the loop-clock ARRIVAL HOLD (continuous in `[0, T_rot)`)
and the PRE-LANDING TRIM (whole loiter revolutions when a loiter was recorded, plus the
limited partial trim the flexible SOI-edge approach arcs allow). The hold and the trim are
the bidirectional room at the arrival. Both act AFTER SOI entry, on the same loop clock
(`GhostPlaybackLogic.TryComputeSpanLoopUT`); neither touches the launch pad or the
heliocentric transfer, both of which are upstream of the capture boundary.

These facts are respected and they do NOT imply the cadence-degrading conclusion of earlier
drafts ("therefore the feature can only relaunch at a rare grid-selected window `k`"). The
arrival is aligned WITHIN each synodic window by spending the hold and the trim jointly; the
launch cadence stays synodic. The hold is continuous, so a no-loiter mission can align the
deorbit within each window from the hold alone, fine-trimmed by whatever flexible-region
approach-arc trim is available (worked through next). The feasibility envelope below
quantifies the room; when the available hold + trim cannot align a given window, the
feature fails closed to faithful for that window and the next synodic window is still on
cadence.

### REJECTED approach: the tof-as-phase-lever (refuted by code verification)

Earlier drafts proposed nudging the synthesized heliocentric TIME OF FLIGHT within
`ReaimPlaybackResolver`'s existing +-6 percent search band to slide the arrival instant and
thus the destination rotation phase. This is REFUTED and must not be reintroduced as an
arrival-alignment mechanism. A code verification established:
`ReaimPlaybackResolver.BuildWindowSegments` replaces ONLY the heliocentric leg over the
FIXED recorded span `[RecordedDepartureUT, RecordedArrivalUT]`; the synthesized tof only
reshapes that heliocentric ARC. The recorded in-SOI arcs (the capture OrbitSegments after
`RecordedArrivalUT`) and the body-fixed descent replay at the loop-clock UT computed by
`GhostPlaybackLogic.TryComputeSpanLoopUT` (`PhaseAnchorUT` + recorded-span offset + cadence
+ loiter cuts), with ZERO dependence on the synthesized tof: the synthesizer's `soiEntryUT`
is only LOGGED (`ReaimPlaybackResolver.cs:246`), never used to re-time anything. So nudging
the tof reshapes the transfer arc but does NOT move the loop-clock-driven in-SOI replay,
which means it CANNOT change the destination's rotation phase at the in-SOI deorbit and
CANNOT fix the ~131-degree offset. The validated mechanism is the loop-clock arrival hold +
pre-landing trim above.

### Worked example: Duna One (s15), the arrival hold is the primary knob

The flagship case (save `s15` "Duna One", Kerbin -> Duna, verified from the
`logs/2026-06-03_1951_duna-arrival/KSP.log` ReaimDiag dump and the recording
`61e9177...prec`) shows the validated mechanism with the simplest possible arrival side: no
parking loiter and no independently constrained moon, so the continuous loop-clock ARRIVAL
HOLD carries the alignment, with the limited approach-region PRE-LANDING TRIM adding room
inside the flexible SOI edge.

The recorded numbers:

- LAUNCH side: a Kerbin parking loiter of ~132 days, already compressed by the shipped
  single launch-side loiter cut (`cut#0 start=52570174 len=11393869`, the `L2` trim).
- Heliocentric transfer: `tof = 6,854,613 s` (about 79.3 days). The Kerbin -> Duna synodic
  period is `19,653,076 s` (about 2.1 Kerbin years): the on-cadence launch interval.
- INSIDE the Duna SOI: arrival at `70,898,646` through the last orbit segment end at
  `70,963,653` is about `65,007 s` (~18.1 hours, about 1 Duna rotation). It is a long
  capture approach (about 0.87 of a Duna rotation), then short decaying approach arcs
  (sma `492 -> 340 km`, fractional revs 0.45 / 0.32 / 0.02 / 0.09), then the descent. So
  Duna One records NO destination parking loiter (it captures and lands within ~1 Duna
  day). It DOES record a brief Ike SOI transit (two short hyperbolic Ike orbit segments,
  ~37 minutes total, about 16 hours after Duna arrival), but that transit imposes NO
  INDEPENDENT constraint: Ike is tidally locked to Duna, so its orbital phase collapses
  onto Duna's rotation phase (the Ike tidal-collapse note below). Aligning Duna's rotation
  auto-aligns Ike, so there is one effective destination constraint, not two.

The implication, which is the whole point:

- There is no parking loiter to step by whole revolutions and no independent moon to align,
  so the CLEAN (whole-period) pre-landing trim room is limited (only the short decaying
  approach arcs, in the flexible SOI edge). The continuous loop-clock ARRIVAL HOLD is the
  primary knob: it defers the in-SOI replay so the deorbit lands on the recorded Duna
  rotation phase, and the approach-region partial trim adds bidirectional room within the
  flexible SOI edge.
- The arithmetic is favorable: `T_rot(Duna)` is about `65,518 s` (about 18.2 hours), so any
  aligning hold is W in `[0, T_rot)`, i.e. at most about 18 hours. That is negligible inside
  the ~2.1 Kerbin-year (about `19,653,076 s`) synodic cycle, so the wait never threatens the
  cadence and the ghost simply pauses at SOI arrival for under one Duna day before the
  recorded capture sequence resumes.
- Because the in-SOI sequence is rigid (no loiter), aligning the deorbit rotation phase
  ALSO aligns the landing: once the deorbit is on the recorded Duna rotation phase the rigid
  recorded sequence reproduces the recorded touchdown.

The consequence: for Duna One the Bug-2 fix is the loop-clock arrival hold (continuous,
under one Duna day) plus the flexible-region approach trim, aligning every synodic window
and preserving the ~2.1 Kerbin-year cadence. This does NOT use the synthesized
heliocentric tof to move the arrival (REFUTED, see the REJECTED-approach note above: the
tof reshapes the transfer arc but the in-SOI replay is on the loop clock, independent of
tof). The synodic launch cadence is preserved regardless: the cadence is set by the window
index `k`, and the hold + trim only re-time the in-SOI replay AFTER SOI entry.

---

## Cross-parent destination-SOI arrival-UT alignment (Phase 4)

*Status: design note for the deferred Phase 4 (the `UnsupportedCrossParent` Support
flag, the "synodic, Phase 4" deferral). It proposes NO production code. It is a NEW
temporal scheduling layer that sits ON TOP of the shipped per-window transfer synthesis
and feeds it an arrival UT; it does not touch the launch pad lock (already correct) or
the geometric Lambert capture seam (already shipped + guarded). The companion
implementation plan lives at
`docs/dev/plans/reaim-destination-arrival-alignment.md`.

### Shipped and confirmed-good: the launch -> destination-SOI-entry pipeline (do NOT regress)

> **This is a regression fence, not a description of new work.** The user confirmed in
> playtest that the ENTIRE pipeline from launch up to the moment the ghost ENTERS the
> destination SOI renders correctly. That stretch is SHIPPED and confirmed-good and MUST
> NOT be regressed by this feature. The only thing broken (and the only thing this feature
> fixes) is the in-SOI arrival AFTER SOI entry: the ~131-degree rotation / landing
> misalignment once the ghost is inside the destination SOI.

The following are SHIPPED and confirmed-good in playtest. They MUST stay byte-identical
when destination alignment is OFF, and their generation machinery is NEVER modified by this
feature:

- recorded ascent and body-fixed landing playback (`L1` / `L9`: meshes + animations exact);
- launch-side loiter playback and its compression (`ReaimLoiterCompressor`, `L2`);
- the launch-pad rotation lock (`ReaimWindowPlanner.PadAlignLaunch`);
- the escape burn / departure SOI exit (`L3`);
- the per-window HELIOCENTRIC TRANSFER regeneration
  (`ReaimPlaybackResolver.BuildWindowSegments` /
  `ReaimSegmentAssembler.ReplaceHeliocentricLeg`) and the accepted SOI-edge geometric seam;
- everything up to and INCLUDING destination-SOI ENTRY.

**This feature is CONFINED to the destination-side in-SOI arrival temporal / rotation
alignment (what happens AFTER SOI entry).** With alignment OFF (or unsupported, or no
aligned window found), the entire launch -> SOI-entry pipeline is byte-identical to today.
That is the fail-closed contract: the feature either aligns the in-SOI arrival or visibly
does nothing, and either way the upstream pipeline is untouched.

**No upstream touch point (the validated mechanism is entirely downstream of SOI entry).**
The validated arrival mechanism is the loop-clock ARRIVAL HOLD + PRE-LANDING TRIM, both of
which act on the in-SOI replay AFTER SOI entry. NOTHING upstream of SOI entry is touched:
the launch pad lock, the escape burn, and the heliocentric transfer (including its time of
flight) are all left exactly as shipped. The earlier "honest exception" that this feature
could nudge the heliocentric tof as a phase lever is RETIRED: that tof-as-phase-lever is
REFUTED (it reshapes the transfer arc but the in-SOI replay is on the loop clock,
independent of the synthesized tof, so it cannot move the deorbit rotation phase). The
working heliocentric transfer is preserved with NO change at all: the feature does not
select, nudge, or otherwise touch the tof.

**Implementation guardrail.** Any P3+ code change is additive and gated such that the
launch -> SOI-entry pipeline code paths are unchanged when alignment is off. A reviewer
must verify this directly: it is the same "null / off => byte-identical" invariant the
earlier phases already hold to.

### Why this exists

Re-aim aligns ONLY the LAUNCH side. `ReaimWindowPlanner.PadAlignLaunch`
(`MissionLoopUnitBuilder.cs:468-502`) snaps the relaunch so
`(livePhaseAnchorUT - recordedLaunchUT)` is a whole number of the LAUNCH body's
sidereal days and quantizes the cadence to that sidereal day, so the body-fixed ascent
feeds the recorded inertial parking orbit with no seam. There is NO equivalent on the
DESTINATION side: nothing forces the live arrival UT onto an instant where the
destination's rotation phase (and any inner moon's orbital phase) match the recorded
entry. So everything inside the destination SOI replays at the recorded epoch under the
loop shift while the destination keeps rotating, and the inertial capture orbit lands
~131 degrees off the body-fixed landing site. That un-chosen arrival UT is exactly the
Bug-2 offset.

This is the deferred cross-parent twin of the same-parent "Landing-body alignment"
control (Off/Loose/Precise) that already ships for Mun/Minmus missions. The cross-parent
version was flagged `UnsupportedCrossParent` and deferred to "Phase 4." This is that
phase, reframed: the controlling variable is the destination-SOI arrival UT (decoupled
from the launch UT by re-aim's per-window regeneration), not a synodic-period launch-UT
lock.

This layer is complementary to, and distinct from, the prior re-aim "arrival fix" work.
That work solved the GEOMETRIC seam (connecting the heliocentric transfer to the
recorded in-SOI arcs in POSITION space via a Lambert capture). This is the
TEMPORAL / ROTATION-PHASE layer: choosing the arrival UT so the destination
CONFIGURATION at arrival matches the recorded entry. Do not conflate the two.

### What three earlier drafts got wrong (corrected against source)

These corrections are load-bearing; the design below reflects them, not the original
assertions.

1. **"Arrival UT is a freely selectable continuous variable."** FALSE as shipped, and
   ALSO false to over-correct into "therefore the feature selects a rare naturally aligned
   window on the synodic grid." The deorbit is moved by the loop-clock ARRIVAL HOLD
   (continuous in `[0, T_rot)`) plus the PRE-LANDING TRIM (whole loiter revolutions when a
   loiter was recorded, plus the limited partial trim the flexible SOI-edge approach arcs
   allow), spent JOINTLY WITHIN each synodic window, NOT by hunting across windows for a
   natural alignment. The launch cadence stays synodic (the HARD REQUIREMENT block);
   alignment happens within a window, with fail-closed-to-faithful for any window the hold +
   trim cannot reach. The hold is CONTINUOUS, so a no-loiter mission can align the deorbit
   within each window from the hold alone, and (REFUTED, see the REJECTED-approach note) the
   synthesized heliocentric tof is NOT a lever: it reshapes the transfer arc but the in-SOI
   replay is on the loop clock, independent of the tof. The cadence-degrading
   single-grid-scalar conclusion of those earlier drafts is the specific error this section
   retires.

2. **"The loiter trim is a continuous fractional residual absorber."** FALSE.
   `ReaimLoiterCompressor.ComputeCuts` excises WHOLE periods from the run START and
   keeps the tail "ending at the recorded run end so the exit phase is preserved"
   (`ReaimLoiterCompressor.cs:54-56,131`). Cuts are integer multiples of the loiter
   period; the recorded EXIT phase relative to the run end is invariant. The compressor
   detects ALL same-body loiter runs and is called with the fixed default
   `keepRevs = 1` (`DefaultKeepRevs`, line 22). So the loiter trim is a COARSE
   integer re-timer, not a fine vernier (see the loiter-trim subsection).

3. **"Aligning the arrival UT makes the body-fixed descent and the inertial orbit
   coincide automatically."** UNVERIFIED, and the render path makes it non-trivial.
   `ReaimedTrajectory` renders the re-aim ghost trajectory ONLY from `OrbitSegments`;
   its `Points` and `TrackSections` return EMPTY
   (`ReaimedTrajectory.cs:15-19,63-64`). The body-fixed descent polyline is drawn by a
   SEPARATE autonomous renderer (`Display/GhostTrajectoryPolylineRenderer.Driver`,
   walking `RecordingStore.CommittedRecordings` at the LIVE clock). The arrival-UT
   decision threads into the OrbitSegment path automatically (via the schedule) but does
   NOT thread into the autonomous surface-polyline path today. Closing that thread is a
   verification gate of this design, not an assumed side effect.

### The destination-side constraint set

Mirror the existing two-kind constraint model (`ConstraintKind` Rotation / Orbital),
but anchored to the recorded DESTINATION configuration rather than to the launch `UT0`:

- **`DestRotation(dest)`**, emitted only when the recorded in-SOI arcs include BOTH a
  destination surface/atmospheric segment AND a destination inertial-orbit segment (the
  arrival-orbit-to-descent hand-off). This is the exact pair rule the launch side uses
  (rules 1 + 2 above). A LANDING fires it; an orbit-only flyby does NOT (orbit-only
  arrivals render correctly at any rotation phase). Period `T_rot(dest)`.
- **`MoonConfig(m)`**, one per inner moon `m` whose phase is visible in the recorded
  in-SOI window. Kind Orbital, period `T_orb(m about dest)`.

The recorded reference UTs are extractable from existing data, with no new persisted
field and no recording-format change:

- The destination surface-start UT (the `DestRotation` reference) is NOT on the re-aim
  plan (an earlier draft referenced a non-existent `ReaimMissionPlan.RecordedDescentUT`;
  no such field exists). It IS recoverable from the recording's surface TrackSections
  via the existing `MissionPeriodicity.ScanSurfaceSegmentsWithinWindow`
  (`MissionPeriodicity.cs:1218-1245`), which already scans rotation-constraining
  surface/atmospheric sections and records the earliest start UT per body. Call the
  earliest destination-body surface-start UT `RecordedDestSurfaceUT`. The deorbit /
  descent is the deterministic body-fixed arc that must coincide with the inertial orbit
  projection, so aligning the destination rotation phase at `RecordedDestSurfaceUT` (not
  merely at SOI entry) is what closes the touchdown overshoot.
- The SOI-entry UT (the `MoonConfig` reference) is `ReaimMissionPlan.RecordedArrivalUT`
  (`ReaimClassifier.cs:32`), the S2 end / target SOI entry. Today it is consumed only
  for GEOMETRIC transfer placement, never to schedule the arrival instant; this design
  adds the scheduling use.

### The arrival-UT solve (reusing the proven scheduler)

This is the SAME zero-drift primitive the launch side uses, with the variable relabeled
from "launch UT" to "arrival window index `k`." Candidate arrival UTs are the synodic
windows reachable by the shipped schedule: window `k` has departure `D_k`, arrival near
`D_k + tof`, with the bounded tof search refining within +-6 percent (a transfer-geometry
degeneracy dodge, NOT an alignment lever; the rotation alignment is the loop-clock arrival hold +
pre-landing trim, below). For each candidate
define the phase residuals against the recorded references:

- `DestRotation` residual =
  `CircularPhaseError(A_surface_k - RecordedDestSurfaceUT, T_rot(dest))`, where
  `A_surface_k` is the effective destination surface-arrival (deorbit) UT for window `k`
  after the loop-clock arrival hold + pre-landing trim. The continuous hold drives this
  residual to zero within each window (the hold is exactly the minimal forward shift that
  zeroes it); the trim supplies the earlier-direction room.
- `MoonConfig(m)` residual =
  `CircularPhaseError(A_entry_k - RecordedArrivalUT, T_orb(m))`, where `A_entry_k` is the
  SOI-entry UT for window `k` (shifted by the same arrival hold).

Use `TryFindNextScheduleK` (`MissionPeriodicity.cs:901-954`) verbatim: scan `k`, accept
the first window where every residual is within its tolerance, else the bounded-best
(minimum worst residual) window, never accumulating drift. The objective is
`worst(k) = max(DestRotation residual, max_m MoonConfig residual)`, per-constraint
acceptance against per-constraint tolerances. Report `ResidualSeconds` and
`WithinTolerance` exactly as the same-parent path does (the green / amber readout in the
Missions tab).

To be clear about what this scan selects: the scan-`k` walks the on-cadence synodic grid
(the pad-aligned windows launched every synodic period); it NEVER skips a launch. Within
each window the loop-clock arrival hold + pre-landing trim drive the deorbit onto the
recorded rotation phase, so the alignment is a per-window operation, not a hunt for a
naturally aligned `k`. A window the hold + trim genuinely cannot bring in-tolerance fails
closed to faithful (still launched on cadence). So "scan `k`" selects the on-cadence
window; the hold + trim select the RENDER alignment WITHIN that window. (The synthesized
heliocentric tof is NOT a lever here, per the REJECTED-approach note: it cannot move the
loop-clock-driven in-SOI replay.)

**Read the live joint resonance; never assume independence.** An earlier draft treated
`T_rot(dest)` and `T_orb(moon)` as independent and multiplied duty cycles. That is wrong
for the actual Bug-2 case: Ike is tidally locked, so `T_orb(Ike)` equals `T_rot(Ike)`
and both equal Duna's rotation period to about 6 significant figures (around 65,517 s).
They are NOT independent; an arrival matching Duna's rotation phase auto-matches Ike's
orbital phase. The solver must therefore evaluate the ACTUAL live periods from
`IBodyInfo` and compute the joint resonance from them. For the real Bug-2 case this
makes the problem EASIER (one effective constraint, not two). The general
independent-moon case (a fast body with a slow moon) is what the math must handle
correctly: it reads the live periods and lets the EXISTING tidal-collapse path handle
coincident periods for free. `MissionPeriodicity` already treats two periods within a
relative tolerance as one (the `tidal-collapse` method; `MissionPeriodicity.cs` around
535 and 651-655, with the no-distinct-period branch at 1102-1127), the same mechanism
that collapses a tidally-locked `Mun.rotationPeriod == Mun.orbit.period` pair to a single
constraint in the same-parent path. No special-case merge is added here. None of this is
window-skipping: the scan stays on the on-cadence synodic grid and the integer loiter
re-timer, selecting the RENDER alignment per on-cadence window; a window it cannot bring
in-tolerance fails closed to faithful (still launched on cadence), so the scan never chases
a global resonance and never relaxes the HARD synodic-cadence requirement.

### Tolerance modes (reuse the existing Off/Loose/Precise ladder)

The destination is, by construction, a TRANSITED (non-launch) body, so it slots into the
existing `TransitedBodyRotationMode { Drop, Loose, Tight }` enum and the
`ScheduleToleranceSecondsFor` dispatch with zero new tolerance vocabulary (the UI labels
Off, Loose, Precise map to enum `Drop`, `Loose`, `Tight` respectively). The existing
"Landing-body alignment" cycle button governs it; only the tooltip widens to name an
interplanetary destination as well as the Mun.

- **`DestRotation` tolerance:**
  - Precise (Tight): `T_rot(dest) * (0.25/360)` (`RotationToleranceFraction`, line 480).
    For Duna about 45.5 s.
  - Loose: `T_rot(dest) * (5.0/360)` (`TransitedBodyLooseRotationDegrees`, line 490).
    For Duna about 910 s.
  - Off (Drop): the `DestRotation` constraint is removed (pre-filtered, as the schedule
    builder already does for transited-body rotation).
- **`MoonConfig` tolerance** (Orbital kind, never dropped by Off):
  `SoiRadius(m) / OrbitalVelocity(m)`, the time the moon crosses its own SOI
  (`ToleranceSecondsFor`, `MissionPeriodicity.cs:1164-1170`). For Ike about 3,400 s.

Off drops only the destination ROTATION term, matching the existing semantics. The
launch pad always stays exact (the launch side is untouched).

**Decision: the default destination tolerance mode is Loose.** Loose is the realistic
default for a mission with a substantial destination loiter. Precise is offered as an
explicit rare-window mode that honestly reports amber when the best achievable window
still misses tolerance. The thresholds were tuned for Mun-class same-parent periods and
do NOT transfer to interplanetary cadence (see the feasibility envelope), so Precise is
deliberately a rare opt-in, not the default.

### The loiter-trim role (a coarse re-timer, not a fine absorber)

Given correction 2 above, the destination loiter trim does ONE useful thing for arrival
alignment: it shifts the post-arrival timeline by WHOLE loiter periods, selecting WHICH
recorded loiter revolution becomes the effective deorbit. The deorbit instant moves by
integer multiples of `T_loiter = 2*pi*sqrt(a^3/mu)`, with
`mu = IBodyInfo.GravParameter(dest)` (already on `IBodyInfo` for the launch-side loiter
compression). Each excised revolution steps the surface-arrival phase by
`frac(T_loiter / T_rot(dest))` of a destination turn (fine for a low loiter orbit, coarse
for a high one).

What it does NOT do: it cannot manufacture a sub-`T_loiter` residual absorber, and it
preserves the recorded exit phase relative to the run end, so it does not continuously
slide the deorbit onto an arbitrary phase. It is a discrete second knob alongside `k`.
The honest consequence:

- When a recorded mission HAS a long, low destination loiter (many revolutions), the
  integer steps materially widen the achievable windows (and can make Precise reachable).
- When a recorded mission has a short loiter (1 to 3 revs) or NO destination loiter
  (direct atmospheric entry), the integer loiter trim contributes little or nothing, and
  the alignment is carried by the continuous loop-clock arrival hold (with whatever
  flexible-region partial approach-arc trim is available for the earlier direction).

Target trim range (user requirement): a typical recorded mission may loiter ~100
revolutions; the player trims that down to ~1-2 by default, with slack up to about 5-10
revolutions to buy alignment. The same intended band applies to BOTH the launch-side
loiter (`L2`, the shipped launch-parking compression) and the destination-side loiter
(`L8`). The arrival solver searches `keepRevs` only within that ~1-10 band; it never
keeps dozens of recorded revolutions just to chase a destination phase.

Implementation note for the eventual plan (read-only here): `keepRevs` is currently a
fixed default and the compressor detects all runs; using it as an alignment knob requires
(a) a per-run "this is the destination loiter" selector and (b) plumbing a chosen
`keepRevs` through the destination-loiter cut. Both are additive to the existing pure
compressor, not a rewrite.

### Composition with PadAlignLaunch and the injection seam

The new step, `DestinationArrivalAlign`, is a pure planner step mirroring
`PadAlignLaunch` (same struct / contract style), wired in `MissionLoopUnitBuilder`
immediately after the `PadAlignLaunch` block. It consumes the recording's destination
surface UT (via `ScanSurfaceSegmentsWithinWindow`), the re-aim plan (`TargetBody`,
`RecordedArrivalUT`, `CommonAncestor`), `IBodyInfo` periods / tolerances for the
destination and its single moon, and the player's `TransitedBodyRotationMode`. It
produces a derived, value-only result (nothing persisted): the chosen window index `k`,
the chosen destination `keepRevs`, `ResidualSeconds`, `WithinTolerance`, `Applied`.

**The composition ordering matters (a real bug if got wrong).** An earlier draft placed
the new step "after `PadAlignLaunch`" and assumed the two ends do not fight. They can:
`PadAlignLaunch` re-snaps the departure by up to half a launch-body sidereal day, which
propagates through the recorded tof to a roughly half-day ARRIVAL shift, which against
`T_rot(dest)` (about 65,517 s for Duna) is hundreds of degrees of destination rotation,
destroying the alignment. Resolution: `DestinationArrivalAlign` must run OVER THE SAME
quantized schedule `PadAlignLaunch` produces, scanning the window index `k` of the
ALREADY-pad-aligned cadence, and NOT propose a free departure shift that pad-align then
re-snaps. Concretely: `PadAlignLaunch` fixes the cadence (= synodic) and the per-window
departure map FIRST; `DestinationArrivalAlign` then chooses which of those fixed,
pad-aligned windows (plus the per-window arrival HOLD and the pre-landing trim) best matches the
destination configuration. The new step never moves the departure off the pad-aligned grid; it only
selects `k`, the arrival hold, and the pre-landing-trim `keepRevs`. This preserves the resolver's window-index-to-departure 1:1
invariant (cadence == synodic) and makes the half-day re-snap a non-issue (there is no
re-snap; the grid is already final when the destination solve runs).

**Why this does not re-enter the all-bodies-resonance dead end.** The launch
(pad-aligned, exact) and the destination (config-aligned, within tolerance) are NOT
required to coincide at one instant. The regenerated transfer absorbs the difference: for
each candidate window the launch is already pixel-perfect on its pad, and the within-window
knobs (the loop-clock arrival hold for the deorbit rotation phase, the pre-landing trim for
earlier-direction room) bring that window's ARRIVAL onto the recorded destination
configuration. We are NOT waiting
for a universal alignment of launch pad and destination config (centuries away for
interplanetary), and we are NOT skipping ahead to a rare naturally aligned window: every
pad-aligned synodic window is a launch opportunity, and we align the arrival WITHIN the
window by spending the knobs. That is the decoupling, and it keeps this clear of the
joint-resonance wall. If a given window's knobs cannot bring the arrival in-band, that
window fails closed to faithful (below) and the next synodic window is still on cadence; we
do not chase a global resonance and we do not stretch the cadence.

### Integration with the shipped geometric (Lambert capture) seam fix

This layer is strictly ON TOP of and orthogonal to the shipped geometric seam
machinery (`ReaimSegmentAssembler.ReplaceHeliocentricLeg` + the full-recorded-span
render window + the per-window Lambert synthesis in
`ReaimPlaybackResolver.BuildWindowSegments`). That machinery connects POSITIONS in
recorded-span time and is invariant to which absolute window `k` is chosen; this layer
only changes WHICH window `k` is synthesized for. It does not modify
`ReplaceHeliocentricLeg`, the render window, or the SOI handoff.

How the transfer leg fails closed (corrected): it fails closed via SYNTHESIS
CONVERGENCE, not a geometric-residual reject. When the per-window Lambert solve does not
converge to a sane transfer conic across the +-6 percent tof search,
`BuildWindowSegments` returns null and that window degrades to faithful replay
(`ReaimPlaybackResolver.cs:205-211`). There is NO 50 km / 5 percent geometric-position
reject on the heliocentric transfer leg today. So an arrival-aligned `k` that cannot be
synthesized cannot produce garbage: it produces the un-aligned (Bug-2) faithful render
for that window, which MUST surface as a distinct state (see the fail-closed contract).

Note on `IsSeamResidualTooLarge` (do not conflate): that guard (> 50 km / 5 percent
radial) lives in `Display/GhostTrajectoryPolylineRenderer.cs` and bounds the
conic-anchor of the body-fixed escape / arrival BURN polylines, NOT the heliocentric
capture. `ReplaceHeliocentricLeg` does not call it. It is a separate, orthogonal guard
and is unaffected by the window choice `k`.

Open interaction (carried to residual risks): the geometric SOI-entry instant from
`CalculatePatch` is not identical to the rotation-aligned arrival; whether a single
chosen window simultaneously yields a convergent, sane capture geometry AND the
rotation-phase match, or whether they trade off, must be validated on the real `s15`
case.

### Threading the decision into both render paths (the actual Bug-2 surface)

Both the OrbitSegment director and the autonomous body-fixed descent polyline get their
effective replay UT from the SAME shared loop clock (`GhostPlaybackLogic.TryComputeSpanLoopUT`,
the `spanLoopUT` mapping). The arrival hold + pre-landing trim are applied INSIDE that one clock
(Phase 3b), so BOTH paths inherit the deorbit alignment AUTOMATICALLY:

- **OrbitSegment director path:** the in-SOI capture orbit is sampled at `spanLoopUT`; the hold
  defers it (and the trim advances it) exactly as for the polyline, because both read the same
  `spanLoopUT`.
- **Autonomous body-fixed surface polyline path:** the descent track is sampled at the same
  `spanLoopUT` (via `DecideUnitMemberRender` / `ResolveTrackingStationSampleUT`, which pass the
  unit's hold + loiter cuts) against the LIVE destination body. So the body-fixed descent and the
  inertial capture orbit are evaluated at the SAME effective UT by construction.

The descent coincidence is therefore STRUCTURAL once the hold + trim live in the shared clock, not
a separate per-path threading requirement. The prior tof-lever framing feared a desync because it
imagined a re-aim schedule that only one path saw; the loop-clock hold has no such split (it is the
inverse of the loiter cut, which both paths already honor). It still gets a P5 playtest
confirmation, but the threading desync the old design feared cannot occur here.

### Feasibility envelope (honest bounds, not an assertion)

The decoupling works because the per-window resolver REGENERATES the transfer for the
target's live position every window, so the transfer's inertial orientation is a free
output. But the slack is bounded:

- The launch pad is pinned exactly every window (`PadAlignLaunch`, whole sidereal-day
  snap, sub-day nudge). UNTOUCHED.
- The deorbit is retimed within each window by the loop-clock arrival hold (continuous in
  `[0, T_rot)`, at most one destination rotation of wait) plus the pre-landing trim (whole
  loiter revolutions when a loiter was recorded, or the flexible-region partial approach-arc
  trim otherwise).

**The numbers below are NAKED (no-knob) bounds, NOT the operative cadence.** They are the
recurrence of a naturally aligned window with NO arrival hold and NO loiter/pre-landing trim
spent. They are stated to show why the bare synodic grid alone does not align the deorbit,
which is exactly why the validated mechanism spends the loop-clock hold + trim WITHIN each
window instead. Do NOT read these as the launch cadence: the launch cadence stays synodic
(the HARD REQUIREMENT); these duty cycles only describe how often a window would align if no
knob were spent.

Because the synodic period and `T_rot(dest)` are incommensurate, the deorbit's rotation
phase samples effectively uniformly across windows. So a NAKED in-band window (no knob
spent) recurs with probability roughly equal to the rotation duty cycle per synodic window:

- Loose (about 910 s / 65,517 s = duty about 0.0139): roughly 1 in 72 synodic windows
  (about 140 Kerbin years) WITHOUT any hold or trim help. This is the naked bound only.
  With the continuous arrival hold spent (it is exactly the minimal forward shift that
  zeroes the rotation residual, at most one Duna day of wait, see the Duna One worked
  example) Loose is reachable EVERY window, loiter or no loiter.
- Precise (about 45.5 s / 65,517 s = duty about 0.00069): roughly 1 in 1,440 synodic
  windows naked. With the continuous arrival hold, Precise is also reachable per-window: the
  hold is continuous, so it can drive the rotation residual arbitrarily close to zero
  regardless of loiter. The integer loiter trim merely supplies the earlier-direction room
  so the wait stays short.

The order-of-magnitude takeaway: tolerances tuned for Mun-class same-parent periods do
NOT transfer to interplanetary, and the bare synodic grid alone (no knob) does not align
the deorbit. But the loop-clock knobs make the per-window cadence work: the continuous
arrival hold (the primary knob, dominant for no-loiter missions like Duna One) and the
pre-landing trim (the earlier-direction room, integer-step when a loiter exists) bring the
deorbit in-band within each synodic window, so the launch stays on cadence. Loose is the
realistic default; Precise is offered as a stricter mode that honestly reports amber for any
window the spent knobs cannot bring in-band. The exact room available from the trim
direction (the recorded loiter / approach-arc revolution count) is a per-mission,
per-planet-pack quantity computed and logged from the live periods, never assumed.

Fail-closed contract (inherited): when no aligned-AND-feasible window exists within the
search horizon (the Lambert solve declines, or the available hold + trim cannot reach the
recorded rotation phase), the window degrades to faithful replay (the shipped
return-null-to-faithful path), which is the un-aligned Bug-2 render for that window. So
the feature never produces garbage; it either aligns or visibly does nothing. This MUST be
surfaced to the player as a distinct, user-visible state (a "no aligned window within
horizon" readout alongside the green / amber residual), never a silent fall-through.

### Scope and the explicit deferral boundary

The user asked for "Kerbin SOI to any other SOI in the star system" (with more complex
transfers handled later). That resolves into three tiers: (a) a direct child of the Sun
with at most one constrained moon, captured-then-deorbit (THIS phase); (b) 2+-moon
planets (the "mini star system", e.g. Jool), which the user explicitly defers; (c)
moons-of-planets and deep / multi-hop chains (e.g. Ike via Duna), excluded upstream by
`ReaimClassifier`. This phase delivers tier (a); tiers (b) and (c) are the named
deferrals below.

**In scope this phase:** a SINGLE cross-parent destination SOI, Kerbin -> X, where X is a
DIRECT child of the common ancestor (the Sun) and has AT MOST ONE constrained moon, and a
CAPTURED-then-deorbit landing (a recorded target-body OrbitSegment arrival leg exists).
This matches the existing `ReaimClassifier` single-hop direct-child scope exactly, so no
classifier extension is needed for the captured-orbit Duna case (Duna is a direct child
of the Sun). The `DestRotation` constraint fires for landings; the single moon adds one
`MoonConfig` constraint when its phase is visible in the recorded arc.

**Decision: atmo-direct landings are OUT OF SCOPE this phase.** An interplanetary landing
that aerocaptures or plows straight into the atmosphere with NO captured destination orbit
segment is scoped OUT. Scope is captured-then-deorbit interplanetary landings only.
`ReaimClassifier` requires an `ArrivalLeg` (the first target-body OrbitSegment after the
heliocentric coast, `ReaimClassifier.cs:106-120`), so an aerocapture / direct plunge that
records no stable target-body OrbitSegment classifies Unsupported and never reaches re-aim
at all; it also lacks the destination loiter the re-timer needs. Admitting it is a
separate, larger classifier change deferred past this phase.

**Decision: validate orbit-only / orbital-arrival FIRST** (risk reducer). Prove the
arrival-UT control on ORBIT-ONLY cross-parent arrival (where the SOI seam is far from
camera and the upstream geometric plan already accepts a small SOI-edge seam) BEFORE
depending on the deterministic landing replay. The on-camera rigid descent (`L9`) depends on the
separately deferred S4 descent re-stitch (the upstream re-aim plan defers S4 entirely; the
render-rewrite plan gates the descent re-stitch behind its seam-tolerance decision). This
layer can be DESIGNED and validated against the orbital-arrival state now; the on-camera
body-fixed descent coincidence lands when the separately deferred S4 descent re-stitch
ships. The arrival-UT layer does not REQUIRE S4 to be correct; it makes S4's job trivial
(the destination is already in the recorded configuration) when S4 ships.

**Decision: destination moon count cutoff is 0 or 1 constrained moon.** A 0-moon
destination (Eve, Moho, Dres) imposes a single `DestRotation` constraint and no
`MoonConfig`; a 1-moon destination adds exactly one `MoonConfig`. Both are IN scope.

**Deferred (carry forward, do not attempt this phase):**

- **Jool-class many-moon "mini star system" destinations** (2+ constrained moons). The
  joint duty product collapses the aligned-window frequency toward the centuries-away
  regime; detect (destination has more than one constrained moon) and report "many-moon
  destination not yet supported," falling to destination-rotation-only or faithful. The
  cutoff is the COUNT of constrained moons: 0 or 1 in scope, 2+ detected-and-reported as
  not-supported. **RESOLVED by M-MIS-6** — the resonant-configuration cut is designed in
  `docs/dev/design-mission-multimoon-alignment.md` (T_config joint-configuration hold,
  finite aligned horizon; incommensurate packs still fail closed with amber).
- **Multi-hop / gravity assist and deep chains** (Ike via Duna). Already excluded upstream
  by `ReaimClassifier` (single-hop direct-child guard), so such a mission never reaches
  `DestinationArrivalAlign`. A recorded landing ON Ike (Ike as the destination) is
  currently classifier-Unsupported (deep chain) and is deferred with the multi-hop case.
  Ike appears in this phase only as a `MoonConfig` of a Duna-destination mission, never as
  the destination.
- **Direct atmospheric entry with no captured destination orbit segment** (the atmo-direct
  decision above). Scoped OUT this phase; admitting it is a separate, larger classifier
  change.

### Dead-end compliance

All five hard dead-ends are honored, including under composition:

1. **No per-element LAN / Kepler-node rotation:** the design chooses a window index (a
   scheduling scalar) and replays the recorded conic verbatim; no orbit element is rotated.
2. **Descent stays body-fixed:** `L9` is hard; the design aligns the inertial orbit
   projection TO the body-fixed descent by choosing WHEN to arrive, never makes the descent
   inertial.
3. **No body-fixed-to-inertial longitude LIFT:** positions come from the recorded arcs
   replayed at the chosen window; only time phases are computed (`CircularPhaseError`).
4. **The heliocentric transfer draw window is not extended into the SOI:** handoff is at
   the recorded boundary (full-recorded-span render, unchanged).
5. **No single joint resonance of all bodies in one launch instant:** the launch
   (pad-aligned) and destination (config-aligned) are decoupled; we choose among
   pad-aligned windows the one whose arrival matches the destination, and fail closed if
   none exists in horizon. The 2+ moon case that would approach a universal resonance is
   the explicit deferral boundary.

### Hard vs soft

HARD (defect if violated): recorded ascent `L1`, recorded escape `L3`, recorded in-SOI
arcs `L7`, recorded descent `L9` all replay exactly relative to their recorded clocks; the
descent stays body-fixed (never inertial); the launch stays pad-aligned (untouched); the
regenerated transfer is a physical Lambert solution at the recorded tof.

SOFT (best-fit within a physics-derived tolerance band, the quality readout):
`DestRotation` within rotation tolerance at the chosen window; each `MoonConfig` within its
orbital tolerance. The soft set is what the window-index solve optimizes; the residual is
reported green / amber.

### Tolerance / mode summary

```
mode        DestRotation tol             MoonConfig tol         window cadence (intuition)
----------- ---------------------------- ---------------------- --------------------------
Off (Drop)  constraint removed           SoiRadius/OrbitVel     shortest; rotation ignored
Loose       T_rot*(5/360)  (~910 s Duna) SoiRadius/OrbitVel     medium; default for loitered
Precise     T_rot*(0.25/360)(~45 s Duna) SoiRadius/OrbitVel     rare; amber unless long loiter
```

Launch pad: always exact, never affected by this control. Loose is the default; Precise is
the explicit rare-window opt-in.

### Residual risks (validate before declaring Bug 2 fixed)

- The geometric SOI-entry instant from `CalculatePatch` is not identical to the
  rotation-aligned arrival; whether a single chosen window yields a convergent, sane
  synthesized capture geometry (the per-window Lambert solve in
  `ReaimPlaybackResolver.BuildWindowSegments`) is one question, and whether the loop-clock
  arrival hold + pre-landing trim can drive the deorbit rotation phase in-band is a separate
  one. Because the hold acts AFTER SOI entry and the transfer is upstream of it, the two do
  NOT fight (the deorbit-phase alignment never reaches back into the capture geometry); this
  decoupling must still be confirmed on the real `s15` case.
- The body-fixed descent coincidence is STRUCTURAL, not a per-path threading requirement: both
  the director and the autonomous `GhostTrajectoryPolylineRenderer.Driver` surface path read the
  same `spanLoopUT` from the one shared loop clock (`TryComputeSpanLoopUT`), into which the arrival
  hold + pre-landing trim are applied (Phase 3b). So aligning the deorbit in that clock reaches both
  paths by construction. This is the literal Bug-2 surface; it still gets a P5 playtest
  confirmation, but the threading desync the prior (tof-lever) framing feared cannot occur with the
  loop-clock hold.
- Window frequency vs tolerance is NOT the gating risk it was under the tof-lever framing: the
  arrival hold is CONTINUOUS, so it reaches any destination rotation phase WITHIN each on-cadence
  synodic window, even with no parking loiter (Loose AND Precise are reachable per window). The
  remaining limit is the earlier-direction trim room when the minimal-wait split prefers a short
  pre-landing trim over a long hold. If neither knob can align a window (degenerate inputs, no
  rotation constraint, or the geometric capture declines) it FAILS CLOSED to the un-aligned Bug-2
  render, which is why the no-aligned-window state MUST be a distinct, user-visible message, never
  a silent fall-through.
- Using `keepRevs` as an alignment knob is a real API change to `ReaimLoiterCompressor`
  (today `keepRevs` is the fixed default 1, and the compressor detects ALL same-body loiter
  runs with no notion of "the destination loiter"); it needs a per-run destination selector
  and a chosen-`keepRevs` plumb-through. Additive, but not a free reuse.
- Eccentric destination heliocentric orbit (Moho-like or modded planets): the synodic
  congruent-window premise and the "arrival rotation phase samples uniformly" assumption
  weaken; the recorded tof at one window may not reproduce the recording-time relative
  geometry, pushing the required tof outside the +-6 percent band and declining the window.
  Not modeled this phase; flag and likely fail closed.
- Polar / retrograde landing sites: the design aligns a single scalar rotation phase, which
  barely affects a polar landing site's inertial position (near the spin axis) and is
  rotation-critical at the equator. A polar landing may align trivially while an equatorial
  one is rare under the same tolerance; the design does not surface this latitude
  dependence. For an off-equatorial site, rotation-phase alignment is necessary but may not
  fully capture the landing-site plane geometry.

### Open questions

- **Default tolerance mode for the destination.** DECIDED: Loose is the default; Precise is
  the explicit rare-window opt-in (Mun-tuned thresholds do not transfer to interplanetary
  cadence, see the feasibility envelope). Retained here only as a tuning note for the
  starting Loose/Precise degree values, not an open scope question.
- **The order in which `DestinationArrivalAlign` and the geometric seam guard are evaluated
  per candidate window.** Whether to reject a geometry-failing window before or after the
  rotation-phase score must be settled in the plan; it affects which window the bounded-best
  search returns when geometry and rotation phase trade off.
- **Where the "no aligned window within horizon" readout lives in the Missions tab** (a new
  distinct state vs an extension of the amber residual cell). The contract is that it is
  distinct and user-visible; the exact surface is a UI detail for the plan.

### Where it plugs into existing code (read-only here)

- A new pure `MissionPeriodicity.DestinationArrivalAlign` (or sibling planner) consuming
  `ScanSurfaceSegmentsWithinWindow`, the `ReaimMissionPlan`, and `IBodyInfo`; reuses
  `CircularPhaseError` + `TryFindNextScheduleK` verbatim. Pure + unit-testable via the
  `IBodyInfo` seam.
- `MissionLoopUnitBuilder` wires `DestinationArrivalAlign` immediately after the
  `PadAlignLaunch` block, OVER the already-pad-aligned synodic schedule (it selects `k` + the
  arrival hold + the pre-landing-trim `keepRevs`, never re-snaps the departure).
- `ReaimLoiterCompressor` gains a per-run destination-loiter selector and a chosen-`keepRevs`
  plumb-through (additive to the pure compressor).
- The chosen window's loop shift must reach BOTH the OrbitSegment director path (automatic)
  and the autonomous `GhostTrajectoryPolylineRenderer.Driver` surface path (the verification
  gate).
- No recording-format change; nothing new persisted (`A` = window index `k` is derived; the
  result is value-only).

See `docs/dev/plans/reaim-destination-arrival-alignment.md` for the phased
implementation plan that consumes this design.

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
    UnsupportedCrossParent        //   sibling/interplanetary target (until Phase 4;
                                  //   now designed, see Cross-parent destination-SOI
                                  //   arrival-UT alignment)
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

- **~~Replay as-is; do not re-aim the trajectory.~~ OBSOLETE (2026-06-02) — see the
  Superseded banner at the top of this doc.** This originally locked replaying the
  recorded transfer unchanged and choosing only WHEN to launch. The interplanetary
  transfer leg is now **re-aimed**: regenerated per launch window via a Lambert solve
  so it always intercepts (`docs/dev/done/plans/reaim-interplanetary-transfers.md`),
  because the exact recorded configuration recurs too rarely (joint resonance of
  incommensurate periods ≈ centuries) for a replay-as-is interplanetary loop to be
  usable. What remains true: the recorded SOI-local segments (ascent, parking,
  arrival, landing) are still replayed as-is and only re-timed; we re-aim the
  heliocentric bridge between SOIs, not those.
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
  rather than silently mis-scheduled. The cross-parent arrival-UT alignment is now
  designed (see the Cross-parent destination-SOI arrival-UT alignment section); it
  reframes the deferral around the destination-SOI ARRIVAL UT rather than a synodic
  launch-UT lock.
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
  same-parent intercepts are v1.]** (now designed: see Cross-parent destination-SOI
  arrival-UT alignment)
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
