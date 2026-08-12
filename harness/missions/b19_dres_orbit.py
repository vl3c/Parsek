"""Mission b19_dres_orbit: HIGH-park ascent + PRE-TRANSFER JETTISON + Dres
interplanetary transfer + CAPTURE burn + park + commit-in-target-orbit terminal.

The B16 -> B17 precedent, applied once more: a THIN ALIAS over the
body-parameterized B5 machine, carrying B7's five INTERPLANETARY params and
B11/B12's ORBIT TAIL, retargeted at Dres. There is no b19_decide and, for the
transfer and capture halves, no new mlib code -- `b5_decide` is target-generic
(docs/dev/design-testing-unified.md S190) and Dres is a parameter.

WHAT IS GENUINELY NEW HERE, and it is exactly one thing: the PRE-TRANSFER
JETTISON phase (`jettisonActivations` > 0). It exists because of a MEASURED
property of this lane's craft, not because Dres is special.

  B18 measured the Duna Rocket in its 80 km park carrying 20.775 t of SPENT
  CHEMICAL HARDWARE -- the core Mainsail, its tanks, the fairing and an unlit
  Skipper -- with a CHEMICAL engine still live. MechJeb autostage never sheds
  any of it, because autostage fires only on EMPTY stages and the core Mainsail
  still held ~40% of its LFO at insertion. The transfer stage's LV-N sits four
  stages further down the list.

  Two things break if that stack is carried into PLAN-TRANSFER. (1) TWR: the
  LV-N is 60 kN, so against the full 74.3 t stack it pushes at 0.08 g and a
  ~1,600 m/s ejection is not a burn, it is a spiral. After the jettison the same
  engine pushes 18.2 t at 0.34 g and the burn is ~7 minutes. (2) The node would
  be planned and sized against a vehicle about to lose two thirds of its mass.

  `_b5_flameout_stage` cannot do this job: it fires only on OBSERVED zero thrust
  under a COMMANDED burn, and this jettison happens at throttle 0 with nothing
  commanded. So the phase pops an EXACT, spec-declared number of stages,
  thrust-safe, and then CERTIFIES the outcome on two independent channels --
  a vessel split (the spent stack became its own vessel) and a live-thrust
  SIGNATURE (60 kN reads as the Nerv; the 650 kN Skipper and 1,500 kN Mainsail
  do not fit under the ceiling). The evidence half is B-DOCK's shared pure
  `separation_evidence`; see `mlib._b5_jettison_step`.

WHY THE PARK IS HIGH (700 km), the B16 reason verbatim and NOT a copied habit:
the Kerbin->Dres ejection window wait is warped through, and KSP's stock rails
table caps warp by ALTITUDE. At a 100 km park the ceiling is 50x, which cannot
cover a multi-million-second window wait inside any sane wall budget; above
600 km the full 100,000x ladder is legal. The park altitude is a WARP decision.

DELTA-V is not the constraint on this lane and that is worth saying plainly,
because on B16 it was the whole argument: B18 MEASURED the LV-N stage at
18.220 t wet / 7.020 t burnout, i.e. 7,483 m/s at Isp 800 s, against a Dres
round trip of roughly 1,600 (ejection) + <=450 (corrections) + ~1,150 (capture)
= ~3,200 m/s. The margin is ~2.3x and the jettisoned chemical residual
(~2,000 m/s) is thrown away deliberately -- it buys a single-engine,
single-Isp transfer with no mid-burn staging, which is worth far more to a
machine-flown mission than the fuel is.

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

MISSION_NAME = "b19_dres_orbit"


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
    # The SAME seam as B11/B12 -- the capture tail's three OBSERVED channels are
    # opt-in and every one of them fails CLOSED at its unread sentinel:
    #   read_docking       -> angular_velocity for the PARK tumble gate
    #   read_node_executor -> NodeExecutor.Enabled, the B11 flight-1 lesson
    #                         (CAPTURE-BURN must OBSERVE that MechJeb engaged,
    #                         never infer it from having commanded it)
    #   read_periapsis     -> Orbit.TimeToPeriapsis, the B12 flight-3 lesson
    #                         (the only legitimate in-SOI warp target is
    #                         periapsis_ut - 900, read off the orbit's own clock)
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
    # the stock 4x ceiling. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries Dres,
    # so the arrival + park legs are clamped from committed data.
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
