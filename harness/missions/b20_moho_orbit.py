"""Mission b20_moho_orbit: HIGH-park ascent + PRE-TRANSFER JETTISON + Moho
interplanetary transfer + CAPTURE burn + park + commit-in-target-orbit terminal.

The B16 -> B17 -> B19 precedent, applied once more: a THIN ALIAS over the
body-parameterized B5 machine, carrying B7's five INTERPLANETARY params and
B11/B12's ORBIT TAIL, retargeted at Moho. There is no b20_decide and no new
mlib code at all -- `b5_decide` is target-generic
(docs/dev/design-testing-unified.md S190) and Moho is a parameter. Even the
JETTISON phase, which was B19's one genuinely new mechanism, is now shared and
default-off; this lane simply arms it with B19's measured values.

WHY MOHO, AND WHY THIS LANE IS NOT "B19 WITH A DIFFERENT STRING". Moho is the
re-aim synthesizer's OWN DOCUMENTED FAILURE CASE: 7 deg inclination and ecc
~0.2 is the population `ReaimTransferSynthesizer`'s comments place beyond
Duna's always-safe 0.06 and beyond Eve's 2.1 / Dres's 5. This flight exists to
PRODUCE THE RECORDING that the V11 / V11A lanes then read that failure case
from. The flight itself asks nothing about re-aim; it asks only for a committed
Moho orbit.

THE TWO THINGS MOHO CHANGES, and both are sizing rather than machinery:

  (1) THE APPROACH IS ~8x SHORTER IN GAME TIME. Moho's SOI is 9,646,663 m
      against Dres's 32,832,840 m, and the arrival v_inf is far higher, so the
      SOI-entry -> periapsis coast is 2,168-4,119 game s where Dres's measured
      ~25,000. Note 2*mu/r_soi = 34,957 m^2/s^2 is negligible against v_inf^2
      (5.5e6-2.0e7), i.e. Moho's gravity barely bends the approach and the passage is
      essentially straight-line at v_inf: t ~ r_soi / v_inf.

      B19's sizing rule ("one poll frame must advance well under a fifth of the
      SOI-entry -> periapsis coast", `test_one_frame_cannot_swallow_the_dres_
      approach`) therefore forces a LOWER ceiling here: sized against a
      pessimistic 2,000 s floor (under the whole band) the budget is 400 game s, so factor 5 (x1,000)
      FAILS and factor 4 (x100) passes with 4x margin.

      What does NOT scale down with the body is `soiLeadSeconds`. That knob is
      sized against the COAST phase's single-frame advance -- B19 flight 4
      measured one x100,000 poll covering 27,596 game s, and reasoned from the
      ~50,000 s a full-rate poll can cover -- which is a function of
      coastWarpFactor and the poll rate, NOT of the target. Scaling it down with
      Moho's smaller SOI would put the lead BELOW one coast frame and reproduce
      exactly the `capture-never-armed (past-periapsis)` give-up the knob
      exists to prevent. It stays at B19's 100,000.

  (2) THE CAPTURE COST IS SET BY MOHO'S ECCENTRICITY, NOT ITS DISTANCE. At
      ecc 0.2 Moho's velocity is not tangential except at its apsides, so at
      r = sma an 11.54 deg flight-path-angle mismatch puts arrival v_inf at
      4,449 m/s where treating Moho as circular gives 2,997. Capture to a
      ~100 km periapsis therefore runs ~1,845 m/s (encounter at Moho
      periapsis) to ~3,862 (at r = sma), against Dres's measured 1,717.4.
      The worst case is ~5.4 t of propellant and ~700 s (~12 min) of
      continuous LV-N thrust, begun ~350 s before periapsis by the node
      executor -- inside a 2,168 s SOI coast at that same v_inf, so it fits,
      but on ~3x margin rather than Dres's ~35x. The burn-liveness and capture
      budgets below are widened for that, and if the single capture burn still
      fails to bind, the MEASURED failure mode is the finding; no new machinery
      is invented ahead of the evidence.

  Moho, like Dres and unlike Eve, is CHEAPER to capture LOW (v_inf is large
  against a small mu), so there is no high-park temptation. Moho HAS NO MOONS,
  so nothing physical constrains the park.

WHY THE PARK IS HIGH (700 km), the B16/B19 reason verbatim and NOT a copied
habit: the ejection window wait is warped through and KSP's stock rails table
caps warp by ALTITUDE. At a 100 km park the ceiling is 50x, which cannot cover
a multi-million-second window wait inside any sane wall budget; above 600 km
the full 100,000x ladder is legal. The park altitude is a WARP decision.

DELTA-V, and this lane's is the tightest in the suite. B18 MEASURED the LV-N
stage at 18.220 t wet / 7.020 t burnout, i.e. 7,483 m/s at Isp 800 s. Against
Moho that buys, with every figure now anchored on flight 1's measurements
rather than on derivation:

  ejection      1,735.5 m/s MEASURED (the planned node). Note this came out
                COPLANAR -- the flight's transfer read inc=0.007 against Moho's
                7 deg -- so it is +4.5% over the ~1,661 m/s coplanar derivation,
                NOT evidence that the 7 deg was folded in. It was not.
  corrections   ~1,090 m/s MEASURED for the encounter-CREATING round (MechJeb's
                own plan), plus a small refining round. This is the number the
                first authoring got wrong by deriving instead of measuring, and
                capping below it cost two flights -- see the spec's
                maxCorrectionDvMps block.
  capture       ~1,845 to ~3,862 m/s, a RANGE set by where in Moho's eccentric
                orbit the encounter falls (see above).
  remaining     5,724 m/s MEASURED still in the stage after the ejection
                (2,240 -> 1,508.915 units of LF).

So the realistic case leaves ~4,434 m/s for a capture costing at most ~3,862 --
it closes, with ~572 spare. The margin is ~1.5x at a cheap encounter and ~1.05x
at the dear one, against Dres's uniform 2.3x, and the one combination that does
NOT close is both correction rounds landing on the 1200 cap AND the worst-case
r=sma encounter. That conjunction is named in the spec rather than hidden.

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

MISSION_NAME = "b20_moho_orbit"


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
    #                         periapsis_ut - 900, read off the orbit's own clock).
    #                         This channel matters MORE here than on any prior
    #                         lane: Moho's approach is the shortest in the suite,
    #                         so periapsis is the one clock worth steering by.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B7 (heliocentric coast at factor 7) and B11/B12 (both
    # NodeExecutor autowarps are RAILS); MechJeb-ascent physics warp capped at
    # the stock 4x ceiling. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries Moho
    # (its row is identical to Dres's), so the arrival + park legs are clamped
    # from committed data with no table change.
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
