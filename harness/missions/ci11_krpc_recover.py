"""Mission ci11_krpc_recover: CI-11, the player recovers a Real-Spawned chain tip
they are NOT flying, through kRPC's ``Vessel.Recover()``.

Runs AFTER the spec's seam steps have pressed a Real Spawn Control row's "Warp"
for a landed chain tip (``RealSpawn``) while the pad vessel stays active. The
mission picks the ONE vessel named ``targetName`` (kRPC 0.5.4 has no persistent
id; an absent or ambiguous name refuses and asks nothing), calls ``recover()`` on
it, and waits for stock's recovery to tear FLIGHT down (the scene goes to
SPACECENTER, so the active vessel's telemetry goes dark).

A THIN shell: every decision is the pure ``mlib.krec_decide`` and
``mlib.evaluate_krec_assertions``; the connect / flight / result write are the
shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
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

MISSION_NAME = "ci11_krpc_recover"


def build_state(params: dict):
    return mlib.krec_initial_state(mlib.krec_params_from_dict(params))


def decide(state, snapshot):
    return mlib.krec_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_krec_assertions(
        frames, mlib.krec_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # No MechJeb, no opt-in channels: the machine reads only UT, vessel_lost and
    # the always-published recover_request_result.
    return mission_runner.KrpcMissionControl(client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # The craft sits on the pad; nothing warps.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
