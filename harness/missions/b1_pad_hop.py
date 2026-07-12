"""Mission b1_pad_hop: pad-hop via RAW kRPC (design M-B1 "Mission B1: pad-hop").

Flies the fixture save's pre-placed SRB-plus-chute vessel with raw kRPC (NO
MechJeb): throttle + activate stage (release clamps / ignite the SRB), burn to
fuel exhaustion, coast to apoapsis, deploy the chute on the way down, and land.
Asserts an apoapsis WINDOW and a landed/splashed situation -- both DRIVER-VALIDITY
telemetry assertions, never a golden trajectory.

This is a THIN shell: every decision is the pure ``mlib.b1_decide`` phase machine
and ``mlib.evaluate_b1_assertions``; the flight, connect, logging, and result
write are the shared ``mission_runner`` runtime. ``import krpc`` never happens at
module top -- it is lazy inside ``mission_runner.KrpcMissionControl.open`` -- so
this module imports clean on the base interpreter (no venv), which is what lets
the unittest discovery + the fake-telemetry tests import it without krpc.

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

# Self-sufficient path bootstrap: as a subprocess this file's dir (missions/) is
# sys.path[0]; put it on the path so ``import mission_runner`` resolves, and
# mission_runner puts missions/lib on the path for mlib.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "b1_pad_hop"


def build_state(params: dict):
    """Build the mlib B1 phase-machine initial state from the missionParams dict."""
    return mlib.b1_initial_state(mlib.b1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b1_decide(state, snapshot)


def evaluate(frames, params: dict) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b1_assertions(frames, mlib.b1_params_from_dict(params))


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb) for B1.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
