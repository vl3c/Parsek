# Plan: Cross-parent destination-SOI arrival-UT alignment (re-aim Phase 4)

Status: DESIGN / PLAN. No production code proposed here.

This is the Step-4 implementation plan for the temporal / rotation-phase layer that
cross-parent looped landings need. It picks up two deferred threads:

- The deferred re-aim S4 / cross-parent landing phase in
  `docs/dev/done/plans/reaim-interplanetary-transfers.md` (section 2 segment-model table
  row S4, and section 13 Deferred). That doc's GEOMETRIC seam decisions (Lambert capture,
  `ReplaceHeliocentricLeg`, the accepted SOI-edge seam) remain authoritative and are
  orthogonal to this plan (section 11); this plan supersedes only the TEMPORAL /
  rotation-phase scope of the S4 deferral and feeds the transfer synthesis an arrival
  window.
- The deferred Phase 4 (the `UnsupportedCrossParent` seam) of
  `docs/dev/design-mission-periodicity.md` (the constraint model this plan reuses).

Branch: TBD (off the post-re-aim HEAD).

References (read before implementing):
- `docs/dev/design-mission-periodicity.md` (the constraint model: Rotation(B) /
  Orbital(C), the zero-drift scheduler, the physics-derived tolerances, the
  `UnsupportedCrossParent` Phase-4 deferral this plan builds out).
- `docs/dev/done/plans/reaim-interplanetary-transfers.md` (the upstream feature this sits
  on top of: section 2 segment model, section 4 stitching / `PadAlignLaunch`, section 13
  the S4 deferral whose temporal layer this plan supersedes).
