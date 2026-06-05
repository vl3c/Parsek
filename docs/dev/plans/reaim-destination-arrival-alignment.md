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

### The two arrival flex points

Inside the destination SOI there are exactly TWO distinct alignment knobs, reasoned about
separately:

- Arrival flex point #1: destination SOI-ENTRY timing. Choose WHEN the ghost enters the
  destination SOI so the internal system configuration (the destination's rotation phase
  and any inner moon's orbital phase) matches the recorded entry, so moon approaches render
  like the original (the same flexibility Kerbin -> Mun uses). Realized through L4 / L6
  timing slack, spent within the window via the tof band around the recorded departure.
- Arrival flex point #2: destination LOITER-orbit count. When a destination loiter was
  recorded and the mission is not going directly to landing / atmospheric entry, choose the
  number of destination loiter orbits to align EXACTLY with the recorded landing-trajectory
  end. This is L8, the integer-period re-timer (section 4).

They are NOT fully independent: when a mission records NO destination loiter (the s15
"Duna One" case below), the in-SOI sequence is rigid (capture-then-land within one
destination day), nothing remains for #2 to trim, and the SOI-entry timing (#1) ALONE also
aligns the landing.

### The cadence-preserving multi-knob model

Every FLEXIBLE link is spent JOINTLY per synodic window, NOT collapsed to a single
grid-selected scalar. Per window k (the coarse cadence knob, fixed at the synodic period
and never lengthened):

- Coarse (selects the window, preserves cadence): the synodic window index k. Window k has
  departure `D_k = RecordedDepartureUT + k * synodic` and arrival near `D_k + tof`. Every k
  is a launch opportunity; we do not skip k values to find a naturally aligned one.
- Fine arrival lever (arrival flex point #1): the tof band around the recorded departure.
  The departure stays pinned at `D_k`; only the target end moves, sliding the arrival
  instant and thus the destination rotation phase. A TINY nudge (a fraction of a percent of
  tof, far inside the band) sweeps a full destination rotation.
- Launch-side trim: the L2 launch-parking compression (already ships).
- Destination-side trim (arrival flex point #2): the L8 destination loiter count, a coarse
  integer re-timer, used only when a loiter was recorded.

Given k and these knobs, L0 / L4 / L6 follow (`A_k` minus the regenerated transfer time,
pad-quantized by the EXISTING `PadAlignLaunch`); L5 is regenerated, not chosen.

### Worked example: Duna One (s15), the tof band is the active arrival knob

Verified from `logs/2026-06-03_1951_duna-arrival/KSP.log` (ReaimDiag dump) and the s15
recording `61e9177...prec`:

- LAUNCH side: a Kerbin parking loiter of ~132 days, already compressed by the shipped
  single launch-side loiter cut (`cut#0 start=52570174 len=11393869`, the L2 trim).
- Heliocentric transfer: `tof = 6,854,613 s` (about 79.3 days). Kerbin -> Duna synodic =
  `19,653,076 s` (about 2.1 Kerbin years), the on-cadence launch interval.
- INSIDE the Duna SOI: arrival `70,898,646` to last orbit segment end `70,963,653` is about
  `65,007 s` (~18.1 hours, about 0.99 of one Duna rotation): an arrival hyperbola
  (negative-sma capture), then a few SUB-1-revolution low orbits (revs 0.45 / 0.32 / 0.02 /
  0.09, about 0.88 rev total), then the descent. So Duna One records NO destination parking
  loiter (captures and lands within ~1 Duna day). It DOES record a brief Ike SOI transit
  (two short hyperbolic Ike orbit segments, ~37 minutes total, about 16 hours after Duna
  arrival), but that transit imposes NO INDEPENDENT constraint: Ike is tidally locked to
  Duna, so its orbital phase collapses onto Duna's rotation phase (the tidal-collapse note,
  section 3a). Aligning Duna's rotation auto-aligns Ike, so there is one effective
  destination constraint, not two.

Implication: knob #2 has nothing to trim and there is no moon to align (the brief Ike
transit collapses onto Duna's rotation, section 3a), so the ONLY within-window alignment
knob is the thin tof band used as a small phase lever on the SOI-entry timing (#1). The
arithmetic is favorable: tof is ~79.3 days; the +-6 percent band is +-4.76 days; one Duna
rotation is `65,518 s` = 0.758 days. So a tof nudge of less than 0.5 percent of tof (about
+-0.38 days, well inside the band, which has roughly 12x that range) would sweep a full
Duna rotation phase, and because the in-SOI sequence is rigid (no loiter) aligning the
entry rotation phase would ALSO align the landing.

But this tof-as-phase-lever is NOT yet validated: it is the open feasibility crux of
section 13 (the one true crux) and Appendix A.2. v1 keeps step 0 (the recorded tof) primary
and uses the band ONLY as a 180-degree degeneracy dodge, NOT as a deliberate phase lever.
The honest consequence: for Duna One the Bug-2 fix HINGES on validating the
tof-as-phase-lever. IF it validates (a sub-half-percent nudge, far inside the +-6 percent
band, kept geometrically faithful), it would align every synodic window and preserve the
~2.1 Kerbin-year cadence. Until and unless it validates, Duna One reaches alignment only on
naturally favorable windows or the amber bounded-best, and otherwise FAILS CLOSED to
faithful (the un-aligned arrival shown above). The synodic launch cadence is preserved
EITHER WAY: the cadence guarantee never depends on the tof lever, only the ALIGNMENT of
Duna One does.

### The honest qualifier (quantized, but spent within the window)

`A_k` is NOT a continuous knob; the arrival is QUANTIZED (synodic window k + a thin tof
band + integer loiter steps). Confirmed in source: `ReaimPlaybackResolver.BuildWindowSegments`
(`ReaimPlaybackResolver.cs:139-211`) pins the heliocentric departure at the nominal
`D_k = RecordedDepartureUT + k * synodic` and searches ONLY the time-of-flight within
about +-6 percent (`TofSearchStepFraction = 0.005`, `SearchMaxSteps = 12`). The tof search
exists primarily as a 180-degree single-rev Lambert degeneracy dodge (step 0 = recorded tof
converges for almost every window). The code carries an explicit comment that searching the
DEPARTURE to move the arrival was tried and reverted (the "transfer hung in front of
Kerbin" regression, `ReaimPlaybackResolver.cs:165-178`).

These honest facts are respected, but they do NOT imply the cadence-degrading conclusion
that the feature selects a rare naturally aligned window. The arrival is quantized, yet the
knobs are spent JOINTLY WITHIN each synodic window to align that window; the launch cadence
stays synodic. The crucial nuance, distinct from the reverted DEPARTURE search: a TINY tof
nudge (far inside the +-6 percent conditioning bound) is a safe arrival PHASE LEVER, because
it moves only the target end while the launch endpoint stays glued to `D_k`. That is what
lets a no-loiter mission like Duna One align every window from the tof band alone. So the
design respects: the arrival is window-quantized plus bounded knobs, spent jointly per
window, with a BOUNDED feasibility envelope (Appendix A) and fail-closed-to-faithful per
window, not a freely dialed scalar and not a rare grid-selected window.

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
  T_rot(dest))`, where `A_surface_k` is the effective destination surface-arrival UT for
  window k AFTER the integer loiter re-timing (section 4).
- MoonConfig(m) residual = `CircularPhaseError(A_entry_k - RecordedArrivalUT, T_orb(m))`,
  where `A_entry_k` is the SOI-entry UT for window k.

Use `TryFindNextScheduleK` (`MissionPeriodicity.cs:901-954`) verbatim: scan k, accept the
first window where every residual is within its tolerance, else the bounded-best (minimum
worst residual) window, never accumulating drift (the residual at k is an absolute function
of k, so the offsets cancel). Objective `worst(k) = max(DestRotation residual,
max_m MoonConfig residual)`. Report `ResidualSeconds` and `WithinTolerance` exactly as the
same-parent path does (green / amber).

This scan-k is the v1 selection WITHIN the on-cadence synodic grid (it chooses among the
pad-aligned windows launched every synodic period, plus the integer loiter re-timer); it
never skips a launch. A window it cannot bring in-tolerance fails closed to faithful (still
launched on cadence), and the tof-as-phase-lever (Appendix A.2) is the deferred extension
that would let MORE windows align within cadence. So "scan k" selects the RENDER alignment
per on-cadence window, not the launch cadence.

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

## 4. The loiter-trim role: a coarse re-timer, not a fine absorber

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
  atmospheric entry), the loiter trim contributes little or nothing, and the alignment must
  be hit on the coarse window grid + tof band alone (Appendix A).

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
to coincide at one instant. The regenerated transfer absorbs the difference: for each
candidate window the launch is already pixel-perfect on its pad, and we are free to pick the
window whose ARRIVAL matches the destination. We are NOT waiting for a universal alignment;
we are choosing, among the infinitely many pad-aligned windows, one whose destination
configuration is in band. If no in-band window exists in the horizon, we fail closed
(section 9); we do not chase a global resonance. "Pick the window whose arrival matches" is
NOT window-skipping against the HARD synodic cadence: the scan stays on the on-cadence
synodic grid (the pad-aligned windows launched every synodic period) plus the integer
loiter re-timer, and a window it cannot bring in-band fails closed to faithful (still
launched on cadence). So this selects the RENDER alignment per on-cadence window, not the
launch cadence; the tof-as-phase-lever (Appendix A.2) is the deferred extension that would
let MORE windows align within that same cadence.

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

### Phase 3 - Re-aim injection seam (`DestinationArrivalAlign` planner step)
- New pure `DestinationArrivalAlign` step mirroring `PadAlignLaunch`, wired in
  `MissionLoopUnitBuilder` immediately AFTER `PadAlignLaunch`, running over the
  already-pad-aligned synodic schedule (6a). Produces `DestinationArrivalAlignResult`
  (window k + keepRevs + residual + within-tolerance + applied), derived, nothing persisted.
  Attaches the chosen window to the re-aim descriptor so the per-window resolver substitution
  synthesizes for k.
- Tests (xUnit pure + a `MissionLoopUnitBuilder` hookup gate):
  - The step never moves the departure off the pad-aligned grid (the 1:1 window-index to
    departure invariant holds); composition-order trap (6a) is covered by a test that a
    pad-align re-snap does not silently shift the chosen window.
  - Source-text gate (per the `ChainSaveLoadTests` pattern) that the builder calls the new
    step after `PadAlignLaunch` and threads its result into the descriptor.
  - Log-assertion test: the wired step logs the chosen window, mode, residual, and
    fail-closed (no-aligned-window) outcome.
- Done: cross-parent re-aim missions schedule for the aligned window; same-parent and
  orbit-only paths unchanged. Deps: Phase 2. Review: full, extra attention on the
  composition order.

### Phase 4 - Loiter-trim integration (the `keepRevs` API change)
- Make `ReaimLoiterCompressor` able to (a) identify the DESTINATION loiter run among all
  same-body runs and (b) accept a chosen `keepRevs` for that run, plumbed from the
  `DestinationArrivalAlign` solve. Additive to the pure compressor; the WHOLE-period cut
  contract (section 4) is unchanged. The solver evaluates `A_surface_k` per candidate keepRevs
  (integer phase steps) and folds the best keepRevs into the objective.
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
(the Lambert solve declines, the tof band cannot reach the phase, or there is no loiter to
re-time), the window degrades to faithful replay (the shipped return-null-to-faithful path),
which is the un-aligned Bug-2 render for that window. The feature never produces garbage; it
either aligns or visibly does nothing. This MUST be surfaced as a distinct user-visible state
(section 5), not a silent fall-through.

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

- OrbitSegment director path: receives the arrival-aligned schedule (chosen window k and
  loiter re-timing) through the EXISTING per-window resolver substitution; automatic once the
  schedule is rewritten.
- Autonomous body-fixed surface polyline path: renders the recorded surface track at the live
  clock from the committed recording. The chosen window k determines the loop shift the
  polyline renderer reads. For the descent to land where the inertial orbit projects, the
  surface polyline and the director orbit must be evaluated at the SAME effective UT for the
  destination. Design requirement: the arrival-aligned window's loop shift MUST be the same
  shift the autonomous polyline uses (it already reads the recording loop / shift via the
  shared span clock), AND the coincidence must be VALIDATED (orbital-arrival alignment far
  from camera first; on-camera descent coincidence when S4 / C2 lands). This is a
  verification gate, not an assumed outcome.

---

## 13. Residual risks / remaining open questions

These are carried into playtest. None block the design; all must be checked before declaring
Bug 2 fixed.

- Geometric capture vs rotation phase may fight. The geometric SOI-entry instant from
  `CalculatePatch` is not identical to the rotation-aligned arrival; whether a single chosen
  window simultaneously yields BOTH a convergent, sane synthesized capture geometry (the
  per-window Lambert solve in `BuildWindowSegments`) AND the rotation-phase match, or whether
  they trade off, is unproven and must be validated on the real s15 case. If they fight, the
  loiter re-timer + tof band must jointly absorb both, which may not be possible for every
  window.
- The body-fixed descent coincidence is the literal Bug-2 surface and is UNVERIFIED.
  `ReaimedTrajectory` renders only from `OrbitSegments` (empty Points / TrackSections), so the
  central claim that the body-fixed descent coincides with the inertial orbit at the aligned
  window holds ONLY if the polyline path is confirmed to read the same loop shift the director
  uses. That threading is a design requirement, not an automatic outcome.
- Window frequency vs tolerance is a genuine usability risk: Precise (about 45.5 s for Duna)
  is roughly 1 in 1,440 synodic windows naked and only practical with a long low destination
  loiter. A short-loiter or no-loiter mission may have NO realistically-soon Precise window,
  and even Loose can be sparse without a loiter. The fail-closed path is the un-aligned Bug-2
  render, which is why the no-aligned-window state MUST be distinct and user-visible.
- The loiter re-timer requires a long, low destination loiter to supply enough integer phase
  steps; for short-loiter or no-loiter (atmo-direct) missions the only knobs are the coarse
  synodic grid + the conditioning-bounded tof band (a 180-degree-degeneracy dodge, not a free
  slider; reverting to a wide departure search re-opens the documented "transfer in front of
  Kerbin" regression). Some missions will only ever reach amber bounded-best.
- Using `keepRevs` as an alignment knob is a real API change to `ReaimLoiterCompressor` (today
  `keepRevs` is the fixed default and the compressor detects ALL same-body loiter runs with no
  notion of "the destination loiter"); it needs a per-run destination selector and a
  chosen-keepRevs plumb-through. Additive, but not a free reuse.
- Eccentric destination heliocentric orbit (Moho-like or modded planets): the synodic
  congruent-window premise and the uniform-phase-sampling assumption weaken; the recorded tof
  at one window may not reproduce the recording-time relative geometry, pushing the required
  tof outside the +-6 percent band and declining the window. Flag and likely fail closed; not
  modeled this phase.
- Polar / retrograde landing sites: the design aligns a single scalar rotation phase, which
  barely affects a polar landing site's inertial position (the site is near the spin axis) and
  is rotation-critical at the equator. A polar landing may align trivially while an equatorial
  one is rare under the same tolerance; the design does not surface this latitude dependence,
  and for an off-equatorial site rotation-phase alignment is necessary but may not fully
  capture the landing-site plane geometry.
- Open feasibility question (the one true crux): can the tof band be used deliberately as a
  rotation-phase lever (Appendix A.2) without re-opening the "transfer in front of Kerbin"
  regression and while keeping the arrival geometry inside the seam guard? It gates whether
  no-loiter missions are feasible at all. Recommendation: keep step 0 (recorded tof) primary,
  treat the band as a degeneracy dodge only for v1, and revisit using it as a phase lever
  after orbit-only validation shows whether it is needed.

---

## 13a. Discovered defect: the arrival hold is loop-independent (per-loop rotation drift)

Status: ROOT CAUSE CONFIRMED (code + math + log), FIX DESIGNED, NOT IMPLEMENTED. This is the
next implementation step for Bug 2.

Context: this section documents a defect in the loop-clock ARRIVAL HOLD, the deorbit-alignment
mechanism that replaces the refuted tof-as-phase-lever for the no-loiter Duna One case. The
hold is the inverse of a loiter cut: it inserts dead time at the heliocentric-to-capture
boundary so the destination's rotation phase at the deorbit recurs to its recorded value. It is
computed by `ArrivalHoldPlanner.ComputeArrivalHold` (`Source/Parsek/Reaim/ArrivalHoldPlanner.cs`)
and applied through the shared loop clock `GhostPlaybackLogic.TryComputeSpanLoopUT` via
`GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` and `GhostPlaybackLogic.ApplyArrivalHoldToPhase`.
The defect: the hold value is computed once against a FIXED anchor and is therefore CONSTANT
across every replayed loop, so the alignment it buys holds on only one reference loop and drifts
away on all the others.

### Root cause (three independent confirmations)

- Code. `ArrivalHoldPlanner.ComputeArrivalHold` computes
  `liveEntryUT = phaseAnchorUT + (CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT)`
  then `w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, T_rot)`.
  It uses the FIXED `phaseAnchorUT` and carries no playback-loop-index (`cycleIndex`) term, so the
  hold value W is the same on every replayed loop (measured 46450.59 s for Duna One, save s15).
  `TryComputeSpanLoopUT` does compute the loop index as `cycleIndex` internally, but the hold it
  receives (`arrivalHoldSeconds` / `arrivalHoldAtUT`) is the same scalar on every cycle, so the
  per-loop rotation phase is never re-aligned.
- Math. The synodic launch cadence is 19,653,075.77 s = 299.9652 destination rotations (Duna
  `T_rot` = 65,517.86 s), which is NOT a whole number. The 0.0348-rotation shortfall means the
  deorbit lands about 12.5 degrees further around the destination's spin on each successive loop.
  A loop-independent hold cannot cancel a per-loop-varying offset, so the offset accumulates.
- Log. The seam diagnostic `EmitOneSidedBracketDiagnostic` in
  `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` (the `overshootGap` / `lonBodyFixed` /
  `lonOrbitSeam` fields it logs for the one-sided descent leg) reported `overshootGap` = 17 km
  (`lonBodyFixed` = 84.5, `lonOrbitSeam` ~ 82) on one viewing and 224 km (`lonBodyFixed` = 45.8,
  `lonOrbitSeam` ~ 80) about three loops later. The 38.7 degree shift in `lonBodyFixed` equals
  3.09 loops times 12.5 degrees per loop, an exact match. `lonOrbitSeam` (the inertial conic)
  stayed put while the body-fixed track rotated away under it.

### Consequence (the user-visible symptom)

The inertial proto orbit conic (the recorded parking orbit) and the body-fixed deorbit/descent
polyline coincide only on the one reference loop and progressively separate on later loops, from
about 0 km (aligned) toward half the destination circumference. That growing separation is what
the user sees as "two straight-ish lines instead of an arc" connecting the proto orbit to the
landing polyline: this IS the Bug 2 proto-to-descent disconnect. The GEOGRAPHIC landing site
stays correct on every loop (it is body-fixed lat/lon painted on the rotating planet); only the
inertial-conic-to-body-fixed JOIN drifts. This also rules out the two earlier suspects: it is
NOT sparse-sample chording (sub-pixel here) and NOT `TryAnchorLegToConicSeam` (the descent leg
is correctly one-sided and left body-fixed, which is why it surfaces through the one-sided
diagnostic at all).

### The designed fix (the dynamic / per-loop hold)

Make the hold a function of the playback loop index N:

```
W_N = (W_0 - N * (cadence mod T_rot)) mod T_rot
```

where `cadence` is the synodic window period, `T_rot` is the destination rotation period, and
`W_0` is the current reference hold (today's constant value). This re-aligns the destination
rotation at the deorbit on EVERY loop, so the inertial conic flows into the body-fixed descent
as one connected arc each loop instead of only on the reference loop.

Why it is free and cadence-preserving: the hold is dead time at the heliocentric-to-capture
boundary that elapses under high time warp, so varying it from 0 to one rotation per loop costs
no visible time and does NOT move the synodic launch schedule (launches still fire every window).
`W_N` stays within `[0, T_rot)`, and the mission still fits the window: `compressedSpan + W_N`
maxes at `compressedSpan + T_rot`, far below one cadence (the same bound the existing
`compressedSpan + hold > cycleDuration` clamp in `TryComputeSpanLoopUT` already enforces).

Implementation seam: the hold is applied through the shared loop clock
`GhostPlaybackLogic.TryComputeSpanLoopUT` (and `GhostPlaybackLogic.ApplyArrivalHoldToPhase`),
which already computes the loop index `cycleIndex` internally. The per-loop adjustment belongs
there: carry `W_0`, `cadence`, and `T_rot` into the loop unit, and compute `W_N` for the current
`cycleIndex` before applying the hold. Because BOTH render paths (the OrbitSegment director and
the autonomous body-fixed polyline, section 12) read this one clock, both inherit the per-loop
hold automatically, the same shared-clock invariant the polyline-warp work (PR #1030) relied on.

Regression-fence compliance (section 15): when alignment is Off (Drop mode) `W_0` = 0, so every
`W_N` = 0 and the clock is byte-identical to today; the launch-to-SOI-entry pipeline is untouched
(the hold acts only AFTER SOI entry); a zero hold stays byte-identical (the existing
`ApplyArrivalHoldToPhase` identity for `holdSeconds <= 0`).

Residual caveat (honest): even the aligned reference loop showed a roughly 17 km / 3 degree
residual. The per-loop fix removes the 12.5-degrees-per-loop drift but NOT this smaller secondary
offset, whose likely source is the OrbitSegment conic-fit at the boundary (the fitted
parking-orbit conic need not pass exactly through the recorded body-fixed deorbit point) or hold
quantization. To be confirmed separately; cosmetically minor next to the 0-to-180-degree
per-loop drift.

Out of scope / still separate: retiring the transfer proto that keeps orbiting past the deorbit
(the proto-overshoot), and the Ike orbital-phase alignment lever. Those remain separate, and the
Ike lever stays deferred (sections 3a, 8b).

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
   destination (config-aligned) are decoupled; we choose among pad-aligned windows the one
   whose arrival matches the destination, and fail closed if none exists in horizon (6b). The
   2+ moon case that would approach a universal resonance is the explicit deferral boundary
   (section 8b).

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

THE ONE HONEST EXCEPTION: the only upstream parameter this feature can affect is the
heliocentric transfer's TIME OF FLIGHT, and only via the tof SELECTION inside
`ReaimPlaybackResolver`'s EXISTING +-6 percent search band (the tof-as-phase-lever, section
13 / Appendix A.2), and only when alignment is engaged. It does NOT modify the
transfer-generation machinery. Alignment-off selects the recorded tof (today's exact
behavior); when engaged the nudge is sub-0.5 percent of tof (visually faithful: the
transfer still renders as the same clean regenerated arc); and it FAILS CLOSED to the
recorded tof if it cannot stay geometrically faithful. So the working heliocentric transfer
is preserved: same machinery, recorded tof by default, at most a sub-0.5 percent in-band tof
selection when alignment is on, fail-closed otherwise. This is the one sanctioned upstream
touch point and it stays gated behind alignment-engaged + fail-closed; it is the open
feasibility crux of section 13 and Appendix A.2, not a free dial.

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
around the circle across windows. So with no tof or loiter help, the probability that a given
window lands the rotation phase inside the tolerance band is just the duty cycle of that
band:

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

"Naked" means without using the tof band or the loiter re-timer. These are NOT the launch
cadence: the launch cadence stays synodic (the HARD REQUIREMENT in section 1), and the
"naked recurrence" column only describes how often a window would align if NO knob were
spent. It is stated to show why the bare grid is not the mechanism, NOT to set the cadence.
Read plainly: Precise on the bare window grid is effectively never (about 1,440 windows is
centuries of in-game time, which brushes the dead-end-5 regime). Loose on the bare grid is
about 1 window in 72, roughly 140 Kerbin years, still impractical as the sole mechanism. The
bare synodic grid alone does NOT solve Bug 2 for any realistic patience. Two amplifiers
change this, and they are what make alignment a per-window (on-cadence) operation:

- The tof band. A +-6 percent tof slide moves the arrival UT by roughly +-6 percent of the
  recorded transfer time (a handful of days for Kerbin to Duna). Against `T_rot(dest)` about
  18 hours (Duna day) that is several full Duna rotations of reach, i.e. the tof band alone
  can sweep the rotation phase across the whole circle. The catch: the tof band is a
  180-degree-degeneracy DODGE, not a free arrival slider. The code pins the departure at `D_k`
  precisely because searching the departure desynced the transfer and produced the "transfer
  in front of Kerbin" regression (`ReaimPlaybackResolver.cs:165-178`). Step 0 (the recorded
  tof) converges for almost every window; the search only nudges the rare exactly-180 window.
  Using the tof band as a deliberate rotation-phase lever (rather than a degeneracy escape) is
  a NEW use of an existing knob and must be validated to not re-open that regression. This is
  the single most important feasibility question in this appendix (see A.2).
- The loiter re-timer. For a mission with a long, low destination loiter, excising whole
  loiter periods steps the surface-arrival phase by `frac(T_loiter / T_rot(dest))` per excised
  revolution. A low Duna orbit has `T_loiter` of a few thousand seconds, a small fraction of a
  Duna day, so each step is fine-grained and a run of dozens of revolutions gives dozens of
  phase samples WITHIN a single window. A mission that records a 100-orbit parking loiter
  therefore has enough integer steps that some `keepRevs` lands Loose, and possibly Precise,
  inside one window. A mission with a 1-to-3-rev loiter, or a direct atmospheric entry with no
  loiter at all, gets no help here and falls back to the grid + tof band.

Best quantitative intuition: with a substantial recorded destination loiter, Loose windows are
routinely reachable (the loiter steps cover the about 1.4 percent band densely) and Precise is
reachable on most windows; without a destination loiter, the feature depends entirely on the
tof band being usable as a phase lever, and if that is judged too risky to push, the honest
answer is that short-loiter and no-loiter missions reach only amber bounded-best. The
crossover (minimum recorded-loiter revolution count for a per-window Loose or Precise hit) is
a per-mission, per-planet-pack quantity the solver computes and logs from the live periods; do
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

### A.2 Feasibility of the Lambert solve (launch-aligned departure to destination-aligned arrival)

The decoupling premise is: the launch pad is pinned exactly every window (`PadAlignLaunch`,
whole sidereal-day snap), and the regenerated transfer absorbs the difference so we are free
to pick a window whose ARRIVAL matches the destination. The honest question is whether the
transfer solve actually has a non-empty, well-conditioned solution for the arrival the
destination alignment wants.

What bounds the solution space:

- The departure is FIXED at `D_k` (not searched). Confirmed in source: searching the
  departure regressed ("transfer in front of Kerbin"). So the ONLY free transfer variable is
  the time of flight, inside the +-6 percent band.
- Within that band the synthesizer must converge a single-rev Lambert arc on the recorded
  plane that intercepts the target. Step 0 (recorded tof) converges for almost every congruent
  window because `D_k` is congruent to the recorded departure by construction (same relative
  geometry rotated to where the bodies are at `D_k`). Off-nominal tof steps converge less
  reliably the further they push from the recorded geometry; the band exists to dodge the
  180-degree degeneracy, not to reshape the transfer.

So the solution space is non-empty but NARROW: it is the recorded transfer shape, rigidly
congruent per window, with a thin tof collar. The arrival UT is therefore selectable across
windows (coarse, about 2 years apart) and, within a window, only across the tof collar (about
+-6 percent) plus the integer loiter re-timer. It is NOT a continuous arrival slider. The
design must respect this: the arrival solve chooses a window index `k` and a destination
`keepRevs`, and at most nudges the tof inside its existing band. It must never propose a
transfer that demands a tof outside the band or a departure shift off `D_k`, because the
synthesizer will decline (return null, fall to faithful) or, worse, re-open the desync
regression.

The open feasibility bound: can the tof band be used deliberately as a rotation-phase lever
without re-opening the "transfer in front of Kerbin" regression? The regression came from
moving the DEPARTURE; the tof search moves only the TARGET end while the departure stays glued
to `D_k`. In principle a tof nudge slides the arrival instant (and thus the destination
rotation phase) while keeping the launch endpoint pinned, which is exactly what we want. But
the band is +-6 percent and was sized for the degeneracy dodge; whether it is wide enough to
reach an in-band rotation phase for a no-loiter mission, and whether pushing it to its edges
keeps the per-window Lambert synthesis convergent and well-conditioned (`BuildWindowSegments`
returns null and falls to faithful otherwise), is unproven and must be measured on the real
s15 case before relying on it. If the tof band turns out to be too narrow
or too ill-conditioned at its edges to serve as a phase lever, then no-loiter missions are
honestly limited to the bare synodic grid (A.1) and reach only amber bounded-best. Widening
the band is NOT a free change: it re-enters the conditioning territory the +-6 percent limit
was chosen to avoid.

Eccentric-destination caveat. For a destination on an eccentric heliocentric orbit (Moho, or
modded planets), the congruent-window premise weakens: the recorded tof at window `k` may not
reproduce the recording-time relative geometry, so the required tof can fall outside the +-6
percent band and the synthesizer declines the window. This phase does not model eccentric
destinations; such a case should be detected and fail closed (faithful replay for that window)
rather than silently mis-aim. Flag it; do not attempt it here.
