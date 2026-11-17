"""Mission air3_drone_intercept: the AIR-3 lane's flight (two aircraft of one tree collide).

Rolls `AIR Drone Carrier` (the stock Mallard with a parachute drone under its right
wing; harness/tools/build_air_drone_carrier.py) out onto the Runway and flies AIR-1's
take-off and climb-out unchanged, then climbs on a loiter leg over the sea east of
KSC, releases the drone level at releaseAltitude (the second stage click: decoupler
and Mk16), extends, turns back and flies the Mallard into the drone hanging under its
canopy, reading the drone's live position every poll. After the collision the
throttle is cut and the machine waits until both vessels are down or gone. The
scenario's seam steps then commit the tree, rewind it to launch and replay both
ghosts at the Space Center.

A THIN shell: every decision is the pure ``mlib.ax_decide`` machine and
``mlib.evaluate_air3_assertions``; the connect, fly loop, logging and result write
are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
``mission_runner``, so this module imports clean on the base interpreter.

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

MISSION_NAME = "air3_drone_intercept"


def build_state(params: dict):
    return mlib.ax_initial_state(mlib.ax_params_from_dict(params))


def decide(state, snapshot):
    return mlib.ax_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_air3_assertions(frames, mlib.ax_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC. `read_vessel_name` and `read_aircraft` as AIR-1 (the rollout gate and
    # the whole guidance input); `read_air_target` is the drone's position relative to
    # the Mallard and both part counts, the pursuit's and the collision check's only
    # input (the sibling watch the release arms names the drone).
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             read_vessel_name=True,
                                             read_aircraft=True,
                                             read_air_target=True,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Real time throughout: a plane under physics warp is a different flight.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # Every assertion is machine-carried evidence; no settle tail to sample.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
