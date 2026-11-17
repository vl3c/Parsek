"""Mission psub_dive: the SUB lanes' run (the stock submarine PSUB-1 dives under the sea).

The host (`psub-splashed`, FORGE-psub-pad's harvest relocated into the sea east of the
Space Center by build_psub_splashed.py) hands over PSUB-1 afloat. The seam has already
started a recording on it. Stock KSP has no ballast pump, so the sub sinks by DROPPING
its empty float pairs and rises by dropping its Ore ballast pairs, one stage click at a
time. How much a part displaces is unmeasured (build_psub_craft.py, assumption C2), so
the machine measures the vertical speed after every drop and only drops the next pair
when the sub is not yet sinking (or rising): it calibrates itself, and every reading it
takes is logged (the `psubReading` gate line and the result's evidence).

A THIN shell: every decision is the pure ``mlib.psub_decide`` machine and
``mlib.evaluate_psub_assertions``; the connect, fly loop, logging and result write are
the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside ``mission_runner``,
so this module imports clean on the base interpreter.

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

MISSION_NAME = "psub_dive"


def build_state(params: dict):
    return mlib.psub_initial_state(mlib.psub_params_from_dict(params))


def decide(state, snapshot):
    return mlib.psub_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_psub_assertions(frames, mlib.psub_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC. `read_vessel_name` is REQUIRED (the handoff gate compares the active
    # vessel's name; the "" UNREAD sentinel matches nothing), and so is `read_depth`:
    # the sea-level altitude, the sea-floor elevation, the part count and the current
    # stage are the machine's whole input, and the handoff refuses a dark channel.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             read_vessel_name=True,
                                             read_depth=True,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Real time throughout: a buoyancy reading under warp is a different reading.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # Every assertion is machine-carried evidence; no settle tail to sample.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
