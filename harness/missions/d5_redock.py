"""Mission d5_redock: D5 `dock-merge-same-tree`, an undock and re-dock in one flight.

Boots a fixture whose ACTIVE vessel is a docked pair recorded in one tree (the
``bdock-second-dock-recorded`` combination: the Station plus the third Kerbal X,
the tip of tree ``ac9641d6``), undocks it, backs off, targets the half the split
just ejected and hard-docks the two halves back together. The undock leaves the
other half recording in the background of the SAME tree, so the dock is a merge of
two members of one tree - Parsek's two-parent Dock branch point.

A THIN shell: every decision is the pure ``mlib.rdock_decide`` (which delegates the
DOCK phase to ``mlib.bdock_decide``) and ``mlib.evaluate_rdock_assertions``; the
connect / flight / result write are the shared ``mission_runner`` runtime.
``import krpc`` is lazy inside ``mission_runner``, so this module imports clean on
the base interpreter.

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

MISSION_NAME = "d5_redock"


def build_state(params: dict):
    return mlib.rdock_initial_state(mlib.rdock_params_from_dict(params))


def decide(state, snapshot):
    return mlib.rdock_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_rdock_assertions(
        frames, mlib.rdock_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # bdock_dock_transfer's seam: MechJeb for the docking autopilot and the opt-in
    # docking telemetry (docking_state, vessel_count, target distance).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # The whole mission is a 1x proximity operation: no warp of either kind.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
