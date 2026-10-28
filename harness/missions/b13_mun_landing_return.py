"""Mission b13_mun_landing_return: B13's Mun landing, an EVA with a flag on the Mun,
the boarding, a Mun ascent, then CS-6's departure and CS-5's return tail down to a
splashdown.

The scripted campaign's CS-7 ("Mun landing, EVA, plant a flag, board, ascend,
return, recover"). B13 ends with the tree committed while the craft stands on the
Mun; nothing comes home. This mission runs B5's pure machine VERBATIM with
captureEnabled and landingEnabled (B13's landing tail) to the end of its landed
dwell, drops the mid-mission CommitTree B13 would send there (the recovery commits
the tree at the scene exit, as on CS-5 and CS-6), sends the seam's EvaExit,
PlantFlag and EvaBoard from inside the flight (kRPC has no EVA API), flies a Mun
ascent on MechJeb's AscentAutopilot, then mor's departure (MechJeb's
OperationMoonReturn) and mfr's return tail (HOME-TRIM, HOME-FALL, B4's machine from
its REENTRY to the splashdown). Every decision is ``mlib.mlr_decide`` (the block
note above ``mlib.MLR_LAND`` carries the plan); every assertion is
``mlib.evaluate_mlr_assertions``.

WHY A NEW MISSION AND NOT A B13 PARAM: hlib.validate_spec allows one mission step
per autopilot spec, and the surface visit and the way home are not B5's machine.
Composing leaves ``b5_decide``, ``b4_decide``, ``mor_decide`` and ``mfr_decide``
untouched, so every lane that flies them is byte-identical by construction.

THE CONTROL: CS-6's reads plus B13's. read_docking=True is the PARK tumble gate's
angular-velocity channel; read_node_executor=True is the capture's and the
departure's OBSERVED executor channel; read_periapsis=True feeds the capture-mode
flyby warp and the fall warp's periapsis clock; read_landing=True is the DESCENT
supervisor's OBSERVED LandingAutopilot channel and the settled-touchdown gate's
horizontal speed (without it the landed gate fails closed forever); read_chute=True
is B4's canopy read.

This is a THIN shell; ``import krpc`` never happens at module top, so this module
imports clean on the base interpreter (no venv).

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

MISSION_NAME = "b13_mun_landing_return"


def build_state(params: dict):
    """The composed machine's initial state, inside B5's own PRELAUNCH."""
    return mlib.mlr_initial_state(mlib.mlr_params_from_dict(params))


def decide(state, snapshot):
    return mlib.mlr_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B13's rows through the landed dwell over the delegated B5 state, the three
    # surface rows, the ascent and departure rows, then the return window and B4's
    # landed / canopy rows.
    return mlib.evaluate_mlr_assertions(frames, mlib.mlr_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_landing=True,
        read_chute=True, read_vessel_name=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B13's warp policy (its rails hops, the native warp_to legs, both MechJeb
    # executor autowarps, MechJeb's landing-state warps, the PARK dwell under 4x
    # physics warp), which also covers the departure coast, B4's hops and the fall.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # B4's settle tail (the default): landedSituation reads the final frame, so
    # the splashed craft is read settled.
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
