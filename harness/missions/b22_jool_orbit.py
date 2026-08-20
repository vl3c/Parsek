"""Mission b22_jool_orbit: HIGH-park ascent + PRE-TRANSFER JETTISON + Jool
interplanetary transfer + CAPTURE burn + park + commit-in-target-orbit terminal.

The B16 -> B17 -> B19 -> B20 -> B21 precedent, applied once more: a THIN ALIAS
over the body-parameterized B5 machine, carrying B7's five INTERPLANETARY params,
B11/B12's ORBIT TAIL and B19's PRE-TRANSFER JETTISON, retargeted at Jool. There
is no b22_decide and no new mlib code at all -- `b5_decide` is target-generic
(docs/dev/design-testing-unified.md S190), the jettison phase is already shipped,
and Jool is a parameter. NINE of the fifty-six missionParams move Eeloo -> Jool
and FORTY-SEVEN are copied verbatim (a MEASUREMENT: parse both specs with
tomllib and compare the blocks key by key); not one SCHEMA declaration moves,
and each of the nine is checked against the bound above it in
b22_jool_orbit.schema.toml's header.

WHAT IS GENUINELY NEW HERE IS A SCALE, NOT A MECHANISM. Jool's SOI is
2,455,985,185 m -- 20.6x Eeloo's 119,082,942 m, the largest in the stock system --
and it is the first target in this family that CARRIES MOONS, so for the first
time the park has somewhere to be WRONG that is neither the terrain nor deep
space. Every value that moves is a metre or an SOI-crossing second; the phase
machine is byte-identical.

THE JETTISON PHASE, inherited unchanged, exists because of a MEASURED property of
this lane's CRAFT -- the same Duna Rocket B18-B21 flew -- and not because of the
target body:

  B18 measured the Duna Rocket in its park carrying 20.775 t of SPENT CHEMICAL
  HARDWARE -- the core Mainsail, its tanks, the fairing and an unlit Skipper --
  with a CHEMICAL engine still live. MechJeb autostage never sheds any of it,
  because autostage fires only on EMPTY stages and the core Mainsail still held
  ~40% of its LFO at insertion. The transfer stage's LV-N sits four stages
  further down the list.

  `_b5_flameout_stage` cannot do this job: it fires only on OBSERVED zero thrust
  under a COMMANDED burn, and this jettison happens at throttle 0 with nothing
  commanded. So the phase pops stages ONE AT A TIME, thrust-safe, re-observing
  between pops, and then CERTIFIES the outcome on two independent channels -- a
  vessel split (the spent stack became its own vessel) and a live-thrust
  SIGNATURE (60 kN reads as the Nerv; the 650 kN Skipper and 1,500 kN Mainsail do
  not fit under the ceiling). `jettisonMaxActivations` is a SAFETY CAP, not a
  spec-declared count: `_b5_jettison_step` evaluates its early stop
  (`split_confirmed and thrust_ok`) BEFORE the pop branch, so a satisfied
  signature can never be followed by one more pop.

WHY THE PARK IS HIGH (700 km), the B16/B19/B20/B21 reason verbatim and NOT a
copied habit: the Kerbin->Jool ejection window wait is warped through, and KSP's
stock rails table caps warp by ALTITUDE. At a 100 km park the ceiling is 50x,
which cannot cover a multi-million-second window wait inside any sane wall
budget; above 600 km the full 100,000x ladder is legal. The park altitude is a
WARP decision. The Kerbin-Jool synodic period is 10,090,901.710 game s (DERIVED
from the stock periods 9,203,544.618 and 104,661,432.108 s:
1/|1/T_Jool - 1/T_Kerbin|, on the ASSET-EXTRACTED mu_Sun 1.1723327948324905e18
and not a rounded 1.1723328e18 -- these are the same three values the spec
carries, so one derived constant has ONE spelling in this commit), so
transferBurnTimeoutSeconds carries the whole
window wait at 18,000,000 / 10,090,901.710 = 1.78x and does not move.

THE AIM IS THE ONE DECISION THIS LANE MAKES ALONE, and it is the reason
courseCorrectPeriapsisMeters moves 300,000 -> 600,000,000. Jool's binding moon is
POL, whose apoapsis plus SOI puts the outermost moon shell at 211,666,345 m of
Jool RADIUS (Bop's 159.9 Mm is inside it, so Pol binds). A 600,000,000 m ALTITUDE
request is 606,000,000 m of radius against Jool's 6,000,000 m body radius --
24.43% of the SOI as a request, 24.67% as a radius -- and it is the SMALLEST
request whose worst in-regime delivery still clears Pol: at the lowest
delivered/requested ratio ever measured anywhere (0.545, the Mun) it lands at
333.0 Mm = 1.573x the Pol edge, where a 500 Mm request gives only 1.316x. The
union of the request-bearing laws spans 333.0 - 833.7 Mm of radius, i.e.
1.573x - 3.939x clear.

  BUT THE CLEARANCE IS REPORTED, NOT GATED, and that is a deliberate choice
  rather than an oversight. targetPeriapsisFloorMeters is 1,000,000 (5x Jool's
  200 km atmosphere top) and guards the FATAL band only. It is NOT set at the
  Pol edge, for three checkable reasons: (a) an actual moon-SOI CLIP already
  ASSERT-FAILs through `_b5_left_target_soi` / the TARGET-FLYBY off-course
  terminal regardless of the floor, so the floor would gate only the RADIUS;
  (b) MISSION-ASSERT-FAIL is retryable-once into a terminal PASS carrying
  `flakedThenPassed`, so a floor-based clearance claim is not durable even when
  armed; and (c) on a FIRST flight a green run that REPORTS the delivered
  periapsis is worth more than a red run that reports a preference. The
  delivered/requested ratio at req/SOI = 24.43% is 2.4x beyond the highest ever
  flown (the Mun's 10.29%) and is OPEN IN BOTH DIRECTIONS -- it may fall below
  0.545, or hold near the 0.997 Eve delivered. One flight settles it; read the
  first in-SOI `telemetry ... pe=` frame as a ratio to 606,000,000 and against
  211,666,345. Once measured, arming the floor at 211,666,346 on flight 2 is the
  named follow-up, and approachMaxWarpFactor must drop 5 -> 4 in the same edit.

THE THREE IN-SOI BUDGETS MOVE WITH THE SOI, and they are the other six of the
nine. The SOI-entry -> periapsis coast worst case is 1,361,783 game s (Eeloo's
was ~52,500-93,000), so flybyTimeoutSeconds is 2,500,000 (1.84x) and
captureBurnTimeoutSeconds is 3,000,000 (2.20x) -- B21's 400,000 is 0.29x of that
coast, i.e. a CERTAIN reap of a healthy Jool arrival. capturePlanTimeoutSeconds
goes 300 -> 300,000 to match planTimeoutSeconds, the structurally identical
phase: PLAN-CAPTURE is entered with a 100,000x native warp still armed and
`phase_entry_ut` is stamped BEFORE the cancel executes, so the phase opens
already carrying residual game time. parkMinPeriapsisMeters is pinned EQUAL to
the floor at 1,000,000 -- the B19/B20/B21 identity -- because B21's 50,000 would
accept a "park" 150 km INSIDE Jool's atmosphere; and parkMaxApoapsisMeters is
1,500,000,000 (1,506 Mm of radius, 61.3% of the SOI), sized to cover the
additive-in-SOI high tail at 1.81x. That ceiling is deliberately ASYMMETRIC: too
tight reds a correct committed orbit as a named FLAKE, and the captured-or-not
evidence is carried by parkMaxEccentricity (0.25, unchanged), which at this
ceiling still forces a periapsis at/above 903.6 Mm of radius.

DELTA-V, and unlike B21 this lane is not close to the constraint. The stage is
the MEASURED one, 18.220 t wet / 7.020 t burnout at Isp 800 s, i.e.
800 * 9.80665 * ln(18.220/7.020) = 7,482.5 m/s (reproducing B18's measured
7,483). Against Jool, from the 700 km park:

  ejection (Hohmann, DERIVED)                              1,928.6
  correction round 1 (B19-MEASURED per-round magnitude)      ~250
  correction round 2                                         ~250
  plane change (Jool i 1.304 deg)                             ~40
  capture at r 606 Mm, v_inf 1,763.2                       1,327.5
                                                          --------
                                                           3,796.1 m/s
  of 7,482.5 -> 3,686.4 m/s in hand (49.3%)

  SIZING WORST CASE (a BOUND, not an expectation):
  worst base = worst ejection 1,964.7 + worst capture over the WHOLE
    predicted delivered band 1,446.4 (r 833.7 Mm at v_inf 1,854.0)
    + plane change 40.1                                       3,451.2
  TWO rounds at the 1,200 cap                                 2,400.0
                                                            ---------
                                                             6,077.3 m/s  ->  1,405.2 in hand

maxCorrectionDvMps therefore goes the OTHER WAY from B21's, 550 -> 1,200, and the
reason is the ROUND COUNT rather than the per-round size. B21 could fire FOUR
capped rounds: two scheduled by correctionTriggerTimeToSoiSeconds plus up to two
arrival-quality extras the spec neither declares nor can decline
(MAX_ARRIVAL_EXTRA_ROUNDS = 2, on the separate `extra_rounds_done` counter). The
extras arm on a sub-floor PREDICTED arrival periapsis -- and at a 1,000,000 m
floor around a 2,456 Mm SOI that is PREDICTED not to be reachable, so the
effective count here is TWO and 2 x 1,200 = 2,400 fits the 4,031.3 m/s reserve
with 1,405 m/s spare. 1,200 is also the only value MEASURED to ACCEPT an
encounter-creating plan: B20's round 1 printed 1,089.5 m/s at Moho where
minimal-impulse physics predicts ~25, and a cap below it silently DISCARDED that
plan and cost two flights. Both halves are OPEN: if the extras do arm the count
becomes four and the tail exceeds even the shed-credited capacity (grep
`extraRounds=`), and if a Jool round 1 prints above 1,200 the fix is a lighter
mission, not a bigger cap.

TWO FIXTURE PROPERTIES THAT MUST BE STATED BEFORE ANYONE BOOKS THIS LANE AS
COVERAGE, both of them properties of Jool rather than of this spec. (1) Jool's
inclination is 1.304 deg -- the LOWEST in the V-lane ladder (Eve 2.1, Dres 5.0,
Eeloo 6.15, Moho 7.0) -- so a V13A on this fixture probes BELOW the
already-covered Eve point and CANNOT discharge the tilt debt; expect the tilt row
to read `state=noop`. (2) A moons-free park gives ConstrainedMoonCount = 0, so
`ArrivalHoldPlanner.ComputeArrivalHold` returns `ArrivalHoldResult.None` -- the
same orbit-only shape V12/V12A already flew green at Eeloo, so the fixture is
usable, but the multi-moon Jool-tour branch does NOT engage and must not be
claimed.

`padAlignEjection` is ABSENT AND MUST STAY ABSENT. Jool is not in
`mlib.STOCK_HELIO_ELEMENTS` ({Kerbin, Duna, Eve}) and its absence is pinned as a
test contract (test_b17_duna_direct.py asserts `b5_params_from_dict` RAISES for a
Jool `padAlign*`), so declaring it would red harness/missions/lib rather than fly.
This lane uses MechJeb's `interplanetaryTransfer` window, which is the B19-B21
mechanism and touches none of that.

Kept as its own mission name so the client name, the result files and the logs
say which body the flight parked at; the schema is a copy for the same reason
(run.py resolves <mission>.schema.toml by name).

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "b22_jool_orbit"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # The SAME seam as B11/B12/B19 -- the capture tail's three OBSERVED channels
    # are opt-in and every one of them fails CLOSED at its unread sentinel:
    #   read_docking       -> angular_velocity for the PARK tumble gate
    #   read_node_executor -> NodeExecutor.Enabled, the B11 flight-1 lesson
    #                         (CAPTURE-BURN must OBSERVE that MechJeb engaged,
    #                         never infer it from having commanded it)
    #   read_periapsis     -> Orbit.TimeToPeriapsis, the B12 flight-3 lesson
    #                         (the only legitimate in-SOI warp target is
    #                         periapsis_ut - 900, read off the orbit's own clock)
    #                         and the one that matters MOST at Jool: its SOI is
    #                         2,455,985,185 m, so the SOI-edge -> periapsis coast
    #                         is up to ~1,361,783 game s, and without this channel
    #                         the capture never arms.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B7 (heliocentric coast at factor 7) and B11/B12/B19
    # (both NodeExecutor autowarps are RAILS); MechJeb-ascent physics warp capped
    # at the stock 4x ceiling. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries
    # Jool -- though its row is NOT Dres's / Eeloo's (factor 7 needs 1,200,000 m
    # of altitude at Jool) -- so the arrival + park legs are clamped from
    # committed data with no table change.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the SF-4 contract): machine-carried assertions, frames
    # discarded, and the terminal frame is the one AFTER the commit.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
