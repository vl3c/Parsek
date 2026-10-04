"""Mission ci9_tip_dock: CI-9, the player docks with a Real-Spawned ghost-chain tip.

Runs AFTER the spec's seam steps have pressed a Real Spawn Control row's "Warp"
for an orbital chain tip (``RealSpawn``) and started a recording on the focused
vessel. The mission targets the spawned vessel by name and docks the active
vessel to it: a MechJeb rendezvous first when the target lies beyond the approach
distance, then B-DOCK's own match-velocity and docking phases.

A THIN shell: every decision is the pure ``mlib.tdock_decide`` (which delegates
RENDEZVOUS / MATCH-VELOCITY / DOCK to ``mlib.bdock_decide``) and
``mlib.evaluate_tdock_assertions``; the connect / flight / result write are the
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

MISSION_NAME = "ci9_tip_dock"


def build_state(params: dict):
    return mlib.tdock_initial_state(mlib.tdock_params_from_dict(params))


def decide(state, snapshot):
    return mlib.tdock_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_tdock_assertions(
        frames, mlib.tdock_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # bdock_dock_transfer's seam: MechJeb for the rendezvous / docking autopilots and
    # the opt-in docking telemetry (docking_state, vessel_count, target distance).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # bdock_dock_transfer's warp contract: rails warp for the rendezvous phasing
    # legs (the rendezvous node-wait hold), the stock 4x physics ceiling otherwise.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