- `docs/dev/plans/map-ts-render-rewrite-phases.md` (Workstream C / C2 descent re-stitch;
  this plan's on-camera landing acceptance gate depends on it).
- `Source/Parsek/MissionPeriodicity.cs` (the same-parent template: `ExtractConstraints`,
  `TryFindNextScheduleK`, `CircularPhaseError`, `SelectAnchorConstraintIndex`,
  `TransitedBodyRotationMode`, `ScanSurfaceSegmentsWithinWindow`, `IBodyInfo`).
- `Source/Parsek/Reaim/ReaimPlaybackResolver.cs`, `ReaimWindowPlanner.cs`,
  `ReaimSegmentAssembler.cs`, `ReaimClassifier.cs`, `ReaimLoiterCompressor.cs`,
  `ReaimedTrajectory.cs` (the per-window synthesis this plan schedules for).
- `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` (the autonomous body-fixed
  surface render path that must be threaded, section 12).

---

## Problem statement

A looped, re-aimed interplanetary mission that LANDS (save s15 "Duna One", Kerbin to
Duna) draws the inertial arrival orbit about 131 degrees off the body-fixed landing site,
the icon overshoots the landing, then teleports onto the body-fixed descent polyline (a
seam jump of about 180,554 km in the captured frame). Re-aim pad-aligns the LAUNCH body
(Kerbin) only (`ReaimWindowPlanner.PadAlignLaunch`); nothing aligns the DESTINATION body's
rotation phase across the loop shift (about 1.572e9 s for s15), so Duna rotates an
arbitrary fraction of a turn under the inertial capture orbit and the inertial orbit
diverges from the rotating-surface landing. This is the deferred cross-parent twin of an
alignment that already SHIPS for same-parent missions (Mun / Minmus) as the Off / Loose /
Precise "Landing-body alignment" control: for a cross-parent target `ExtractConstraints`
returns `Support.UnsupportedCrossParent` and does NOT phase-lock. The fix is a new
scheduling layer that chooses the destination-SOI arrival UT so the destination's rotation
phase (and any single inner moon's orbital phase) recur to their recorded values, decoupled
from the already-correct Kerbin launch by re-aim's per-window transfer regeneration.

What this is NOT:
- NOT a render bug. The descent is correctly body-fixed; the inertial-orbit-vs-body-fixed-
  landing mismatch is a TEMPORAL / rotation-phase mismatch.
- NOT the conic-anchor / escape-burn fix (shipped and guarded: `TryAnchorLegToConicSeam`,
  `IsSeamResidualTooLarge`).
- NOT the geometric (Lambert capture) seam. That work connects POSITIONS in recorded-span
  time; this is the complementary TEMPORAL layer that schedules WHEN the ghost arrives so
  the destination is in its recorded configuration. The two are orthogonal (section 11).
- NOT inherent / unsolvable. The cross-parent version is a missing PHASE, not a wall.

---

## 1. The requirements chain and the degree-of-freedom model

The user's flexibility chain, each link tagged DETERMINISTIC (replays exactly relative to
its own recorded clock) or FLEXIBLE (slack the scheduler may spend):

```
L0  moment of launch          FLEXIBLE        delay until the schedule says "go"
L1  launch -> loiter orbit     DETERMINISTIC   recorded ascent (mesh + animation exact)
L2  loiter orbit count         FLEXIBLE        trim recorded N orbits down (launch side)
L3  orbit -> SOI exit          DETERMINISTIC   recorded escape burn
L4  departure SOI exit          FLEXIBLE        angle AND timing free (high-warp coast)
L5  heliocentric transfer      DETERMINISTIC*  solver-REGENERATED per window, then exact
L6  destination SOI entry       FLEXIBLE        angle AND timing free (favorable config)
L7  in-SOI recorded arcs       DETERMINISTIC   recorded RELATIVE to SOI entry
L8  destination loiter count    FLEXIBLE        trim to align with recorded descent
L9  descent / landing          DETERMINISTIC   recorded body-fixed, plays exactly
```

L5 is "deterministic after regeneration": its SHAPE (recorded time-of-flight, handedness)
is fixed; it is recomputed each window so it points where the bodies actually are (the
shipped re-aim contract, `ReaimPlaybackResolver.BuildWindowSegments`). SOI exit AND entry
DIRECTION / ANGLE never appear in the constraint set: the recorded arcs are anchored to
entry, so where the ghost crosses the SOI edge is irrelevant. Only the destination CONFIG
at entry and surface-arrival matters.

Everything inside the destination SOI (L7, L8, L9) is recorded RELATIVE to entry or
body-fixed, so it replays faithfully relative to its OWN clock the moment we fix WHEN that
clock starts. Only two live-sky quantities can be misaligned, and both are pure periodic
functions of absolute UT:

- The destination body ROTATION phase, period `T_rot(dest)` (`IBodyInfo.RotationPeriod`).
  This is the 131-degree offset.
- Each inner moon ORBITAL phase, period `T_orb(moon)` (`IBodyInfo.OrbitPeriod`).

### Anchor at the LANDING; the SOI entry is a DERIVED variable (the controlling-variable model, 2026-06-04)

The binding constraint is the LANDING (L9), not the SOI entry (L7). So anchor the whole
in-SOI chain at the landing and let the SOI entry fall out as a CALCULATED, VARIABLE point:

1. Choose the arrival hold so the destination's rotation phase at the DEORBIT / landing
   recurs to its recorded value (align L9, the thing the user actually sees). For a mission
   with no in-SOI (destination-side) loiter cut this is identical to aligning the SOI entry
   (the entry-anchored hold already shipped in PR #1030), because the entry-to-landing span
   replays at recorded rate, so the two holds are equal mod `T_rot`. Anchoring at the landing
   is the more robust statement: it stays correct even if an in-SOI rate change or loiter is
   introduced, and it ties the alignment directly to the only frame that must be exact.
2. Render the recorded in-SOI tail (capture hyperbola -> parking -> descent -> landing) as
   ONE connected chain, working BACKWARD from the landing. The recorded chain IS connected,
   and the hold PRESERVES that connection: at the deorbit instant the inertial orbit point is
   `R_recorded x bodyfixed` and the body-fixed descent renders at `R_now x bodyfixed`, and the
   hold forces `R_now = R_recorded`, so the two coincide. Render it exactly as the recording.
3. The SOI ENTRY POINT is NOT controlled or aligned. It is wherever the connected recorded
   chain, anchored at the landing, places it on the destination SOI sphere. We just CALCULATE
   it and connect the regenerated heliocentric transfer (L5) to that point. Because the entry
   is a free derived variable (its angle is irrelevant, per the paragraph above), everything
   downstream of it (L7/L8/L9) falls into place automatically. This is the "variable SOI
   entry makes it all fall into place" result: by NOT constraining the entry, the recorded
   chain stays connected end to end and lands on-site.

The only residual is the transfer-to-SOI-entry seam: the recorded capture hyperbola carries
the recorded window's approach direction while the regenerated transfer arrives from the new
window's direction, so a small kink can remain right at the SOI edge. This is cosmetically
irrelevant (deep space, far from the landing) and is exactly the "SOI entry DIRECTION is
irrelevant" point above. Ike's orbital phase is the one alignment lever still open.

STATUS CORRECTION (supersedes the "NOT a render bug" line in the Problem statement, for the
REMAINING work only): the TEMPORAL alignment is now DONE and CORRECT. PR #1030's hold renders
the landing SITE on the recorded site (playtest-confirmed), and the in-SOI proto orbit lines
connect. With the alignment correct, the remaining defects are RENDER bugs: the body-fixed
descent polyline is drawn (reaches `Draw3D` with all points) but invisible on screen; the
proto-vessel icon overshoots its inertial orbit past the deorbit point and then the descent
marker teleports back to the descent start; and the proto orbit does not visually hand off to
the descent. These are failures of the renderer to honor a connection that EXISTS in the data,
not alignment failures. The fix is to make the renderer draw the recorded in-SOI tail as the
single connected chain this section describes (visible descent line + clean orbit->descent
marker handoff + the deorbit point shared by both), anchored at the landing.

### HARD REQUIREMENT: the synodic launch cadence is preserved

The looped launch cadence MUST stay at the synodic period (about 2 Kerbin years for
Kerbin to Duna). The feature does NOT relaunch only at rare naturally aligned windows.
Every synodic window is a launch opportunity. Arrival alignment is achieved by spending the
FLEXIBLE knobs WITHIN each window, never by skipping ahead to a window where the bodies
happen to be naturally aligned. If a given window cannot be aligned by any available knob,
it FAILS CLOSED to faithful for THAT window (showing the un-aligned arrival as a visible
state, section 9) and the next synodic window is still on cadence. Either way the cadence
stays synodic. An earlier draft mis-modelled this as "collapse to ONE selected scalar k =
a synodic window index, selected on the ~2-year grid"; taken literally that would degrade
launch opportunities from every ~2 years to (at Loose) roughly every ~140 years. That
single-grid-scalar conclusion is WRONG and is replaced by the multi-knob model here. See
`docs/dev/design-mission-periodicity.md` (the HARD REQUIREMENT block and the
cadence-preserving multi-knob model) for the authoritative statement.

### The arrival-alignment TARGET and the two arrival flex points

The alignment TARGET is the DEORBIT instant: the moment where the inertial proto-vessel
orbit hands off to the body-fixed descent. Aligning the deorbit to the destination body's
RECORDED rotation phase lands the body-fixed descent on the recorded site and removes the
orbit->descent teleport. The in-SOI replay's live UT is set by the loop clock (the
recorded-span -> live remap); to shift the deorbit, adjust that remap in the FLEXIBLE
arrival region. There are exactly TWO knobs, both at the arrival region, neither requiring
a parking loiter, and together they are bidirectional room:

- Arrival flex point #1: ARRIVAL HOLD (the deorbit shifts LATER). Insert dead time at the
  heliocentric->capture boundary, the exact INVERSE of a loiter cut: a loiter cut REMOVES
  recorded-span time, the hold INSERTS it, so the in-SOI replay (including the deorbit)
  starts LATER in live time (the ghost waits at SOI arrival). CONTINUOUS (any hold W in
  `[0, T_rot)`) and POSITION-CONTINUOUS (a wait, never a teleport). Phase 3a implements the
  pure helpers `GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` (the minimal forward W
  that aligns the deorbit rotation phase) and `GhostPlaybackLogic.ApplyArrivalHoldToPhase`
  (the effective-span -> compressed-span remap, composing with the existing loiter-cut
  `DecompressSpanUT`).
- Arrival flex point #2: PRE-LANDING TRIM (the deorbit shifts EARLIER). Remove time from
  the proto-vessel orbital segment RIGHT BEFORE the landing, reusing the loiter-cut
  machinery (section 4). Usable when that segment is "long enough to be trimmed", and it
  works even without parking-loiter orbits. CONSTRAINT: keep the proto-vessel
  POSITION-CONTINUOUS. Skipping WHOLE orbital periods is clean; trimming a PARTIAL arc of a
  non-periodic approach jumps the proto-vessel along its orbit and is acceptable ONLY in the
  flexible SOI-edge region (which the design already designates as flexible). When a
  destination loiter was recorded this is L8, the integer-period re-timer (whole recorded
  loiter revolutions); a short or non-loiter approach offers only the flexible-region
  partial trim.

The alignment picks the MINIMAL / least-disruptive combination of trim (earlier) and hold
(later) that lands the deorbit on the recorded rotation phase (prefer a short trim over a
long wait). They are NOT fully independent of the in-SOI sequence shape: when a mission
records NO destination loiter (the s15 "Duna One" case below), the in-SOI sequence is rigid
(capture-then-land within one destination day), the integer pre-landing trim has little
whole-period room, and the continuous arrival hold (#1) carries the alignment; once the
deorbit is on the recorded rotation phase the rigid sequence reproduces the recorded
landing.

### The cadence-preserving multi-knob model

Every FLEXIBLE link is spent JOINTLY per synodic window, NOT collapsed to a single
grid-selected scalar. Per window k (the coarse cadence knob, fixed at the synodic period
and never lengthened):

- Coarse (selects the window, preserves cadence): the synodic window index k. Window k has
  departure `D_k = RecordedDepartureUT + k * synodic` and arrival near `D_k + tof`. Every k
  is a launch opportunity; we do not skip k values to find a naturally aligned one.
- Arrival hold (arrival flex point #1, the deorbit shifts LATER): the loop-clock dead time
  inserted at the heliocentric->capture boundary
  (`GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` /
  `GhostPlaybackLogic.ApplyArrivalHoldToPhase`). CONTINUOUS over `[0, T_rot)` and
  position-continuous; the primary continuous knob for a no-loiter mission.
- Pre-landing trim (arrival flex point #2, the deorbit shifts EARLIER): time removed from
  the orbital segment right before the landing, reusing the loiter-cut machinery (L8
  whole-revolution steps when a loiter was recorded; a flexible-region partial trim
  otherwise, kept position-continuous).
- Launch-side trim: the L2 launch-parking compression (already ships).

Given k and the arrival hold + pre-landing trim, L0 / L4 / L6 follow (`A_k` minus the
regenerated transfer time, pad-quantized by the EXISTING `PadAlignLaunch`); L5 is
regenerated, not chosen. Both knobs act on the loop clock AFTER SOI entry, so neither
touches the launch or the transfer.

### Worked example: Duna One (s15), the arrival hold is the primary knob

Verified from `logs/2026-06-03_1951_duna-arrival/KSP.log` (ReaimDiag dump) and the s15
recording `61e9177...prec`:

- LAUNCH side: a Kerbin parking loiter of ~132 days, already compressed by the shipped
  single launch-side loiter cut (`cut#0 start=52570174 len=11393869`, the L2 trim).
- Heliocentric transfer: `tof = 6,854,613 s` (about 79.3 days). Kerbin -> Duna synodic =
  `19,653,076 s` (about 2.1 Kerbin years), the on-cadence launch interval.
- INSIDE the Duna SOI: arrival `70,898,646` to last orbit segment end `70,963,653` is about
  `65,007 s` (~18.1 hours, about 1 Duna rotation): a long capture approach (about 0.87 of a
  Duna rotation), then short decaying approach arcs (sma `492 -> 340 km`, fractional revs
  0.45 / 0.32 / 0.02 / 0.09), then the descent. So Duna One records NO destination parking
  loiter (captures and lands within ~1 Duna day). It DOES record a brief Ike SOI transit
  (two short hyperbolic Ike orbit segments, ~37 minutes total, about 16 hours after Duna
  arrival), but that transit imposes NO INDEPENDENT constraint: Ike is tidally locked to
  Duna, so its orbital phase collapses onto Duna's rotation phase (the tidal-collapse note,
  section 3a). Aligning Duna's rotation auto-aligns Ike, so there is one effective
  destination constraint, not two.

Implication: there is no parking loiter to step by whole revolutions and no independent moon
to align (the brief Ike transit collapses onto Duna's rotation, section 3a), so the clean
(whole-period) pre-landing trim room is limited to the short decaying approach arcs in the
flexible SOI edge. The continuous loop-clock ARRIVAL HOLD is the primary knob: it defers the
in-SOI replay so the deorbit lands on the recorded Duna rotation phase, with the
approach-region partial trim adding earlier-direction room within the flexible SOI edge. The
arithmetic is favorable: `T_rot(Duna)` is about `65,518 s` (about 18.2 hours), so any
aligning hold is W in `[0, T_rot)`, i.e. at most about 18 hours of wait at SOI arrival,
negligible inside the ~2.1 Kerbin-year synodic cycle. Because the in-SOI sequence is rigid
(no loiter), aligning the deorbit rotation phase ALSO aligns the landing.

The consequence: for Duna One the Bug-2 fix is the loop-clock arrival hold (continuous,
under one Duna day) plus the flexible-region approach trim, aligning every synodic window
and preserving the ~2.1 Kerbin-year cadence. This does NOT use the synthesized heliocentric
tof to move the arrival (REFUTED, see section 13 and Appendix A.2: the tof reshapes the
transfer arc but the in-SOI replay is on the loop clock, independent of tof). The synodic
launch cadence is preserved regardless: the cadence is the window index k, and the hold +
trim re-time only the in-SOI replay AFTER SOI entry.

### The honest qualifier (the deorbit is moved by the loop-clock hold + trim)

The deorbit is moved by the loop-clock ARRIVAL HOLD (continuous in `[0, T_rot)`) plus the
PRE-LANDING TRIM (whole loiter revolutions when a loiter was recorded, plus the limited
partial trim the flexible SOI-edge approach arcs allow). Both act AFTER SOI entry, on the
same loop clock (`GhostPlaybackLogic.TryComputeSpanLoopUT`); neither touches the launch pad
or the heliocentric transfer, both upstream of the capture boundary. Confirmed in source:
`ReaimPlaybackResolver.BuildWindowSegments` (`ReaimPlaybackResolver.cs:139-248`) replaces
ONLY the heliocentric leg over the FIXED recorded span
`[RecordedDepartureUT, RecordedArrivalUT]`; its `soiEntryUT` is only LOGGED
(`ReaimPlaybackResolver.cs:246`), never used to re-time the in-SOI replay.

These facts are respected and they do NOT imply the cadence-degrading conclusion that the
feature selects a rare naturally aligned window. The arrival is aligned WITHIN each synodic
window by the hold + trim, jointly; the launch cadence stays synodic. The hold is
continuous, so a no-loiter mission like Duna One can align the deorbit within every window
from the hold alone. So the design respects: the cadence is the window index k, the
in-SOI alignment is the loop-clock hold + trim, bounded by the recorded approach/loiter room
(Appendix A) and fail-closed-to-faithful per window, not a freely dialed scalar and not a
rare grid-selected window.

---

## 2. The destination-side constraint set

Mirror the existing two-kind constraint model (`MissionPeriodicity` ConstraintKind Rotation
/ Orbital), anchored to the recorded destination configuration rather than to the launch
UT0:

- DestRotation(dest): emitted only when the recorded in-SOI arcs include BOTH a destination
  surface / atmospheric segment AND a destination inertial-orbit segment (the arrival-orbit
  to descent hand-off). This is the exact pair rule the launch side uses. A LANDING fires
  it; an orbit-only flyby does NOT (orbit-only arrivals render correctly at any rotation
  phase). Period `T_rot(dest)`. Phase reference = the recorded destination surface-start UT
  (see 2a).
- MoonConfig(m), one per inner moon m whose phase is visible in the recorded in-SOI window.
  Period `T_orb(m about dest)`. Phase reference = the recorded SOI-entry UT
  (`ReaimMissionPlan.RecordedArrivalUT`).

2a. The recorded surface-arrival UT is extractable. The recorded touchdown / surface-start
UT is NOT on the re-aim plan, but it IS recoverable from the recording's surface
TrackSections via the existing `MissionPeriodicity.ScanSurfaceSegmentsWithinWindow`
(`MissionPeriodicity.cs:1218-1245`), which already scans rotation-constraining surface /
atmospheric sections and records the earliest start UT per body. The DestRotation
constraint keys on that recorded surface-start UT on the destination body (call it
`RecordedDestSurfaceUT`). Aligning the destination rotation phase at `RecordedDestSurfaceUT`
(not merely at SOI entry) is what closes the touchdown overshoot.

---

## 3. The arrival-UT window solve (reuse the proven scheduler)

This reuses the SAME zero-drift primitive the launch side uses, variable relabeled from
launch UT to arrival window index k. Candidates are the synodic windows reachable by the
shipped schedule (departure `D_k`, arrival near `D_k + tof`, with the bounded tof search
refining within +-6 percent). For each candidate:

- DestRotation residual = `CircularPhaseError(A_surface_k - RecordedDestSurfaceUT,
  T_rot(dest))`, where `A_surface_k` is the effective destination surface-arrival (deorbit)
  UT for window k AFTER the loop-clock arrival hold + pre-landing trim (sections 1, 4). The
  continuous hold drives this residual to zero within each window (it is exactly the minimal
  forward shift that zeroes it); the trim supplies the earlier-direction room.
- MoonConfig(m) residual = `CircularPhaseError(A_entry_k - RecordedArrivalUT, T_orb(m))`,
  where `A_entry_k` is the SOI-entry UT for window k (shifted by the same arrival hold).

Use `TryFindNextScheduleK` (`MissionPeriodicity.cs:901-954`) verbatim: scan k, accept the
first window where every residual is within its tolerance, else the bounded-best (minimum
worst residual) window, never accumulating drift (the residual at k is an absolute function
of k, so the offsets cancel). Objective `worst(k) = max(DestRotation residual,
max_m MoonConfig residual)`. Report `ResidualSeconds` and `WithinTolerance` exactly as the
same-parent path does (green / amber).

This scan-k walks the on-cadence synodic grid (the pad-aligned windows launched every
synodic period); it never skips a launch. Within each window the loop-clock arrival hold +
pre-landing trim drive the deorbit onto the recorded rotation phase, so the alignment is a
per-window operation, not a hunt for a naturally aligned k. A window the hold + trim
genuinely cannot bring in-tolerance fails closed to faithful (still launched on cadence). So
"scan k" selects the on-cadence window; the hold + trim select the RENDER alignment WITHIN
that window. (The synthesized heliocentric tof is NOT a lever here, per section 13 and
Appendix A.2: it cannot move the loop-clock-driven in-SOI replay.)

3a. The live joint resonance, NOT a duty-cycle product. The solver reads the ACTUAL live
`IBodyInfo` periods and computes the joint resonance from them; it NEVER multiplies duty
cycles assuming independence. For the real Bug-2 case Ike is tidally locked, so `T_orb(Ike)`
equals `T_rot(Ike)` and both equal Duna's rotation period to about 6 significant figures
(around 65,517 s). They are NOT independent: an arrival that matches Duna's rotation phase
auto-matches Ike's orbital phase. The joint solver must let the EXISTING tidal-collapse path
handle coincident periods for free: `MissionPeriodicity` already treats two periods within a
relative tolerance as one (the `tidal-collapse` method; `MissionPeriodicity.cs` around 535 and
651-655, no-distinct-period branch 1102-1127), the same mechanism that collapses a
tidally-locked Mun `rotationPeriod == orbit.period` pair in the same-parent path. No
special-case merge is added here.
For the actual mission this makes Bug 2 EASIER (one effective constraint); the general
independent-moon case (a fast body with a slow moon) is what the math must handle by reading
live periods, never by assuming independence.

---

## 4. The pre-landing trim role: a coarse re-timer (the deorbit shifts EARLIER)

The pre-landing trim is the EARLIER-direction half of the arrival's bidirectional room; the
loop-clock arrival hold (section 1) is the LATER-direction, continuous half. The trim
removes time from the proto-vessel orbital segment right before the landing, keeping the
proto-vessel position-continuous (whole orbital periods always, partial arcs only in the
flexible SOI edge).

`ReaimLoiterCompressor.ComputeCuts` excises WHOLE periods from the run START and keeps the
tail ending at the recorded run end so the exit phase is preserved
(`ReaimLoiterCompressor.cs:54-56,131`). Cuts are integer multiples of the loiter period; it
detects ALL same-body loiter runs and is called with the fixed default `keepRevs = 1`. It is
a COARSE re-timer, not a fine vernier.

For arrival alignment it does ONE useful thing: it shifts the post-arrival timeline by WHOLE
loiter periods, selecting WHICH recorded loiter revolution becomes the effective deorbit /
surface arrival. The deorbit instant moves by integer multiples of
`T_loiter = 2 * pi * sqrt(a^3 / mu)`, with `mu = IBodyInfo.GravParameter(dest)`. Each excised
revolution steps the surface-arrival phase by `frac(T_loiter / T_rot(dest))` of a destination
turn (fine for a low loiter orbit, coarse for a high one).

What it does NOT do: it cannot manufacture a sub-`T_loiter` continuous absorber, and it
preserves the recorded exit phase relative to the run end. The honest consequence:

- When a recorded mission HAS a long, low destination loiter (many revolutions), the integer
  steps provide enough phase granularity that some keepRevs lands the surface arrival within
  tolerance for a given window, materially widening achievable windows (and making Precise
  reachable).
- When a recorded mission has a short loiter (1 to 3 revs) or NO destination loiter (direct
  atmospheric entry), the integer pre-landing trim contributes little or nothing, and the
  alignment is carried by the continuous loop-clock arrival hold (section 1), with whatever
  flexible-region partial approach-arc trim is available for the earlier direction
  (Appendix A).

Target trim range (user requirement): a typical recorded mission may loiter ~100
revolutions; the player trims that down to ~1-2 by default, with slack up to about 5-10
revolutions to buy alignment. The same intended band applies to BOTH the launch-side loiter
(L2, the shipped launch-parking compression) and the destination-side loiter (L8). The
arrival solver searches `keepRevs` only within that ~1-10 band; it never keeps dozens of
recorded revolutions just to chase a destination phase.

`keepRevs` as an alignment knob is a real (additive) API change. Today `keepRevs` is a fixed
default and the compressor detects ALL same-body loiter runs with no notion of "the
destination loiter". Using it for alignment requires (a) a per-run "this is the destination
loiter" selector and (b) plumbing a chosen `keepRevs` through the destination-loiter cut.
Both are ADDITIVE to the existing pure compressor, not a rewrite, but they are a real API
change (not the free reuse the alignment knob superficially suggests).

---

## 5. Tolerance modes (reuse the existing Off / Loose / Precise ladder)

The destination is, by construction, a transited (non-launch) body, so it slots into the
existing `TransitedBodyRotationMode {Drop, Loose, Tight}` enum and the
`ScheduleToleranceSecondsFor` dispatch with zero new tolerance vocabulary. The existing
"Landing-body alignment" cycle button (`UI/SettingsWindowUI.cs:348-390`) governs it; only the
tooltip widens to name an interplanetary destination as well as the Mun.

- DestRotation tolerance:
  - Precise (Tight): `T_rot(dest) * (0.25/360)` (`RotationToleranceFraction`). For Duna about
    45.5 s.
  - Loose: `T_rot(dest) * (5.0/360)` (`TransitedBodyLooseRotationDegrees`). For Duna about
    910 s.
  - Off (Drop): the DestRotation constraint is pre-filtered out (as the schedule builder
    already does for transited-body rotation).
- MoonConfig tolerance (Orbital kind, NEVER dropped by Off): `SoiRadius(m) /
  OrbitalVelocity(m)` (`ToleranceSecondsFor`, `MissionPeriodicity.cs:1164-1170`). For Ike
  about 3,400 s.

Off drops only the destination ROTATION term, matching the existing semantics. The launch
pad always stays exact (the launch side is untouched). Mun-tuned thresholds do NOT transfer
to interplanetary cadence (Appendix A): Loose is the realistic default for a loitered
mission and Precise is a rare-window mode that honestly reports amber when the best window
still misses.

```
mode        DestRotation tol             MoonConfig tol         window cadence (intuition)
----------- ---------------------------- ---------------------- ---------------------------
Off (Drop)  constraint removed           SoiRadius/OrbitVel     shortest; rotation ignored
Loose       T_rot*(5/360)  (~910 s Duna) SoiRadius/OrbitVel     medium; default for loitered
Precise     T_rot*(0.25/360)(~45 s Duna) SoiRadius/OrbitVel     rare; amber unless long loiter
```

Launch pad: always exact, never affected by this control.

UI states the player must see (never a silent fall-through):
- a green / amber residual readout (within / over tolerance) per existing same-parent UI,
- a "no aligned window within horizon" fail-closed state (the un-aligned render is in effect
  for that window),
- the period-cell countdown to the next ALIGNED departure window.

---

## 6. Composition and the injection seam

The new step, `DestinationArrivalAlign`, is a pure planner step mirroring `PadAlignLaunch`
(same struct / contract style), wired in `MissionLoopUnitBuilder` immediately after the
`PadAlignLaunch` block. It consumes: the recording's destination surface UT (via
`ScanSurfaceSegmentsWithinWindow`), the re-aim plan (`TargetBody`, `RecordedArrivalUT`,
`CommonAncestor`), `IBodyInfo` periods / tolerances for the destination and its single moon,
and the player's `TransitedBodyRotationMode`. It produces a `DestinationArrivalAlignResult`
(value-only, derived, nothing persisted): the chosen window index k, the chosen destination
keepRevs, `ResidualSeconds`, `WithinTolerance`, `Applied`.

6a. The composition-order trap (resolved). Placing the new step "after `PadAlignLaunch`" is
not enough on its own: the two ends CAN fight. `PadAlignLaunch` re-snaps the departure by up
to half a launch-body sidereal day, which propagates through the recorded tof to a roughly
half-day ARRIVAL shift, which against `T_rot(dest)` about 65,517 s is hundreds of degrees of
destination rotation, destroying the alignment. Resolution: the destination solve runs OVER
THE SAME quantized schedule `PadAlignLaunch` produces, scanning the window index k of the
ALREADY-pad-aligned cadence; it does NOT propose a free departure shift that pad-align then
re-snaps. Concretely: `PadAlignLaunch` fixes the cadence (= synodic) and the per-window
departure map FIRST; `DestinationArrivalAlign` then chooses which of those fixed, pad-aligned
windows (plus the integer loiter re-timer) best matches the destination configuration. The
new step never moves the departure off the pad-aligned grid; it only selects k and keepRevs.
This preserves the resolver's window-index-to-departure 1:1 invariant (`ReaimWindowPlanner`
cadence == synodic) and makes the half-day re-snap a non-issue (there is no re-snap; the
grid is final when the destination solve runs).

6b. Why this does not re-enter dead-end 5 (single joint resonance of all bodies). The launch
(pad-aligned, exact) and the destination (config-aligned, within tolerance) are NOT required
to coincide at one instant. The launch is pixel-perfect on its pad every window, and the
loop-clock arrival hold + pre-landing trim retime the in-SOI replay so that window's DEORBIT
matches the recorded destination rotation phase. We are NOT waiting for a universal
alignment; we align WITHIN each pad-aligned window via the hold + trim. If the hold + trim
cannot bring a window in-band within the horizon, we fail closed (section 9); we do not
chase a global resonance. This is NOT window-skipping against the HARD synodic cadence: the
scan stays on the on-cadence synodic grid (the pad-aligned windows launched every synodic
period) and the hold + trim do the in-SOI alignment. So this selects the on-cadence window
and aligns the RENDER within it; it never lengthens the cadence. (The synthesized
heliocentric tof is NOT a lever here, per section 13 and Appendix A.2: it cannot move the
loop-clock-driven in-SOI replay.)

---

## 7. Phasing (each phase ends with a clean-context review)

`P0 -> P1 -> P2 -> P3 -> P4 -> P5 -> P6` along the critical path. P1 (the pure window solver)
and P2 (the cross-parent constraint extraction) are the math core and land first, behind no
wiring. The loiter-knob API change (P4) and the render-thread validation (P5) are the
riskiest.

### Phase 0 - Grounding probes (gating, no code)
- Confirm against a real s15 recording: `ScanSurfaceSegmentsWithinWindow` returns a
  destination surface-start UT for the Duna landing leg (the DestRotation phase reference
  exists and is non-degenerate).
- Confirm the s15 mission CLASSIFIES Supported in `ReaimClassifier` end-to-end with a
  recorded LANDING (parking -> heliocentric -> Duna arrival -> descent), i.e. the S3
  arrival-leg detection still fires when the mission ends in a surface landing rather than a
  stable orbit (`ReaimClassifier.cs:106-120` requires a target-body OrbitSegment arrival leg;
  verify the captured-orbit-then-deorbit shape supplies one).
- Confirm `ReaimMissionPlan.RecordedArrivalUT` is the SOI-entry UT used as the MoonConfig
  phase reference (`ReaimClassifier.cs:29-33,237-239`).
- Record the live `T_rot(Duna)`, `T_orb(Ike)`, synodic period, and the s15 recorded loiter
  revolution count, to populate the feasibility table (Appendix A) with real numbers.
- Done: probe findings recorded; a real classify-and-extract path for s15 is confirmed before
  any solver work. Review: orchestrator notes (load-bearing assumptions).

### Phase 1 - Arrival-UT window solver (pure)
- New pure helper (home: alongside `MissionPeriodicity` or a new
  `Reaim/DestinationArrivalSolver.cs`): given candidate window indices, recorded phase
  references, live periods, and per-constraint tolerances, return the chosen k +
  `ResidualSeconds` + `WithinTolerance` by reusing `TryFindNextScheduleK` and
  `CircularPhaseError`. The joint solve reads live periods and collapses coincident periods
  (3a); it never multiplies duty cycles.
- Tests (xUnit pure): synthetic body system via the `IBodyInfo` fake.
  - DestRotation-only (0-moon body): first in-band window selected; bounded-best when none;
    residual is an absolute function of k (no drift across kStart shifts).
  - DestRotation + MoonConfig with INDEPENDENT periods: joint worst-residual objective.
  - Tidally-locked-moon collapse: `T_orb(moon) == T_rot(dest)` yields ONE effective
    constraint (the Bug-2 Duna / Ike case); assert the solver does not double-count.
  - Loose vs Precise tolerance bands change the selected k and the window cadence.
  - Off (Drop) removes the DestRotation term; MoonConfig still enforced.
  - Log-assertion test: the solver emits a single summary line (chosen k, residual,
    within-tolerance, mode, effective-constraint count) per the batch-counting convention.
- Done: tests pass; callable but unwired. Deps: Phase 0. Review: full (math).

### Phase 2 - Cross-parent destination constraint SELECTION (pure, additive)

CORRECTED from the original framing during implementation (do NOT regress to it): do NOT modify
`ExtractConstraints` and do NOT flip `Support.UnsupportedCrossParent` to `Supported`. The cross-parent
re-aim path in `MissionLoopUnitBuilder` is entered precisely BECAUSE `phaseLocked == false`, and that
flag is false BECAUSE the extraction reports `UnsupportedCrossParent` (the `if (!phaseLocked)` re-aim
block in `MissionLoopUnitBuilder.cs`; `MissionPeriodicity.Solve` returns the no-lock sentinel only when
`Support != Supported`, and sets `ShouldPhaseLock == true` for ANY `Supported` config). Flipping
`Support` would make the periodicity scheduler phase-lock the mission and SKIP the re-aim transfer
rendering entirely - a regression of a working system. So `Support` stays `UnsupportedCrossParent`.

Instead, a pure ADDITIVE downstream selector (`DestinationConstraintExtractor.ExtractDestinationConstraints`)
picks the arrival-solve constraint set out of the constraints the existing extraction ALREADY emits
(`Rotation(target)` for the landing surface + arrival-orbit hand-off, `MissionPeriodicity.cs:391-414`;
`Orbital(body)` for each SOI entry, `:416-458`):
- DestRotation = the Rotation constraint on the TARGET body.
- MoonConfig = each Orbital constraint on a MOON of the target (a body whose `ReferenceBodyName` is the
  target) the recorded arc enters the SOI of.
- The target's OWN Orbital (its heliocentric SOI-entry) is EXCLUDED: arrival alignment is invariant to
  where the ghost crosses the destination SOI edge, only the destination configuration at entry/landing.
- 2+ constrained moons (Jool-class) fail closed to faithful (the section 8b deferral).
The selector does not mutate the extraction output and never touches `Support` or the periodicity solve.

- Tests (xUnit pure): cross-parent Duna landing -> DestRotation only (target's own orbital excluded);
  + one moon -> DestRotation + MoonConfig(Ike); orbit-only arrival -> no DestRotation (empty unless a
  moon SOI is entered); 0-moon destination (Moho) -> DestRotation only; 2+ moons -> fail closed; a moon
  of a different body (parent != target) is not counted; a duplicate moon SOI is counted once;
  does-not-mutate-input; one summary log line.
- Done: tests pass; selector is additive and unwired (P3 calls it inside the re-aim path). Same-parent
  missions and the existing extraction / `Solve` are untouched by construction. Deps: Phase 1.
  Review: full (regression-focused: confirm no existing path changes).

### Phase 3 - The loop-clock arrival hold (the deorbit-alignment mechanism)

The arrival mechanism is the loop-clock ARRIVAL HOLD + PRE-LANDING TRIM (sections 1, 4),
both acting on the in-SOI replay AFTER SOI entry. This splits into three sub-phases.

Phase 3a - the pure hold helpers. DONE. `GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds`
(the minimal forward W in `[0, T_rot)` that aligns the deorbit rotation phase) and
`GhostPlaybackLogic.ApplyArrivalHoldToPhase` (the effective-span -> compressed-span remap
that INSERTS dead time at the heliocentric->capture boundary, composing with the existing
loiter-cut `DecompressSpanUT`). Both are PURE and (this phase) UNWIRED. xUnit: a zero hold
is the identity; a positive hold holds the phase at the boundary across the hold window and
resumes shifted earlier; the hold composes with loiter cuts; `ComputeArrivalAlignHoldSeconds`
returns the minimal forward W and 0 for a degenerate / NaN rotation period.

Phase 3b - wiring (gated; off => byte-identical). Thread the hold + the pre-landing trim
into `GhostPlaybackLogic.TryComputeSpanLoopUT` (the one shared loop clock both render paths
read, section 12), carried on a new `LoopUnit` arrival-hold/trim field populated by
`MissionLoopUnitBuilder` from the `DestinationArrivalAlign` solve. The
`DestinationArrivalAlign` step mirrors `PadAlignLaunch`, runs over the already-pad-aligned
synodic schedule (6a), and produces `DestinationArrivalAlignResult` (window k + arrival hold
+ pre-landing trim keepRevs + residual + within-tolerance + applied), derived, nothing
persisted. The wiring is gated: with alignment OFF (zero hold, no trim) the loop clock is
BYTE-IDENTICAL to today.
- Tests (xUnit pure + a `MissionLoopUnitBuilder` hookup gate):
  - Zero-hold/no-trim path is byte-identical to the pre-wiring loop clock (the gate
    invariant).
  - The step never moves the departure off the pad-aligned grid (the 1:1 window-index to
    departure invariant holds); the composition-order trap (6a) is covered by a test that a
    pad-align re-snap does not silently shift the chosen window or the hold.
  - Source-text gate (per the `ChainSaveLoadTests` pattern) that the builder calls the new
    step after `PadAlignLaunch` and threads the hold/trim result into the `LoopUnit` field.
  - Log-assertion test: the wired step logs the chosen window, mode, hold seconds, residual,
    and fail-closed (no-aligned-window) outcome.
- Done: cross-parent re-aim missions defer the in-SOI replay by the aligned hold (+ trim);
  same-parent and orbit-only paths unchanged; alignment-off byte-identical. Deps: Phase 2.
  Review: full, extra attention on the composition order and the off => byte-identical gate.

Phase 3c - live-KSP playtest. Validate on the real s15 "Duna One" case that the deorbit
lands on the recorded Duna rotation phase (the ~131-degree offset collapses), that the wait
is under one Duna day, and that both render paths inherit the shift (section 12). Deps:
Phase 3b. Review: playtest report.

### Phase 4 - Pre-landing trim integration (the `keepRevs` API change)
This deepens the EARLIER-direction trim room (section 4) introduced in Phase 3b: make
`ReaimLoiterCompressor` able to (a) identify the DESTINATION loiter / pre-landing run among
all same-body runs and (b) accept a chosen `keepRevs` for that run, plumbed from the
`DestinationArrivalAlign` solve. Additive to the pure compressor; the WHOLE-period cut
contract (section 4) is unchanged. The solver evaluates `A_surface_k` per candidate keepRevs
(integer phase steps) and folds the best keepRevs (the minimal-trim half of the hold + trim
combination) into the objective.
- Tests (xUnit pure):
  - A long low destination loiter supplies enough integer steps to bring some window within
    Loose / Precise; a short / absent loiter does not (the feasibility crossover, asserted
    with real periods from Phase 0).
  - The destination-run selector picks the destination loiter, not a launch-side parking run,
    when both exist.
  - keepRevs preserves the recorded EXIT phase relative to the run end (no continuous slide).
- Done: loiter trim widens achievable windows where a loiter exists. Deps: Phase 3. Review:
  full (real compressor API change).

### Phase 5 - Render-path threading + validation (orbital-arrival first)
- Verify the chosen window's loop shift is the SAME shift the autonomous
  `GhostTrajectoryPolylineRenderer.Driver` body-fixed surface path reads (section 12), so the
  director orbit and the surface polyline evaluate the destination at the same effective UT.
  VALIDATE on ORBITAL ARRIVAL FIRST (far-from-camera SOI seam, accepted by the upstream
  geometric plan's SOI-edge seam tolerance).
- Tests:
  - In-game canary (`RuntimeTests`, FLIGHT): a synthetic Kerbin -> Duna captured-orbit re-aim
    mission, arrival-aligned: assert the chosen window's destination rotation phase matches
    the recorded entry within the active tolerance, and the director orbit + the body-fixed
    surface polyline read the same loop shift.
  - In-game canary: with alignment ON, the steady-state `angleIconVsOrbitEff` for the arrival
    orbit is within tolerance (the ~96.5 / ~131 deg offset collapses), versus the un-aligned
    baseline.
  - Playtest: s15 "Duna One" orbital-arrival case visually aligned; the no-aligned-window
    fail-closed path renders the un-aligned baseline (no garbage), surfaced in UI.
- Done: orbital-arrival alignment proven end to end. Deps: Phase 4. Review: full, extra
  attention (the literal Bug-2 surface).

### Phase 6 - Tolerance UI + on-camera landing acceptance (gated on S4 / C2)
- Widen the existing "Landing-body alignment" Off / Loose / Precise tooltip to name an
  interplanetary destination (no new control). The period cell / TTL / residual readout
  reuses the same-parent green / amber surfacing.
- The on-camera RIGID descent coincidence (L9) is validated only when the separately deferred
  S4 descent re-stitch (`map-ts-render-rewrite-phases.md` Workstream C2, gated on its section
  15.5 seam-tolerance decision) lands. This layer does not REQUIRE S4 to be correct; it makes
  S4's job trivial (the destination is already in the recorded configuration). Until S4, the
  on-camera landing coincidence is a follow-up (this is the Phase A / Phase B split, see the
  Resolved scope decisions).
- Tests: UI label / tooltip; in-game landing-coincidence canary (deferred behind S4).
- Done: UI reflects the destination mode; on-camera landing acceptance recorded as a
  follow-up gated on C2. Deps: Phase 5 + S4 / C2. Review: self-review (UI) + full when the
  C2-gated landing canary lands.

---

## 8. Scope and the explicit deferral boundary

The user asked for "Kerbin SOI to any other SOI in the star system" (more complex transfers
later). That resolves into three tiers: (a) a direct child of the Sun with at most one
constrained moon, captured-then-deorbit (THIS phase, 8a); (b) 2+-moon planets (the "mini star
system", e.g. Jool), explicitly deferred by the user (8b); (c) moons-of-planets and deep /
multi-hop chains (e.g. Ike via Duna), excluded upstream by `ReaimClassifier` (8b). This phase
delivers tier (a).

8a. In scope this phase: a SINGLE cross-parent destination SOI, Kerbin -> X, where X is a
DIRECT child of the common ancestor (the Sun) and has AT MOST ONE constrained moon, and a
CAPTURED-then-deorbit landing (a recorded target-body OrbitSegment arrival leg exists). This
matches the existing `ReaimClassifier` single-hop direct-child scope, so no classifier
extension is needed for the captured-orbit Duna case. DestRotation fires for landings; the
single moon adds one MoonConfig when its phase is visible in the recorded arc. A 0-moon
destination (Eve / Moho / Dres) is the simplest case (DestRotation only).

8b. Deferred (carry forward, do not attempt this phase):
- Jool-class many-moon "mini star system" destinations (2 or more constrained moons). The
  joint duty-cycle product collapses aligned-window frequency toward the centuries-away
  regime; detect (destination has more than one moon whose phase is constrained) and report
  "many-moon destination not yet supported", falling to destination-rotation-only or
  faithful. The cutoff is the COUNT of constrained moons: 0 or 1 in scope, 2 or more deferred.
- Multi-hop / gravity assist and deep chains (Ike via Duna). Already excluded upstream by
  `ReaimClassifier` (single-hop direct-child guard, `ReaimClassifier.cs:13-16,125`), so such a
  mission never reaches `DestinationArrivalAlign`. A recorded landing ON Ike (Ike as the
  DESTINATION) is currently classifier-Unsupported (deep chain) and is deferred with the
  multi-hop case. Ike appears in this phase ONLY as a MoonConfig of a Duna-destination
  mission, never as the destination.
- Direct atmospheric entry with no captured destination orbit segment (8c).
- Steeply-inclined and eccentric-heliocentric destinations: the upstream re-aim plan already
  declines to faithful when the ecliptic-projection encounter check finds no encounter
  (`reaim-interplanetary-transfers.md:491-498`); an eccentric destination weakens the
  congruent-window premise and the uniform-phase-sampling assumption (Appendix A). Flag and
  fail closed; not modeled this phase.

8c. The atmo-direct gap (out of scope this phase). A mission that aerocaptures or plows
straight into the atmosphere on arrival may record NO stable target-body OrbitSegment; its
arrival arc is an atmospheric / surface TrackSection, not an OrbitSegment. `ReaimClassifier`
requires an ArrivalLeg = the first target-body OrbitSegment after the heliocentric coast
(`ReaimClassifier.cs:106-120`), so such a mission classifies Unsupported and never reaches
the re-aim path at all, let alone `DestinationArrivalAlign`. This is arguably the most common
"just land on Duna" shape. This phase scopes it OUT (see Resolved scope decisions): only
captured-then-deorbit interplanetary landings are in scope. Extending the classifier to admit
a no-captured-orbit arrival leg is a separate, larger change (it also lacks the loiter the
residual re-timer needs).

---

## 9. Fail-closed contract

Inherited from re-aim: when no aligned-AND-feasible window exists within the search horizon
(the Lambert solve declines, or the available arrival hold + pre-landing trim cannot reach
the recorded rotation phase), the window degrades to faithful replay (the shipped
return-null-to-faithful path), which is the un-aligned Bug-2 render for that window. The
feature never produces garbage; it either aligns or visibly does nothing. This MUST be
surfaced as a distinct user-visible state (section 5), not a silent fall-through.

---

## 10. Resolved scope decisions

These were settled before build and are LOCKED. They are decisions, not open questions.

1. Atmo-direct landings are OUT OF SCOPE this phase. A mission with no captured destination
   orbit segment (aerocapture or straight-in atmospheric entry) records no target-body
   OrbitSegment, classifies Unsupported in `ReaimClassifier`, and never reaches
   `DestinationArrivalAlign`; it also lacks the destination loiter the re-timer needs. Scope =
   captured-then-deorbit interplanetary landings only. Admitting atmo-direct is a separate,
   larger classifier change carried as a follow-up.

2. Default destination tolerance mode = Loose. Mun-tuned thresholds do not transfer to
   interplanetary cadence. Loose (about 910 s for Duna) is the realistic default for a
   loitered mission. Precise (about 45.5 s) is the explicit rare-window opt-in mode that
   honestly reports amber when the best achievable window still misses.

3. Validate the arrival-UT control on ORBIT-ONLY cross-parent arrival FIRST (Phase A);
   on-camera body-fixed descent coincidence (Phase B) lands when the deferred S4 descent
   re-stitch ships. Phase A = orbit-only arrival alignment, provable now far from camera
   (`ReaimedTrajectory` renders only from `OrbitSegments`; the far-from-camera SOI seam is
   accepted by the upstream geometric plan there). Phase B is gated on S4
   (`map-ts-render-rewrite-phases.md` Workstream
   C2); the body-fixed descent polyline is a separate live-clock renderer and the descent
   replay is deferred upstream.

4. Moon count cutoff = COUNT of constrained moons. 0 or 1 constrained moon is IN scope. 2 or
   more constrained moons (Jool-class) is detect-and-report-not-supported, falling to
   rotation-only or faithful. The 2+ moon joint duty-cycle product collapses the window rate
   toward the dead-end-5 regime, which is why it is deferred; the design detects and reports
   rather than attempting a joint solve at the cutoff.

---

## 11. Integration with the geometric (Lambert capture) seam fix

This layer is strictly ON TOP of and orthogonal to the shipped geometric seam machinery
(`ReaimSegmentAssembler.ReplaceHeliocentricLeg` + the full-recorded-span render window + the
per-window Lambert synthesis in `ReaimPlaybackResolver.BuildWindowSegments`). That machinery
connects POSITIONS in recorded-span time and is invariant to which absolute window k is
chosen; this layer only changes WHICH window k is synthesized for, so it does not modify
`ReplaceHeliocentricLeg`, the render window, or the SOI handoff.

How the transfer leg fails closed (corrected): via SYNTHESIS CONVERGENCE, not a
geometric-residual reject. When the per-window Lambert solve does not converge to a sane
transfer conic across the +-6 percent tof search, `BuildWindowSegments` returns null and that
window degrades to faithful replay (`ReaimPlaybackResolver.cs:205-211`). There is NO 50 km /
5 percent geometric-position reject on the heliocentric transfer leg today, so an
arrival-aligned k that cannot be synthesized produces the un-aligned (Bug-2) faithful render
for that window (which must surface as a distinct state, section 9), never garbage.

Do not conflate `IsSeamResidualTooLarge` with the capture: that guard (> 50 km / 5 percent
radial) lives in `Display/GhostTrajectoryPolylineRenderer.cs` and bounds the conic-anchor of
the body-fixed escape / arrival BURN polylines (`TryAnchorLegToConicSeam`), NOT the
heliocentric capture; `ReplaceHeliocentricLeg` does not call it. It is a separate, orthogonal
guard, unaffected by the window choice k.

Open interaction (carried to residual risks, section 13): the geometric SOI-entry instant
from `CalculatePatch` is not identical to the rotation-aligned arrival; whether a single
chosen window simultaneously yields a convergent, sane synthesized capture geometry AND the
rotation-phase match, or whether they trade off, must be validated on the real s15 case.

---

## 12. Threading the decision into BOTH render paths (the actual Bug-2 surface)

`ReaimedTrajectory` renders only from `OrbitSegments`; its `Points` and `TrackSections` return
EMPTY (`ReaimedTrajectory.cs:15-19,63-64`). The body-fixed descent polyline is drawn by a
SEPARATE autonomous renderer (`Display/GhostTrajectoryPolylineRenderer.Driver`, walking
`RecordingStore.CommittedRecordings` at the LIVE clock). The arrival-UT decision threads into
the OrbitSegment path automatically but does NOT thread into the autonomous surface-polyline
path today. Choosing `A_k` must reach BOTH paths or the two desync exactly as the playtest
showed:

Both paths inherit the alignment AUTOMATICALLY because they share the one loop clock
(`GhostPlaybackLogic.TryComputeSpanLoopUT`): the arrival hold + pre-landing trim are applied
inside that clock (Phase 3b), so any renderer reading the clock sees the deferred in-SOI
replay. There is no per-renderer plumbing of the hold.

- OrbitSegment director path: receives the arrival-aligned schedule (chosen window k, plus
  the arrival hold + pre-landing trim folded into the loop clock) through the EXISTING
  per-window resolver substitution and the shared span clock; automatic once the loop clock
  carries the hold.
- Autonomous body-fixed surface polyline path: renders the recorded surface track at the live
  clock from the committed recording, reading the SAME loop clock. Because the hold + trim
  live in `TryComputeSpanLoopUT`, the surface polyline and the director orbit are evaluated
  at the SAME effective UT for the destination by construction. This must still be VALIDATED
  (orbital-arrival alignment far from camera first; on-camera descent coincidence when
  S4 / C2 lands): a verification gate, not an assumed outcome, but the shared-clock design
  is what makes the coincidence hold rather than ad-hoc per-path plumbing.

---

## 13. Residual risks / remaining open questions

These are carried into playtest. None block the design; all must be checked before declaring
Bug 2 fixed.

- Geometric capture vs rotation phase may fight. The geometric SOI-entry instant from
  `CalculatePatch` is not identical to the rotation-aligned arrival; whether a single chosen
  window simultaneously yields BOTH a convergent, sane synthesized capture geometry (the
  per-window Lambert solve in `BuildWindowSegments`) is one question; whether the loop-clock
  arrival hold + pre-landing trim can drive the deorbit rotation phase in-band is a separate
  one. Because the hold acts AFTER SOI entry and the transfer is upstream of it, they do NOT
  fight (the deorbit-phase alignment never reaches into the capture geometry); confirm this
  decoupling on the real s15 case.
- The body-fixed descent coincidence is the literal Bug-2 surface and must be validated.
  `ReaimedTrajectory` renders only from `OrbitSegments` (empty Points / TrackSections), and
  the body-fixed descent polyline is a separate autonomous renderer; the central claim that
  the two coincide at the aligned window holds because BOTH read the one shared loop clock
  (`TryComputeSpanLoopUT`) into which the hold + trim are applied (section 12). The shared
  clock makes the coincidence structural rather than ad-hoc, but it must still be confirmed in
  playtest.
- Window frequency vs tolerance: this is now bounded by the recorded EARLIER-direction trim
  room, not by a naked grid. The continuous arrival hold reaches any rotation phase within
  each window (it can drive the residual arbitrarily close to zero), so Loose and Precise are
  both reachable per-window even with no loiter; the only limit is how much earlier-direction
  trim is available (whole loiter revolutions, or flexible-region approach arcs) when the
  minimal-wait combination wants a short trim instead of a long hold. The fail-closed path
  (no in-band window) stays the un-aligned Bug-2 render and MUST be distinct and user-visible.
- Using `keepRevs` as a pre-landing-trim knob is a real API change to `ReaimLoiterCompressor`
  (today `keepRevs` is the fixed default and the compressor detects ALL same-body loiter runs
  with no notion of "the destination loiter"); it needs a per-run destination selector and a
  chosen-keepRevs plumb-through. Additive, but not a free reuse.
- Partial-arc trim position-continuity: a partial trim of a non-periodic approach arc jumps
  the proto-vessel along its orbit. This is acceptable ONLY in the flexible SOI-edge region
  (where the design already permits a seam); a partial trim of a segment outside that region
  would teleport the visible proto-vessel and must be rejected. Whole-period trims are always
  clean.
- Eccentric destination heliocentric orbit (Moho-like or modded planets): the synodic
  congruent-window premise and the uniform-phase-sampling assumption weaken; the recorded tof
  at one window may not reproduce the recording-time relative geometry, pushing the required
  tof outside the +-6 percent band and declining the window (the transfer leg fails closed,
  independent of the in-SOI hold). Flag and likely fail closed; not modeled this phase.
- Polar / retrograde landing sites: the design aligns a single scalar rotation phase, which
  barely affects a polar landing site's inertial position (the site is near the spin axis) and
  is rotation-critical at the equator. A polar landing may align trivially while an equatorial
  one needs a larger hold under the same tolerance; the design does not surface this latitude
  dependence, and for an off-equatorial site rotation-phase alignment is necessary but may not
  fully capture the landing-site plane geometry.
- RESOLVED (was "the one true crux"): the tof-as-phase-lever question is closed by code
  verification. `BuildWindowSegments` replaces ONLY the heliocentric leg over the fixed
  recorded span `[RecordedDepartureUT, RecordedArrivalUT]`, and the in-SOI replay is driven by
  the loop clock (`TryComputeSpanLoopUT`) with ZERO dependence on the synthesized tof (the
  `soiEntryUT` is only logged, `ReaimPlaybackResolver.cs:246`). So a tof nudge reshapes the
  transfer arc but CANNOT move the deorbit rotation phase, and is NOT the arrival mechanism.
  The mechanism is the loop-clock arrival hold + pre-landing trim (Appendix A.2); no-loiter
  missions are feasible via the continuous hold, not via the tof band.

---

## 14. Dead-end compliance (all five honored, including under composition)

1. No per-element LAN / Kepler-node rotation: the design chooses a window INDEX (a scheduling
   scalar) and replays the recorded conic verbatim; no orbit element is rotated.
2. Descent stays body-fixed: L9 is hard; the design aligns the inertial orbit projection TO
   the body-fixed descent by choosing WHEN to arrive, never makes the descent inertial.
3. No body-fixed to inertial longitude LIFT: positions come from the recorded arcs replayed
   at the chosen window; only time phases are computed (`CircularPhaseError`).
4. The heliocentric transfer draw window is not extended into the SOI: handoff stays at the
   recorded boundary (full-recorded-span render, unchanged).
5. No single joint resonance of all bodies in one launch instant: the launch (pad-aligned) and
   destination (config-aligned) are decoupled; every pad-aligned window is launched on
   cadence, and the loop-clock arrival hold + pre-landing trim align that window's deorbit
   (fail closed if the hold + trim cannot, 6b). The 2+ moon case that would approach a
   universal resonance is the explicit deferral boundary (section 8b).

---

## 15. What does NOT change

### Regression fence: the launch -> destination-SOI-entry pipeline is shipped and confirmed-good (do NOT regress)

The user confirmed in playtest that the ENTIRE pipeline from launch up to the moment the
ghost ENTERS the destination SOI renders correctly. That stretch is SHIPPED and
confirmed-good and MUST NOT be regressed by this feature. The only thing broken (and the
only thing this feature fixes) is the in-SOI arrival AFTER SOI entry: the ~131-degree
rotation / landing misalignment once the ghost is inside the destination SOI.

The following are SHIPPED and confirmed-good in playtest. They MUST stay byte-identical
when destination alignment is OFF, and their generation machinery is NEVER modified by this
feature:

- recorded ascent and body-fixed landing playback (L1 / L9: meshes + animations exact);
- launch-side loiter playback and its compression (`ReaimLoiterCompressor`, L2);
- the launch-pad rotation lock (`ReaimWindowPlanner.PadAlignLaunch`);
- the escape burn / departure SOI exit (L3);
- the per-window HELIOCENTRIC TRANSFER regeneration
  (`ReaimPlaybackResolver.BuildWindowSegments` /
  `ReaimSegmentAssembler.ReplaceHeliocentricLeg`) and the accepted SOI-edge geometric seam;
- everything up to and INCLUDING destination-SOI ENTRY.

This feature is CONFINED to the destination-side in-SOI arrival temporal / rotation
alignment (what happens AFTER SOI entry). With alignment OFF (or unsupported, or no aligned
window found), the entire launch -> SOI-entry pipeline is byte-identical to today. That is
the fail-closed contract (section 9): the feature either aligns the in-SOI arrival or
visibly does nothing, and either way the upstream pipeline is untouched.

NO UPSTREAM TOUCH POINT: the validated mechanism (the loop-clock arrival hold + pre-landing
trim) acts entirely on the in-SOI replay AFTER SOI entry, so NOTHING upstream of SOI entry is
touched: the launch pad lock, the escape burn, and the heliocentric transfer (including its
time of flight) are left exactly as shipped. The earlier "honest exception" that this feature
could nudge the heliocentric tof as a phase lever is RETIRED: that tof-as-phase-lever is
REFUTED (it reshapes the transfer arc but the in-SOI replay is on the loop clock, independent
of the synthesized tof, so it cannot move the deorbit rotation phase, section 13 /
Appendix A.2). The working heliocentric transfer is preserved with NO change: the feature
does not select, nudge, or otherwise touch the tof.

Implementation guardrail: any P3+ code change is additive and gated such that the launch ->
SOI-entry pipeline code paths are unchanged when alignment is off. A reviewer must verify
this directly: it is the same "null / off => byte-identical" invariant Phases 1-2 already
hold to.

### The rest of What does NOT change

- Same-parent (Mun / Minmus) looped missions: untouched (the existing faithful periodicity
  path).
- The shipped re-aim geometric layer: `ReplaceHeliocentricLeg`, the full-recorded-span render
  window, `PadAlignLaunch` (launch side), and the per-window Lambert synthesis. This layer
  only SELECTS the window k they run for.
- The shipped body-fixed burn-polyline conic-anchor (`TryAnchorLegToConicSeam` /
  `IsSeamResidualTooLarge`): a separate renderer, unaffected by the window choice k.
- The recording format (nothing new persisted; the alignment is fully derived from the
  recording's parking / arrival / surface segments + live bodies + the existing
  `BuildSignature`).
- The playback engine, `IPlaybackTrajectory`, the orbit-segment renderer, the loop span clock.

---

## Appendix A: Feasibility envelope

This appendix is where the quantitative uncertainty lives so the body above reads clean. It
covers how often an acceptable destination arrival configuration actually recurs at each
tolerance (with the period arithmetic), and whether the Lambert solve between a
launch-aligned departure and a destination-aligned arrival has a non-empty solution space.

All numbers below are stock-KSP order-of-magnitude intuition computed from the live periods
the design reads at build time (`IBodyInfo`). They are NOT hardcoded and they shift under
planet packs. Treat them as "what to expect for Kerbin to Duna", not as constants.

### A.1 Window frequency vs tolerance (the period arithmetic)

The candidate arrival instants are NOT continuous. They are the synodic windows the shipped
schedule reaches: window index `k` gives departure `D_k = RecordedDepartureUT + k*synodic`
and an arrival near `D_k + tof`, refined within the bounded tof band
(`ReaimPlaybackResolver.cs:179-204`, about +-6 percent of the recorded tof). For Kerbin to
Duna the synodic period is about 2 Kerbin years (roughly 1.9e7 s) and the tof band is a few
days. So the design samples the destination rotation phase once per window, roughly 2 years
apart.

The destination rotation phase at successive windows steps by `frac(synodic / T_rot(dest))`
of a turn. Because the synodic period and `T_rot(dest)` are mutually incommensurate, that
fractional step is irrational and the sampled rotation phase walks effectively UNIFORMLY
around the circle across windows. So with no knob spent, the probability that a given window
naturally lands the deorbit rotation phase inside the tolerance band is just the duty cycle
of that band:

```
P(in-band per window)  ~=  rotation_tolerance_seconds / T_rot(dest)
```

For Duna (`T_rot ~= 65,517 s`):

```
mode      tol formula             tol (Duna)   duty       naked recurrence (windows)
--------- ---------------------- ------------ ---------- ----------------------------
Precise   T_rot * (0.25/360)      ~45.5 s      ~0.00069   ~1 in 1,440  (centuries)
Loose     T_rot * (5.0/360)       ~910 s       ~0.0139    ~1 in 72     (~140 yr)
Off       constraint dropped      n/a          1.0        every window (rotation ignored)
```

"Naked" means without spending the loop-clock arrival hold or the pre-landing trim. These
are NOT the launch cadence: the launch cadence stays synodic (the HARD REQUIREMENT in
section 1), and the "naked recurrence" column only describes how often a window would align
if NO knob were spent. It shows why the bare grid is not the mechanism, NOT the cadence.
Read plainly: Precise on the bare window grid is effectively never (about 1,440 windows is
centuries of in-game time, which brushes the dead-end-5 regime); Loose on the bare grid is
about 1 in 72 windows, roughly 140 Kerbin years, still impractical as the sole mechanism.
The bare synodic grid alone does NOT solve Bug 2. The validated mechanism makes alignment a
per-window (on-cadence) operation via two loop-clock knobs at the arrival:

- The arrival hold (the dominant knob). The loop-clock dead time inserted at the
  heliocentric->capture boundary defers the in-SOI replay, so the deorbit starts later in
  live time. It is CONTINUOUS over `[0, T_rot)` and is exactly the minimal forward shift that
  zeroes the rotation residual, so it reaches ANY rotation phase within each window,
  regardless of loiter. For Duna any aligning hold is under one Duna day (~18 hours),
  negligible inside the ~2.1-year synodic cycle. Because it acts on the loop clock AFTER SOI
  entry, it never touches the transfer or the launch (no "transfer in front of Kerbin"
  exposure: the transfer is upstream and untouched).
- The pre-landing trim (the earlier-direction room). Removing whole loiter periods (or, in
  the flexible SOI edge, a partial approach arc) steps the deorbit EARLIER by
  `frac(T_loiter / T_rot(dest))` per excised revolution. This lets the alignment pick the
  MINIMAL-disruption combination: a short trim plus a short hold instead of a near-full-turn
  hold. A long, low destination loiter supplies dense earlier-direction room; a short or no
  loiter offers only the flexible-region approach arcs, in which case the continuous hold
  carries the alignment by itself.

Best quantitative intuition: the continuous arrival hold reaches both Loose and Precise on
EVERY window, loiter or no loiter (it is continuous, so the residual goes to zero). The
pre-landing trim only changes HOW the alignment is split (short trim + short hold vs a longer
hold) and how short the wait is; it is not required for in-band alignment. The available
earlier-direction trim (the recorded loiter / approach-arc revolution count) is a
per-mission, per-planet-pack quantity the solver computes and logs from the live periods; do
NOT bake a constant.

Inner-moon note (the Ike collapse). For the actual Bug 2 case the moon constraint is FREE: Ike
is tidally locked, so `T_orb(Ike) == T_rot(Ike)` and both equal Duna's rotation period to
about six significant figures (around 65,517 to 65,518 s). An arrival that matches Duna's
rotation phase auto-matches Ike's orbital phase; the joint solver lets the EXISTING
tidal-collapse path collapse the two coincident periods, the same mechanism
(`MissionPeriodicity` tidal-collapse: relative-tolerance period equality, cs around 535 and
651-655) that already collapses the tidally-locked Mun (`rotationPeriod == orbit.period`) in
the same-parent path. The solver must read the LIVE periods and let that collapse happen,
never assume the two are independent and never multiply duty cycles as if they were.
The general independent-moon case (a fast body with a slow moon) is where a real second
constraint appears, and its duty-cycle product genuinely lowers the window rate; that is the
2-plus-moon boundary the design defers (section 8b).

### A.2 The arrival mechanism is the loop-clock hold + trim (the tof-as-phase-lever is REFUTED)

RESOLVED by code verification. Earlier drafts framed the arrival-alignment crux as "can the
synthesized heliocentric tof be used as a rotation-phase lever within the +-6 percent band?"
That question is closed: the tof is NOT a phase lever, and the arrival mechanism is the
loop-clock arrival hold + pre-landing trim (sections 1, 4; A.1).

The code evidence:

- `ReaimPlaybackResolver.BuildWindowSegments` replaces ONLY the heliocentric leg over the
  FIXED recorded span `[RecordedDepartureUT, RecordedArrivalUT]`. The synthesized tof only
  reshapes that heliocentric ARC.
- The recorded in-SOI arcs (the capture OrbitSegments after `RecordedArrivalUT`) and the
  body-fixed descent replay at the loop-clock UT from `GhostPlaybackLogic.TryComputeSpanLoopUT`
  (`PhaseAnchorUT` + recorded-span offset + cadence + loiter cuts), with ZERO dependence on the
  synthesized tof. The synthesizer's `soiEntryUT` is only LOGGED
  (`ReaimPlaybackResolver.cs:246`), never used to re-time anything.

So a tof nudge reshapes the transfer arc but does NOT move the loop-clock-driven in-SOI
replay; it cannot change the destination rotation phase at the deorbit and cannot fix the
~131-degree offset. The arrival is aligned instead by the loop-clock ARRIVAL HOLD (continuous
over `[0, T_rot)`, the minimal forward shift that zeroes the rotation residual) plus the
PRE-LANDING TRIM (earlier-direction room), both acting AFTER SOI entry. The continuous hold
reaches any rotation phase per window, so no-loiter missions ARE feasible (the Duna One case),
not limited to the bare grid.

The transfer leg's own feasibility (a separate, still-real concern). The launch pad is pinned
exactly every window (`PadAlignLaunch`), and the per-window synthesizer must still converge a
single-rev Lambert arc: the departure is FIXED at `D_k` (searching the departure regressed,
"transfer in front of Kerbin", `ReaimPlaybackResolver.cs:165-178`) and step 0 (recorded tof)
converges for almost every congruent window because `D_k` is congruent to the recorded
departure by construction. The thin +-6 percent tof band is the 180-degree degeneracy DODGE
and stays exactly that: the arrival mechanism does NOT use it to move the arrival. When the
synthesizer cannot converge a sane transfer for a window, `BuildWindowSegments` returns null
and that window falls to faithful (independent of the in-SOI hold). The arrival solve never
proposes a tof outside the band or a departure shift off `D_k`.

Eccentric-destination caveat. For a destination on an eccentric heliocentric orbit (Moho, or
modded planets), the congruent-window premise weakens: the recorded tof at window `k` may not
reproduce the recording-time relative geometry, so the required tof can fall outside the +-6
percent band and the synthesizer declines the window (the transfer leg fails closed,
independent of the in-SOI hold). This phase does not model eccentric destinations; such a case
should be detected and fail closed (faithful replay for that window) rather than silently
mis-aim. Flag it; do not attempt it here.
