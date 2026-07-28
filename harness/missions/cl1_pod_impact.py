"""Mission cl1_pod_impact: THE CREW-LOSS ATOM, via RAW kRPC (NO MechJeb).

A crewed pod launches, does not deploy a chute, and hits the ground. The crew
dies. That is the entire mission.

Its job is to make ONE thing observable end to end for the first time: what
Parsek records when a kerbal dies, and what the career ledger does about it.

THE INVERSION. Everywhere else in this library a vessel-lost terminal is a
FAILURE. Here the death is the SUCCESS terminal, and it has to be: an unmet
mission drives `hlib.plan_unmet_mission_tail`, which runs the CLEANUP steps only,
so CommitTree would be SKIPPED and there would be no committed recording and no
ledger row left to check. The rationale in full is at the CL1_* constants in
mlib; the SPEC (`harness/scenarios/CL-1-pod-impact.toml`) states it too, because
a reader of the spec must not have to find it here.

WHAT MAKES THE TERMINAL HONEST. The machine gates on an OBSERVED kRPC read of the
KERBAL'S OWN roster status (`SpaceCenter.GetKerbal(name).RosterStatus`), never on
its own "we watched it fall" latch - the documented COMMANDED-vs-OBSERVED defect
class (autotest-status known-gate 7). ``read_roster`` is not a constructor flag
like ``read_chute``: the machine ARMS the watch with ACTION_SET_ROSTER_WATCH on
its first frame, because the read needs the kerbal's NAME and
``MissionSpec.make_control`` takes no parameters.

This is a THIN shell: every decision is the pure ``mlib.cl1_decide`` /
``mlib.evaluate_cl1_assertions``; the flight, connect, logging and result write
are the shared ``mission_runner`` runtime. ``import krpc`` never happens at module
top, so this module imports clean on the base interpreter (no venv), which is what
lets the unittest discovery and the fake-telemetry tests import it without krpc.

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

MISSION_NAME = "cl1_pod_impact"


def build_state(params: dict):
    """Build the mlib CL-1 phase-machine initial state from the missionParams dict."""
    return mlib.cl1_initial_state(mlib.cl1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.cl1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Both assertions read OBSERVED state carried out of the machine (the sticky
    # alive-aboard latch and the terminal roster reading), never a commanded latch
    # and never the post-terminal frames, which are debris.
    return mlib.evaluate_cl1_assertions(frames, mlib.cl1_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC, no MechJeb: a pad hop needs a throttle and one stage activation.
    # No read_chute: this mission never touches a parachute, and a channel it does
    # not gate on is an RPC per poll bought for nothing.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # settle_frames=0: the CREW-LOST terminal means the craft is GONE, so a settle
    # tail would only gather vessel_lost / debris frames. Both assertions are
    # machine-carried evidence, so `evaluate` needs no settled frames at all - the
    # same reasoning B5/B6 use for their 0.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
