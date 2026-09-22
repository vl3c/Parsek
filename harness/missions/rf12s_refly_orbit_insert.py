"""Mission rf12s_refly_orbit_insert: put a re-flown crewed stack into orbit.

Scenario RF-12S. The spec's seam steps rewind `refly-autopilot-recorded` to its
RewindPoint slot 0 BEFORE this mission starts, which restores Bill and Bob on the
Kerbal X upper stack FLYING at ~40 km. This mission lights the Poodle (one stage
activation), holds a fixed surface-frame pitch / heading and burns until the
orbit's periapsis clears the atmosphere, then cuts the throttle and ends in FLIGHT
with the re-fly recorder live. The spec's post-mission `AnswerMergeDialog merge`
concludes the re-fly; the crew SURVIVE it, which is the shape the
TOMBSTONE-GUARD-SCREENS-AN-INTERVAL-ACTION-BY-ITS-START ruling needs.

WHY ORBIT AND NOT A LANDING. That stack has no parachute and its Poodle cannot hold
it up at sea level even with the tank empty, and a landing in flight stamps no
terminal state anyway: the proving merge is the scene-exit one, where finalization
stamps the terminal. What the lane needs is crew who are ALIVE at that merge.

A THIN shell: every decision is the pure ``mlib.rfo_decide`` machine and
``mlib.evaluate_rfo_assertions``; the flight, connect, logging and result write are
the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
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

MISSION_NAME = "rf12s_refly_orbit_insert"


def build_state(params: dict):
    """Build the mlib RF-12S machine initial state (at IGNITE)."""
    return mlib.rfo_initial_state(mlib.rfo_params_from_dict(params))


def decide(state, snapshot):
    return mlib.rfo_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is machine-carried evidence stamped on the frame that produced it.
    return mlib.evaluate_rfo_assertions(frames, mlib.rfo_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC, no MechJeb: one stage activation, a throttle and the native AP.
    # read_crew is this lane's one opt-in: the handoff gate must OBSERVE that the
    # restored craft carries the pre-rewind-boarded crew (one extra RPC per poll).
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             client_name=MISSION_NAME,
                                             read_crew=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # 1x throughout: the burn is the re-fly's recording, and a warped burn would both
    # distort it and risk the physics-warp guard.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # No settle tail: every assertion is machine-carried evidence.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
