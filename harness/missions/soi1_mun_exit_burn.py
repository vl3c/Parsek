"""Mission soi1_mun_exit_burn: burn through the Mun SOI exit at 1x.

Scenario SOI-1 (R3 of the 2026-11-04 replay-theory audit). From the crewed Kerbal X
upper stage parked in a ~139.6 km Mun orbit (`mun-park-kerbalx`, the spec's seam
StartRecording opens the recording first), this mission burns prograde to a Mun
escape hyperbola, rails-warps the coast to a short lead before the Mun SOI exit,
drops to 1x and burns prograde at a modest throttle across the boundary, so the SOI
change happens in PHYSICS with the engine firing. It ends once the stage has been in
Kerbin's SOI for a short trail. The spec's post-mission CommitTree lands the tree.

WHAT IT DOES NOT VERIFY: whether the recorder split the open TrackSection at the
first sample on the new body. That is the spec's log contracts (the mission's
handoff contract says so in its own result).

A THIN shell: every decision is the pure ``mlib.soi1_decide`` machine and
``mlib.evaluate_soi1_assertions``; the flight, connect, logging and result write are
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

MISSION_NAME = "soi1_mun_exit_burn"


def build_state(params: dict):
    """Build the mlib SOI-1 machine initial state (at START)."""
    return mlib.soi1_initial_state(mlib.soi1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.soi1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is machine-carried evidence stamped on the frame that produced it.
    return mlib.evaluate_soi1_assertions(frames, mlib.soi1_params_from_dict(params),
                                         state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC, no MechJeb: the native AutoPilot points prograde and the machine owns
    # the throttle, so no MechJeb thrust controller can hold the throttle at zero.
    # read_crew is the one opt-in: the handoff gate must OBSERVE the crewed stage.
    # Everything else a gate reads (body, eccentricity, periapsis, time_to_soi,
    # next_body, ap_error, available_thrust, throttle, warp state, warping_to) is in
    # the base snapshot.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             client_name=MISSION_NAME,
                                             read_crew=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS only in COAST, the one leg far from the boundary. Near the crossing any
    # rails warp would put the SOI change back on the on-rails path.
    allow_rails_warp=mlib.soi1_rails_warp_permitted,
    # PHYSICS warp only for the ALIGN slew (the configured index) and the tail of
    # kRPC's WarpTo in COAST; the escape burn, the approach and the straddle burn
    # stay at 1x and any physics warp there flakes the run.
    max_physics_warp=mlib.soi1_max_physics_warp,
    # No settle tail: every assertion is machine-carried evidence.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
