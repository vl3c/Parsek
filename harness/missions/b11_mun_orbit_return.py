"""Mission b11_mun_orbit_return: B11's Mun capture and park, a departure back to
Kerbin, then CS-5's return tail down to a splashdown.

The scripted campaign's CS-6 ("Mun orbit and return"). B11 ends with the tree
committed while the craft is parked in the Mun SOI; nothing comes home. This
mission runs B5's pure machine VERBATIM with captureEnabled (B11's capture tail)
to the end of its PARK dwell, drops the mid-mission CommitTree B11 would send there
(the recovery commits the tree at the scene exit, as on CS-5), plans and flies a
Mun departure with MechJeb's OperationMoonReturn, warps out of the Mun SOI, and
then hands the craft to mfr's return tail (HOME-TRIM, HOME-FALL, B4's machine from
its REENTRY to the splashdown). Every decision is ``mlib.mor_decide`` (the block
note above ``mlib.MOR_ORBIT`` carries the plan); every assertion is
``mlib.evaluate_mor_assertions``.

WHY A NEW MISSION AND NOT A B11 PARAM: hlib.validate_spec allows one mission step
per autopilot spec, and the return half is mfr's and B4's machines, not B5's.
Composing leaves ``b5_decide``, ``b4_decide`` and ``mfr_decide`` untouched, so
every lane that flies them is byte-identical by construction.

THE CONTROL: B11's reads plus mfr's. read_docking=True is the PARK tumble gate's
angular-velocity channel (B11's load-bearing read); read_node_executor=True is the
capture's and the departure's OBSERVED executor channel; read_periapsis=True feeds
the capture-mode flyby warp and the fall warp's periapsis clock; read_chute=True is
B4's canopy read (craftCanopyObserved gates on the OBSERVED ParachuteState).

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

MISSION_NAME = "b11_mun_orbit_return"


def build_state(params: dict):
    """The composed machine's initial state, inside B5's own PRELAUNCH."""
    return mlib.mor_initial_state(mlib.mor_params_from_dict(params))


def decide(state, snapshot):
    return mlib.mor_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B11's rows through the park over the delegated B5 state, the departure row,
    # then the return window and B4's landed / canopy rows.
    return mlib.evaluate_mor_assertions(frames, mlib.mor_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_chute=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B11's warp policy (its rails hops, the native warp_to legs, both MechJeb
    # executor autowarps, the PARK dwell under 4x physics warp), which also covers
    # the departure coast, B4's hops and the fall warp.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # B4's settle tail (the default): landedSituation reads the final frame, so
    # the splashed craft is read settled.
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
