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
      SOI-entry -> periapsis coast is ~2,400-3,900 game s where Dres's measured
      ~25,000. Note 2*mu/r_soi = 34,955 m^2/s^2 is negligible against v_inf^2
      (~9e6), i.e. Moho's gravity barely bends the approach and the passage is
      essentially straight-line at v_inf: t ~ r_soi / v_inf.

      B19's sizing rule ("one poll frame must advance well under a fifth of the
      SOI-entry -> periapsis coast", `test_one_frame_cannot_swallow_the_dres_
      approach`) therefore forces a LOWER ceiling here: at the pessimistic end
      of the band the budget is 2,000/5 = 400 game s, so factor 5 (x1,000)
      FAILS and factor 4 (x100) passes with 4x margin.

      What does NOT scale down with the body is `soiLeadSeconds`. That knob is
      sized against the COAST phase's single-frame advance -- B19 flight 4
      measured one x100,000 poll covering 27,596 game s, and reasoned from the
      ~50,000 s a full-rate poll can cover -- which is a function of
      coastWarpFactor and the poll rate, NOT of the target. Scaling it down with
      Moho's smaller SOI would put the lead BELOW one coast frame and reproduce
      exactly the `capture-never-armed (past-periapsis)` give-up the knob
      exists to prevent. It stays at B19's 100,000.

  (2) THE CAPTURE IS ~2x THE BURN. Moho's mu is 1.6860938e11 and the arrival
      v_inf is ~3,000 m/s, so capturing to a ~100 km periapsis costs
      sqrt(v_inf^2 + 2mu/r) - sqrt(mu/r) ~= 3,157 - 694 ~= 2,463 m/s, against
      Dres's measured 1,717.4 m/s node. On ~13.85 t at the LV-N's 60 kN /
      Isp 800 that is ~485 s (~8 min) of continuous thrust, begun ~242 s before
      periapsis by the node executor -- inside a 2,400-3,900 s SOI coast, but
      with far less slack than Dres had. The burn-liveness and capture budgets
      below are widened for that, and if the single capture burn still fails to
      bind, the MEASURED failure mode is the finding; no new machinery is
      invented ahead of the evidence.

  Moho, like Dres and unlike Eve, is CHEAPER to capture LOW (v_inf is large
  against a small mu), so there is no high-park temptation. Moho HAS NO MOONS,
  so nothing physical constrains the park.

WHY THE PARK IS HIGH (700 km), the B16/B19 reason verbatim and NOT a copied
habit: the ejection window wait is warped through and KSP's stock rails table
caps warp by ALTITUDE. At a 100 km park the ceiling is 50x, which cannot cover
a multi-million-second window wait inside any sane wall budget; above 600 km
the full 100,000x ladder is legal. The park altitude is a WARP decision.

DELTA-V. B18 MEASURED the LV-N stage at 18.220 t wet / 7.020 t burnout, i.e.
7,483 m/s at Isp 800 s. Against Moho: ~1,700 (ejection from the 700 km park,
v_inf ~2,349 m/s for the inward Hohmann, plus Moho's 7 deg folded in) + <=450
(corrections) + ~2,463 (capture) = ~4,600 m/s. The margin is ~1.6x, TIGHTER
than Dres's 2.3x and stated plainly because it is the one budget on this lane
that is not comfortable. B19 measured 3,933 m/s still in the stage at its Dres
commit, which is the direct evidence this fits.

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
