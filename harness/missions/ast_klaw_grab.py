"""Mission ast_klaw_grab: a probe grabs a real rock with the Advanced Grabbing Unit.

The Klaw lanes (AST-1, AST-2, AST-3, AST-4, COM-1; owner rulings V 2026-11-15) boot a
fixture that parks a Klaw probe on a rock's own orbit a few tens of metres behind it
(harness/tools/build_ast_klaw_fixtures.py). The mission arms the Klaw, targets the rock,
closes on it under RCS with the nose held on the target (SAS Target) until stock merges
the rock's part into the probe, and holds. Optional phases, each switched on by a
mission param: a drill run on the held rock (three mid-mission seam PartActions), the
release (the Klaw's own Release event), a back-off and a coast, and a dock with a named
vessel through MechJeb's docking autopilot (the B-DOCK DOCK phase).

A THIN shell: every decision is the pure ``mlib.klaw_decide`` (which delegates the DOCK
phase to ``mlib.bdock_decide``) and ``mlib.evaluate_klaw_assertions``; the connect /
flight / result write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy
inside ``mission_runner``, so this module imports clean on the base interpreter.

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

MISSION_NAME = "ast_klaw_grab"


def build_state(params: dict):
    return mlib.klaw_initial_state(mlib.klaw_params_from_dict(params))


def decide(state, snapshot):
    return mlib.klaw_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_klaw_assertions(
        frames, mlib.klaw_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # MechJeb for the optional DOCK phase, the docking telemetry (part_count, the
    # capture / release signal, target distance and port state) and the Klaw
    # approach channel (the rock's relative position and velocity).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True, read_klaw=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # A 1x proximity operation throughout: no warp of either kind.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
