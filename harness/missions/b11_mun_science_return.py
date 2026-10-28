"""Mission b11_mun_science_return: B11's Mun capture and park, readings in high and
low Mun space with an EVA report on each, then CS-6's departure and CS-5's return
tail down to a splashdown.

The scripted campaign's CS-6c ("a science run to the Mun"). CS-7's lander makes no
power and nothing on the career's tech does, so this flight earns the science for
Basic Science (the Z-100) and Electrics (the OX-STAT). It runs B5's pure machine
VERBATIM with captureEnabled (B11's capture tail) to the end of its PARK dwell,
drops the mid-mission CommitTree B11 would send there (the recovery commits the
tree at the scene exit, as on CS-6), takes the high-space readings in the park
(one Goo, one thermometer, then the seam's EvaExit, the kerbal's EVA report and
the seam's EvaBoard), lowers the orbit under the Mun's 60 km space threshold with
two retrograde nodes, takes the low-space readings (the crew report, the other
Goo, the other thermometer, and a second EVA cycle), then departs on mor's
moon-return planner and hands the craft to mfr's return tail. Every decision is
``mlib.msc_decide`` (the block note above ``mlib.MSC_SCIENCE`` carries the plan);
every assertion is ``mlib.evaluate_msc_assertions``.

WHY A NEW MISSION AND NOT A B11 PARAM: hlib.validate_spec allows one mission step
per autopilot spec, and the science, the departure and the return half are not
B5's machine. Composing leaves ``b5_decide``, ``mor_decide``, ``mlr_decide`` and
``mfr_decide`` untouched, so every lane that flies them is byte-identical by
construction.

THE CONTROL: CS-6's reads plus the science channels. read_docking=True is the PARK
tumble gate's angular-velocity channel; read_node_executor=True is the capture's,
the two lowering nodes' and the departure's OBSERVED executor channel;
read_periapsis=True feeds the capture-mode flyby warp, the lowering nodes' periapsis
clock and the fall warp; read_chute=True is B4's canopy read; read_science=True is
the OBSERVED reading count every science gate and the scienceKeptToSplashdown row
read (the active vessel's experiments holding data).

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

MISSION_NAME = "b11_mun_science_return"


def build_state(params: dict):
    """The composed machine's initial state, inside B5's own PRELAUNCH."""
    return mlib.msc_initial_state(mlib.msc_params_from_dict(params))


def decide(state, snapshot):
    return mlib.msc_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B11's rows through the park over the delegated B5 state, the five science
    # rows, the departure row, the return window and B4's landed / canopy rows, then
    # the readings the pod carried home.
    return mlib.evaluate_msc_assertions(frames, mlib.msc_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_chute=True,
        read_science=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B11's warp policy (its rails hops, the native warp_to legs, the MechJeb
    # executor autowarps, the PARK dwell under 4x physics warp), which also covers
    # the lowering nodes' autowarp, the departure coast, B4's hops and the fall warp.
    # The science and the EVA cycles run at 1x.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # B4's settle tail (the default): landedSituation reads the final frame, so the
    # splashed craft is read settled.
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
