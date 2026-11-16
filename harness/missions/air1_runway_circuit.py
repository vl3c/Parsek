"""Mission air1_runway_circuit: the AIR-1 lane's flight (the first lane that flies a plane).

Rolls the stock Mallard out onto the Runway, stages it where it stands (brakes on,
throttle zero) so Parsek's own first-staging-on-the-pad trigger starts the
recording, releases the brakes at full throttle, rotates, climbs out east, flies a
circuit of waypoints at a cruise altitude, glides east to a touchdown point in the
sea, flares, splashes down and comes to rest. The scenario's seam steps then
commit the tree; its harvest is the fixture `air-circuit-recorded`. AIR-2 flies this
same mission with the same parameters, then rewinds it to launch and replays it at the
Space Center (a harvest carries no rewind save, so AIR-2 cannot rewind the harvest).

A THIN shell: every decision is the pure ``mlib.air_decide`` machine and
``mlib.evaluate_air_assertions``; the connect, fly loop, logging and result write
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

MISSION_NAME = "air1_runway_circuit"


def build_state(params: dict):
    return mlib.air_initial_state(mlib.air_params_from_dict(params))


def decide(state, snapshot):
    return mlib.air_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_air_assertions(frames, mlib.air_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC. `read_vessel_name` is REQUIRED (the rollout gate compares the active
    # vessel's name with the expected one; the "" UNREAD sentinel matches nothing),
    # and so is `read_aircraft`: heading, roll, latitude, longitude and surface speed
    # are the whole guidance input, and the machine refuses to stage on a dark
    # channel rather than fly the runway blind.
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
