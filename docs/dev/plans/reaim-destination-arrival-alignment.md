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

Every FLEXIBLE link reduces to a single SELECTED scalar: the arrival window index k
(target arrival UT `A_k`). Given k, L0 / L4 / L6 are `A_k` minus the regenerated transfer
time, pad-quantized by the EXISTING `PadAlignLaunch`; L5 is regenerated, not chosen; L2
already ships; L8 is a COARSE integer-period re-timer (section 4).

The honest qualifier: `A_k` is selectable only on the SYNODIC window grid (about 2 Kerbin
years for Kerbin to Duna) plus a thin time-of-flight band plus integer loiter steps. It is
NOT a continuous knob. Confirmed in source: `ReaimPlaybackResolver.BuildWindowSegments`
(`ReaimPlaybackResolver.cs:139-211`) pins the heliocentric departure at the nominal
`D_k = RecordedDepartureUT + k * synodic` and searches ONLY the time-of-flight within
about +-6 percent (`TofSearchStepFraction = 0.005`, `SearchMaxSteps = 12`). The tof search
is a 180-degree single-rev Lambert degeneracy dodge (step 0 = recorded tof converges for
almost every window), NOT an arrival slider. The code carries an explicit comment that
searching the DEPARTURE to move the arrival was tried and reverted (the "transfer hung in
front of Kerbin" regression, `ReaimPlaybackResolver.cs:165-178`). So the design respects: A
is a SELECTED window index plus bounded knobs, with a BOUNDED feasibility envelope, not a
freely dialed scalar.

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

3a. The live joint resonance, NOT a duty-cycle product. The solver reads the ACTUAL live
`IBodyInfo` periods and computes the joint resonance from them; it NEVER multiplies duty
cycles assuming independence. For the real Bug-2 case Ike is tidally locked, so `T_orb(Ike)`
equals `T_rot(Ike)` and both equal Duna's rotation period to about 6 significant figures
(around 65,517 s). They are NOT independent: an arrival that matches Duna's rotation phase
auto-matches Ike's orbital phase. The joint solver must collapse coincident periods for
free, exactly as the same-parent Mun `rotationPeriod == orbit.period` collapse already does.
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
(section 9); we do not chase a global resonance.

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

### Phase 2 - Cross-parent destination constraint extraction (pure)
- Extend the constraint extraction so a cross-parent LANDING mission emits DestRotation (the
  destination surface-orbit hand-off pair rule) + one MoonConfig per visible inner moon,
  keyed to `RecordedDestSurfaceUT` / `RecordedArrivalUT` respectively, instead of
  short-circuiting to `Support.UnsupportedCrossParent`. Keep the `UnsupportedCrossParent`
  outcome for the cases this phase does NOT cover (section 8).
- Tests (xUnit pure):
  - Cross-parent LANDING (Duna, captured-then-deorbit) emits DestRotation + MoonConfig (Ike),
    no longer UnsupportedCrossParent.
  - Cross-parent ORBIT-ONLY flyby emits MoonConfig only (no DestRotation: no surface + orbit
    pair).
  - 0-moon destination (Eve / Moho / Dres) emits DestRotation only.
  - 2+ constrained-moon destination still reports not-supported (section 8 cutoff).
  - Same-parent missions UNCHANGED (regression guard: byte-identical extraction).
- Done: tests pass. Deps: Phase 1. Review: full (the deferral-seam change).

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
  VALIDATE on ORBITAL ARRIVAL FIRST (far-from-camera SOI seam, tolerated by
  `IsSeamResidualTooLarge`).
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
   (`ReaimedTrajectory` renders only from `OrbitSegments`; `IsSeamResidualTooLarge` tolerates
   the SOI seam there). Phase B is gated on S4 (`map-ts-render-rewrite-phases.md` Workstream
   C2); the body-fixed descent polyline is a separate live-clock renderer and the descent
   replay is deferred upstream.

4. Moon count cutoff = COUNT of constrained moons. 0 or 1 constrained moon is IN scope. 2 or
   more constrained moons (Jool-class) is detect-and-report-not-supported, falling to
   rotation-only or faithful. The 2+ moon joint duty-cycle product collapses the window rate
   toward the dead-end-5 regime, which is why it is deferred; the design detects and reports
   rather than attempting a joint solve at the cutoff.

---

## 11. Integration with the geometric (Lambert capture) seam fix

This layer is strictly ON TOP of and orthogonal to the shipped geometric seam fix
(`ReaimSegmentAssembler.ReplaceHeliocentricLeg` + the full-recorded-span render window). The
geometric fix connects POSITIONS in recorded-span time and is invariant to which absolute
window k is chosen; this layer only changes WHICH window k is synthesized for, so it does not
modify `ReplaceHeliocentricLeg`, the render window, or the SOI handoff. The
`IsSeamResidualTooLarge` guard (> 50 km / 5 percent radial) still bounds the geometric
residual for any k; a window whose geometry would exceed it is rejected as a synthesis miss
and falls to faithful, so an arrival-aligned k can never make the geometric seam worse than
the guard permits.

Open interaction (carried to residual risks, section 13): the geometric SOI-entry instant
from `CalculatePatch` is not identical to the rotation-aligned arrival; whether a single
chosen window simultaneously satisfies both the geometric position seam and the rotation-phase
match, or whether they trade off, must be validated on the real s15 case.

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

- Geometric seam vs rotation phase may fight. The geometric SOI-entry instant from
  `CalculatePatch` is not identical to the rotation-aligned arrival; whether a single chosen
  window simultaneously satisfies BOTH the position seam (`IsSeamResidualTooLarge` under 50 km
  / 5 percent radial) AND the rotation-phase match, or whether they trade off, is unproven and
  must be validated on the real s15 case. If they fight, the loiter re-timer + tof band must
  jointly absorb both, which may not be possible for every window.
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

- Same-parent (Mun / Minmus) looped missions: untouched (the existing faithful periodicity
  path).
- The shipped re-aim geometric layer: `ReplaceHeliocentricLeg`, the full-recorded-span render
  window, `IsSeamResidualTooLarge`, `PadAlignLaunch` (launch side), the per-window Lambert
  synthesis. This layer only SELECTS the window k they run for.
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

"Naked" means without using the tof band or the loiter re-timer. Read plainly: Precise on the
bare window grid is effectively never (about 1,440 windows is centuries of in-game time,
which brushes the dead-end-5 regime). Loose on the bare grid is about 1 window in 72, roughly
140 Kerbin years, still impractical as the sole mechanism. The bare synodic grid alone does
NOT solve Bug 2 for any realistic patience. Two amplifiers change this:

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
rotation phase auto-matches Ike's orbital phase; the joint solver collapses the two coincident
periods exactly the way the same-parent path already collapses the tidally-locked Mun
(`rotationPeriod == orbit.period`). The solver must read the LIVE periods and let the collapse
happen, never assume the two are independent and never multiply duty cycles as if they were.
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
keeps the arrival geometry inside the `IsSeamResidualTooLarge` guard, is unproven and must be
measured on the real s15 case before relying on it. If the tof band turns out to be too narrow
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
