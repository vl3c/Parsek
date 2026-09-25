"""Mission bay1_runway_bays: the BAY-1 lane's flight half (D7 `bays`).

Rolls a stock spaceplane out onto the Runway, stages it where it stands (brakes
on, throttle zero) so Parsek's own first-staging-on-the-pad trigger starts the
recording, cycles every cargo bay open and shut, and stops. The plane never
moves: the bay animation is the subject. The scenario's seam steps then commit,
Rewind-to-Launch and let the Space Center replay the recording as a ghost.

A THIN shell: every decision is the pure ``mlib.bay1_decide`` machine and
``mlib.evaluate_bay1_assertions``; the connect, fly loop, logging and result
write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
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

MISSION_NAME = "bay1_runway_bays"


def build_state(params: dict):
    return mlib.bay1_initial_state(mlib.bay1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.bay1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_bay1_assertions(frames, mlib.bay1_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC. `read_vessel_name` is REQUIRED: the rollout gate compares the active
    # vessel's name with the expected one, and the "" UNREAD sentinel matches no
    # declared name, so without it the gate deadlocks into its named give-up.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             read_vessel_name=True,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # Every assertion is machine-carried evidence; no settle tail to sample.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
