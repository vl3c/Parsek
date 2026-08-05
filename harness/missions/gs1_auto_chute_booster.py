"""Mission gs1_auto_chute_booster: the routine two-stage flight, via RAW kRPC.

Flies the fixture save's pre-placed two-stage craft with raw kRPC (NO MechJeb):
throttle + ignite, climb to a LOW target apoapsis, cut throttle, hold a few frames
so engine thrust decays, then STAGE -- one click that fires the decoupler AND arms
the booster's radial chutes. Focus stays on the upper stage, which arms its own
chute at the apoapsis crossing and lands. The booster lands under its own canopy a
few hundred metres away, unwatched by this mission (it is not the active vessel).

This is a THIN shell: every decision is the pure ``mlib.gs1_decide`` phase machine
and ``mlib.evaluate_gs1_assertions``; the flight, connect, logging, and result
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

MISSION_NAME = "gs1_auto_chute_booster"


def build_state(params: dict):
    """Build the mlib GS-1 phase-machine initial state from the missionParams dict."""
    return mlib.gs1_initial_state(mlib.gs1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.gs1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every one of the five rows reads MACHINE-CARRIED evidence rather than
    # re-deriving from the frame tail: the separation and canopy latches are both
    # sticky (a canopy cut on the final frame, or a thrust read that faults after
    # the split, must not erase something that really happened), and the stage
    # altitude is a single-frame fact no tail scan could recover.
    return mlib.evaluate_gs1_assertions(frames, mlib.gs1_params_from_dict(params),
                                        state=state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb). read_chute=True is LOAD-BEARING, not diagnostic: the
    # craftCanopyObserved row gates on the OBSERVED ParachuteState, never on the
    # machine's own "we commanded it" latch (B1's four-month inert-chute lesson).
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True)


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
