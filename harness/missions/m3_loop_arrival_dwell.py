"""Mission m3_loop_arrival_dwell: arm mission-level LOOP on the committed
duna-direct tree via the MissionConfig seam verb, then dwell the map camera
through the loop's departure, arrival and parked-tail windows.

The Tier-2 playback half of the looped-interplanetary arrival-validation lane
("Looped re-aim interplanetary transfer" todo entry): M1/M2 gate the re-aim
SOLVER and S1.8 gates segment position RESOLUTION; this mission is the first
thing in the suite that makes a looped interplanetary mission actually REPLAY
under observation. It flies nothing (no MechJeb): one seam round trip, three
camera actions, three native warps, three 1x holds. The RENDER truth (ghost
sampled frames, parity counters, anomaly raises, the [ReaimDiag]/ENGAGED mode
evidence) rides the spec's log contracts over the probe/tracer lines; the
machine carries only its own arm/camera/window evidence.

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

MISSION_NAME = "m3_loop_arrival_dwell"


def build_state(params: dict):
    return mlib.m3_initial_state(mlib.m3_params_from_dict(params))


def decide(state, snapshot):
    return mlib.m3_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_m3_assertions(
        frames, mlib.m3_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # No MechJeb (nothing flies); read_camera is the V1 observed-mode channel
    # the CAMERA gate advances on (fails closed at its unread sentinel).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=False, client_name=MISSION_NAME, read_camera=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # The three inter-window legs are native warp_to_ut (the game picks legal
    # rates); the holds are 1x. No physics warp anywhere.
    allow_rails_warp=True,
    max_physics_warp=1.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
