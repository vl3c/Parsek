"""Mission boat1_water_taxi: the BOAT-1 lane's run (the first lane that drives a vessel on water).

The host (`mallard-splashed`, AIR-1's harvest with Parsek stripped) hands over the stock
Mallard at rest in the sea east of the Space Center, both jets lit at zero throttle. The
seam has already started a recording on it. The machine checks the handoff (the Mallard,
SPLASHED, position read), turns SAS on and the brakes off, drives the jets on a
proportional speed governor until the boat is ``targetDistanceMeters`` from where it
started, cuts the throttle and waits for it to come to rest on the water. The scenario's
seam steps then commit the tree, rewind it to launch and replay it at the Space Center.

A THIN shell: every decision is the pure ``mlib.boat_decide`` machine and
``mlib.evaluate_boat_assertions``; the connect, fly loop, logging and result write are
the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside ``mission_runner``,
so this module imports clean on the base interpreter.

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

MISSION_NAME = "boat1_water_taxi"


def build_state(params: dict):
    return mlib.boat_initial_state(mlib.boat_params_from_dict(params))


def decide(state, snapshot):
    return mlib.boat_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_boat_assertions(frames, mlib.boat_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC. `read_vessel_name` is REQUIRED (the handoff gate compares the active
    # vessel's name; the "" UNREAD sentinel matches nothing), and so is `read_aircraft`:
    # latitude, longitude and surface speed are the governor's and the distance's whole
    # input, and the handoff refuses a dark channel.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             read_vessel_name=True,
                                             read_aircraft=True,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Real time throughout: a surface run under warp is a different run.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # Every assertion is machine-carried evidence; no settle tail to sample.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
